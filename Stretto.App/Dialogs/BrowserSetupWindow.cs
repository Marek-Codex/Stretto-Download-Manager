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
/// The three-step walkthrough that gets Stretto's extension into a Chromium browser.
///
/// One column, one step at a time. The earlier version put a mock browser beside the steps
/// that repeated each step's text a second time, which doubled the reading without adding
/// anything. What replaced it does the work instead: the first step opens the extensions
/// page itself, and the last watches for the extension to connect and says so, so the
/// user never has to go and check.
///
/// It stays above other windows but does not pin Stretto: the browser has to be able to
/// come forward during the walkthrough.
/// </summary>
public sealed class BrowserSetupWindow : Window
{
    /// <summary>How the extension gets in. Chromium loads a folder in Developer mode;
    /// Firefox builds that allow unsigned add-ons install the packed .xpi for good; regular
    /// Firefox only accepts an unsigned add-on until it restarts.</summary>
    private enum Family { Chromium, Firefox, FirefoxTemporary }

    private sealed record Browser(string Name, string Scheme, string Exe, Family Family = Family.Chromium)
    {
        /// <summary>The page the first step opens.</summary>
        public string Page => Family switch
        {
            Family.Firefox => "about:config",
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
    private readonly string _extensionPath = Path.Combine(AppContext.BaseDirectory, "extensions", "chromium");
    private readonly string _firefoxPath = Path.Combine(AppContext.BaseDirectory, "extensions", "firefox");
    private readonly string _xpiPath = Path.Combine(AppContext.BaseDirectory, "extensions", "stretto-firefox.xpi");

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

    private readonly ComboBox _browser = new() { MinWidth = 200 };
    private readonly Border[] _pills = new Border[3];
    private readonly FrameworkElement[] _steps = new FrameworkElement[3];
    private readonly TextBox _address = ReadOnly();
    private readonly TextBlock _openResult = FormKit.Hint("");
    private readonly TextBlock _pasteHint = FormKit.Hint("");
    private readonly FontIcon _connectIcon = new() { FontSize = 30 };
    private readonly TextBlock _connectTitle = new() { FontSize = 15, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextAlignment = TextAlignment.Center };
    private readonly TextBlock _connectBody = FormKit.Hint("");
    private readonly ProgressRing _connectRing = new() { IsActive = true, Width = 30, Height = 30 };
    private readonly Button _back = new() { Content = "Back", MinWidth = 80 };
    private readonly Button _next = new() { Content = "Next", MinWidth = 80 };
    private int _step;
    private bool _connected;

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

        var stepHost = new Grid { Padding = new Thickness(24, 4, 24, 12) };
        foreach (var step in _steps)
            stepHost.Children.Add(step);

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
        string[] titles = ["Open extensions", "Load Stretto", "Connect"];
        var bar = new Grid { Padding = new Thickness(24, 6, 24, 14), ColumnSpacing = 8 };

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
        _openButton.Click += (_, _) => OpenExtensionsPage();
        var copy = FormKit.PushButton("Copy", (_, _) => Copy(_address.Text));

        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(_openHeading);
        panel.Children.Add(_openBody);
        panel.Children.Add(FormKit.Horizontal(_browser, _openButton));
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
        var tile = new Border
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
        panel.Children.Add(FormKit.WithButton(_pathBox, FormKit.Horizontal(
            FormKit.PushButton("Copy", (_, _) => Copy(_pathBox.Text)),
            FormKit.PushButton("Show in folder", (_, _) => Reveal(_pathBox.Text)))));
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
            _steps[i].Visibility = i == _step ? Visibility.Visible : Visibility.Collapsed;
            _pills[i].Background = FormKit.Res(i <= _step ? "StrettoAccentBrush" : "StrettoTrackBrush");
        }

        _back.IsEnabled = _step > 0;
        _next.Content = _step == 2 ? "Finish" : "Next";
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

        switch (browser.Family)
        {
            case Family.Chromium:
                _openHeading.Text = "Open your browser's extensions page";
                _openBody.Text = "Stretto's extension is side-loaded rather than installed from a store, so it goes in through the extensions page.";
                // Chromium browsers won't open their own extensions page when another program asks, so the button
                // copies the address rather than launching a browser window that would only open blank.
                _openButton.Content = "Copy address";
                _pasteHint.Text = $"Paste this into {browser.Name}'s address bar:";
                _openCallout.Text = "Paste the address into the address bar and press Enter, then turn on Developer mode, the switch in the top corner of that page.";
                _loadHeading.Text = "Load Stretto's extension";
                _loadBody.Text = "Drop the tile below anywhere on the extensions page.";
                _tileTitle.Text = "Drag this onto the extensions page";
                _tileHint.Text = "It is the extension folder itself";
                _fallbackHint.Text = "If the browser will not take the drop, choose Load unpacked and pick this folder:";
                _pathBox.Text = _extensionPath;
                break;

            case Family.Firefox:
                _openHeading.Text = "Let it accept Stretto's add-on";
                _openBody.Text = "Stretto's add-on is not signed by Mozilla, and this browser can be told to allow that, once.";
                _openButton.Content = "Open about:config";
                _pasteHint.Text = "Or paste this into the address bar:";
                _openCallout.Text = "Accept the warning, search for xpinstall.signatures.required and set it to false. LibreWolf and some others already have it off.";
                _loadHeading.Text = "Install Stretto's add-on";
                _loadBody.Text = "Drop the tile below onto any window of the browser, then choose Add.";
                _tileTitle.Text = "Drag this onto the browser";
                _tileHint.Text = "It is the packed add-on, stretto-firefox.xpi";
                _fallbackHint.Text = "Or open about:addons, click the gear, choose Install Add-on From File, and pick this file:";
                _pathBox.Text = _xpiPath;
                break;

            case Family.FirefoxTemporary:
                _openHeading.Text = "Open Firefox's debugging page";
                _openBody.Text = "Regular Firefox only runs add-ons Mozilla has signed. It will still load Stretto's until it restarts, which is fine for trying it; for good, use Developer Edition, ESR or LibreWolf.";
                _openButton.Content = "Open about:debugging";
                _pasteHint.Text = "Or paste this into the address bar:";
                _openCallout.Text = "It opens on This Firefox. If not, choose This Firefox on the left.";
                _loadHeading.Text = "Load Stretto's add-on for this session";
                _loadBody.Text = "Click Load Temporary Add-on and pick the manifest.json file in this folder. Firefox forgets it on restart.";
                _tileTitle.Text = "Or drag the packed add-on onto Firefox";
                _tileHint.Text = "Firefox refuses it unless it allows unsigned add-ons";
                _fallbackHint.Text = "The folder holding manifest.json:";
                _pathBox.Text = _firefoxPath;
                break;
        }
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
    /// Brings the browser up and puts its extensions page address on the clipboard. It asks
    /// for the page too, but Chromium browsers decline that from outside, so pasting the
    /// address is the step the user is told to take.
    /// </summary>
    private void OpenExtensionsPage()
    {
        var browser = Chosen;

        // Chromium: launching the browser only produced a blank window (it won't open its own extensions page for
        // another program), so the button just copies, and says plainly what to do next.
        if (!browser.IsFirefox)
        {
            Copy(_address.Text);
            Say(browser.Exe.Length == 0
                ? "Copied. Paste it into your browser's address bar. Most Chromium browsers use chrome://extensions; if yours differs, open its extensions page from its menu."
                : $"Copied — paste it into {browser.Name}'s address bar and press Enter.");
            return;
        }

        if (Installed(browser) is not { } exe)
        {
            Copy(_address.Text);
            if (browser.Exe.Length == 0)
            {
                Say("Address copied. Paste it into your browser. Most Chromium browsers use chrome://extensions; if yours differs, open its extensions page from its menu.");
                return;
            }

            Say($"{browser.Name} was not found, so the address was copied instead. Paste it into the browser.");
            return;
        }

        // Firefox pages (about:config, about:debugging): the address goes on the clipboard too, in case the browser
        // shows a new tab instead of the page.
        Copy(_address.Text);

        try
        {
            Process.Start(new ProcessStartInfo(exe, browser.Page) { UseShellExecute = false });
            Say($"Opened {browser.Name}. If it shows a new tab instead, paste the copied address into the address bar.");
        }
        catch (Exception exception)
        {
            App.Log($"Could not open {browser.Name}: {exception.Message}");
            Copy(_address.Text);
            Say("The browser would not open, so the address was copied instead.");
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
    /// Connected means the extension reached Stretto after this window opened, or a new one
    /// was approved. Either is proof enough; an extension that was already paired before
    /// the walkthrough will ping on its own schedule and show up too.
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

        _connectTitle.Text = _connected ? "Connected" : "Waiting for the extension…";
        _connectBody.Text = _connected
            ? "Downloads you start in the browser now come to Stretto. You can close this."
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
