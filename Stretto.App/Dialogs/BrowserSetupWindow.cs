using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Win32;
using Stretto.App.Services;
using System.Diagnostics;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.Storage;

namespace Stretto.App.Dialogs;

/// <summary>
/// The three-step walkthrough that gets Stretto's extension into a browser.
///
/// One column, one step at a time. The earlier version put a mock browser beside the steps
/// that repeated each step's text a second time, which doubled the reading without adding
/// anything. The first step prepares the selected browser, and the last watches for the
/// extension to connect and says so, so the
/// user never has to go and check.
///
/// It stays above other windows but does not pin Stretto: the browser has to be able to
/// come forward during the walkthrough.
/// </summary>
public sealed class BrowserSetupWindow : Window
{
    /// <summary>How the extension gets in. Chromium loads a folder in Developer mode;
    /// Firefox loads the bundled unsigned add-on temporarily until the browser restarts.</summary>
    private enum Family { Chromium, Firefox, FirefoxTemporary }

    private sealed record Browser(string Name, string Scheme, string Exe, Family Family = Family.Chromium)
    {
        /// <summary>The page the first step opens.</summary>
        public string Page => Family switch
        {
            Family.Firefox => "about:debugging#/runtime/this-firefox",
            Family.FirefoxTemporary => "about:debugging#/runtime/this-firefox",
            _ => $"{Scheme}://extensions",
        };

        public bool IsFirefox => Family != Family.Chromium;
    }

    // Anything built on Chromium loads the same unpacked extension, and anything built on
    // Firefox the same .xpi, so the list is about finding the browser and the right page.
    // Exe is how App Paths knows it; an empty one is the catch-all for browsers not listed.
    private static readonly Browser[] Browsers =
    [
        new("Brave", "brave", "brave.exe"),
        new("Google Chrome", "chrome", "chrome.exe"),
        new("Microsoft Edge", "edge", "msedge.exe"),
        new("Vivaldi", "vivaldi", "vivaldi.exe"),
        new("Opera / Opera GX", "opera", "opera.exe"),
        new("Chromium / Ungoogled Chromium", "chrome", "chromium.exe"),
        new("Thorium", "chrome", "thorium.exe"),
        new("Supermium", "chrome", "supermium.exe"),
        new("Yandex Browser", "browser", "browser.exe"),
        new("Another Chromium browser", "chrome", ""),
        new("Firefox Developer, Nightly or ESR", "", "firefox.exe", Family.Firefox),
        new("LibreWolf", "", "librewolf.exe", Family.Firefox),
        new("Waterfox", "", "waterfox.exe", Family.Firefox),
        new("Floorp", "", "floorp.exe", Family.Firefox),
        new("Zen Browser", "", "zen.exe", Family.Firefox),
        new("Firefox (regular release)", "", "firefox.exe", Family.FirefoxTemporary),
    ];

    private readonly TaskCompletionSource _closed = new();
    private readonly Func<DateTimeOffset?> _lastContact;
    private readonly DateTimeOffset _openedAt = DateTimeOffset.UtcNow;
    private readonly int _approvedAtStart = Pairing.Approved.Count();
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly string _extensionPath = Path.Combine(BundledExtensions.Root, "chromium");
    private readonly string _firefoxPath = Path.Combine(BundledExtensions.Root, "firefox");
    private readonly string _xpiPath = Path.Combine(BundledExtensions.Root, "stretto-firefox.xpi");

    // The text of the first two steps follows the browser family, so it lives in fields.
    private readonly TextBlock _openHeading = Heading();
    private readonly TextBlock _openBody = Body();
    private readonly TextBlock _openCallout = new() { FontSize = 12.5, TextWrapping = TextWrapping.Wrap };
    private readonly Button _openButton = new() { MinWidth = 72 };
    private readonly TextBlock _loadHeading = Heading();
    private readonly TextBlock _loadBody = Body();
    private readonly TextBlock _tileTitle = new() { FontSize = 13, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly TextBlock _tileHint = new() { FontSize = 11.5, Opacity = 0.6, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly TextBlock _fallbackHint = FormKit.Hint("");
    private readonly TextBox _pathBox = ReadOnly();

    private readonly ComboBox _browser = new() { MinWidth = 200, MaxDropDownHeight = 280, HorizontalAlignment = HorizontalAlignment.Stretch };
    private Border _dragTile = null!;
    private readonly Border[] _pills = new Border[3];
    private readonly FrameworkElement[] _steps = new FrameworkElement[3];
    private readonly ScrollViewer[] _stepViews = new ScrollViewer[3];
    private readonly TextBox _address = ReadOnly();
    private readonly TextBlock _openResult = FormKit.Hint("");
    private readonly TextBlock _pasteHint = FormKit.Hint("");
    private readonly TextBlock _readiness = FormKit.Hint("");
    private readonly FontIcon _connectIcon = new() { FontSize = 30 };
    private readonly TextBlock _connectTitle = new() { FontSize = 15, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextAlignment = TextAlignment.Center };
    private readonly TextBlock _connectBody = FormKit.Hint("");
    private readonly ProgressRing _connectRing = new() { IsActive = true, Width = 30, Height = 30 };
    private readonly Button _back = new() { Content = "Back", MinWidth = 80 };
    private readonly Button _next = new() { Content = "Next", MinWidth = 80 };
    private int _step;
    private bool _connected;
    private bool _filesReady;

    public BrowserSetupWindow(AppWindow owner, Func<DateTimeOffset?> lastContact)
    {
        _lastContact = lastContact;
        var titleBar = WindowAppearance.ConfigureUtilityWindow(this, "Set up browser integration");

        const int width = 560, height = 470;
        AppWindow.Resize(new SizeInt32(width, height));
        AppWindow.Move(new PointInt32(
            owner.Position.X + Math.Max(0, (owner.Size.Width - width) / 2),
            owner.Position.Y + Math.Max(0, (owner.Size.Height - height) / 2)));

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsAlwaysOnTop = true;
            presenter.IsResizable = false;
        }

        foreach (var browser in Browsers)
            _browser.Items.Add(browser.Exe.Length == 0 || Installed(browser) is not null ? browser.Name : $"{browser.Name} (not found)");

        // Start on the first browser that is actually installed, Brave first.
        _browser.SelectedIndex = Math.Max(0, Array.FindIndex(Browsers, b => Installed(b) is not null));
        _steps[0] = BuildOpenStep();
        _steps[1] = BuildLoadStep();

        _browser.SelectionChanged += (_, _) => { ApplyBrowser(); Say(""); };
        ApplyBrowser();
        _steps[2] = BuildConnectStep();

        var stepHost = new Grid { Padding = new Thickness(16, 4, 16, 12) };
        for (var i = 0; i < _steps.Length; i++)
        {
            _stepViews[i] = new ScrollViewer
            {
                Content = _steps[i],
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            };
            stepHost.Children.Add(_stepViews[i]);
        }

        var close = new Button { Content = "Close", MinWidth = 80 };
        close.Click += (_, _) => Close();
        close.KeyboardAccelerators.Add(new Microsoft.UI.Xaml.Input.KeyboardAccelerator { Key = Windows.System.VirtualKey.Escape });
        _back.Click += (_, _) => Go(_step - 1);
        _next.Click += (_, _) => { if (_step == 2) Close(); else Go(_step + 1); };

        var shell = new Grid { Background = FormKit.Res("StrettoWindowBrush") };
        shell.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        shell.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        shell.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        shell.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        shell.Children.Add(titleBar);

        var progress = BuildStepBar();
        Grid.SetRow(progress, 1);
        shell.Children.Add(progress);
        Grid.SetRow(stepHost, 2);
        shell.Children.Add(stepHost);

        var footer = WindowAppearance.Footer([_back, _next], [close]);
        Grid.SetRow(footer, 3);
        shell.Children.Add(footer);

        Content = shell.Compact();

        _poll.Tick += (_, _) => CheckConnection();
        Closed += (_, _) => { _poll.Stop(); _closed.TrySetResult(); };
        Go(0);
    }

    public Task WaitForCloseAsync() => _closed.Task;

    /// <summary>Jumps to a step, for screenshotting each one.</summary>
    public void ShowStep(int step) => Go(step);

    /// <summary>Picks a browser by name, for screenshotting each family.</summary>
    public void ChooseBrowser(string name) =>
        _browser.SelectedIndex = Math.Max(0, Array.FindIndex(Browsers, b => b.Name.Contains(name, StringComparison.OrdinalIgnoreCase)));

    private Browser Chosen => Browsers[Math.Max(0, _browser.SelectedIndex)];

    // ═══════════════════════════════════════
    // ═══ Steps
    // ═══════════════════════════════════════

    private Grid BuildStepBar()
    {
        string[] titles = ["Prepare browser", "Load Stretto", "Connect"];
        var bar = new Grid { Padding = new Thickness(16, 8, 16, 14), ColumnSpacing = 8 };

        for (var i = 0; i < titles.Length; i++)
        {
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var label = new StackPanel { Spacing = 6 };
            _pills[i] = new Border { Height = 3, CornerRadius = new CornerRadius(2) };
            label.Children.Add(_pills[i]);
            label.Children.Add(new TextBlock { Text = $"{i + 1}  {titles[i]}", FontSize = 12 });
            Grid.SetColumn(label, i);
            bar.Children.Add(label);
        }

        return bar;
    }

    private StackPanel BuildOpenStep()
    {
        _openButton.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
        _openButton.Click += (_, _) => OpenBrowser();
        var copy = FormKit.PushButton("Copy address", (_, _) =>
        {
            Copy(_address.Text);
            Say("Copied. Paste into the browser's address bar and press Enter.");
        });

        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(_openHeading);
        panel.Children.Add(_openBody);
        panel.Children.Add(FormKit.WithButton(_browser, _openButton));
        panel.Children.Add(_readiness);
        panel.Children.Add(_openResult);
        Say("");
        panel.Children.Add(_pasteHint);
        panel.Children.Add(FormKit.WithButton(_address, copy));
        panel.Children.Add(Callout("\uE946", _openCallout));
        return panel;
    }

    private StackPanel BuildLoadStep()
    {
        var accent = FormKit.Res("StrettoAccentBrush");
        var tile = _dragTile = new Border
        {
            Height = 104,
            BorderBrush = FormKit.Res("StrettoAccentStrokeBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Background = FormKit.Res("StrettoAccentSoftBrush"),
            CanDrag = true,
            Child = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Spacing = 4,
                Children =
                {
                    new FontIcon { Glyph = "", FontSize = 26, Foreground = accent },
                    _tileTitle,
                    _tileHint,
                },
            },
        };

        // WinUI's own drag pipeline, which owns pointer capture and the OLE handoff.
        // Calling DoDragDrop by hand from PointerPressed left the cursor stuck.
        tile.DragStarting += DragTile_DragStarting;

        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(_loadHeading);
        panel.Children.Add(_loadBody);
        panel.Children.Add(tile);
        panel.Children.Add(_fallbackHint);
        panel.Children.Add(_pathBox);
        panel.Children.Add(FormKit.Horizontal(
            FormKit.PushButton("Copy folder path", (_, _) => Copy(_pathBox.Text)),
            FormKit.PushButton("Show in folder", (_, _) => Reveal(_pathBox.Text))));
        return panel;
    }

    private StackPanel BuildConnectStep()
    {
        var status = new StackPanel
        {
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Center,
            Padding = new Thickness(0, 18, 0, 10),
            Children = { _connectRing, _connectIcon, _connectTitle },
        };
        _connectIcon.HorizontalAlignment = HorizontalAlignment.Center;
        _connectRing.HorizontalAlignment = HorizontalAlignment.Center;
        _connectBody.TextAlignment = TextAlignment.Center;
        _connectBody.MaxWidth = 420;

        var panel = Step("Connect", "Nothing to type: the extension finds Stretto and pairs itself.");
        var card = new Border
        {
            Background = FormKit.Res("StrettoCardBrush"),
            BorderBrush = FormKit.Res("StrettoCardBorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(16, 0, 16, 18),
            Child = new StackPanel { Spacing = 4, Children = { status, _connectBody } },
        };
        panel.Children.Add(card);
        panel.Children.Add(Callout("", "Pinning the extension to the toolbar is optional. Its button lists video found on the page."));
        UpdateConnectState();
        return panel;
    }

    private static StackPanel Step(string title, string body)
    {
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(new TextBlock { Text = title, FontSize = 18, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text = body, FontSize = 13, TextWrapping = TextWrapping.Wrap, Foreground = FormKit.Res("TextFillColorSecondaryBrush") });
        return panel;
    }

    private static Border Callout(string glyph, string text) =>
        Callout(glyph, new TextBlock { Text = text, FontSize = 12.5, TextWrapping = TextWrapping.Wrap });

    private static Border Callout(string glyph, TextBlock label)
    {
        var row = new Grid { ColumnSpacing = 10 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.Children.Add(new FontIcon { Glyph = glyph, FontSize = 14, Foreground = FormKit.Res("StrettoAccentBrush"), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0) });
        Grid.SetColumn(label, 1);
        row.Children.Add(label);

        return new Border
        {
            Margin = new Thickness(0, 4, 0, 0),
            Padding = new Thickness(12, 9, 12, 9),
            CornerRadius = new CornerRadius(8),
            Background = FormKit.Res("StrettoCardBrush"),
            Child = row,
        };
    }

    private void Go(int step)
    {
        _step = Math.Clamp(step, 0, 2);

        for (var i = 0; i < _steps.Length; i++)
        {
            _stepViews[i].Visibility = i == _step ? Visibility.Visible : Visibility.Collapsed;
            _pills[i].Background = FormKit.Res(i <= _step ? "StrettoAccentBrush" : "StrettoTrackBrush");
        }

        _back.IsEnabled = _step > 0;
        _next.Content = _step == 2 ? (_connected ? "Finish" : "Close setup") : "Next";
        _next.IsEnabled = _step != 0 || _filesReady;
        _next.Style = _step < 2 || _connected ? (Style)Application.Current.Resources["AccentButtonStyle"] : null;

        // Only the last step watches for the connection, and only while it is on screen.
        if (_step == 2) { CheckConnection(); _poll.Start(); }
        else _poll.Stop();
    }

    // ═══════════════════════════════════════
    // ═══ Actions
    // ═══════════════════════════════════════

    /// <summary>Rewrites the first two steps for the chosen browser's family.</summary>
    private void ApplyBrowser()
    {
        var browser = Chosen;
        _address.Text = browser.Page;
        _dragTile.Visibility = Visibility.Visible;
        _dragTile.CanDrag = true;
        _openButton.Content = "Open browser";
        _openButton.IsEnabled = Installed(browser) is not null;
        _filesReady = browser.IsFirefox
            ? File.Exists(_xpiPath) && File.Exists(Path.Combine(_firefoxPath, "manifest.json"))
            : File.Exists(Path.Combine(_extensionPath, "manifest.json"));
        _readiness.Text = !_filesReady
            ? "The bundled extension files are missing. Reinstall Stretto before continuing."
            : Installed(browser) is not null ? "Browser found · Stretto extension ready"
            : "Stretto extension ready · Open your browser manually, or choose another browser.";
        _next.IsEnabled = _step != 0 || _filesReady;

        switch (browser.Family)
        {
            case Family.Chromium:
                _openHeading.Text = "Prepare your browser";
                _openBody.Text = "Choose the browser where you want Stretto to catch downloads. Open it, then use the address below to reach its extensions page.";
                _pasteHint.Text = "Extensions page — paste into the browser's address bar:";
                _openCallout.Text = "Turn on Developer mode on the extensions page. Leave that page open, then choose Next to load Stretto.";
                _loadHeading.Text = "Load Stretto's extension";
                _loadBody.Text = "Drop the tile below anywhere on the extensions page.";
                _tileTitle.Text = "Drag this onto the extensions page";
                _tileHint.Text = "It is the extension folder itself";
                _fallbackHint.Text = "If the browser will not take the drop, choose Load unpacked and pick this folder:";
                _pathBox.Text = _extensionPath;
                break;

            case Family.Firefox:
            case Family.FirefoxTemporary:
                _openHeading.Text = "Open Firefox's debugging page";
                _openBody.Text = "The bundled add-on is unsigned. Load it temporarily to try Stretto; it must be loaded again after the browser restarts. Regular Firefox requires Mozilla signing for permanent installation.";
                _pasteHint.Text = "Temporary add-on page — paste into the browser's address bar:";
                _openCallout.Text = "Choose This Firefox if needed. This is a temporary installation; your browser’s signature checks stay enabled.";
                _loadHeading.Text = "Load Stretto's add-on for this session";
                _loadBody.Text = browser.Family == Family.Firefox
                    ? "The XPI below is unsigned. Installation depends on whether your browser is configured to accept unsigned add-ons. If it rejects the file, use about:debugging → This Firefox → Load Temporary Add-on and select manifest.json in the folder below."
                    : "For regular Firefox, choose Load Temporary Add-on and select manifest.json in the folder below. This lasts until restart. The XPI drag option needs a browser configured to accept unsigned add-ons.";
                _tileTitle.Text = "Drag the XPI onto your browser";
                _tileHint.Text = "Unsigned: regular Firefox will reject permanent installation";
                _fallbackHint.Text = "The folder holding manifest.json:";
                _pathBox.Text = _firefoxPath;
                break;
        }
        ToolTipService.SetToolTip(_pathBox, _pathBox.Text);
    }

    private static TextBlock Heading() => new() { FontSize = 18, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };

    private static TextBlock Body() => new() { FontSize = 13, TextWrapping = TextWrapping.Wrap, Foreground = FormKit.Res("TextFillColorSecondaryBrush") };

    /// <summary>Opens Explorer on a file or folder, selected.</summary>
    private static void Reveal(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\""));
        }
        catch (Exception exception)
        {
            App.Log($"Could not show {path}: {exception.Message}");
        }
    }

    /// <summary>
    /// Opens the selected browser. Copying its internal-page address is a separate action:
    /// those pages cannot reliably be opened by another application.
    /// </summary>
    private void OpenBrowser()
    {
        var browser = Chosen;

        if (Installed(browser) is not { } exe)
        {
            Say("Open your browser manually, then copy the address below.");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
            Say($"Opened {browser.Name}. Use Copy address below to open its setup page.");
        }
        catch (Exception exception)
        {
            App.Log($"Could not open {browser.Name}: {exception.Message}");
            Say("The browser could not be opened. Open it manually, then copy the address below.");
        }
    }

    /// <summary>
    /// Supplies the folder once the drag begins, through a deferral. This used to block on
    /// the async folder lookup from inside the drag handler, on the UI thread, which is how
    /// a drag hangs.
    /// </summary>
    private async void DragTile_DragStarting(UIElement sender, Microsoft.UI.Xaml.DragStartingEventArgs e)
    {
        var deferral = e.GetDeferral();

        try
        {
            IStorageItem item = Chosen.IsFirefox
                ? await StorageFile.GetFileFromPathAsync(_xpiPath)
                : await StorageFolder.GetFolderFromPathAsync(_extensionPath);
            e.Data.SetStorageItems([item]);
            e.Data.Properties.Title = "Stretto browser extension";
            e.Data.RequestedOperation = DataPackageOperation.Copy;
        }
        catch (Exception exception)
        {
            App.Log($"Extension drag failed: {exception.Message}");
            e.Cancel = true;
        }
        finally
        {
            deferral.Complete();
        }
    }

    /// <summary>
    /// Detects fresh extension contact or a new approval, not the selected browser's identity.
    /// Another browser can ring this bell, so the UI asks the user to check its own popup.
    /// Encouraging news, yes. A passport check, no.
    /// </summary>
    private void CheckConnection()
    {
        var contact = _lastContact();
        var connected = (contact is { } seen && seen >= _openedAt) || Pairing.Approved.Count() > _approvedAtStart;

        if (connected == _connected) return;
        _connected = connected;
        UpdateConnectState();
        Go(_step);
    }

    private void UpdateConnectState()
    {
        _connectRing.Visibility = _connected ? Visibility.Collapsed : Visibility.Visible;
        _connectIcon.Visibility = _connected ? Visibility.Visible : Visibility.Collapsed;
        _connectIcon.Glyph = "";
        _connectIcon.Foreground = FormKit.Res("StrettoDoneBrush");

        _connectTitle.Text = _connected ? "An extension is connected" : "Waiting for the extension…";
        _connectBody.Text = _connected
            ? "Stretto has heard from a browser extension. Open the extension in your chosen browser and check that it says Connected. Capture rules still decide which downloads Stretto takes."
            : "This updates by itself once the extension is loaded. If Stretto asks whether to allow an extension, choose Allow. Turning the extension off and on again makes it try straight away.";
    }

    /// <summary>An empty line still takes its height, so the result hides when blank.</summary>
    private void Say(string text)
    {
        _openResult.Text = text;
        _openResult.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private static string? Installed(Browser browser)
    {
        if (browser.Exe.Length == 0) return null;

        foreach (var root in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            try
            {
                using var key = root.OpenSubKey($@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\{browser.Exe}");
                if (key?.GetValue(null) is string path && File.Exists(path.Trim('"')))
                    return path.Trim('"');
            }
            catch
            {
                // An unreadable key is the same as a missing one here.
            }
        }

        return null;
    }

    private static TextBox ReadOnly() => new()
    {
        IsReadOnly = true,
        FontFamily = new FontFamily("Consolas"),
        FontSize = 12,
    };

    private static void Copy(string text)
    {
        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);
    }
}
