using Stretto.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Stretto.App.Services;
using static Stretto.App.Dialogs.FormKit;

namespace Stretto.App.Dialogs;

/// <summary>The contents of each settings page, one builder per section.</summary>
public sealed partial class SettingsDialog
{
    // ═══════════════════════════════════════
    // ═══ Pages
    // ═══════════════════════════════════════

    private IEnumerable<UIElement> BuildGeneral()
    {
        _folderBox.Text = _folder;
        _doubleClick.Items.Add("Open the file");
        _doubleClick.Items.Add("Open its folder");
        _doubleClick.Items.Add("Do nothing");
        _doubleClick.SelectedIndex = (int)_settings.DoubleClick;
        _keepDays.Value = _settings.KeepInCompleteDays;
        _nameFrom.Items.Add("Page title if the file has no real name");
        _nameFrom.Items.Add("Always the file's own name");
        _nameFrom.Items.Add("Always the page title");
        _nameFrom.SelectedIndex = (int)_settings.NameFrom;

        yield return GroupTitle("Saving");
        yield return Card(
            StackRow("Download folder", null, WithButton(_folderBox, PushButton("Browse", BrowseFolder_Click))),
            Row("Sort into subfolders by file type", "Uses the folders set for each category below.", _sortByType),
            Row("Overwrite a file that already exists", "When off, a clashing name is saved as \"name (2).ext\".", _overwrite),
            Row("Tell clashing names apart by source", "A clashing name gets a short tag from where it came from, like \"name [a8f3k2].ext\", before any number.", _tagClashes),
            Row("Name videos from the browser after", "A video's own address often says nothing, like master.m3u8.", _nameFrom),
            Row("Use the server's date on saved files", null, _serverTimestamp));

        yield return GroupTitle("Categories");
        yield return Hint("Which file types belong to which category, and where each is filed. Library filters by these too.");
        yield return new Border { Height = 8 };

        _categoryTable.Activated += rule => _ = EditCategoryAsync(rule);
        _categoryTable.SelectionChanged += () =>
        {
            if (_categoryEdit is null || _categoryDelete is null) return;
            _categoryEdit.IsEnabled = _categoryTable.Selected is not null;
            _categoryDelete.IsEnabled = _categoryTable.Selected is not null;
        };
        yield return _categoryTable.View;

        _categoryEdit = ToolButton("", "Edit", (_, _) => { if (_categoryTable.Selected is { } rule) _ = EditCategoryAsync(rule); });
        _categoryDelete = ToolButton("", "Delete", (_, _) =>
        {
            if (_categoryTable.Selected is { } rule) _categories.Remove(rule);
            ShowCategories();
        });
        yield return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
            Margin = new Thickness(0, 4, 0, 0),
            Children =
            {
                ToolButton("", "Add", (_, _) => _ = AddCategoryAsync()),
                _categoryEdit,
                _categoryDelete,
                ToolButton("", "Restore defaults", (_, _) =>
                {
                    _categories.Clear();
                    foreach (var rule in CategoryRules.Defaults()) _categories.Add(rule);
                    ShowCategories();
                }),
            },
        };
        ShowCategories();

        yield return GroupTitle("When a download is added");
        yield return Card(
            Row("Start it straight away", "Otherwise it waits in the list until started.", _startAuto),
            Row("Open a progress window for it", "The list shows speed and time left either way.", _progressWindow),
            Row("Say when it finishes", "A small window with Open, Show in folder and Close.", _completeDialog),
            Row("Double-clicking a download", null, _doubleClick),
            Row("Show finished downloads under All in Library for", "Days. Older ones stay under their file type, and nothing is deleted. 0 keeps them all.", _keepDays));
    }

    private IEnumerable<UIElement> BuildConnections()
    {
        _concurrent.Value = _settings.MaxConcurrentDownloads;
        _connections.Value = _settings.DefaultConnections;
        _timeout.Value = _settings.ConnectionTimeoutSeconds;
        _retries.Value = _settings.MaxRetriesPerSegment;
        _speedLimit.Value = _settings.SpeedLimitBytesPerSecond / 1024d;
        _hostLimits.Text = string.Join(Environment.NewLine,
            _settings.HostConnectionLimits.OrderBy(pair => pair.Key).Select(pair => $"{pair.Key} = {pair.Value}"));

        yield return GroupTitle("Speed");
        yield return Card(
            Row("Downloads at the same time", "The rest wait their turn.", _concurrent),
            Row("Connections per download", "The most one download may use. Servers that limit each connection need more; others need fewer.", _connections),
            Row("Add connections automatically", "Starts with 4 and adds more while the speed keeps improving, up to the number above. Stops if a site turns one away. Off opens them all at once.", _gentle),
            Row("Speed limit", "KB/s across all downloads. 0 means no limit.", _speedLimit));

        yield return GroupTitle("Reliability");
        yield return Card(
            Row("Connection timeout", "Seconds to wait for a server to answer.", _timeout),
            Row("Retries per connection", "A dropped range resumes from where it stopped.", _retries),
            Row("Retry failed downloads automatically", "Up to three times: after 30 seconds, 2 minutes and 10 minutes. Not for expired links or sign-in pages.", _autoRetry),
            StackRow("Per-site connection limits", "One per line. Some sites hand out single-use links and refuse extra range requests.", _hostLimits));

        yield return GroupTitle("Sign-in");
        yield return Card(
            Row("Sign in with my Windows account when asked",
                "For intranet servers and proxies using NTLM or Kerberos. Off by default: it offers your account to any host that asks.",
                _windowsAuth));
    }

    private IEnumerable<UIElement> BuildProxy()
    {
        _proxyMode.Items.Add("Use the Windows proxy settings");
        _proxyMode.Items.Add("No proxy");
        _proxyMode.Items.Add("Use this proxy");
        _proxyMode.SelectedIndex = (int)_settings.ProxyMode;
        _proxyProtocol.Items.Add("HTTP proxy");
        _proxyProtocol.Items.Add("SOCKS5 proxy");
        _proxyProtocol.SelectedIndex = (int)_settings.ProxyProtocol;
        _proxyHost.Text = _settings.ProxyHost;
        _proxyPort.Value = _settings.ProxyPort;
        _proxyUser.Text = _settings.ProxyUsername;
        _proxyPassword.Password = _settings.ProxyPassword;
        _proxyMode.SelectionChanged += (_, _) => UpdateProxyFields();
        UpdateProxyFields();

        var endpoint = new Grid { ColumnSpacing = 10 };
        endpoint.ColumnDefinitions.Add(new ColumnDefinition());
        endpoint.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        endpoint.Children.Add(_proxyHost);
        Grid.SetColumn(_proxyPort, 1);
        endpoint.Children.Add(_proxyPort);

        var login = new Grid { ColumnSpacing = 10 };
        login.ColumnDefinitions.Add(new ColumnDefinition());
        login.ColumnDefinitions.Add(new ColumnDefinition());
        login.Children.Add(_proxyUser);
        Grid.SetColumn(_proxyPassword, 1);
        login.Children.Add(_proxyPassword);

        var manual = new StackPanel { Spacing = 10, Padding = new Thickness(14, 10, 14, 14) };
        manual.Children.Add(endpoint);
        manual.Children.Add(login);

        var windows = new HyperlinkButton { Content = "Open the Windows proxy settings", Padding = new Thickness(0), FontSize = 12.5 };
        windows.Click += (_, _) => Launch("ms-settings:network-proxy");

        yield return GroupTitle("Connection");
        yield return Card(
            Row("How Stretto reaches the internet", "One download can override this in its Advanced options.", _proxyMode),
            Row("Manual proxy type", "HTTP uses the standard web proxy protocol. SOCKS5 supports optional user name and password authentication.", _proxyProtocol),
            manual);
        yield return new Border { Height = 10 };
        yield return windows;
    }

    private IEnumerable<UIElement> BuildBrowser()
    {
        _port.Value = _settings.HandoffPort;
        _extensionList.ItemsSource = _extensions;
        _extensionList.ItemTemplate = (DataTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load("""
            <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
              <TextBlock Text="{Binding}" FontSize="12" FontFamily="Consolas" TextTrimming="CharacterEllipsis" Padding="0,4" />
            </DataTemplate>
            """);

        var setup = PushButton("Set up an extension…", (_, _) =>
        {
            RequestedSetup = true;
            Apply();
            CloseRequested?.Invoke(this, new WindowFormCloseEventArgs(ContentDialogResult.None));
        }, accent: true);

        var extensions = new StackPanel { Spacing = 8, Padding = new Thickness(14, 10, 14, 12) };
        extensions.Children.Add(new TextBlock { Text = "Extensions allowed to send downloads", FontSize = 13 });
        extensions.Children.Add(_extensions.Count == 0
            ? Hint("None yet. An extension is added here the first time it pairs.")
            : _extensionList);
        if (_extensions.Count > 0)
            extensions.Children.Add(Horizontal(_revoke, Hint("A revoked extension has to be allowed again next time it asks.")));

        yield return GroupTitle("Monitoring");
        yield return Card(
            Row("Take over downloads from the browser",
                _browserStatus.Length > 0 ? _browserStatus : "Downloads started in a paired browser are handed to Stretto.",
                _monitoring),
            Row("Browser extension", "Chrome, Edge, Brave, Opera, Vivaldi, or any other Chromium browser. It pairs itself.", setup),
            extensions);

        yield return GroupTitle("When a download is captured");
        yield return Card(
            Row("Ask first", "Shows the New Download window filled in from the browser. The browser holds its own copy until you choose.", _confirmCaptures),
            Row("Tell me when a page plays video", null, _notifyMedia),
            Row("Watch the clipboard for download links", null, _clipboard));

        yield return GroupTitle("Connection");
        yield return Card(Row("Local port", "Stretto only listens on this computer (127.0.0.1). The extension finds it within 9615 to 9619.", _port));
    }

    private IEnumerable<UIElement> BuildCaptureRules()
    {
        _captureTypes.Text = Join(_settings.CaptureTypes);
        _videoTypes.Text = Join(_settings.VideoTypes);
        _mediaTypes.Text = Join(_settings.MediaTypes);
        _skipHosts.Text = Join(_settings.SkipHosts);
        _alwaysHosts.Text = Join(_settings.AlwaysHosts);
        _minVideo.Value = _settings.MinVideoSizeKb;

        yield return Hint("The extension fetches these from Stretto, so there is one set of rules for every browser.");
        yield return GroupTitle("What gets taken over");
        yield return Card(
            StackRow("File types", "Downloads of these types go to Stretto. An address with no type at all is taken too; the real name comes from the server.",
                WithResetBelow(_captureTypes, () => _captureTypes.Text = Join(AppSettings.DefaultCaptureTypes))),
            StackRow("Media offered while playing", null,
                WithResetBelow(_videoTypes, () => _videoTypes.Text = Join(AppSettings.DefaultVideoTypes))),
            StackRow("Also treat as media", "Content-Type fragments, for media served from an address with no file extension. Part of a Content-Type counts, so video/ and mpegurl both work.",
                WithResetBelow(_mediaTypes, () => _mediaTypes.Text = Join(AppSettings.DefaultMediaTypes))),
            Row("Ignore media smaller than", "KB. Only when the browser knows the size in advance.", _minVideo));

        yield return GroupTitle("Exceptions");
        yield return Card(
            StackRow("Never capture from these sites", "Their downloads stay with the browser.",
                WithResetBelow(_skipHosts, () => _skipHosts.Text = Join(AppSettings.DefaultSkipHosts))),
            StackRow("Always capture from these sites", "Downloads from these hosts are taken whatever their type, instead of being filtered through the list above. Matched on the site's own domain, so one entry covers its subdomains.",
                WithResetBelow(_alwaysHosts, () => _alwaysHosts.Text = string.Empty)));
    }

    private IEnumerable<UIElement> BuildPasswords()
    {
        yield return Hint("Logins for sites that ask for one. Basic and Digest challenges are supported; credentials are encrypted for your Windows account and only sent to the site they belong to.");
        yield return new Border { Height = 12 };

        _loginTable.Activated += row => _ = EditLoginAsync(row, isNew: false);
        _loginTable.SelectionChanged += () =>
        {
            if (_loginEdit is null || _loginDelete is null) return;
            _loginEdit.IsEnabled = _loginTable.Selected is not null;
            _loginDelete.IsEnabled = _loginTable.Selected is not null;
        };
        yield return _loginTable.View;

        _loginEdit = ToolButton("", "Edit", (_, _) => { if (_loginTable.Selected is { } row) _ = EditLoginAsync(row, isNew: false); });
        _loginDelete = ToolButton("", "Delete", (_, _) =>
        {
            if (_loginTable.Selected is { } row) _logins.Remove(row);
            ShowLogins();
        });
        yield return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
            Margin = new Thickness(0, 4, 0, 0),
            Children = { ToolButton("", "Add", (_, _) => _ = EditLoginAsync(new LoginRow(), isNew: true)), _loginEdit, _loginDelete },
        };
        ShowLogins();
    }

    private IEnumerable<UIElement> BuildSystem()
    {
        _runWhenDone.Text = _settings.RunWhenDone;
        _antivirusPath.Text = _settings.AntivirusPath;
        _antivirusArgs.Text = _settings.AntivirusArguments;
        _trayIcon.Toggled += (_, _) => UpdateTrayOptions();
        _scan.Toggled += (_, _) => UpdateScanFields();
        UpdateTrayOptions();
        UpdateScanFields();

        yield return GroupTitle("Notification area");
        yield return Card(
            Row("Show an icon in the notification area", null, _trayIcon),
            Row("Minimising hides the window", null, _minimizeToTray),
            Row("Closing hides Stretto instead of quitting", null, _closeToTray),
            Row("Start hidden", null, _startMinimized));

        yield return GroupTitle("Startup and power");
        yield return Card(
            Row("Start Stretto when I sign in", null, _startWithWindows),
            Row("Resume paused downloads when Stretto starts", null, _resumeOnStartup),
            Row("Keep the computer awake while downloading", null, _preventSleep));

        yield return GroupTitle("When everything finishes");
        yield return Card(
            Row("Shut down the computer", "Windows shows its own countdown first, so it can still be cancelled.", _shutdown),
            StackRow("Run a program", null, WithButton(_runWhenDone, PushButton("Browse", async (_, _) =>
            {
                if (await PickProgramAsync() is { } path) _runWhenDone.Text = path;
            }))));

        yield return GroupTitle("Virus scan");
        yield return Card(
            Row("Scan each finished file", null, _scan),
            StackRow("Scanner", "The file path is added at the end, quoted. Write {file} to put it somewhere else.",
                new StackPanel
                {
                    Spacing = 8,
                    Children =
                    {
                        WithButton(_antivirusPath, PushButton("Browse", async (_, _) =>
                        {
                            if (await PickProgramAsync() is { } path) _antivirusPath.Text = path;
                        })),
                        _antivirusArgs,
                    },
                }));
    }

    private IEnumerable<UIElement> BuildAdvanced()
    {
        _userAgent.Text = _settings.UserAgent;
        _ffmpegInstall.Click += async (_, _) => await InstallAsync(Tool.Ffmpeg, _ffmpegInstall, _ffmpegStatus);
        _ytDlpInstall.Click += async (_, _) => await InstallAsync(Tool.YtDlp, _ytDlpInstall, _ytDlpStatus);
        _ffmpegWinget.Click += async (_, _) => await WingetAsync(Tool.Ffmpeg, _ffmpegWinget, _ffmpegStatus);
        _ytDlpWinget.Click += async (_, _) => await WingetAsync(Tool.YtDlp, _ytDlpWinget, _ytDlpStatus);
        ToolTipService.SetToolTip(_ffmpegWinget, "Install or update ffmpeg with winget, for the whole machine");
        ToolTipService.SetToolTip(_ytDlpWinget, "Install or update yt-dlp with winget, for the whole machine");
        RefreshComponents();

        yield return GroupTitle("Optional components");
        yield return Hint("Plain downloads need nothing extra. These are for streams, converting and video sites. A copy already on your PATH is preferred.");
        yield return new Border { Height = 8 };
        yield return Card(
            ComponentRow("ffmpeg", "Joins HLS and DASH streams, converts audio and video.", _ffmpegStatus, _ffmpegWinget, _ffmpegInstall),
            ComponentRow("yt-dlp", "Reads video sites and playlists.", _ytDlpStatus, _ytDlpWinget, _ytDlpInstall),
            new Border { Padding = new Thickness(14, 8, 14, 10), Child = _componentSource });

        var reset = PushButton("Default", (_, _) => _userAgent.Text = AppSettings.DefaultUserAgent);
        yield return GroupTitle("Requests");
        yield return Card(StackRow("User agent", "Sent when a download did not come from the browser, which supplies its own.", WithButton(_userAgent, reset)));

        // One handler for both jobs: once a newer release is found the button opens it.
        _checkNow.Click += async (_, _) =>
        {
            if (_updatePage is { } page) OpenPage(page);
            else await CheckUpdatesNowAsync();
        };
        _updateStatus.Text = Updates.IsConfigured
            ? $"You have {Updates.CurrentText}."
            : $"You have {Updates.CurrentText}. No release channel set up yet.";
        _checkNow.IsEnabled = Updates.IsConfigured;
        _checkUpdates.IsEnabled = Updates.IsConfigured;

        var version = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        version.Children.Add(new TextBlock { Text = "Version", FontSize = 13 });
        version.Children.Add(_updateStatus);
        var versionRow = Row("Version", null, _checkNow);
        versionRow.Children.RemoveAt(0);
        versionRow.Children.Insert(0, version);

        yield return GroupTitle("Updates");
        yield return Card(
            Row("Check for updates automatically", "Once a day. Stretto only tells you; it never installs anything by itself.", _checkUpdates),
            versionRow);

        _diagnosticsStatus.Text = "Versions, the settings that affect downloads, and the end of the log. No passwords, logins, proxy addresses or file names.";
        yield return GroupTitle("Troubleshooting");
        yield return Card(
            Row("Copy diagnostics", null, Horizontal(
                PushButton("Open log", (_, _) => OpenLog()),
                PushButton("Copy", (_, _) => CopyDiagnostics()))),
            new Border { Padding = new Thickness(14, 0, 14, 10), Child = _diagnosticsStatus });
    }

    private async Task CheckUpdatesNowAsync()
    {
        _checkNow.IsEnabled = false;
        _updateStatus.Text = "Checking…";

        try
        {
            var found = await Updates.CheckAsync(_http);
            _settings.LastUpdateCheck = DateTime.UtcNow;

            if (found.IsNewer)
            {
                _updateStatus.Text = $"Stretto {found.Tag} is available. You have {Updates.CurrentText}.";
                if (found.PageUrl.Length > 0)
                {
                    _updatePage = found.PageUrl;
                    _checkNow.Content = "Get it";
                }
            }
            else
            {
                _updateStatus.Text = $"You have the latest version, {Updates.CurrentText}.";
            }
        }
        catch (Exception exception)
        {
            App.Log($"Update check failed: {exception}");
            _updateStatus.Text = $"Could not check: {exception.Message}";
        }
        finally
        {
            _checkNow.IsEnabled = true;
        }
    }

    private static void OpenPage(string url)
    {
        if (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
    }

    private void CopyDiagnostics()
    {
        try
        {
            var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
            package.SetText(Diagnostics.Build(_settings));
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
            _diagnosticsStatus.Text = "Copied. Paste it into your bug report.";
        }
        catch (Exception exception)
        {
            _diagnosticsStatus.Text = $"Could not copy: {exception.Message}";
        }
    }

    private void OpenLog()
    {
        try
        {
            // Selects the file in Explorer when it exists, otherwise just opens the folder.
            var arguments = File.Exists(Diagnostics.LogPath)
                ? $"/select,\"{Diagnostics.LogPath}\""
                : $"\"{Path.GetDirectoryName(Diagnostics.LogPath)}\"";
            System.Diagnostics.Process.Start("explorer.exe", arguments);
        }
        catch (Exception exception)
        {
            _diagnosticsStatus.Text = $"Could not open the log: {exception.Message}";
        }
    }

    private static Grid ComponentRow(string name, string purpose, TextBlock status, Button winget, Button install)
    {
        var text = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = name, FontSize = 13 });
        text.Children.Add(Hint(purpose));
        text.Children.Add(status);

        // A normal row, with the three-line label swapped in for the one-line one.
        var row = Row(name, null, Horizontal(winget, install));
        row.Children.RemoveAt(0);
        row.Children.Insert(0, text);
        return row;
    }
}
