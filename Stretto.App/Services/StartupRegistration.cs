using Microsoft.Win32;
using Windows.ApplicationModel;
using Microsoft.Windows.AppLifecycle;
using AppInstance = Microsoft.Windows.AppLifecycle.AppInstance;

namespace Stretto.App.Services;

/// <summary>
/// Whether Stretto starts with Windows.
///
/// MSIX uses its declared StartupTask; the EXE uses the per-user Run key.
/// Both are opt-in and require no service or elevation.
/// </summary>
public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Stretto";
    private static StartupTask? _startupTask;
    public static bool IsPackaged { get; } = DetectPackage();

    private static bool DetectPackage()
    {
        uint length = 0;
        return GetCurrentPackageFullName(ref length, IntPtr.Zero) == 122; // ERROR_INSUFFICIENT_BUFFER
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern int GetCurrentPackageFullName(ref uint length, IntPtr packageFullName);

    public static async Task InitializeAsync()
    {
        if (IsPackaged)
        {
            Stretto.Core.DownloadStore.Directory = Path.Combine(Windows.Storage.ApplicationData.Current.LocalFolder.Path, "Stretto");
            BundledExtensions.RefreshPackagedCopy();
            _startupTask = await StartupTask.GetAsync("StrettoStartup");
        }
    }

    /// <summary>Tells a launched copy to come up hidden, as a startup launch should.</summary>
    public const string HiddenSwitch = "--tray";

    public static bool IsEnabled
    {
        get
        {
            if (IsPackaged)
                return _startupTask?.State is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy;
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
    public static async Task ApplyAsync(bool enabled)
    {
        try
        {
            if (IsPackaged)
            {
                var task = _startupTask ??= await StartupTask.GetAsync("StrettoStartup");
                if (enabled) await task.RequestEnableAsync();
                else task.Disable();
                if (enabled && !IsEnabled)
                    App.Log("Windows has disabled Stretto startup. Enable it in Windows Startup apps if desired.");
                return;
            }
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
        Environment.GetCommandLineArgs().Any(a => a.Equals(HiddenSwitch, StringComparison.OrdinalIgnoreCase))
        || (IsPackaged && AppInstance.GetCurrent().GetActivatedEventArgs().Kind == ExtendedActivationKind.StartupTask);

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
            a.StartsWith($"{ProtocolScheme}:", StringComparison.OrdinalIgnoreCase))
        || (IsPackaged && AppInstance.GetCurrent().GetActivatedEventArgs().Kind == ExtendedActivationKind.Protocol);

    /// <summary>
    /// Claims (or releases) the scheme. Called on every launch rather than from a setting,
    /// because the executable can move -- a download manager that gets moved and rebuilt
    /// would otherwise leave the scheme pointing at a path that no longer exists.
    /// </summary>
    public static void RegisterProtocol(bool enabled)
    {
        // MSIX owns registration and removes it with the package.
        if (IsPackaged) return;
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

            using var command = key.CreateSubKey(@"shell\open\command", writable: true);
            command?.SetValue(null, $"\"{executable}\" \"%1\"");
        }
        catch (Exception exception)
        {
            App.Log($"Could not change the {ProtocolScheme}: link: {exception.Message}");
        }
    }
}
