using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Stretto.App.Dialogs;
using Stretto.App.Services;
using Stretto.Core;
using System.Diagnostics;

namespace Stretto.App;

/// <summary>The browser extension: pairing, captured downloads and batches, and link refresh.</summary>
public sealed partial class MainWindow
{
    private readonly Dictionary<string, DownloadProgressWindow> _progressWindows = [];
    // ═══════════════════════════════════════
    // ═══ Browser handoff
    // ═══════════════════════════════════════

    private void StartHandoff()
    {
        // Never leave an old listener holding the port: a second bind fails, and the
        // failure then reports "could not open the port" while the first one is still
        // happily serving.
        _handoff?.Stop();
        _handoff = null;

        try
        {
            _handoff = new HandoffListener(() => Pairing.Token, _settings.HandoffPort);
            _handoff.Rules = BuildRulesJson;
            _handoff.PairRequested = OnPairRequestedAsync;
            _handoff.Decide = OnCaptureDecidedAsync;
            _handoff.DecideBatch = OnBatchDecidedAsync;
            _handoff.SkipHost = OnSkipHost;
            _handoff.MediaSeen += OnMediaSeen;
            _handoff.Log += message => App.Log($"Handoff: {message}");
            _handoff.Start();
        }
        catch (Exception exception)
        {
            // Usually another process already owns the port.
            App.Log($"Could not start handoff listener: {exception}");
            _handoff = null;
        }

        UpdateBrowserStatus();
    }

    /// <summary>
    /// The capture rules the extension asks for. Serialised here rather than held in the
    /// extension so there is exactly one copy of the truth; the extension caches the last
    /// answer only so it still behaves while the app is closed.
    /// </summary>
    private string BuildRulesJson() => System.Text.Json.JsonSerializer.Serialize(new
    {
        captureTypes = _settings.CaptureTypes,
        videoTypes = _settings.VideoTypes,
        mediaTypes = _settings.MediaTypes,
        minVideoKb = _settings.MinVideoSizeKb,
        skipHosts = _settings.SkipHosts,
        alwaysHosts = _settings.AlwaysHosts,
        nameFrom = _settings.NameFrom.ToString(),
    });

    /// <summary>
    /// The popup's "Don't capture on this site". Runs on the listener thread, so the list is
    /// replaced rather than edited: BuildRulesJson reads it from that thread too, and a
    /// swapped reference is never seen half changed. Saving hops to the UI thread.
    /// </summary>
    private void OnSkipHost(string host, bool skip)
    {
        var current = _settings.SkipHosts;
        var has = current.Contains(host, StringComparer.OrdinalIgnoreCase);
        if (has == skip)
            return;

        _settings.SkipHosts = skip
            ? [.. current, host]
            : [.. current.Where(h => !string.Equals(h, host, StringComparison.OrdinalIgnoreCase))];

        DispatcherQueue.TryEnqueue(() =>
        {
            _settings.Save();
            if (skip)
                ShowNotice("Capture turned off", $"Downloads from {host} will be left to the browser.",
                    "Undo", () => OnSkipHost(host, skip: false));
        });
    }

    /// <summary>
    /// Decides whether an extension may have the pairing token. Called on a listener thread,
    /// so the decision hops to the UI and the listener awaits the answer.
    /// </summary>
    private Task<string?> OnPairRequestedAsync(string origin)
    {
        // Already trusted: answer without bothering anyone. This is the path every launch
        // after the first takes.
        var generation = Pairing.ApprovalGeneration;
        if (Pairing.ApprovedToken(origin) is { } approvedToken)
            return Task.FromResult<string?>(approvedToken);

        var decision = new TaskCompletionSource<string?>();

        DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                decision.SetResult(await AskToPairAsync(origin, generation));
            }
            catch (Exception exception)
            {
                App.Log($"Pairing prompt failed: {exception}");
                decision.SetResult(null);
            }
        });

        return decision.Task;
    }

    private async Task<string?> AskToPairAsync(string origin, long generation)
    {
        var panel = new StackPanel { Spacing = 12 };

        panel.Children.Add(new TextBox
        {
            Text = origin,
            IsReadOnly = true,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
        });

        panel.Children.Add(new TextBlock
        {
            Text = "Allow this only if you just installed Stretto's extension. Once allowed it "
                 + "can queue downloads without asking again.",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 11.5,
            Opacity = 0.65,
        });

        var form = new MessageWindowForm("A browser extension is asking to send downloads to Stretto.",
            primaryText: "Allow", cancelText: "Deny", extraContent: panel, width: 460);

        if (await ShowInWindowAsync(form, "Browser integration") != ContentDialogResult.Primary)
            return null;

        var token = Pairing.ApproveAndGetToken(origin, generation);
        UpdateBrowserStatus();
        return token;
    }

    /// <summary>
    /// Decides what happens to a download the browser just handed over. Returning false
    /// means Stretto declined it, and the extension resumes the browser's own download.
    ///
    /// Called on a listener thread. Everything real happens on the UI thread, because the
    /// refresh arming below is UI state and reading it from here would be a race.
    /// </summary>
    private Task<bool> OnCaptureDecidedAsync(HandoffRequest request)
    {
        var decision = new TaskCompletionSource<bool>();

        DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                decision.SetResult(await DecideOnUiThreadAsync(request));
            }
            catch (Exception exception)
            {
                // Handing it back is the safe failure: the browser still gets its download.
                App.Log($"Capture decision failed: {exception}");
                decision.SetResult(false);
            }
        });

        return decision.Task;
    }

    /// <summary>
    /// A page's worth of links from the extension. Shown as one list in the Batch window
    /// when captures are confirmed, rather than a New Download window per link.
    /// </summary>
    private Task<int> OnBatchDecidedAsync(BatchHandoff batch)
    {
        var decision = new TaskCompletionSource<int>();

        DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                var urls = (IReadOnlyList<string>)batch.Urls;

                if (_settings.ConfirmCaptures)
                {
                    var dialog = new BatchDownloadDialog();
                    dialog.PrefillLinks(urls);
                    AppWindow.Show();

                    if (await ShowInWindowAsync(dialog, "Links from the browser", 500, 300) != ContentDialogResult.Primary)
                    {
                        decision.SetResult(0);
                        return;
                    }

                    urls = dialog.Urls;
                }

                ShowActive();
                foreach (var url in urls)
                {
                    _downloads.Add(url, _downloads.OptionsFromSettings() with
                    {
                        Referrer = batch.Referrer,
                        UserAgent = batch.UserAgent,
                        Cookie = batch.CookieFor(url),
                    });
                }

                decision.SetResult(urls.Count);
            }
            catch (Exception exception)
            {
                App.Log($"Batch capture failed: {exception}");
                decision.SetResult(0);
            }
        });

        return decision.Task;
    }

    private async Task<bool> DecideOnUiThreadAsync(HandoffRequest request)
    {
        // A title arrives only from the popup's "send", for a video the person picked by hand.
        // A download the browser started carries none and is never renamed. When there is one,
        // the name is chosen here so the dialog shows the name that will be used.
        if (!string.IsNullOrWhiteSpace(request.PageTitle) && Uri.TryCreate(request.Url, UriKind.Absolute, out var address))
        {
            var chosen = FileNaming.Choose(
                request.FileName, address, request.PageTitle, _settings.NameFrom, _settings.VideoTypes);

            request = request with { FileName = chosen, NamedFromTitle = chosen != request.FileName };
        }

        // A refresh in progress claims the next capture. Checked first: the user has just
        // gone and clicked the download button again specifically to produce this, so
        // asking them what to do with it would be asking a question they already answered.
        if (TakeRefreshTarget() is { } stale)
        {
            _downloads.RefreshLink(stale, request.Url, request.Cookie, request.Referrer, request.UserAgent);
            ShowActive();

            ShowNotice("Link refreshed", $"{stale.Name} picked up where it left off.",
                "Show", () => ShowProgressFor(stale));

            return true;
        }

        // Straight through when the user has asked not to be interrupted.
        if (!_settings.ConfirmCaptures)
        {
            var added = AddFromCapture(request, _settings.StartAutomatically);
            ShowActive();

            if (_settings.ShowProgressWindow && _settings.StartAutomatically)
                ShowProgressFor(added);

            return true;
        }

        return await ConfirmCaptureAsync(request);
    }

    // ═══════════════════════════════════════
    // ═══ Link refresh
    // ═══════════════════════════════════════

    private DownloadItem? _refreshTarget;
    private DateTime _refreshDeadline;

    /// <summary>
    /// How long a refresh stays armed. Long enough to find the page and click the button,
    /// short enough that a capture an hour later is not silently swallowed into some
    /// download the user has long forgotten about.
    /// </summary>
    private static readonly TimeSpan RefreshWindow = TimeSpan.FromMinutes(3);

    /// <summary>Consumes the armed refresh, if there is one and it has not lapsed.</summary>
    private DownloadItem? TakeRefreshTarget()
    {
        if (_refreshTarget is not { } target || DateTime.UtcNow > _refreshDeadline)
        {
            CancelRefresh();
            return null;
        }

        CancelRefresh();
        return target;
    }

    private void CancelRefresh()
    {
        _refreshTarget = null;
        RefreshBanner.IsOpen = false;
    }

    /// <summary>
    /// Sends the user back to the page the download came from, and waits for the browser to
    /// hand over whatever the download button produces this time.
    ///
    /// This is the only way to recover a download whose URL carried a one-time token: the
    /// token cannot be renewed from here, only reissued by the site.
    /// </summary>
    private void RefreshLink_Click(object sender, RoutedEventArgs e)
    {
        if (Selection().FirstOrDefault() is { } item)
            BeginRefresh(item);
    }

    /// <summary>
    /// Takes the item rather than reading the selection, because the notices below can be
    /// acted on later, by which point the selected row may be a different download.
    /// </summary>
    private void BeginRefresh(DownloadItem item)
    {
        // Only a web page counts: the referrer arrives from the browser, and the shell would
        // run a file: or UNC target rather than show it.
        if (!Uri.TryCreate(item.Referrer, UriKind.Absolute, out var referrer)
            || referrer.Scheme is not ("http" or "https"))
        {
            ShowNotice("Nothing to go back to",
                $"{item.Name} was not captured from a page, so there is no page to ask for a new link.",
                "Copy link", () => SetClipboard(item.Url));
            return;
        }

        // The new link arrives through the extension and nothing else, so a refresh with
        // the listener down would just sit there until it lapsed, looking like the site
        // refused rather than like Stretto was never listening.
        if (_handoff?.IsListening != true)
        {
            ShowNotice(
                "Browser monitoring is off",
                "A refreshed link comes in from the browser extension, so monitoring has to be on for this.",
                "Turn it on",
                () =>
                {
                    BrowserMonitoringToggle.IsOn = true;
                    BeginRefresh(item);
                });

            return;
        }

        // Same dead end by a different route: a host on the ignore list is never handed
        // over, so the click would produce a browser download and no capture at all.
        if (Uri.TryCreate(item.Url, UriKind.Absolute, out var address)
            && _settings.SkipHosts.Contains(address.Host, StringComparer.OrdinalIgnoreCase))
        {
            var host = address.Host;

            ShowNotice(
                $"{host} is on the ignore list",
                "Stretto does not take downloads from it, so it cannot pick up a refreshed link either.",
                "Capture it again",
                () =>
                {
                    _settings.SkipHosts.RemoveAll(h => string.Equals(h, host, StringComparison.OrdinalIgnoreCase));
                    _settings.Save();
                    BeginRefresh(item);
                });

            return;
        }

        _refreshTarget = item;
        _refreshDeadline = DateTime.UtcNow + RefreshWindow;

        RefreshBanner.Title = "Waiting for a new link";
        RefreshBanner.Message = $"Start the download for {item.Name} again on the page that just opened. "
                              + "Stretto will attach it to the existing file instead of starting over.";
        RefreshBanner.IsOpen = true;

        WebLinks.Open(referrer.AbsoluteUri);
    }

    private void RefreshBanner_Closed(InfoBar sender, InfoBarClosedEventArgs args) => _refreshTarget = null;

    /// <summary>
    /// One at a time: selecting
    /// several files in a page fires several captures at once. Without this they would
    /// all but the first fail to open and fall back to the browser.
    /// </summary>
    private readonly SemaphoreSlim _capturePrompt = new(1, 1);

    /// <summary>Shows the New Download dialog filled in from the capture.</summary>
    private async Task<bool> ConfirmCaptureAsync(HandoffRequest request)
    {
        await _capturePrompt.WaitAsync();
        try
        {
            return await PromptForCaptureAsync(request);
        }
        finally
        {
            _capturePrompt.Release();
        }
    }

    private async Task<bool> PromptForCaptureAsync(HandoffRequest request)
    {
        // Keep the decision in a separate movable window while leaving the main library
        // available. The browser request waits until this small prompt is answered.
        var promptWindow = new NewDownloadWindow(_settings, _downloads.DefaultDirectory, "Download captured");
        var dialog = promptWindow.Dialog;

        dialog.PrefillFromCapture(request.Url, request.FileName, request.Referrer, request.Size, request.Mime);
        promptWindow.Show();
        var outcome = await promptWindow.ClosedTask;

        switch (outcome)
        {
            case NewDownloadDialog.Outcome.StopCapturingHost when dialog.HostToIgnore is { } host:
                // Remember it, tell the extension, and let the browser finish the download
                // it had already started.
                if (!_settings.SkipHosts.Contains(host, StringComparer.OrdinalIgnoreCase))
                {
                    _settings.SkipHosts.Add(host);
                    _settings.Save();
                }

                ShowNotice("Capture turned off", $"Downloads from {host} will be left to the browser.",
                    "Undo", () =>
                    {
                        _settings.SkipHosts.Remove(host);
                        _settings.Save();
                    });

                return false;

            case NewDownloadDialog.Outcome.DownloadNow:
            case NewDownloadDialog.Outcome.DownloadLater:
                var startNow = outcome == NewDownloadDialog.Outcome.DownloadNow;

                // Declining here hands the file back to the browser, which is the right
                // answer: the user said they did not want Stretto fetching it twice.
                if (!await ConfirmNotDuplicateAsync(request.Url, dialog.FileNameOrNull ?? request.FileName))
                    return false;

                dialog.CommitAdvanced();

                var options = dialog.BuildOptions(StartsImmediately(startNow, dialog.Later));
                var added = AddFromCapture(request, options.StartNow, options);
                ShowActive();

                AfterAdd(added, startNow, dialog.Later);

                // Queued rather than awaited: the extension is holding its download paused
                // on this answer, and it should not wait on a second window.
                if (dialog.Later is NewDownloadDialog.LaterChoice.OpenQueues)
                    DispatcherQueue.TryEnqueue(async () => await ShowQueuesAsync());

                return true;

            default:
                return false;
        }
    }

    /// <summary>Builds the download from a capture, keeping the browser's request context.</summary>
    private DownloadItem AddFromCapture(HandoffRequest request, bool startNow, DownloadOptions? chosen = null)
        => _downloads.Add(request.Url, new DownloadOptions
        {
            // The dialog's folder and connection choices win when it was shown.
            Directory = chosen?.Directory,
            SortByType = chosen?.SortByType ?? _settings.SortByType,
            Connections = chosen?.Connections ?? _settings.DefaultConnections,
            FileName = chosen?.FileName ?? request.FileName,
            Cookie = request.Cookie,
            Referrer = request.Referrer,
            UserAgent = request.UserAgent,
            Headers = request.Headers,
            Mime = request.Mime,
            Username = chosen?.Username,
            Password = chosen?.Password,
            ProxyModeOverride = chosen?.ProxyModeOverride,
            ProxyHost = chosen?.ProxyHost,
            ProxyPort = chosen?.ProxyPort ?? 0,
            ProxyUsername = chosen?.ProxyUsername,
            ProxyPassword = chosen?.ProxyPassword,
            IsStream = request.IsStream,
            NamedFromTitle = request.NamedFromTitle,
            StartNow = startNow,
        });

    /// <summary>Raised on a listener thread like handoffs, so this hops to the UI too:
    /// the feed's collection is bound straight into the grabber dialog.</summary>
    private void OnMediaSeen(MediaReport report)
    {
        DispatcherQueue.TryEnqueue(() => _feed.Add(report));
    }

    private void BrowserMonitoring_Toggled(object sender, RoutedEventArgs e)
    {
        // Assigning IsOn while wiring up the window raises this too; the constructor starts
        // the listener itself once everything is in place.
        if (!_uiReady) return;

        _settings.BrowserMonitoring = BrowserMonitoringToggle.IsOn;
        _settings.Save();

        if (_settings.BrowserMonitoring)
        {
            StartHandoff();
        }
        else
        {
            _handoff?.Stop();
            _handoff = null;
            UpdateBrowserStatus();
        }
    }

    /// <summary>
    /// The Browser entry in the pane: a status dot and a word, with the full sentence
    /// and the switch in its flyout.
    /// </summary>
    private void UpdateBrowserStatus()
    {
        var listening = _handoff?.IsListening == true;
        var paired = Pairing.Approved.Any();

        BrowserStatusText.Text = DescribeBrowserLink();
        PairingButton.Visibility = listening ? Visibility.Visible : Visibility.Collapsed;

        (BrowserNavStatus.Text, var badge) = (listening, paired, BrowserMonitoringToggle.IsOn) switch
        {
            (true, true, _) => ("Connected", "SuccessDotInfoBadgeStyle"),
            (true, false, _) => ("Waiting for extension", "InformationalDotInfoBadgeStyle"),
            (false, _, true) => ("Port unavailable", "CriticalDotInfoBadgeStyle"),
            _ => ("Off", null),
        };

        BrowserBadge.Visibility = badge is null ? Visibility.Collapsed : Visibility.Visible;
        if (badge is not null)
            BrowserBadge.Style = (Style)Application.Current.Resources[badge];

        // The empty Transfers view offers to connect a browser only while there is none.
        if (_uiReady) UpdateEmptyState();
    }

    /// <summary>One line on the state of the browser link, for Settings and the wizard.</summary>
    private string DescribeBrowserLink()
    {
        if (!_settings.BrowserMonitoring)
            return "Off. Downloads stay with the browser.";

        if (_handoff?.IsListening != true)
            return $"Port {_settings.HandoffPort} is in use. Close the other application or pick another port below.";

        var paired = Pairing.Approved.Count();
        return paired switch
        {
            0 => $"Listening on port {_settings.HandoffPort}. No extension has paired yet.",
            1 => $"Listening on port {_settings.HandoffPort}. One extension paired.",
            _ => $"Listening on port {_settings.HandoffPort}. {paired} extensions paired.",
        };
    }

    private async void Pairing_Click(object sender, RoutedEventArgs e)
    {
        await ShowBrowserSetupAsync();
    }
}
