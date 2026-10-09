using Microsoft.UI.Xaml;

namespace Stretto.App;

public partial class App : Application
{
    private static readonly object LogGate = new();
    private MainWindow? _window;

    /// <summary>Beside the settings rather than in %TEMP%, where disk cleanup deletes it
    /// just when someone needs it for a bug report.</summary>
    internal static readonly string StartupLogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Stretto", "stretto.log");

    /// <summary>Past this the log is rolled over to stretto.old.log, so it cannot grow forever.</summary>
    private const long MaxLogBytes = 2 * 1024 * 1024;

    public App()
    {
        RequestedTheme = ApplicationTheme.Dark;
        InitializeComponent();

        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log($"AppDomain exception: {e.ExceptionObject}");
        UnhandledException += (_, e) => Log($"WinUI exception: {e.Exception}");
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            _window = new MainWindow();
            _window.Activate();
        }
        catch (Exception exception)
        {
            Log($"Launch exception: {exception}");
            throw;
        }
    }

    internal static void Log(string message)
    {
        try
        {
            lock (LogGate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(StartupLogPath)!);

                var existing = new FileInfo(StartupLogPath);
                if (existing.Exists && existing.Length > MaxLogBytes)
                    File.Move(StartupLogPath, Path.ChangeExtension(StartupLogPath, ".old.log"), overwrite: true);

                File.AppendAllText(StartupLogPath, $"{DateTime.Now:O} {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never take the app down.
        }
    }
}
