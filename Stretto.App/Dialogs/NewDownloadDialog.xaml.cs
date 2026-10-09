using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Stretto.App.Services;

namespace Stretto.App.Dialogs;

public sealed partial class NewDownloadDialog : UserControl
{
    private readonly nint _ownerHandle;
    private readonly AppSettings _settings;
    private readonly string _downloadFolder;
    private string? _chosenFolder;
    private DownloadAdvancedWindow? _advancedWindow;
    private string _username = string.Empty;
    private string _password = string.Empty;
    private bool _rememberAuth;
    private ProxyMode? _proxyOverride;
    private ProxyProtocol? _proxyProtocolOverride;
    private string _proxyHost = string.Empty;
    private int _proxyPort;
    private string _proxyUsername = string.Empty;
    private string _proxyPassword = string.Empty;

    // ComboBox raises SelectionChanged while InitializeComponent is still running,
    // before the other named fields exist. Handlers must not touch them until then.
    private readonly bool _ready;

    /// <summary>
    /// The last place a download was sent this session, when it was not the automatic
    /// choice. Static and never saved: it carries across New Download windows while someone
    /// works through a batch of files, and is forgotten when they pick "Choose by file type"
    /// again or quit the app. Hiding to the tray keeps it.
    /// </summary>
    private static int _sessionChoice;
    private static string? _sessionFolder;

    public NewDownloadDialog(nint ownerHandle, AppSettings settings, string downloadFolder)
    {
        InitializeComponent();
        _ownerHandle = ownerHandle;
        _settings = settings;
        _downloadFolder = downloadFolder;

        // Set before _ready, so restoring "Choose a folder" does not open the picker.
        if (_sessionChoice == 1 || (_sessionChoice == 2 && Directory.Exists(_sessionFolder)))
        {
            _chosenFolder = _sessionFolder;
            SaveInBox.SelectedIndex = _sessionChoice;
        }

        _ready = true;
        UpdateHint();
        UpdateButtons();

        DownloadNowButton.IsEnabled = false;
        DownloadLaterButton.IsEnabled = false;
    }

    /// <summary>What the user chose in the compact download form.</summary>
    public enum Outcome { Cancelled, DownloadNow, DownloadLater, StopCapturingHost }

    /// <summary>Where "Add to queue" puts it.</summary>
    public enum LaterChoice
    {
        /// <summary>Back of the default queue, so large files do not all run at once.</summary>
        DefaultQueue,

        /// <summary>Parked in the list until started by hand.</summary>
        NoQueue,

        /// <summary>Queued, and the Queues page opens to place it.</summary>
        OpenQueues,
    }

    /// <summary>Set once the Add to queue menu has been answered.</summary>
    public LaterChoice? Later { get; private set; }
    public Outcome Result { get; private set; } = Outcome.Cancelled;
    public event Action? CloseRequested;

    /// <summary>Set when the user asked to stop capturing from this address.</summary>
    public string? HostToIgnore { get; private set; }

    public string Address => AddressBox.Text.Trim();

    /// <summary>The file name typed or captured, or null when left to the server.</summary>
    public string? FileNameOrNull => string.IsNullOrWhiteSpace(FileNameBox.Text) ? null : FileNameBox.Text.Trim();

    public bool IsAddressValid =>
        Uri.TryCreate(Address, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "ftp";

    public DownloadOptions BuildOptions(bool startNow)
    {
        // Remembered only once a download actually goes there, so browsing to a folder and
        // then cancelling does not redirect the next download.
        _sessionChoice = SaveInBox.SelectedIndex == 2 && _chosenFolder is null ? 0 : SaveInBox.SelectedIndex;
        _sessionFolder = _sessionChoice == 2 ? _chosenFolder : null;

        return BuildOptionsCore(startNow);
    }

    private DownloadOptions BuildOptionsCore(bool startNow) => new()
    {
        // Index 0 is auto sort; index 2 is an explicit folder the user picked.
        SortByType = SaveInBox.SelectedIndex == 0,
        Directory = SaveInBox.SelectedIndex == 2 ? _chosenFolder : null,
        FileName = string.IsNullOrWhiteSpace(FileNameBox.Text) ? null : FileNameBox.Text.Trim(),
        // Segment count is a network-wide tuning knob rather than a per-file choice, so it
        // comes from Settings instead of being asked again on every download.
        Connections = _settings.DefaultConnections,
        Username = string.IsNullOrWhiteSpace(_username) ? null : _username.Trim(),
        Password = _password.Length == 0 ? null : _password,
        ProxyModeOverride = _proxyOverride,
        ProxyProtocolOverride = _proxyProtocolOverride,
        ProxyHost = string.IsNullOrWhiteSpace(_proxyHost) ? null : _proxyHost.Trim(),
        ProxyPort = _proxyPort,
        ProxyUsername = string.IsNullOrWhiteSpace(_proxyUsername) ? null : _proxyUsername.Trim(),
        ProxyPassword = _proxyPassword.Length == 0 ? null : _proxyPassword,
        StartNow = startNow,
    };

    /// <summary>
    /// Saves what outlives one download: the login, when asked to remember it. The proxy
    /// override stays on the pending download itself; the global proxy lives in Settings.
    /// </summary>
    public void CommitAdvanced()
    {
        if (_rememberAuth && _username.Trim().Length > 0)
            SiteCredentials.Remember(Address, _username.Trim(), _password);
    }

    /// <summary>
    /// Fills the dialog from a download the browser just handed over, and offers the escape
    /// hatch for sites whose download button does not survive being re-requested.
    /// </summary>
    public void PrefillFromCapture(string url, string? fileName, string? referrer = null)
    {
        AddressBox.Text = url;
        if (!string.IsNullOrWhiteSpace(fileName))
            FileNameBox.Text = fileName;

        // The site the user was on, not the server the file came from. A site hands out
        // downloads from any number of CDN hosts (civitai-delivery-worker-prod.….r2.
        // cloudflarestorage.com), so skipping one of those skips almost nothing.
        // Without a page, only the exact server: widening a CDN host to its domain would
        // block cloudflarestorage.com, which thousands of unrelated sites share.
        HostToIgnore = Uri.TryCreate(referrer, UriKind.Absolute, out var from) && from.Scheme is "http" or "https"
            ? SiteOf(from.Host)
            : Uri.TryCreate(url, UriKind.Absolute, out var direct) ? direct.Host.ToLowerInvariant() : null;

        if (HostToIgnore is not null)
        {
            IgnoreHostLink.Content = $"Do not capture downloads from {HostToIgnore}";
            IgnoreHostLink.Visibility = Visibility.Visible;
        }

        UpdateButtons();
    }

    /// <summary>
    /// The registrable part of a host: www.civitai.com and cdn.civitai.com are both
    /// civitai.com. Without a public suffix list this uses the common rule of thumb: the
    /// last two labels, or three when the second-to-last is a short second-level domain
    /// under a country code (bbc.co.uk, example.com.au).
    /// </summary>
    public static string SiteOf(string host)
    {
        var labels = host.Trim('.').ToLowerInvariant().Split('.');
        if (labels.Length <= 2 || System.Net.IPAddress.TryParse(host, out _))
            return host.ToLowerInvariant();

        var countryCode = labels[^1].Length == 2;
        var secondLevel = labels[^2] is "co" or "com" or "org" or "net" or "gov" or "ac" or "edu" or "ne" or "or";
        var take = countryCode && secondLevel ? 3 : 2;
        return string.Join('.', labels[^take..]);
    }

    private void IgnoreHost_Click(object sender, RoutedEventArgs e)
    {
        // Closing as "nothing chosen" lets the caller tell this apart from Cancel by
        // checking HostToIgnore, and hands the download back to the browser either way.
        Result = Outcome.StopCapturingHost;
        CloseRequested?.Invoke();
    }

    private void DownloadNow_Click(object sender, RoutedEventArgs e)
    {
        if (!IsAddressValid) return;
        Result = Outcome.DownloadNow;
        CloseRequested?.Invoke();
    }

    private void DownloadLater_Click(object sender, RoutedEventArgs e)
    {
        ShowLaterMenu();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Result = Outcome.Cancelled;
        CloseRequested?.Invoke();
    }

    /// <summary>
    /// Add to queue offers a choice of destination, so keep those choices in a small
    /// menu anchored to the button rather than adding another row to the form.
    /// </summary>
    private void ShowLaterMenu()
    {
        var menu = new MenuFlyout { Placement = FlyoutPlacementMode.Top };

        menu.Items.Add(Choice("Main queue", LaterChoice.DefaultQueue));
        menu.Items.Add(Choice("Add paused, in no queue", LaterChoice.NoQueue));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(Choice("Choose a queue…", LaterChoice.OpenQueues));

        // Anchoring to the button keeps the menu where the pointer already is. Falling
        // back to the dialog itself is only for a restyled template without that part.
        menu.ShowAt(DownloadLaterButton);

        MenuFlyoutItem Choice(string text, LaterChoice choice)
        {
            var entry = new MenuFlyoutItem { Text = text };

            entry.Click += (_, _) =>
            {
                Later = choice;
                Result = Outcome.DownloadLater;
                CloseRequested?.Invoke();
            };

            return entry;
        }
    }

    /// <summary>Prefills from the clipboard when it holds a link, which is how a download
    /// usually starts.</summary>
    public async Task PrefillFromClipboardAsync()
    {
        try
        {
            var clipboard = Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();
            if (!clipboard.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text))
                return;

            var text = (await clipboard.GetTextAsync())?.Trim();
            if (Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
                AddressBox.Text = text!;
        }
        catch (Exception exception)
        {
            App.Log($"Clipboard read failed: {exception.Message}");
        }

        UpdateButtons();
        FillRememberedLogin();
    }

    private void Address_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateButtons();
        FillRememberedLogin();
    }

    private async void More_Click(object sender, RoutedEventArgs e)
    {
        if (_advancedWindow is { } existing)
        {
            existing.Activate();
            if (await existing.ClosedTask)
                ReadAdvanced(existing);
            _advancedWindow = null;
            return;
        }

        var window = new DownloadAdvancedWindow(_settings, _username, _password, _rememberAuth,
            _proxyOverride, _proxyProtocolOverride, _proxyHost, _proxyPort, _proxyUsername, _proxyPassword);
        _advancedWindow = window;
        window.Activate();
        if (await window.ClosedTask)
            ReadAdvanced(window);
        _advancedWindow = null;
    }

    /// <summary>Shows a login already saved for this host, so the user is not asked twice.</summary>
    private void FillRememberedLogin()
    {
        if (!_ready || _username.Length > 0) return;
        if (SiteCredentials.For(Address) is not { } login) return;

        _username = login.Username;
        _password = login.Password;
        _rememberAuth = true;
        UpdateAdvancedSummary();
    }

    private void UpdateButtons()
    {
        var valid = IsAddressValid;
        DownloadNowButton.IsEnabled = valid;
        DownloadLaterButton.IsEnabled = valid;
    }

    /// <summary>
    /// Takes the button row out of the form so the window can seat it in its footer bar,
    /// the same strip every other Stretto window ends with.
    /// </summary>
    public Grid DetachButtons()
    {
        // By name rather than through Parent, which is not reliable on markup built
        // content before it has been loaded into a tree.
        FormRoot.Children.Remove(ButtonRow);
        return ButtonRow;
    }

    private void UpdateAdvancedSummary()
    {
        var parts = new List<string>();
        if (_username.Trim().Length > 0) parts.Add($"Login: {_username.Trim()}");
        if (_proxyOverride is ProxyMode.None) parts.Add("No proxy");
        else if (_proxyOverride is ProxyMode.Manual)
            parts.Add($"{_proxyProtocolOverride ?? _settings.ProxyProtocol} proxy: {_proxyHost}:{_proxyPort}");
        AdvancedSummary.Text = string.Join("  ·  ", parts);
    }

    private void ReadAdvanced(DownloadAdvancedWindow window)
    {
        _username = window.Username;
        _password = window.Password;
        _rememberAuth = window.RememberLogin;
        _proxyOverride = window.ProxyModeOverride;
        _proxyProtocolOverride = window.ProxyProtocolOverride;
        _proxyHost = window.ProxyHost;
        _proxyPort = window.ProxyPort;
        _proxyUsername = window.ProxyUsername;
        _proxyPassword = window.ProxyPassword;
        UpdateAdvancedSummary();
    }

    private async void SaveIn_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;

        // Picking "Choose a folder" with nothing chosen yet should open the picker rather
        // than silently leaving the selection in an unusable state.
        if (SaveInBox.SelectedIndex == 2 && _chosenFolder is null)
            await BrowseAsync();

        UpdateHint();
    }

    private async void Browse_Click(object sender, RoutedEventArgs e)
    {
        await BrowseAsync();
        if (_chosenFolder is not null)
            SaveInBox.SelectedIndex = 2;

        UpdateHint();
    }

    private async Task BrowseAsync()
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FolderPicker();
            picker.FileTypeFilter.Add("*");

            // Unpackaged apps have no implicit window for the picker to parent itself to.
            WinRT.Interop.InitializeWithWindow.Initialize(picker, _ownerHandle);

            var folder = await picker.PickSingleFolderAsync();
            if (folder is not null)
                _chosenFolder = folder.Path;
        }
        catch (Exception exception)
        {
            App.Log($"Folder picker failed: {exception.Message}");
        }
    }

    private void UpdateHint()
    {
        SaveInHint.Text = SaveInBox.SelectedIndex switch
        {
            0 => $"Sorted into subfolders of {_downloadFolder}",
            1 => _downloadFolder,
            _ => _chosenFolder ?? "No folder chosen yet",
        };

        // Said out loud, so a remembered folder is never a surprise. Short, so it fits the
        // one line; the tooltip says it in full.
        if (SaveInBox.SelectedIndex != 0 && !(SaveInBox.SelectedIndex == 2 && _chosenFolder is null))
            SaveInHint.Text += "  ·  kept for this session";

        ToolTipService.SetToolTip(SaveInHint, SaveInBox.SelectedIndex == 0
            ? SaveInHint.Text
            : SaveInHint.Text + ". Used for later downloads too, until you pick \"Choose by file type\" or close Stretto.");
    }
}
