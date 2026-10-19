using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Stretto.App.Dialogs;
using Stretto.App.Services;
using Stretto.Core;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using WinRT.Interop;

namespace Stretto.App;

public sealed partial class MainWindow : Window
{
    // Settings are declared first on purpose: field initialisers run in declaration order
    // and the download service reads them as it is constructed.
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly DownloadService _downloads;
    private readonly MediaFeed _feed = new();
    private readonly List<DownloadItem> _visible = [];
    private readonly ObservableCollection<DownloadGroup> _groups = [];
    private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly SystemPolicy _policy;
    private readonly TaskbarProgress _taskbar = new();
    private nint _windowHandle;

    private HandoffListener? _handoff;
    private int _ticksSinceSave;
    private bool _uiReady;

    private BrowserSetupWindow? _setup;

    private async Task ShowBrowserSetupAsync()
    {
        // One guide at a time; asking again brings the open one forward.
        if (_setup is not null)
        {
            _setup.Activate();
            return;
        }

        try
        {
            // Only the small setup window is topmost. Keeping the main window topmost
            // would prevent the browser from coming forward during the walkthrough.
            // Monitoring has to be on for the extension to have anything to connect to.
            if (!_settings.BrowserMonitoring)
                BrowserMonitoringToggle.IsOn = true;

            _setup = new BrowserSetupWindow(AppWindow, () => _handoff?.LastContact);
            _setup.Activate();
            await _setup.WaitForCloseAsync();
            _setup = null;
            UpdateBrowserStatus();
        }
        catch (Exception exception)
        {
            _setup = null;
            App.Log($"Browser setup failed: {exception}");
            await ShowMessageAsync("Browser setup could not be opened. The error was written to the Stretto log.");
        }
    }

    /// <summary>Transfers, Library or Queues.</summary>
    private string _view = "Transfers";

    /// <summary>Transfers' state filter: Active (the default), Attention, Paused, Finished or All.</summary>
    private string _stateFilter = "Active";

    /// <summary>Library's file-type chip. Null is All.</summary>
    private string? _category;

    private string _search = "";

    public MainWindow()
    {
        _downloads = new DownloadService(_settings);
        _policy = new SystemPolicy(_settings);
        _windowHandle = WindowNative.GetWindowHandle(this);

        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "stretto.ico"));
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1180, 720));
        CenterOnScreen();
        _downloads.SetSpeedLimit(_settings.SpeedLimitBytesPerSecond);

        DownloadList.ItemsSource = new Microsoft.UI.Xaml.Data.CollectionViewSource
        {
            IsSourceGrouped = true,
            Source = _groups,
        }.View;
        _downloads.Items.CollectionChanged += OnItemsChanged;
        _downloads.Restore();

        // Queues reference downloads by id, so drop references to anything that is gone.
        _downloads.Queues.Prune(_downloads.Items);

        // Opt in only: resuming everything unasked on launch is a surprise by default.
        if (_settings.ResumeOnStartup)
            _downloads.ResumeAll();

        StartTray();
        WatchWindowState();

        // Re-claimed every launch, so a move or a rebuild does not leave the extension's
        // Launch button pointing at a path that no longer exists.
        StartupRegistration.RegisterProtocol(enabled: true);

        // Activate still has to happen for the content tree to build, so this hides the
        // window immediately afterwards rather than never showing it.
        // Not on a launch from the extension's Launch button: that button is only ever
        // clicked by someone who cannot see Stretto, so coming up in the tray would look
        // like nothing happened.
        if (_tray is not null && (_settings.StartMinimized || StartupRegistration.LaunchedHidden)
            && !StartupRegistration.LaunchedFromProtocol)
            DispatcherQueue.TryEnqueue(() => AppWindow.Hide());

        Closed += (_, _) =>
        {
            _tray?.Dispose();
            _handoff?.Stop();
            _policy.Release();
            _downloads.Save();
            _downloads.Queues.Save();
            _settings.Save();
        };

        BrowserMonitoringToggle.IsOn = _settings.BrowserMonitoring;
        if (_settings.BrowserMonitoring)
            StartHandoff();
        else
            UpdateBrowserStatus();

        // Items migrate between Active and Complete as they finish, and the status bar
        // aggregates live speed -- both are cheap enough to just re-evaluate on a tick.
        _refresh.Tick += (_, _) =>
        {
            // Hidden in the tray, nobody is looking at the list, so there is no point
            // re-syncing it twice a second. The status bar still runs: keep-awake and the
            // finish actions hang off its count. Showing the window syncs on the next tick.
            if (AppWindow.IsVisible)
                Refresh();

            UpdateLiveSummary();
            UpdateTrayTooltip();
            _taskbar.Update(_windowHandle, _downloads.Items);

            // The list is small, but rewriting it twice a second is still pointless, and
            // the store skips the write entirely when nothing changed.
            // The same tick evaluates queue timetables, which need nothing finer than this.
            if (++_ticksSinceSave >= 20)
            {
                _ticksSinceSave = 0;
                _downloads.Save();
                _downloads.TickSchedules();
                _ = CheckClipboardAsync();
            }
        };
        _refresh.Start();

        WatchMediaFeed();
        _downloads.Completed += OnDownloadCompleted;
        _downloads.Failed += OnDownloadFailed;

        _uiReady = true;

        // Asked rather than switched on silently: a program that adds itself to startup
        // without asking feels sneaky, but for a download manager it is the right default,
        // so Yes is the highlighted answer.
        // Not on a launch into the tray: nobody is looking at the window to answer it.
        if (!_settings.NeverAskAboutStartup
            && !_settings.StartMinimized && !StartupRegistration.LaunchedHidden
            && !StartupRegistration.LaunchedFromProtocol
            && !Environment.GetCommandLineArgs().Any(a => a.StartsWith("--show", StringComparison.Ordinal)))
            RootGrid.Loaded += async (_, _) => await AskAboutStartupAsync();
        RootGrid.Loaded += async (_, _) => await CheckForUpdatesQuietlyAsync();
        RefreshCategoryNav();
        BuildStateChips();
        ApplySortChoice();
        DetailsToggle.IsChecked = _settings.ShowDetailsPane;
        Nav.SelectedItem = TransfersNavItem;
        UpdateViewChrome();
        Refresh();

#if DEBUG
        // --seed=<url> populates the list at launch, so the layout can be checked against
        // real rows instead of an empty state.
        foreach (var argument in Environment.GetCommandLineArgs()
                     .Where(a => a.StartsWith("--seed=", StringComparison.Ordinal)))
        {
            _downloads.Add(argument["--seed=".Length..]);
        }

        // --seed-media=<url> fakes a browser sighting so the grabber's browser tab can be
        // checked without running the extension.
        foreach (var argument in Environment.GetCommandLineArgs()
                     .Where(a => a.StartsWith("--seed-media=", StringComparison.Ordinal)))
        {
            var url = argument["--seed-media=".Length..];
            _feed.Add(new MediaReport
            {
                Url = url,
                Name = url.Split('/').LastOrDefault() is { } last && last.Length > 0 ? last : url,
                Kind = url.EndsWith(".mpd", StringComparison.OrdinalIgnoreCase) ? "stream"
                    : url.Contains(".m3u8") ? "stream" : "video",
            });
        }

        // --filter=<name> opens on a given view, so every tab can be captured without clicking.
        var requested = Environment.GetCommandLineArgs()
            .FirstOrDefault(a => a.StartsWith("--filter=", StringComparison.Ordinal));

        if (requested is not null)
        {
            // Old names still work, so existing capture scripts keep producing every view.
            switch (requested["--filter=".Length..])
            {
                case "Active" or "Transfers": ShowView("Transfers"); break;
                case "Complete" or "Library": ShowView("Library"); break;
                case "Queues": ShowView("Queues"); break;
                case var category: _category = category; ShowView("Library"); break;
            }
        }

        // --show=<screen> opens a dialog or flyout on launch so it can be captured.
        var show = Environment.GetCommandLineArgs()
            .FirstOrDefault(a => a.StartsWith("--show=", StringComparison.Ordinal))?["--show=".Length..];

        if (show is not null)
            RootGrid.Loaded += async (_, _) => await ShowScreenAsync(show);
#endif
    }

#if DEBUG
    private async Task ShowScreenAsync(string screen)
    {
        await Task.Delay(400);   // let the window settle before anything is drawn over it

        switch (screen)
        {
            case "capture":
                await OnCaptureDecidedAsync(new Stretto.Core.HandoffRequest
                {
                    Url = "https://civitai.com/api/download/models/3289643?fileId=3174160",
                    FileName = "example-model.safetensors",
                });
                break;

            case "newmenu":
                NewButton.Flyout.ShowAt(NewButton);
                break;

            case "new":
                var newWindow = new NewDownloadWindow(_settings, _downloads.DefaultDirectory);
                var newDialog = newWindow.Dialog;
                await newDialog.PrefillFromClipboardAsync();
                newWindow.Show();
                await newWindow.ClosedTask;
                break;

            case "video":
                var video = new VideoDownloadDialog(_downloads.Http, _downloads.DefaultDirectory);
                video.Prefill("https://www.w3schools.com/html/html5_video.asp");
                await ShowInWindowAsync(video, "Video download", 560, 380);
                break;

            case "video-empty":
                // A page with no media, to show what happens when nothing is found.
                var empty = new VideoDownloadDialog(_downloads.Http, _downloads.DefaultDirectory);
                empty.Prefill("https://example.com/");
                await ShowInWindowAsync(empty, "Video download", 560, 380);
                break;

            case "batch":
            case "batchlinks":
                var batch = new BatchDownloadDialog();
                if (screen == "batchlinks") batch.SelectLinksTab();
                await ShowInWindowAsync(batch, "Batch download", 500, 300);
                break;

            case "queue":
                var queue = new QueueDialog(_downloads);
                await ShowInWindowAsync(queue, "Queues", 660, 380);
                break;

            case "advanced":
                var advanced = new DownloadAdvancedWindow(_settings, "", "", false);
                advanced.Activate();
                await advanced.ClosedTask;
                break;

            case "properties":
                if (_downloads.Items.FirstOrDefault() is { } first)
                    await ShowInWindowAsync(new PropertiesDialog(first), "Download properties");
                break;

            case "complete":
                if (_downloads.Items.FirstOrDefault() is { } done)
                    await ShowInWindowAsync(new DownloadCompleteDialog(done), "Download complete");
                break;

            case "progress":
                // Seeded downloads exist by the time this runs, so show the newest one.
                if (_downloads.Items.FirstOrDefault() is { } newest)
                    ShowProgressFor(newest);
                break;

            case "settings":
            case "settings-network":
            case "settings-proxy":
            case "settings-rules":
            case "settings-system":
            case "settings-browser":
            case "settings-passwords":
            case "settings-advanced":
                // Goes through the same method the menu uses. This case used to build its own
                // window with no size, so screenshots of it were of code nobody actually runs.
                await OpenSettingsAsync(screen switch
                {
                    "settings-network" => SettingsDialog.Section.Connections,
                    "settings-proxy" => SettingsDialog.Section.Proxy,
                    "settings-browser" => SettingsDialog.Section.Browser,
                    "settings-rules" => SettingsDialog.Section.CaptureRules,
                    "settings-passwords" => SettingsDialog.Section.Passwords,
                    "settings-system" => SettingsDialog.Section.System,
                    "settings-advanced" => SettingsDialog.Section.Advanced,
                    _ => SettingsDialog.Section.General,
                });
                break;
            
            case "setup":
            case "setup-load":
            case "setup-connect":
            case "setup-firefox":
            case "setup-firefox-load":
                var showing = ShowBrowserSetupAsync();
                if (screen.Contains("firefox")) _setup?.ChooseBrowser("LibreWolf");
                _setup?.ShowStep(screen switch { "setup-load" or "setup-firefox-load" => 1, "setup-connect" => 2, _ => 0 });
                await showing;
                break;
        }
    }
#endif

    /// <summary>
    /// Puts the window in the middle of the display it opened on. Without this Windows
    /// cascades it to wherever the last window landed.
    ///
    /// Centres on the work area rather than the full display bounds, so the window is
    /// centred in the space the user can actually see instead of sitting partly behind
    /// the taskbar.
    /// </summary>
    private void CenterOnScreen()
    {
        var display = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(
            AppWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Nearest);

        if (display is null)
            return;

        var work = display.WorkArea;
        var size = AppWindow.Size;

        AppWindow.Move(new Windows.Graphics.PointInt32(
            work.X + Math.Max(0, (work.Width - size.Width) / 2),
            work.Y + Math.Max(0, (work.Height - size.Height) / 2)));
    }

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e) => Refresh();

    // ═══════════════════════════════════════
    // ═══ What is shown
    // ═══════════════════════════════════════

    private IEnumerable<DownloadItem> Filtered()
    {
        var items = _view switch
        {
            // Everything not finished, failures included: a failed download has to stay in
            // sight or its error and Retry are unreachable.
            "Transfers" => _stateFilter switch
            {
                "Attention" => _downloads.Items.Where(i => i.Status == DownloadStatus.Failed),
                "Paused" => _downloads.Items.Where(i => i.Status == DownloadStatus.Paused),
                "Finished" => _downloads.Items.Where(i => i.Status == DownloadStatus.Complete),
                "All" => _downloads.Items,
                _ => _downloads.Items.Where(i => i.IsActive),
            },

            // All is the recent run of finished files; older ones age out of it but stay
            // under their file-type chip. Nothing is deleted either way.
            "Library" when _category is null =>
                _downloads.Items.Where(i => i.Status == DownloadStatus.Complete && IsRecent(i)),
            "Library" => _downloads.Items.Where(i => i.Status == DownloadStatus.Complete
                && string.Equals(i.Category, _category, StringComparison.OrdinalIgnoreCase)),

            _ => [],
        };

        if (!string.IsNullOrWhiteSpace(_search))
            items = items.Where(i => i.Name.Contains(_search, StringComparison.OrdinalIgnoreCase));

        return items;
    }

    private bool IsRecent(DownloadItem item) =>
        _settings.KeepInCompleteDays == 0
        || (item.CompletedAt ?? item.Added) > DateTimeOffset.Now.AddDays(-_settings.KeepInCompleteDays);

    /// <summary>
    /// The groups the visible rows fall into, in display order.
    ///
    /// Transfers go by stage, so a failure is under a heading that says so rather than lost
    /// in the middle of a flat list. Library goes by when things finished, which is how
    /// people look for "the thing from this morning", unless another order was asked for.
    /// </summary>
    private List<(string Key, string Title, List<DownloadItem> Items)> Grouped(List<DownloadItem> items)
    {
        if (_view == "Transfers")
        {
            return Buckets(items, i => i.Status switch
            {
                DownloadStatus.Downloading => "Downloading",
                DownloadStatus.Failed => "Needs attention",
                DownloadStatus.Paused => "Paused",
                DownloadStatus.Complete => "Finished",
                _ => "Up next",
            }, ["Downloading", "Needs attention", "Up next", "Paused", "Finished"]);
        }

        static DateTimeOffset Finished(DownloadItem i) => i.CompletedAt ?? i.Added;

        switch (_settings.LibrarySort)
        {
            case "Name":
                return [("Name", "By name", items.OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase).ToList())];

            case "Size":
                return [("Size", "Largest first", items.OrderByDescending(i => i.TotalBytes ?? 0).ToList())];

            case "Type":
                var types = items.Select(i => i.Category).Distinct(StringComparer.OrdinalIgnoreCase)
                                 .OrderBy(c => c, StringComparer.CurrentCultureIgnoreCase).ToList();
                return Buckets(items.OrderByDescending(Finished).ToList(), i => i.Category, types);

            default:
                var oldest = _settings.LibrarySort == "Oldest";
                var ordered = oldest ? items.OrderBy(Finished).ToList() : items.OrderByDescending(Finished).ToList();
                List<string> ages = ["Today", "Yesterday", "Earlier this week", "Earlier this month", "Older"];
                if (oldest) ages.Reverse();
                return Buckets(ordered, i => Age(Finished(i)), ages);
        }

        static string Age(DateTimeOffset when)
        {
            var days = (DateTime.Today - when.LocalDateTime.Date).TotalDays;
            return days <= 0 ? "Today" : days <= 1 ? "Yesterday" : days < 7 ? "Earlier this week"
                : days < 31 ? "Earlier this month" : "Older";
        }

        static List<(string Key, string Title, List<DownloadItem> Items)> Buckets(
            List<DownloadItem> source, Func<DownloadItem, string> key, List<string> order) =>
            order.Select(k => (k, k, source.Where(i => string.Equals(key(i), k, StringComparison.OrdinalIgnoreCase)).ToList()))
                 .Where(g => g.Item3.Count > 0)
                 .ToList();
    }

    private bool _syncing;

    /// <summary>
    /// Syncs the groups in place rather than rebuilding them, so a half-second tick does
    /// not throw away the user's selection. A row that changes group (Downloading to
    /// Paused, say) is removed from every group first and only then added to its new one,
    /// and anything that was selected is selected again afterwards.
    /// </summary>
    private void Refresh()
    {
        var desired = _view == "Queues" ? [] : Grouped(Filtered().ToList());
        var selected = Selection();

        _syncing = true;
        try
        {
            var wanted = desired.ToDictionary(g => g.Key, g => new HashSet<DownloadItem>(g.Items, ReferenceEqualityComparer.Instance));

            for (var g = _groups.Count - 1; g >= 0; g--)
            {
                if (!wanted.TryGetValue(_groups[g].Key, out var keep))
                {
                    _groups.RemoveAt(g);
                    continue;
                }

                for (var i = _groups[g].Count - 1; i >= 0; i--)
                    if (!keep.Contains(_groups[g][i]))
                        _groups[g].RemoveAt(i);
            }

            for (var g = 0; g < desired.Count; g++)
            {
                var (key, title, items) = desired[g];
                var group = _groups.FirstOrDefault(x => x.Key == key);

                if (group is null)
                {
                    group = new DownloadGroup(key);
                    _groups.Insert(g, group);
                }
                else if (_groups.IndexOf(group) != g)
                {
                    _groups.Move(_groups.IndexOf(group), g);
                }

                for (var i = 0; i < items.Count; i++)
                {
                    if (i < group.Count && ReferenceEquals(group[i], items[i]))
                        continue;

                    var existing = group.IndexOf(items[i]);
                    if (existing >= 0) group.Move(existing, i);
                    else group.Insert(i, items[i]);
                }

                group.Title = title;
                group.CountText = items.Count.ToString();
                group.Aside = key == "Downloading" && _view == "Transfers"
                    ? $"{DownloadItem.FormatBytes((long)items.Sum(i => i.BytesPerSecond))}/s"
                    : "";
            }

            var visible = new HashSet<DownloadItem>(desired.SelectMany(d => d.Items), ReferenceEqualityComparer.Instance);
            foreach (var item in selected)
                if (visible.Contains(item) && !DownloadList.SelectedItems.Contains(item))
                    DownloadList.SelectedItems.Add(item);
        }
        finally
        {
            _syncing = false;
        }

        _visible.Clear();
        _visible.AddRange(desired.SelectMany(d => d.Items));

        UpdateEmptyState();
        UpdateChipCounts();
        UpdateSummary();

        // Downloads finish while the view is open, so the commands have to follow them.
        if (_uiReady)
        {
            UpdateToolbar();
            UpdateDetails();
        }
    }

    /// <summary>What an empty view says, and the next step it offers.</summary>
    private void UpdateEmptyState()
    {
        EmptyState.Visibility = _visible.Count == 0 && _view != "Queues" ? Visibility.Visible : Visibility.Collapsed;
        if (EmptyState.Visibility != Visibility.Visible) return;

        var browserOn = _handoff?.IsListening == true;

        var (glyph, title, body, action, secondary) = true switch
        {
            _ when !string.IsNullOrWhiteSpace(_search) =>
                ("IconSearch", $"Nothing called “{_search.Trim()}”", "Search looks at file names in this view.", "Clear search", (string?)null),

            _ when _view == "Transfers" && _downloads.Items.Count == 0 =>
                ("IconTransfers", "Nothing downloading yet",
                 browserOn ? "Paste a link with Ctrl+V, or start a download in your browser and Stretto will take it from there."
                           : "Paste a link with Ctrl+V. Connect your browser and its downloads will arrive here on their own.",
                 "Add download", browserOn ? null : "Connect a browser"),

            _ when _view == "Transfers" && _stateFilter != "Active" =>
                ("IconTransfers", _stateFilter switch
                {
                    "Attention" => "Nothing needs attention",
                    "Paused" => "Nothing is paused",
                    "Finished" => "Nothing has finished yet",
                    _ => "No transfers",
                }, "Pick another filter above to see the rest.", "Show active", null),

            _ when _view == "Transfers" =>
                ("IconTransfers", "Nothing downloading", "Finished downloads are in Library.",
                 "Go to Library", null),

            _ when _category is not null =>
                ("IconLibrary", $"No {_category.ToLowerInvariant()} files", "Finished downloads of this type appear here.", "Show all", null),

            _ => ("IconLibrary", "Library is empty",
                  _settings.KeepInCompleteDays > 0 && _downloads.Items.Any(i => i.Status == DownloadStatus.Complete)
                      ? $"Nothing finished in the last {_settings.KeepInCompleteDays} days. Older files are under their type above."
                      : "Downloads land here when they finish.",
                  _downloads.Items.Any(i => i.IsActive) ? "Go to Transfers" : "Add download", null),
        };

        EmptyGlyph.Glyph = (string)Application.Current.Resources[glyph];
        EmptyStateText.Text = title;
        EmptyBodyText.Text = body;
        EmptyActionButton.Content = action;
        EmptyActionButton.Visibility = action is null ? Visibility.Collapsed : Visibility.Visible;
        EmptySecondaryButton.Content = secondary;
        EmptySecondaryButton.Visibility = secondary is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void EmptyAction_Click(object sender, RoutedEventArgs e) =>
        await RunEmptyActionAsync(EmptyActionButton.Content as string);

    private async void EmptySecondary_Click(object sender, RoutedEventArgs e) =>
        await RunEmptyActionAsync(EmptySecondaryButton.Content as string);

    private async Task RunEmptyActionAsync(string? action)
    {
        switch (action)
        {
            case "Clear search": SearchBox.Text = ""; break;
            case "Add download": NewDownload_Click(this, new RoutedEventArgs()); break;
            case "Connect a browser": await ShowBrowserSetupAsync(); break;
            case "Go to Library": ShowView("Library"); break;
            case "Go to Transfers": ShowView("Transfers"); break;
            case "Show all": _category = null; RefreshCategoryNav(); Refresh(); break;
            case "Show active": _stateFilter = "Active"; BuildStateChips(); Refresh(); break;
        }
    }

    /// <summary>The heading's second line, which is where the live numbers live now.</summary>
    private void UpdateSummary()
    {
        if (ViewSummaryText is null) return;

        switch (_view)
        {
            case "Transfers":
                var running = _downloads.Items.Where(i => i.Status == DownloadStatus.Downloading).ToList();
                var waiting = _downloads.Items.Count(i => i.Status is DownloadStatus.Queued or DownloadStatus.Pending);
                var failed = _downloads.Items.Count(i => i.Status == DownloadStatus.Failed);

                var parts = new List<string>();
                if (running.Count > 0)
                {
                    parts.Add($"{running.Count} downloading at {DownloadItem.FormatBytes((long)running.Sum(i => i.BytesPerSecond))}/s");

                    // The whole batch's time left, when every running transfer knows its own.
                    if (running.All(i => i.TimeLeft is not null))
                        parts.Add($"{DownloadItem.FormatDuration(running.Max(i => i.TimeLeft!.Value))} to go");
                }
                if (waiting > 0) parts.Add($"{waiting} waiting");
                if (failed > 0) parts.Add(failed == 1 ? "1 needs attention" : $"{failed} need attention");

                ViewSummaryText.Text = parts.Count > 0 ? string.Join("  ·  ", parts) : "Nothing running";
                break;

            case "Library":
                var size = _visible.Sum(i => i.TotalBytes ?? 0);
                var files = _visible.Count == 1 ? "1 file" : $"{_visible.Count} files";
                ViewSummaryText.Text = _category is null && _settings.KeepInCompleteDays > 0
                    ? $"{files}  ·  {DownloadItem.FormatBytes(size)}  ·  finished in the last {_settings.KeepInCompleteDays} days"
                    : $"{files}  ·  {DownloadItem.FormatBytes(size)}";
                break;

            default:
                ViewSummaryText.Text = "Run downloads one after another, on a timetable if you like";
                break;
        }
    }

    /// <summary>
    /// The pane badge, the heading, and the policies hanging off the running count. Runs on
    /// the tick even while the window is hidden: keep-awake and the finish actions need it.
    /// </summary>
    private void UpdateLiveSummary()
    {
        var active = _downloads.Items.Count(i => i.Status == DownloadStatus.Downloading);

        TransfersBadge.Value = active;
        TransfersBadge.Visibility = active > 0 ? Visibility.Visible : Visibility.Collapsed;

        if (AppWindow.IsVisible)
            UpdateSummary();

        // Keep-awake and the "when everything finishes" actions are both a function of this
        // same count, so they ride the tick rather than needing their own plumbing.
        _policy.Update(active);
    }

    // ═══════════════════════════════════════
    // ═══ Navigation
    // ═══════════════════════════════════════

    private void TitleBar_PaneToggleRequested(TitleBar sender, object args) => Nav.IsPaneOpen = !Nav.IsPaneOpen;

    private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem { Tag: string view } && view != _view)
            ShowView(view, fromNav: true);
    }

    /// <summary>Browser and Settings act rather than navigate.</summary>
    private async void Nav_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if (args.InvokedItemContainer == BrowserNavItem)
            FlyoutBase.ShowAttachedFlyout(BrowserNavItem);
        else if (args.InvokedItemContainer == SettingsNavItem)
        {
            // Deferred until the pane has finished handling the click: opened inside it,
            // the pane took focus back afterwards and Settings ended up behind the window.
            await Task.Yield();
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                async () => await OpenSettingsAsync());
        }
    }

    private void ShowView(string view, bool fromNav = false)
    {
        if (_view == "Queues" && view != "Queues")
            CloseQueuesPage();

        _view = view;

        if (!fromNav)
        {
            Nav.SelectedItem = view switch
            {
                "Library" => LibraryNavItem,
                "Queues" => QueuesNavItem,
                _ => TransfersNavItem,
            };
        }

        if (view == "Queues" && _queuePage is null)
            OpenQueuesPage();

        DownloadList.SelectedItems.Clear();
        UpdateViewChrome();
        Refresh();
    }

    /// <summary>Title, which bars show, and which whole-view commands apply.</summary>
    private void UpdateViewChrome()
    {
        var queues = _view == "Queues";

        ViewTitleText.Text = _view;
        ChipBar.Visibility = _view == "Library" ? Visibility.Visible : Visibility.Collapsed;
        StateBar.Visibility = _view == "Transfers" ? Visibility.Visible : Visibility.Collapsed;
        SortButton.Visibility = _view == "Library" ? Visibility.Visible : Visibility.Collapsed;
        CommandBand.Visibility = queues ? Visibility.Collapsed : Visibility.Visible;
        ListArea.Visibility = queues ? Visibility.Collapsed : Visibility.Visible;
        QueueHost.Visibility = queues ? Visibility.Visible : Visibility.Collapsed;
        DetailsPane.Visibility = !queues && DetailsToggle.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        UpdateSummary();
    }

    /// <summary>
    /// Rebuilds Library's file-type chips from the table. Called after Settings closes,
    /// because a category can be added, renamed or removed there.
    /// </summary>
    private void RefreshCategoryNav()
    {
        var names = CategoryRules.Active.Select(r => (r.Name, r.Icon)).ToList();

        // Other is not in the table -- it is what nothing matched -- but it still needs
        // somewhere to be looked at.
        names.Add((CategoryRules.Fallback, "IconOther"));

        // A chip for a category that no longer exists would show nothing forever.
        if (_category is not null && names.All(n => !string.Equals(n.Name, _category, StringComparison.OrdinalIgnoreCase)))
            _category = null;

        ChipPanel.Children.Clear();
        ChipPanel.Children.Add(Chip("All", null, "IconLibrary"));
        foreach (var (name, icon) in names)
            ChipPanel.Children.Add(Chip(name, name, icon));

        UpdateChipCounts();

        ToggleButton Chip(string text, string? category, string icon)
        {
            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
            content.Children.Add(new FontIcon { Glyph = Application.Current.Resources[icon] as string ?? "", FontSize = 13 });
            content.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
            content.Children.Add(new TextBlock { Opacity = 0.6, VerticalAlignment = VerticalAlignment.Center });

            var chip = new ToggleButton
            {
                Content = content,
                Tag = category,
                IsChecked = string.Equals(category, _category, StringComparison.OrdinalIgnoreCase),
                CornerRadius = new CornerRadius(15),
                Padding = new Thickness(12, 5, 12, 5),
                FontSize = 12.5,
            };
            chip.Click += (_, _) =>
            {
                _category = category;
                foreach (var other in ChipPanel.Children.OfType<ToggleButton>())
                    other.IsChecked = ReferenceEquals(other, chip);
                DownloadList.SelectedItems.Clear();
                Refresh();
            };
            return chip;
        }
    }

    private static readonly (string Key, string Label, string Icon)[] StateChips =
    [
        ("Active", "Active", "IconTransfers"),
        ("Attention", "Needs attention", "IconRetry"),
        ("Paused", "Paused", "IconResume"),
        ("Finished", "Finished", "IconFolder"),
        ("All", "All", "IconLibrary"),
    ];

    private static bool InState(DownloadItem item, string state) => state switch
    {
        "Attention" => item.Status == DownloadStatus.Failed,
        "Paused" => item.Status == DownloadStatus.Paused,
        "Finished" => item.Status == DownloadStatus.Complete,
        "All" => true,
        _ => item.IsActive,
    };

    /// <summary>Builds Transfers' state chips. Counts are kept up to date by UpdateStateChipCounts.</summary>
    private void BuildStateChips()
    {
        StatePanel.Children.Clear();
        foreach (var (key, label, icon) in StateChips)
        {
            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
            content.Children.Add(new FontIcon { Glyph = Application.Current.Resources[icon] as string ?? "", FontSize = 13 });
            content.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
            content.Children.Add(new TextBlock { Opacity = 0.6, VerticalAlignment = VerticalAlignment.Center });

            var chip = new ToggleButton
            {
                Content = content,
                Tag = key,
                IsChecked = key == _stateFilter,
                CornerRadius = new CornerRadius(15),
                Padding = new Thickness(12, 5, 12, 5),
                FontSize = 12.5,
            };
            chip.Click += (_, _) =>
            {
                _stateFilter = key;
                foreach (var other in StatePanel.Children.OfType<ToggleButton>())
                    other.IsChecked = ReferenceEquals(other, chip);
                DownloadList.SelectedItems.Clear();
                Refresh();
            };
            StatePanel.Children.Add(chip);
        }

        UpdateStateChipCounts();
    }

    /// <summary>Counts on the state chips. Needs attention hides itself when nothing has failed.</summary>
    private void UpdateStateChipCounts()
    {
        foreach (var chip in StatePanel.Children.OfType<ToggleButton>())
        {
            var key = (string)chip.Tag;
            var count = _downloads.Items.Count(i => InState(i, key));

            if (chip.Content is StackPanel panel && panel.Children.Count == 3 && panel.Children[2] is TextBlock number)
                number.Text = count.ToString();

            chip.Visibility = key == "Attention" && count == 0 && chip.IsChecked != true
                ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    /// <summary>Counts on the chips, so an empty type can be skipped without a click.</summary>
    private void UpdateChipCounts()
    {
        UpdateStateChipCounts();

        if (_view != "Library") return;

        foreach (var chip in ChipPanel.Children.OfType<ToggleButton>())
        {
            var category = chip.Tag as string;
            var count = category is null
                ? _downloads.Items.Count(i => i.Status == DownloadStatus.Complete && IsRecent(i))
                : _downloads.Items.Count(i => i.Status == DownloadStatus.Complete
                    && string.Equals(i.Category, category, StringComparison.OrdinalIgnoreCase));

            if (chip.Content is StackPanel panel && panel.Children.Count == 3 && panel.Children[2] is TextBlock number)
                number.Text = count.ToString();

            // A type with nothing in it is a dead end; it comes back when a file of that type does.
            chip.Visibility = count > 0 || category is null || chip.IsChecked == true
                ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void Sort_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioMenuFlyoutItem { Tag: string sort }) return;

        _settings.LibrarySort = sort;
        _settings.Save();
        ApplySortChoice();
        Refresh();
    }

    private void ApplySortChoice()
    {
        foreach (var item in new[] { SortNewest, SortOldest, SortName, SortSize, SortType })
        {
            item.IsChecked = (string)item.Tag == _settings.LibrarySort;
            if (item.IsChecked) SortLabel.Text = item.Text;
        }
    }

    private void Search_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        _search = sender.Text ?? "";
        Refresh();
    }

    // ═══════════════════════════════════════
    // ═══ Queues page
    // ═══════════════════════════════════════

    private QueueDialog? _queuePage;

    /// <summary>The queue editor, hosted in the window rather than a dialog of its own.</summary>
    private void OpenQueuesPage()
    {
        _queuePage = new QueueDialog(_downloads);

        // Sized for the window it used to have; here it fills the page instead.
        if (_queuePage.Content is FrameworkElement root)
        {
            root.Width = double.NaN;
            root.Height = double.NaN;
        }

        QueueHost.Child = _queuePage;
    }

    /// <summary>Membership edits apply live but only Create/Remove save on their own, so
    /// persist everything on the way out. Commit pulls the time pickers first.</summary>
    private void CloseQueuesPage()
    {
        if (_queuePage is null) return;

        _queuePage.Commit();
        _downloads.Queues.Save();
        QueueHost.Child = null;
        _queuePage = null;
    }

    // ═══════════════════════════════════════
    // ═══ Keyboard
    // ═══════════════════════════════════════

    private void NewAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        NewDownload_Click(this, new RoutedEventArgs());
    }

    private void SearchAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        SearchBox.Focus(FocusState.Keyboard);
    }

    private void ClearSelection_Click(object sender, RoutedEventArgs e) => DownloadList.SelectedItems.Clear();
    // ═══════════════════════════════════════
    // ═══ Toolbar
    // ═══════════════════════════════════════

    private async void NewDownload_Click(object sender, RoutedEventArgs e)
    {
        var window = new NewDownloadWindow(_settings, _downloads.DefaultDirectory);
        var dialog = window.Dialog;

        await dialog.PrefillFromClipboardAsync();
        window.Show();

        var outcome = await window.ClosedTask;
        if (outcome is NewDownloadDialog.Outcome.Cancelled || !dialog.IsAddressValid)
            return;

        if (!await ConfirmNotDuplicateAsync(dialog.Address, dialog.FileNameOrNull))
            return;

        ShowActive();

        dialog.CommitAdvanced();

        var startNow = outcome == NewDownloadDialog.Outcome.DownloadNow;
        var added = _downloads.Add(dialog.Address, dialog.BuildOptions(StartsImmediately(startNow, dialog.Later)));

        AfterAdd(added, startNow, dialog.Later);

        if (dialog.Later is NewDownloadDialog.LaterChoice.OpenQueues)
            await ShowQueuesAsync();
    }

    /// <summary>
    /// Whether the download is allowed to run the moment it is added.
    ///
    /// A queued one still says yes: it is the queue that holds it, not the download, so it
    /// starts the instant the queue does. "Do not use queue" is the only Later choice that
    /// parks it outright.
    /// </summary>
    private static bool StartsImmediately(bool downloadNow, NewDownloadDialog.LaterChoice? later) =>
        downloadNow || later is not NewDownloadDialog.LaterChoice.NoQueue;

    /// <summary>
    /// Stops the same file being fetched twice by accident. Returns true to go ahead.
    ///
    /// Matched on the address without its #fragment, or on the file name. The name matters
    /// most: sites like civitai hand out a freshly signed CDN link for every click, so the
    /// same file never arrives at the same address twice. " (2)" style copies count as the
    /// same name. A plain number or other name too generic to mean anything is not matched
    /// on its own, so "download.bin" from two sites is not called a duplicate.
    /// </summary>
    private async Task<bool> ConfirmNotDuplicateAsync(string url, string? fileName = null)
    {
        static string Key(string address) => address.Split('#')[0].Trim();

        // "name (3).ext" -> "name.ext", the numbering Stretto itself adds to avoid a clash.
        static string BaseName(string name) =>
            System.Text.RegularExpressions.Regex.Replace(name.Trim(), @" \(\d+\)(?=\.[^.]+$|$)", "");

        var key = Key(url);
        var name = string.IsNullOrWhiteSpace(fileName) ? null : BaseName(fileName);
        var nameIsDistinctive = name is not null && Path.HasExtension(name) && Path.GetFileNameWithoutExtension(name).Length >= 6;

        var existing = _downloads.Items.FirstOrDefault(i =>
            (i.IsActive || (i.Status == DownloadStatus.Complete && File.Exists(i.ResolvedPath ?? "")))
            && (string.Equals(Key(i.Url), key, StringComparison.OrdinalIgnoreCase)
                || (nameIsDistinctive && string.Equals(BaseName(i.Name), name, StringComparison.OrdinalIgnoreCase))));

        if (existing is null)
            return true;

        var finished = existing.Status == DownloadStatus.Complete;
        var form = new MessageWindowForm(
            finished
                ? $"You already downloaded this as {existing.Name}."
                : $"This is already in your list as {existing.Name}, and has not finished yet.",
            primaryText: "Download again",
            secondaryText: finished ? "Open it" : "Show it",
            cancelText: "Cancel",
            accentPrimary: false);

        switch (await ShowInWindowAsync(form, "Already downloaded"))
        {
            case ContentDialogResult.Primary:
                return true;

            case ContentDialogResult.Secondary when finished:
                OpenPath(existing.ResolvedPath!);
                return false;

            case ContentDialogResult.Secondary:
                ShowActive();
                ShowProgressFor(existing);
                return false;

            default:
                return false;
        }
    }

    /// <summary>
    /// Queue placement and the progress window, shared by the New button and captures.
    /// Opening the Queues page is left to the caller, because the capture
    /// path must answer the browser before it puts another window on screen.
    /// </summary>
    private void AfterAdd(DownloadItem added, bool startNow, NewDownloadDialog.LaterChoice? later)
    {
        if (later is NewDownloadDialog.LaterChoice.DefaultQueue or NewDownloadDialog.LaterChoice.OpenQueues)
            _downloads.Queues.Enqueue(_downloads.Queues.Default, added);

        // Only for Download: opening a progress window for something deliberately
        // deferred would be the opposite of what was asked for.
        if (startNow && _settings.ShowProgressWindow)
            ShowProgressFor(added);
    }

    private async void BatchDownload_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new BatchDownloadDialog();

        if (await ShowInWindowAsync(dialog, "Batch download", 500, 300) != ContentDialogResult.Primary || dialog.Urls.Count == 0)
            return;

        ShowActive();
        _downloads.AddMany(dialog.Urls);

        if (dialog.Urls.Count > _downloads.MaxConcurrent)
        {
            await ShowMessageAsync(
                $"Queued {dialog.Urls.Count} downloads. " +
                $"{_downloads.MaxConcurrent} run at a time; the rest start as slots free up.");
        }
    }

    private void ShowActive()
    {
        if (_view != "Transfers")
            ShowView("Transfers");
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        var selection = Selection();
        if (selection.Count == 0)
            return;

        // Unticked by default: removing a row from the list is trivially undone by adding
        // the link again, but erasing the bytes is not.
        var deleteFiles = new CheckBox { Content = "Delete files from disk", IsChecked = false };

        var form = new MessageWindowForm(
            selection.Count == 1
                ? $"Remove “{selection[0].Name}” from the list?"
                : $"Remove these {selection.Count} downloads from the list?",
            primaryText: "Delete", cancelText: "Cancel", extraContent: deleteFiles, width: 420);

        if (await ShowInWindowAsync(form, selection.Count == 1 ? "Delete download" : "Delete downloads") != ContentDialogResult.Primary)
            return;

        foreach (var item in selection)
            _downloads.Remove(item, deleteFiles.IsChecked == true);

        _downloads.Queues.Prune(_downloads.Items);
        _downloads.Queues.Save();
        _downloads.Save();
    }

    private void Pause_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in Selection())
            _downloads.Pause(item);
    }

    private void Resume_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in Selection())
            _downloads.Resume(item);
    }

    private List<DownloadItem> Selection() => DownloadList.SelectedItems.OfType<DownloadItem>().ToList();

    private void DownloadList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // The tick re-selects rows that moved between groups; that is not a user change.
        if (_syncing) return;

        UpdateToolbar();
        UpdateDetails();
    }

    /// <summary>
    /// Puts the command bar in one of two modes. With nothing selected it holds commands
    /// for the whole view; with a selection it says how many and holds what applies to
    /// them. Commands that cannot apply are hidden rather than left disabled, except that
    /// Pause and Resume stay side by side so neither jumps under the pointer.
    /// </summary>
    private void UpdateToolbar()
    {
        var selection = Selection();
        var selecting = selection.Count > 0;
        var complete = (DownloadItem i) => i.Status == DownloadStatus.Complete;
        var unfinished = selection.Any(i => !complete(i));
        var single = selection.Count == 1 ? selection[0] : null;

        SelectionPill.Visibility = selecting ? Visibility.Visible : Visibility.Collapsed;
        SelectionCountText.Text = selection.Count == 1 ? "1 selected" : $"{selection.Count} selected";

        Show(PauseAllButton, !selecting && _view == "Transfers");
        Show(ResumeAllButton, !selecting && _view == "Transfers");
        Show(OpenDownloadsButton, !selecting && _view == "Library");
        Show(ClearFinishedButton, !selecting && _view == "Library");

        PauseAllButton.IsEnabled = _visible.Any(i => i.CanPause);
        ResumeAllButton.IsEnabled = _visible.Any(i => i.CanResume);
        ClearFinishedButton.IsEnabled = _downloads.Items.Any(complete);

        Show(PauseButton, unfinished);
        Show(ResumeButton, unfinished);
        PauseButton.IsEnabled = selection.Any(i => i.CanPause);
        ResumeButton.IsEnabled = selection.Any(i => i.CanResume);

        // Opening only makes sense for a single row, and only once a file is actually there.
        Show(OpenButton, single is not null && complete(single));
        OpenButton.IsEnabled = single?.ResolvedPath is { } path && File.Exists(path);
        Show(OpenFolderButton, single is not null);
        Show(DeleteButton, selecting);

        static void Show(UIElement element, bool visible) =>
            element.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// The row's own button: pause, resume, retry, or show in folder, by state. It acts on
    /// its row alone, whatever else is selected.
    /// </summary>
    private void RowAction_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not DownloadItem item)
            return;

        if (item.Status == DownloadStatus.Complete)
            RevealPath(item);
        else if (item.CanPause)
            _downloads.Pause(item);
        else if (item.CanResume)
            _downloads.Resume(item);

        Refresh();
    }

    /// <summary>The body of the split button is New download; the arrow holds the rest.</summary>
    private void NewSplit_Click(SplitButton sender, SplitButtonClickEventArgs args) =>
        NewDownload_Click(sender, new RoutedEventArgs());

    // ═══════════════════════════════════════
    // ═══ Details pane
    // ═══════════════════════════════════════

    private DownloadItem? _detailsItem;
    private DownloadStatus _detailsStatus;

    private void DetailsToggle_Click(object sender, RoutedEventArgs e)
    {
        _settings.ShowDetailsPane = DetailsToggle.IsChecked == true;
        _settings.Save();
        UpdateViewChrome();
        _detailsItem = null;
        UpdateDetails();
    }

    /// <summary>
    /// Shows the one selected download in the pane. Rebuilt when the selection or the
    /// download's state changes; the row itself carries the live numbers.
    /// </summary>
    private void UpdateDetails()
    {
        if (DetailsPane.Visibility != Visibility.Visible)
            return;

        var selection = Selection();
        var single = selection.Count == 1 ? selection[0] : null;

        if (single is null)
        {
            _detailsItem = null;
            DetailsHost.Content = null;
            DetailsScroller.Visibility = Visibility.Collapsed;
            DetailsEmpty.Visibility = Visibility.Visible;
            DetailsEmptyText.Text = selection.Count > 1
                ? $"{selection.Count} downloads selected. Pick one to see its details."
                : "Pick a download to see where it came from and where it is going.";
            return;
        }

        if (ReferenceEquals(single, _detailsItem) && single.Status == _detailsStatus)
            return;

        _detailsItem = single;
        _detailsStatus = single.Status;
        DetailsHost.Content = new PropertiesDialog(single, embedded: true);
        DetailsScroller.Visibility = Visibility.Visible;
        DetailsEmpty.Visibility = Visibility.Collapsed;
    }

    // ═══════════════════════════════════════
    // ═══ Menu
    // ═══════════════════════════════════════

    /// <summary>
    /// Browser monitoring opens the settings section of that name, not the setup wizard.
    /// The wizard is "browser integration" and is for the first time; this entry is reached
    /// far more often by someone who just wants to change a capture rule.
    /// </summary>
    private async void BrowserSettings_Click(object sender, RoutedEventArgs e) =>
        await OpenSettingsAsync(SettingsDialog.Section.Browser);

    private async Task OpenSettingsAsync(SettingsDialog.Section section = SettingsDialog.Section.General)
    {
        var port = _settings.HandoffPort;

        var dialog = new SettingsDialog(_settings, WindowNative.GetWindowHandle(this), _downloads.Http,
            DescribeBrowserLink());
        dialog.SelectSection(section);

        // One static size for every section: the window never reshapes itself while
        // hunting through settings. Long sections scroll inside their own page.
        var result = await ShowInWindowAsync(dialog, "Settings", 820, 560);
        if (result != ContentDialogResult.Primary && !dialog.RequestedSetup)
            return;

        if (result == ContentDialogResult.Primary)
            dialog.Apply();

        // Timeout and retry count are baked into the HTTP stack, so they only take effect
        // once it is rebuilt. This also re-applies the speed limit and pumps the queue in
        // case the concurrency cap went up.
        _downloads.ApplySettings();

        // The categories table may have gained or lost a row.
        RefreshCategoryNav();

        // The tray icon may have just been turned on or off.
        StartTray();

        // The toggle's own handler bounces the listener for a monitoring switch; a bare
        // port change needs a rebind here. The listener reads the port at startup.
        BrowserMonitoringToggle.IsOn = _settings.BrowserMonitoring;

        if (_settings.BrowserMonitoring && _settings.HandoffPort != port)
            StartHandoff();

        if (dialog.RequestedSetup)
            await ShowBrowserSetupAsync();
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(_downloads.DefaultDirectory);
            Process.Start(new ProcessStartInfo(_downloads.DefaultDirectory) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            App.Log($"Open folder failed: {exception.Message}");
        }
    }

    private void RemoveFinished_Click(object sender, RoutedEventArgs e) => _downloads.RemoveCompleted();

    private async void About_Click(object sender, RoutedEventArgs e) => await ShowMessageAsync(
        "Stretto Download Manager\n\n" +
        $"Version {typeof(MainWindow).Assembly.GetName().Version?.ToString() ?? "unknown"}\n" +
        "Segmented downloads over parallel connections, with resume.\n" +
        "Part of the Plexus apps.");

    private void Exit_Click(object sender, RoutedEventArgs e) => ExitApplication();

    private async Task AskAboutStartupAsync()
    {
        if (StartupRegistration.IsEnabled)
            return;

        await Task.Delay(600);   // let the main window settle first

        var neverAgain = new CheckBox { Content = "Don't ask me again" };
        var form = new MessageWindowForm(
            "Start Stretto when you sign in to Windows? It waits quietly in the notification area, "
            + "so downloads from your browser are always caught.",
            primaryText: "Yes, start with Windows", cancelText: "Not now",
            extraContent: neverAgain, width: 400);

        var answer = await ShowInWindowAsync(form, "Welcome to Stretto");

        // Only a "Not now" can be made permanent; a yes is remembered by Windows itself.
        if (answer != ContentDialogResult.Primary)
        {
            if (neverAgain.IsChecked == true)
            {
                _settings.NeverAskAboutStartup = true;
                _settings.Save();
            }
            return;
        }

        _settings.StartWithWindows = true;
        _settings.ShowTrayIcon = true;
        _settings.CloseToTray = true;
        _settings.Save();
        StartupRegistration.Apply(true);
        StartTray();
    }

    private async Task ShowMessageAsync(string message)
    {
        await ShowInWindowAsync(new MessageWindowForm(message), "Stretto");
    }
}


/// <summary>
/// One heading's worth of rows: a Transfers stage or a Library date. The header text is
/// set on every refresh, so it notifies like any other bound property.
/// </summary>
public sealed class DownloadGroup(string key) : ObservableCollection<DownloadItem>
{
    private string _title = "";
    private string _countText = "";
    private string _aside = "";

    public string Key { get; } = key;

    public string Title { get => _title; set => Set(ref _title, value); }
    public string CountText { get => _countText; set => Set(ref _countText, value); }

    /// <summary>Right-aligned extra, like the combined speed of everything downloading.</summary>
    public string Aside { get => _aside; set => Set(ref _aside, value); }

    private void Set(ref string field, string value, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (field == value) return;
        field = value;
        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(name));
    }
}
