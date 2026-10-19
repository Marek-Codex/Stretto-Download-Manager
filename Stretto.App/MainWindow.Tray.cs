using Stretto.Core;
using Microsoft.UI.Windowing;
using Stretto.App.Dialogs;
using Stretto.App.Interop;

namespace Stretto.App;

/// <summary>
/// The notification area icon, and the window hiding that goes with it.
/// </summary>
public sealed partial class MainWindow
{
    private TrayIcon? _tray;

    /// <summary>
    /// Set when the app is genuinely shutting down, so "close to tray" knows to let the
    /// close through instead of hiding the window for the hundredth time.
    /// </summary>
    private bool _exiting;

    private void StartTray()
    {
        _tray?.Dispose();
        _tray = null;

        if (!_settings.ShowTrayIcon)
            return;

        try
        {
            _tray = new TrayIcon(
                Path.Combine(AppContext.BaseDirectory, "Assets", "stretto.ico"),
                "Stretto Download Manager");

            _tray.Activated += RestoreFromTray;

            _tray.AddMenuItem("Show Stretto", RestoreFromTray);
            _tray.AddSeparator();
            _tray.AddMenuItem("Resume all", () => _downloads.ResumeAll());
            _tray.AddMenuItem("Pause all", () => _downloads.PauseAll());
            _tray.AddSubmenu("Speed limit", SpeedLimitChoices());
            _tray.AddSeparator();
            _tray.AddMenuItem("Exit", ExitApplication);
        }
        catch (Exception exception)
        {
            // The app is perfectly usable without it, so a shell that refuses the icon is
            // a log line rather than a failure.
            App.Log($"Could not create the tray icon: {exception.Message}");
            _tray = null;
        }
    }

    /// <summary>Hooks the window events that hiding depends on.</summary>
    private void WatchWindowState()
    {
        AppWindow.Closing += (_, args) =>
        {
            // Only intercept while there is an icon to restore from. Hiding the window with
            // no tray icon would leave the app running with no way back to it.
            if (!_exiting && _settings.CloseToTray && _tray is not null)
            {
                args.Cancel = true;
                AppWindow.Hide();
            }
        };

        AppWindow.Changed += (_, args) =>
        {
            if (!args.DidPresenterChange || !_settings.MinimizeToTray || _tray is null)
                return;

            if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized })
                AppWindow.Hide();
        };
    }

    private void RestoreFromTray()
    {
        try
        {
            AppWindow.Show();

            if (AppWindow.Presenter is OverlappedPresenter presenter)
                presenter.Restore();

            Activate();
        }
        catch (Exception exception)
        {
            App.Log($"Could not restore from the tray: {exception.Message}");
        }
    }

    private void ExitApplication()
    {
        _exiting = true;
        _tray?.Dispose();
        _tray = null;

        Microsoft.UI.Xaml.Application.Current.Exit();
    }

    /// <summary>
    /// Keeps the hover text current, so the icon answers "is anything still going?" without
    /// the window having to be opened.
    /// </summary>
    private void UpdateTrayTooltip()
    {
        if (_tray is null)
            return;

        var active = _downloads.Items.Count(i => i.IsRunning);

        if (active == 0)
        {
            _tray.SetTooltip("Stretto — idle");
            return;
        }

        var speed = _downloads.Items.Where(i => i.IsRunning).Sum(i => i.BytesPerSecond);
        _tray.SetTooltip($"Stretto — {active} active, {DownloadItem.FormatBytes((long)speed)}/s");
    }

    /// <summary>
    /// Speed limits one click away, for throttling during a call or a game without going
    /// through Settings. Other opens the setting itself for any value not listed.
    /// </summary>
    private IEnumerable<(string, Func<bool>, Action)> SpeedLimitChoices()
    {
        (string Label, long Bytes)[] presets =
        [
            ("No limit", 0),
            ("512 KB/s", 512L * 1024),
            ("1 MB/s", 1024L * 1024),
            ("5 MB/s", 5L * 1024 * 1024),
            ("10 MB/s", 10L * 1024 * 1024),
        ];

        foreach (var (label, bytes) in presets)
            yield return (label, () => _settings.SpeedLimitBytesPerSecond == bytes, () => SetSpeedLimit(bytes));

        yield return ("Other…",
            () => presets.All(p => p.Bytes != _settings.SpeedLimitBytesPerSecond),
            () => DispatcherQueue.TryEnqueue(async () =>
            {
                RestoreFromTray();
                await OpenSettingsAsync(SettingsDialog.Section.Connections);
            }));
    }

    private void SetSpeedLimit(long bytesPerSecond)
    {
        _settings.SpeedLimitBytesPerSecond = bytesPerSecond;
        _settings.Save();
        _downloads.SetSpeedLimit(bytesPerSecond);
    }
}
