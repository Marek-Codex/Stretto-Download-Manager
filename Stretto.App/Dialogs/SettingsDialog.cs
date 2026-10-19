using Stretto.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Stretto.App.Services;
using System.Collections.ObjectModel;
using static Stretto.App.Dialogs.FormKit;

namespace Stretto.App.Dialogs;

/// <summary>
/// Stretto's settings: a sidebar of sections, each a page of grouped cards.
///
/// Built in code rather than XAML. It is eight pages of the same three shapes (card, row,
/// stacked row), which a builder says once and markup said two hundred times; and the old
/// markup was the file most likely to trip XamlCompiler's silent failures.
///
/// Everything edits copies until Save, so Cancel really cancels.
/// </summary>
public sealed partial class SettingsDialog : UserControl, ICloseRequestedWindowForm, IWindowFormChrome
{
    /// <summary>
    /// What the main window can ask to open. Several map onto one page now: the pages are
    /// grouped by what someone came to change, not one per subsystem.
    /// </summary>
    public enum Section { General, Connections, Proxy, Browser, CaptureRules, Passwords, System, Advanced }

    private static readonly (Section Section, string Title, string Glyph)[] Sections =
    [
        (Section.General, "General", ""),
        (Section.Connections, "Connections", ""),
        (Section.Proxy, "Proxy", ""),
        (Section.Browser, "Browser", ""),
        (Section.CaptureRules, "Capture rules", ""),
        (Section.Passwords, "Passwords", ""),
        (Section.System, "System", ""),
        (Section.Advanced, "Advanced", ""),
    ];

    private readonly AppSettings _settings;
    private readonly nint _owner;
    private readonly HttpClient _http;
    private readonly string _browserStatus;

    private readonly ListView _nav = new() { SelectionMode = ListViewSelectionMode.Single };
    /// <summary>
    /// The sidebar, in order: each page, its icon, and the sections it holds. Six pages
    /// where there were eight, because nobody goes looking for "Proxy" as somewhere other
    /// than the network, or for capture rules as somewhere other than the browser.
    /// </summary>
    private static readonly (string Title, Section Icon, Section[] Holds)[] Pages =
    [
        ("Downloads", Section.General, [Section.General]),
        ("Network", Section.Connections, [Section.Connections, Section.Proxy]),
        ("Browser", Section.Browser, [Section.Browser, Section.CaptureRules]),
        ("Sign-ins", Section.Passwords, [Section.Passwords]),
        ("Windows", Section.System, [Section.System]),
        ("Components & about", Section.Advanced, [Section.Advanced]),
    ];

    private readonly ScrollViewer[] _pages = new ScrollViewer[Pages.Length];

    private readonly ObservableCollection<CategoryRule> _categories = [];
    private readonly ObservableCollection<LoginRow> _logins = [];
    private readonly ObservableCollection<string> _extensions = [];

    private string _folder;

    public string PrimaryText => "Save";
    public string? SecondaryText => null;
    public string? CancelText => "Cancel";
    public bool AccentPrimary => true;
    public bool CanPrimary => true;
    public bool CanSecondary => false;
    public Thickness BodyPadding => new(0);
    public UIElement? FooterStart => null;
    public event EventHandler<WindowFormCloseEventArgs>? CloseRequested;

    /// <summary>Set when the user asked for the browser extension walkthrough.</summary>
    public bool RequestedSetup { get; private set; }

    // ═══ General ═══
    private readonly TextBox _folderBox = new() { IsReadOnly = true };
    private readonly ToggleSwitch _sortByType;
    private readonly ToggleSwitch _overwrite;
    private readonly ToggleSwitch _tagClashes;
    private readonly ComboBox _nameFrom = new() { MinWidth = 190 };
    private readonly ToggleSwitch _serverTimestamp;
    private readonly ToggleSwitch _startAuto;
    private readonly ToggleSwitch _progressWindow;
    private readonly ToggleSwitch _completeDialog;
    private readonly ComboBox _doubleClick = new() { MinWidth = 190 };
    private readonly NumberBox _keepDays = Number(0, 3650);
    private readonly SimpleTable<CategoryRule> _categoryTable;
    private Button? _categoryEdit, _categoryDelete;

    // ═══ Connections ═══
    private readonly NumberBox _concurrent = Number(1, 16);
    private readonly NumberBox _connections = Number(1, 32);
    private readonly NumberBox _timeout = Number(5, 300);
    private readonly NumberBox _retries = Number(0, 20);
    private readonly NumberBox _speedLimit = Number(0, 10_000_000);
    private readonly ToggleSwitch _gentle;
    private readonly ToggleSwitch _windowsAuth;
    private readonly ToggleSwitch _autoRetry;
    private readonly TextBox _hostLimits = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 72, PlaceholderText = "example.com = 1" };

    // ═══ Proxy ═══
    private readonly ComboBox _proxyMode = new() { MinWidth = 220 };
    private readonly ComboBox _proxyProtocol = new() { MinWidth = 220 };
    private readonly TextBox _proxyHost = new() { Header = "Host" };
    private readonly NumberBox _proxyPort = new() { Header = "Port", Minimum = 1, Maximum = 65535, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Hidden };
    private readonly TextBox _proxyUser = new() { Header = "User name" };
    private readonly PasswordBox _proxyPassword = new() { Header = "Password" };

    // ═══ Browser ═══
    private readonly ToggleSwitch _monitoring;
    private readonly ToggleSwitch _confirmCaptures;
    private readonly ToggleSwitch _notifyMedia;
    private readonly ToggleSwitch _clipboard;
    private readonly NumberBox _port = Number(1024, 65535);
    private readonly ListView _extensionList = new() { SelectionMode = ListViewSelectionMode.Single, MaxHeight = 120 };
    private readonly Button _revoke;

    // ═══ Capture rules ═══
    private readonly TextBox _captureTypes = Multiline(64);
    private readonly TextBox _videoTypes = Multiline(48);
    private readonly TextBox _mediaTypes = Multiline(48);
    private readonly TextBox _skipHosts = Multiline(48);
    private readonly TextBox _alwaysHosts = Multiline(48);
    private readonly NumberBox _minVideo = Number(0, 1_048_576);

    // ═══ Passwords ═══
    private readonly SimpleTable<LoginRow> _loginTable;
    private Button? _loginEdit, _loginDelete;

    // ═══ System ═══
    private readonly ToggleSwitch _trayIcon;
    private readonly ToggleSwitch _minimizeToTray;
    private readonly ToggleSwitch _closeToTray;
    private readonly ToggleSwitch _startMinimized;
    private readonly ToggleSwitch _startWithWindows;
    private readonly ToggleSwitch _resumeOnStartup;
    private readonly ToggleSwitch _preventSleep;
    private readonly ToggleSwitch _shutdown;
    private readonly TextBox _runWhenDone = new() { PlaceholderText = "Nothing" };
    private readonly ToggleSwitch _scan;
    private readonly TextBox _antivirusPath = new() { PlaceholderText = "Path to the scanner" };
    private readonly TextBox _antivirusArgs = new() { PlaceholderText = "Command line parameters" };

    // ═══ Advanced ═══
    private readonly TextBlock _ffmpegStatus = Status();
    private readonly TextBlock _ytDlpStatus = Status();
    private readonly Button _ffmpegInstall = new() { MinWidth = 84 };
    private readonly Button _ytDlpInstall = new() { MinWidth = 84 };
    private readonly Button _ffmpegWinget = new() { Content = "winget", MinWidth = 68 };
    private readonly Button _ytDlpWinget = new() { Content = "winget", MinWidth = 68 };
    private readonly TextBlock _componentSource = Hint("");
    private readonly TextBox _userAgent = new();
    private readonly ToggleSwitch _checkUpdates;
    private readonly TextBlock _updateStatus = Status();
    private readonly Button _checkNow = new() { Content = "Check now", MinWidth = 84 };
    private readonly TextBlock _diagnosticsStatus = Status();
    private string? _updatePage;

    public SettingsDialog(AppSettings settings, nint owner, HttpClient http, string browserStatus = "")
    {
        _settings = settings;
        _owner = owner;
        _http = http;
        _browserStatus = browserStatus;
        _folder = settings.ResolvedDownloadFolder;

        _sortByType = Toggle(settings.SortByType);
        _overwrite = Toggle(settings.OverwriteExisting);
        _tagClashes = Toggle(settings.TagClashingNames);
        _serverTimestamp = Toggle(settings.UseServerTimestamp);
        _startAuto = Toggle(settings.StartAutomatically);
        _progressWindow = Toggle(settings.ShowProgressWindow);
        _completeDialog = Toggle(settings.ShowCompleteDialog);
        _gentle = Toggle(settings.AdaptiveConnections);
        _windowsAuth = Toggle(settings.UseWindowsCredentials);
        _autoRetry = Toggle(settings.AutoRetry);
        _resumeOnStartup = Toggle(settings.ResumeOnStartup);
        _monitoring = Toggle(settings.BrowserMonitoring);
        _confirmCaptures = Toggle(settings.ConfirmCaptures);
        _notifyMedia = Toggle(settings.NotifyOnMedia);
        _clipboard = Toggle(settings.MonitorClipboard);
        _trayIcon = Toggle(settings.ShowTrayIcon);
        _minimizeToTray = Toggle(settings.MinimizeToTray);
        _closeToTray = Toggle(settings.CloseToTray);
        _startMinimized = Toggle(settings.StartMinimized);
        // Read from the registry, not from settings: it may have been removed elsewhere.
        _startWithWindows = Toggle(StartupRegistration.IsEnabled);
        _preventSleep = Toggle(settings.PreventSleep);
        _shutdown = Toggle(settings.ShutdownWhenDone);
        _scan = Toggle(settings.ScanWithAntivirus);
        _checkUpdates = Toggle(settings.CheckForUpdates);
        _revoke = PushButton("Revoke", (_, _) =>
        {
            if (_extensionList.SelectedItem is string origin)
                _extensions.Remove(origin);
        });

        // Clones, so Cancel really cancels: the live table is only replaced in Apply.
        foreach (var rule in settings.Categories)
            _categories.Add(rule.Clone());

        foreach (var saved in SiteCredentials.All())
            _logins.Add(new LoginRow { Host = saved.Host, Username = saved.Username, Password = saved.Password });

        foreach (var origin in Pairing.Approved)
            _extensions.Add(origin);

        _categoryTable = new SimpleTable<CategoryRule>(
            ["Category", "File types", "Folder"],
            [new GridLength(96), new GridLength(1, GridUnitType.Star), new GridLength(210)],
            rule => [rule.Name, rule.ExtensionsText, rule.FolderText]);

        _loginTable = new SimpleTable<LoginRow>(
            ["Website or server", "User name"],
            [new GridLength(1, GridUnitType.Star), new GridLength(170)],
            row => [row.Host, row.Username]);

        _pages[0] = Page(Pages[0].Title, BuildGeneral());
        _pages[1] = Page(Pages[1].Title, BuildConnections().Append(PartTitle("Proxy")).Concat(BuildProxy()));
        _pages[2] = Page(Pages[2].Title, BuildBrowser().Append(PartTitle("What gets captured")).Concat(BuildCaptureRules()));
        _pages[3] = Page(Pages[3].Title, BuildPasswords());
        _pages[4] = Page(Pages[4].Title, BuildSystem());
        _pages[5] = Page(Pages[5].Title, BuildAdvanced());

        Content = BuildShell();
        _nav.SelectedIndex = 0;
    }

    public void SelectSection(Section section) =>
        _nav.SelectedIndex = Math.Max(0, Array.FindIndex(Pages, p => p.Holds.Contains(section)));

    /// <summary>A heading partway down a page, where two old sections now share it.</summary>
    private static TextBlock PartTitle(string text) => new()
    {
        Text = text,
        FontSize = 16,
        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        Margin = new Thickness(2, 26, 0, 0),
    };

    // ═══════════════════════════════════════
    // ═══ Shell
    // ═══════════════════════════════════════

    private Grid BuildShell()
    {
        foreach (var (title, icon, _) in Pages)
        {
            var glyph = Sections[(int)icon].Glyph;
            var item = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Padding = new Thickness(2, 6, 2, 6) };
            item.Children.Add(new FontIcon { Glyph = glyph, FontSize = 15 });
            item.Children.Add(new TextBlock { Text = title, FontSize = 13, VerticalAlignment = VerticalAlignment.Center });
            _nav.Items.Add(new ListViewItem { Content = item, MinHeight = 0, Padding = new Thickness(10, 0, 10, 0) });
        }

        _nav.SelectionChanged += (_, _) =>
        {
            var selected = Math.Max(0, _nav.SelectedIndex);
            for (var i = 0; i < _pages.Length; i++)
                _pages[i].Visibility = i == selected ? Visibility.Visible : Visibility.Collapsed;
        };

        var sidebar = new Border
        {
            Width = 196,
            Padding = new Thickness(6, 10, 6, 10),
            Background = Res("StrettoSidebarBrush"),
            BorderBrush = Res("StrettoSidebarBorderBrush"),
            BorderThickness = new Thickness(0, 0, 1, 0),
            Child = _nav,
        };

        var content = new Grid();
        foreach (var page in _pages)
            content.Children.Add(page);

        var shell = new Grid { Width = 820, Height = 560 };
        shell.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        shell.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        shell.Children.Add(sidebar);
        Grid.SetColumn(content, 1);
        shell.Children.Add(content);
        return shell;
    }

    private static ScrollViewer Page(string title, IEnumerable<UIElement> blocks)
    {
        var stack = new StackPanel { Padding = new Thickness(24, 14, 24, 20), MaxWidth = 720, HorizontalAlignment = HorizontalAlignment.Stretch };
        stack.Children.Add(PageTitle(title));
        foreach (var block in blocks)
            stack.Children.Add(block);

        return new ScrollViewer
        {
            Content = stack,
            Visibility = Visibility.Collapsed,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollMode = ScrollMode.Disabled,
        };
    }

    // ═══════════════════════════════════════
    // ═══ Tables
    // ═══════════════════════════════════════

    private void ShowCategories() => _categoryTable.Show(_categories, "No categories. Everything is filed under Other.");

    private void ShowLogins() => _loginTable.Show(_logins, "No saved logins yet. Add one here, or tick \"Remember this login\" when a download asks for one.");

    private async Task AddCategoryAsync()
    {
        var rule = new CategoryRule { Name = "New category" };
        if (await EditCategoryAsync(rule, isNew: true))
            _categories.Add(rule);
        ShowCategories();
        _categoryTable.Select(_categories.Contains(rule) ? rule : null);
    }

    private async Task<bool> EditCategoryAsync(CategoryRule rule, bool isNew = false)
    {
        var form = new CategoryEditorForm(rule, _owner);
        var window = new UtilityWindow(form, form, isNew ? "New category" : $"Edit {rule.Name}", 400, 230);
        if (await window.ShowAsync() != ContentDialogResult.Primary)
            return false;

        form.ApplyTo(rule);
        if (!isNew) ShowCategories();
        return true;
    }

    private async Task EditLoginAsync(LoginRow row, bool isNew)
    {
        var form = new LoginEditorForm(row);
        var window = new UtilityWindow(form, form, isNew ? "New login" : "Edit login", 400, 170);
        if (await window.ShowAsync() != ContentDialogResult.Primary)
            return;

        form.ApplyTo(row);
        if (isNew) _logins.Add(row);
        ShowLogins();
        _loginTable.Select(row);
    }

    // ═══════════════════════════════════════
    // ═══ Apply
    // ═══════════════════════════════════════

    /// <summary>Writes the dialog back into settings. Call only on Save.</summary>
    public void Apply()
    {
        _settings.DownloadFolder = _folder;
        _settings.SortByType = _sortByType.IsOn;
        _settings.OverwriteExisting = _overwrite.IsOn;
        _settings.TagClashingNames = _tagClashes.IsOn;
        _settings.NameFrom = (NameSource)Math.Max(0, _nameFrom.SelectedIndex);
        _settings.UseServerTimestamp = _serverTimestamp.IsOn;
        _settings.StartAutomatically = _startAuto.IsOn;
        _settings.ShowProgressWindow = _progressWindow.IsOn;
        _settings.ShowCompleteDialog = _completeDialog.IsOn;
        _settings.DoubleClick = (DoubleClickAction)Math.Max(0, _doubleClick.SelectedIndex);
        _settings.KeepInCompleteDays = IntOf(_keepDays, _settings.KeepInCompleteDays);

        // A nameless row would be a category nothing could ever be filed under.
        _settings.Categories = [.. _categories.Where(c => c.Name.Trim().Length > 0)];

        // A row with no host could never be matched against a download.
        SiteCredentials.Replace(_logins
            .Where(l => l.Host.Trim().Length > 0)
            .Select(l => new SiteCredentials.Saved(l.Host.Trim(), l.Username.Trim(), l.Password)));

        _settings.MaxConcurrentDownloads = IntOf(_concurrent, _settings.MaxConcurrentDownloads);
        _settings.DefaultConnections = IntOf(_connections, _settings.DefaultConnections);
        _settings.ConnectionTimeoutSeconds = IntOf(_timeout, _settings.ConnectionTimeoutSeconds);
        _settings.MaxRetriesPerSegment = IntOf(_retries, _settings.MaxRetriesPerSegment);
        _settings.SpeedLimitBytesPerSecond = double.IsNaN(_speedLimit.Value) ? 0 : (long)Math.Round(Math.Max(0, _speedLimit.Value) * 1024);
        _settings.AdaptiveConnections = _gentle.IsOn;
        _settings.UseWindowsCredentials = _windowsAuth.IsOn;
        _settings.AutoRetry = _autoRetry.IsOn;
        _settings.HostConnectionLimits = ParseHostLimits(_hostLimits.Text);

        _settings.ProxyMode = (ProxyMode)Math.Max(0, _proxyMode.SelectedIndex);
        _settings.ProxyProtocol = (ProxyProtocol)Math.Max(0, _proxyProtocol.SelectedIndex);
        _settings.ProxyHost = _proxyHost.Text.Trim();
        _settings.ProxyPort = IntOf(_proxyPort, 0);
        _settings.ProxyUsername = _proxyUser.Text.Trim();
        _settings.ProxyPassword = _proxyPassword.Password;

        // Revocations are applied on Save like everything else, so Cancel really does leave
        // the approved list alone.
        foreach (var origin in Pairing.Approved.Where(o => !_extensions.Contains(o)).ToList())
            Pairing.Forget(origin);

        _settings.BrowserMonitoring = _monitoring.IsOn;
        _settings.ConfirmCaptures = _confirmCaptures.IsOn;
        _settings.NotifyOnMedia = _notifyMedia.IsOn;
        _settings.MonitorClipboard = _clipboard.IsOn;
        _settings.HandoffPort = IntOf(_port, _settings.HandoffPort);

        _settings.CaptureTypes = Split(_captureTypes.Text);
        _settings.VideoTypes = Split(_videoTypes.Text);
        _settings.MediaTypes = Split(_mediaTypes.Text);
        _settings.MinVideoSizeKb = IntOf(_minVideo, 0);
        _settings.SkipHosts = Split(_skipHosts.Text);
        _settings.AlwaysHosts = Split(_alwaysHosts.Text);

        _settings.ShowTrayIcon = _trayIcon.IsOn;
        _settings.MinimizeToTray = _minimizeToTray.IsOn;
        _settings.CloseToTray = _closeToTray.IsOn;
        _settings.StartMinimized = _startMinimized.IsOn;
        // Switching it off here is a deliberate answer, so the launch question stops asking.
        if (_settings.StartWithWindows && !_startWithWindows.IsOn)
            _settings.NeverAskAboutStartup = true;
        _settings.StartWithWindows = _startWithWindows.IsOn;
        StartupRegistration.Apply(_settings.StartWithWindows);
        _settings.ResumeOnStartup = _resumeOnStartup.IsOn;
        _settings.PreventSleep = _preventSleep.IsOn;
        _settings.ShutdownWhenDone = _shutdown.IsOn;
        _settings.RunWhenDone = _runWhenDone.Text.Trim();
        _settings.ScanWithAntivirus = _scan.IsOn;
        _settings.AntivirusPath = _antivirusPath.Text.Trim();
        _settings.AntivirusArguments = _antivirusArgs.Text.Trim();

        _settings.UserAgent = _userAgent.Text.Trim();
        _settings.CheckForUpdates = _checkUpdates.IsOn;

        _settings.Save();
    }

    // ═══════════════════════════════════════
    // ═══ Behaviour
    // ═══════════════════════════════════════

    /// <summary>The manual fields only mean anything for a manual proxy.</summary>
    private void UpdateProxyFields()
    {
        var manual = _proxyMode.SelectedIndex == (int)ProxyMode.Manual;
        _proxyProtocol.IsEnabled = manual;
        _proxyHost.IsEnabled = manual;
        _proxyPort.IsEnabled = manual;
        _proxyUser.IsEnabled = manual;
        _proxyPassword.IsEnabled = manual;
    }

    /// <summary>Hiding the window only makes sense with an icon to get it back from.</summary>
    private void UpdateTrayOptions()
    {
        _minimizeToTray.IsEnabled = _trayIcon.IsOn;
        _closeToTray.IsEnabled = _trayIcon.IsOn;
        _startMinimized.IsEnabled = _trayIcon.IsOn;
    }

    private void UpdateScanFields()
    {
        _antivirusPath.IsEnabled = _scan.IsOn;
        _antivirusArgs.IsEnabled = _scan.IsOn;
    }

    private void RefreshComponents()
    {
        _ffmpegStatus.Text = ToolBox.Describe(Tool.Ffmpeg);
        _ytDlpStatus.Text = ToolBox.Describe(Tool.YtDlp);

        UpdateInstallButtons();

        // winget installs system wide and keeps the tool updated, the better outcome where
        // it is available. Hidden entirely where it is not.
        var winget = ToolBox.HasWinget();
        _ffmpegWinget.Visibility = winget ? Visibility.Visible : Visibility.Collapsed;
        _ytDlpWinget.Visibility = winget ? Visibility.Visible : Visibility.Collapsed;

        _componentSource.Text = winget
            ? "winget installs for the whole machine and keeps it updated. Install keeps a private copy in Stretto's folder instead."
            : $"Downloaded from {ToolBox.SourceUrl(Tool.Ffmpeg)} and {ToolBox.SourceUrl(Tool.YtDlp)}.";
    }

    private async Task WingetAsync(Tool tool, Button button, TextBlock status)
    {
        button.IsEnabled = false;
        status.Text = $"Asking winget for {ToolBox.WingetId(tool)}…";

        try
        {
            var (ok, message) = await ToolBox.InstallWithWingetAsync(tool);
            status.Text = ok ? message : $"winget: {message}";
        }
        catch (Exception exception)
        {
            App.Log($"winget install failed: {exception}");
            status.Text = exception.Message;
        }
        finally
        {
            button.IsEnabled = true;

            // Deliberately not RefreshComponents: a fresh install is often not on this
            // process's PATH yet, and "Not installed" would read as a failure.
            UpdateInstallButtons();
        }
    }

    /// <summary>
    /// Install is only an offer where there is nothing to use yet. A copy found on PATH is
    /// preferred over Stretto's own, so installing a second one beside it would do nothing;
    /// the button says so instead of inviting the click. yt-dlp is worth re-fetching from
    /// Stretto's own copy because the sites it handles keep changing under it.
    /// </summary>
    private void UpdateInstallButtons()
    {
        SetInstallButton(_ffmpegInstall, Tool.Ffmpeg, "Reinstall");
        SetInstallButton(_ytDlpInstall, Tool.YtDlp, "Update");
    }

    private static void SetInstallButton(Button button, Tool tool, string whenLocal)
    {
        var local = ToolBox.Local(tool) is not null;
        var elsewhere = !local && ToolBox.IsAvailable(tool);
        button.Content = local ? whenLocal : elsewhere ? "Installed" : "Install";
        button.IsEnabled = !elsewhere;
    }

    private async Task InstallAsync(Tool tool, Button button, TextBlock status)
    {
        button.IsEnabled = false;

        var progress = new Progress<double>(fraction =>
            status.Text = $"Downloading {ToolBox.DisplayName(tool)}… {fraction * 100:F0}%");

        try
        {
            await ToolBox.InstallAsync(tool, _http, progress);
            status.Text = ToolBox.Describe(tool);
        }
        catch (Exception exception)
        {
            App.Log($"Installing {ToolBox.DisplayName(tool)} failed: {exception}");
            status.Text = $"Could not install: {exception.Message}";
        }
        finally
        {
            button.IsEnabled = true;
            RefreshComponents();
        }
    }

    private async void BrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        var picker = new Windows.Storage.Pickers.FolderPicker();
        picker.FileTypeFilter.Add("*");

        // Unpackaged apps have no implicit window for the picker to parent itself to.
        WinRT.Interop.InitializeWithWindow.Initialize(picker, _owner);

        try
        {
            if (await picker.PickSingleFolderAsync() is { } folder)
            {
                _folder = folder.Path;
                _folderBox.Text = _folder;
            }
        }
        catch (Exception exception)
        {
            App.Log($"Folder picker failed: {exception.Message}");
        }
    }

    private async Task<string?> PickProgramAsync()
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        picker.FileTypeFilter.Add(".exe");
        picker.FileTypeFilter.Add(".bat");
        picker.FileTypeFilter.Add(".cmd");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, _owner);

        try
        {
            if (await picker.PickSingleFileAsync() is { } file)
                return file.Path;
        }
        catch (Exception exception)
        {
            App.Log($"File picker failed: {exception.Message}");
        }

        return null;
    }

    private static void Launch(string target)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            App.Log($"Could not open {target}: {exception.Message}");
        }
    }

    // ═══════════════════════════════════════
    // ═══ Small pieces
    // ═══════════════════════════════════════

    private static NumberBox Number(double min, double max) => new()
    {
        Minimum = min,
        Maximum = max,
        Width = 120,
        SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
    };

    private static TextBox Multiline(double height) => new()
    {
        AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap,
        Height = height,
    };

    private static TextBlock Status() => new()
    {
        FontSize = 11.5,
        Foreground = Res("StrettoAccentBrush"),
        TextTrimming = TextTrimming.CharacterEllipsis,
    };

    private static StackPanel WithResetBelow(TextBox box, Action reset)
    {
        var link = new HyperlinkButton { Content = "Restore defaults", Padding = new Thickness(0), FontSize = 12 };
        link.Click += (_, _) => reset();
        return new StackPanel { Spacing = 6, Children = { box, link } };
    }

    private static int IntOf(NumberBox box, int fallback) =>
        double.IsNaN(box.Value) ? fallback : (int)box.Value;

    /// <summary>
    /// Reads the "host = count" lines. A line not in that shape is dropped rather than
    /// guessed at: a mistyped limit that silently became something else would be worse than
    /// one that plainly did not take.
    /// </summary>
    private static Dictionary<string, int> ParseHostLimits(string text)
    {
        var limits = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var line in text.ReplaceLineEndings()
                                 .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split('=', 2);
            if (parts.Length != 2)
                continue;

            var host = parts[0].Trim();
            if (host.Length == 0 || !int.TryParse(parts[1].Trim(), out var count))
                continue;

            limits[host] = Math.Clamp(count, 1, Stretto.Core.SegmentedDownloader.MaxConnections);
        }

        return limits;
    }

    private static string Join(IEnumerable<string> values) => string.Join(", ", values);

    /// <summary>Accepts commas, spaces or newlines, and tolerates a leading dot on a type.</summary>
    private static List<string> Split(string text) =>
    [
        .. text.Split([',', ' ', '\r', '\n', ';'], StringSplitOptions.RemoveEmptyEntries)
               .Select(part => part.Trim().TrimStart('.').ToLowerInvariant())
               .Where(part => part.Length > 0)
               .Distinct()
    ];
}
