using Microsoft.Win32;

namespace Stretto.App.Services;

/// <summary>
/// Whether Stretto starts with Windows.
///
/// The per-user Run key rather than a scheduled task or a service: it needs no elevation,
/// it is where users expect to find and remove such things, and an app that manages
/// downloads has no business running before anyone has signed in.
/// </summary>
public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Stretto";

    /// <summary>Tells a launched copy to come up hidden, as a startup launch should.</summary>
    public const string HiddenSwitch = "--tray";

    public static bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey);
                return key?.GetValue(ValueName) is string;
            }
            catch (Exception exception)
            {
                App.Log($"Could not read the startup entry: {exception.Message}");
                return false;
            }
        }
    }

    /// <summary>
    /// Adds or removes the entry. Rewritten every time it is enabled rather than only when
    /// absent, so moving the executable does not leave a startup entry pointing at nothing.
    /// </summary>
    public static void Apply(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key is null)
                return;

            if (!enabled)
            {
                if (key.GetValue(ValueName) is not null)
                    key.DeleteValue(ValueName, throwOnMissingValue: false);

                return;
            }

            if (Environment.ProcessPath is not { Length: > 0 } executable)
                return;

            key.SetValue(ValueName, $"\"{executable}\" {HiddenSwitch}");
        }
        catch (Exception exception)
        {
            App.Log($"Could not change the startup entry: {exception.Message}");
        }
    }

    /// <summary>True when this launch was the automatic one, or asked to be hidden.</summary>
    public static bool LaunchedHidden =>
        Environment.GetCommandLineArgs().Any(a => a.Equals(HiddenSwitch, StringComparison.OrdinalIgnoreCase));

    // ═══ Being launched by the browser extension ═══
    //
    // The extension's popup says "Stretto is not running" and, like XDM's, offers a button
    // that starts it. The scheme is a per-user registry key rather than a machine-wide one:
    // no elevation, and removing it is one key.

    public const string ProtocolScheme = "stretto";

    private const string ClassesKey = @"Software\Classes\" + ProtocolScheme;

    /// <summary>True when this launch came from the extension's Launch button.</summary>
    public static bool LaunchedFromProtocol =>
        Environment.GetCommandLineArgs().Any(a =>
            a.StartsWith($"{ProtocolScheme}:", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Claims (or releases) the scheme. Called on every launch rather than from a setting,
    /// because the executable can move -- a download manager that gets moved and rebuilt
    /// would otherwise leave the scheme pointing at a path that no longer exists.
    /// </summary>
    public static void RegisterProtocol(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(ClassesKey, writable: enabled);

            if (key is null)
                return;

            if (!enabled)
            {
                Registry.CurrentUser.DeleteSubKeyTree(ClassesKey, throwOnMissingSubKey: false);
                return;
            }

            if (Environment.ProcessPath is not { Length: > 0 } executable)
                return;

            key.SetValue(null, $"URL:Stretto Download Manager");
            key.SetValue("URL Protocol", "");

            // No %1: the extension only ever asks Stretto to start, and passing the URI
            // through would put an argument nobody reads on the command line.
            using var command = key.CreateSubKey(@"shell\open\command", writable: true);
            command?.SetValue(null, $"\"{executable}\"");
        }
        catch (Exception exception)
        {
            App.Log($"Could not change the {ProtocolScheme}: link: {exception.Message}");
        }
    }
}
