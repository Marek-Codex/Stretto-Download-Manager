using System.Collections.ObjectModel;

namespace Stretto.Core;

/// <summary>Owns the queue and drives <see cref="SegmentedDownloader"/> on its behalf.</summary>
public sealed partial class DownloadService
{
    private readonly AppSettings _settings;
    private readonly DownloadStore _store = new();
    private readonly StreamDownloader _streams = new(ToolBox.Resolve(Tool.Ffmpeg));
    private readonly FtpDownloader _ftp = new();
    private readonly BandwidthLimiter _bandwidthLimiter = new();

    private readonly SynchronizationContext? _context;
    private DownloadNetwork _network;

    public QueueService Queues { get; } = new();

    /// <summary>
    /// <paramref name="context"/> is where the service does its work: transfers report back
    /// to it, and state is only touched from it, so nothing here needs a lock. The app passes
    /// nothing and gets the UI thread's, which is current when the window builds the service.
    /// A host with no UI thread passes a <see cref="SerialSynchronizationContext"/> and makes
    /// its own calls to the service on that same context.
    /// </summary>
    public DownloadService(AppSettings settings, SynchronizationContext? context = null)
    {
        _settings = settings;
        _context = context ?? SynchronizationContext.Current;
        _network = CreateNetwork(settings);
        _bandwidthLimiter.SetLimit(settings.SpeedLimitBytesPerSecond);
    }

    /// <summary>
    /// Cap on downloads transferring at once. A batch of 100 files at 8 connections each
    /// would otherwise open 800 sockets to one host, which is how you get rate limited
    /// rather than how you go faster.
    /// </summary>
    public int MaxConcurrent => _settings.MaxConcurrentDownloads;

    /// <summary>
    /// Rebuilds the HTTP stack after the network settings change. The connect timeout lives
    /// on the handler, so it cannot be adjusted in place -- the client has to be replaced.
    /// In-flight downloads keep the old client until they finish, which is fine: the new
    /// settings are for the next transfer, not a reason to interrupt a running one.
    /// </summary>
    public void ApplySettings()
    {
        _network = CreateNetwork(_settings);
        _bandwidthLimiter.SetLimit(_settings.SpeedLimitBytesPerSecond);
        Pump();
    }

    public ObservableCollection<DownloadItem> Items { get; } = [];

    /// <summary>
    /// Raised on the UI thread when a download finishes, so the app can say so. The item is
    /// already in its final state by the time this runs.
    /// </summary>
    public event Action<DownloadItem>? Completed;

    /// <summary>Raised on the UI thread when a download gives up.</summary>
    public event Action<DownloadItem>? Failed;

    /// <summary>Shared with the video dialog so page scraping reuses the same connection pool.</summary>
    public HttpClient Http => _network.Http;
    public void SetSpeedLimit(long bytesPerSecond) => _bandwidthLimiter.SetLimit(bytesPerSecond);

    /// <summary>Caption for the progress window, matching XDM's "No speed limit".</summary>
    public string SpeedLimitLabel => _settings.SpeedLimitBytesPerSecond <= 0
        ? "No speed limit"
        : $"Limit {_settings.SpeedLimitBytesPerSecond / 1024:N0} KB/s";

    /// <summary>Where downloads land, from settings.</summary>
    public string DefaultDirectory => _settings.ResolvedDownloadFolder;

    /// <summary>
    /// Where a finished download in this category goes. A rule with no folder of its own
    /// gets a subfolder named after it, which is what the table shows as the default.
    /// </summary>
    public string DirectoryFor(string category)
    {
        if (CategoryRules.Find(category) is not { } rule)
            return DefaultDirectory;

        return rule.Folder.Length > 0 ? rule.Folder : Path.Combine(DefaultDirectory, rule.Name);
    }

    /// <summary>
    /// Options seeded from settings, for callers with no reason to differ. Without this the
    /// record's own defaults win and the General settings silently do nothing.
    /// </summary>
    public DownloadOptions OptionsFromSettings(bool? startNow = null) => new()
    {
        SortByType = _settings.SortByType,
        Connections = _settings.DefaultConnections,
        StartNow = startNow ?? _settings.StartAutomatically,
    };

    /// <summary>
    /// Points an existing download at a freshly issued link and starts it again.
    ///
    /// The partial file and its sidecar are left alone on purpose: the engine probes the
    /// new URL and only resumes when the length and validator still match, so a genuinely
    /// different file restarts by itself rather than being appended to.
    /// </summary>
    public void RefreshLink(DownloadItem item, string url, string? cookie, string? referrer, string? userAgent)
    {
        item.Url = url;
        item.Cookie = cookie ?? item.Cookie;
        item.Referrer = referrer ?? item.Referrer;
        item.UserAgent = userAgent ?? item.UserAgent;

        // Pin the name to what is already on disk. Without this a reissued link that
        // suggests a different filename would resolve to a different path, and the partial
        // file and its sidecar would be quietly abandoned for a fresh start from zero --
        // the exact opposite of what a refresh is for.
        item.PreferredFileName ??= item.Name;

        item.Error = null;
        item.FailureStatus = null;
        item.Status = DownloadStatus.Queued;

        Pump();
    }

    public DownloadItem Add(string url, DownloadOptions? options = null)
    {
        options ??= OptionsFromSettings();

        var item = new DownloadItem
        {
            Url = url,
            TargetDirectory = options.Directory ?? DefaultDirectory,
            SortByType = options.SortByType && options.Directory is null,
            PreferredFileName = options.FileName,
            Connections = options.Connections,
            Cookie = options.Cookie,
            IsStream = options.IsStream,
            IsSiteVideo = options.IsSiteVideo,
            FormatId = options.FormatId,
            Referrer = options.Referrer,
            UserAgent = options.UserAgent,
            Headers = options.Headers,
            Mime = options.Mime,
            Username = options.Username,
            Password = options.Password,
            ProxyModeOverride = options.ProxyModeOverride,
            ProxyProtocolOverride = options.ProxyProtocolOverride,
            ProxyHost = options.ProxyHost,
            ProxyPort = options.ProxyPort,
            ProxyUsername = options.ProxyUsername,
            ProxyPassword = options.ProxyPassword,
            // Placeholder until the probe returns the server's own filename.
            Name = options.FileName ?? GuessName(url),
        };

        Items.Insert(0, item);

        if (options.StartNow)
            item.Status = DownloadStatus.Queued;

        Pump();
        return item;
    }

    public IReadOnlyList<DownloadItem> AddMany(IEnumerable<string> urls, DownloadOptions? options = null) =>
        [.. urls.Select(url => Add(url, options))];

    /// <summary>Rebuilds the list from the last run. Nothing starts on its own: an app that
    /// silently resumes six downloads the moment it opens is a nasty surprise.</summary>
    public void Restore()
    {
        foreach (var entry in _store.Load().OrderBy(e => e.Added))
            Items.Insert(0, FromEntry(entry));
    }

    /// <summary>
    /// Adds downloads from an exported list, skipping any address already in the list.
    /// Everything lands paused: importing a list should never set forty transfers running.
    /// </summary>
    public int Import(IEnumerable<DownloadStore.Entry> entries)
    {
        var known = Items.Select(i => i.Url).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = 0;

        foreach (var entry in entries.OrderBy(e => e.Added))
        {
            if (string.IsNullOrWhiteSpace(entry.Url) || !known.Add(entry.Url))
                continue;

            var item = FromEntry(entry);

            // An exported "complete" is only complete on the machine it came from.
            if (item.Status is DownloadStatus.Complete && !File.Exists(item.ResolvedPath ?? ""))
            {
                item.Status = DownloadStatus.Paused;
                item.BytesCompleted = 0;
            }

            Items.Insert(0, item);
            added++;
        }

        if (added > 0)
            Save();

        return added;
    }

    /// <summary>One place where a stored entry becomes a download, used by both paths.</summary>
    private static DownloadItem FromEntry(DownloadStore.Entry entry) => new()
    {
        Id = string.IsNullOrEmpty(entry.Id) ? Guid.NewGuid().ToString("n") : entry.Id,
        Url = entry.Url,
        Name = entry.Name,
        TargetDirectory = entry.TargetDirectory,
        FilePath = entry.FilePath,
        PreferredFileName = entry.PreferredFileName,
        SortByType = entry.SortByType,
        IsStream = entry.IsStream,
        Connections = entry.Connections,
        ProxyModeOverride = entry.ProxyModeOverride,
        ProxyProtocolOverride = entry.ProxyProtocolOverride,
        ProxyHost = entry.ProxyHost,
        ProxyPort = entry.ProxyPort,
        ProxyUsername = entry.ProxyUsername,
        // ProxyPassword is memory-only and never persisted.
        RestoredAt = entry.Added,
        CompletedAt = entry.CompletedAt,
        Status = entry.Status,
        TotalBytes = entry.TotalBytes,
        BytesCompleted = entry.BytesCompleted,
        Error = entry.Status is DownloadStatus.Failed ? entry.Error : null,
        // Filtered on the way in as well as out: an imported list may have been written by
        // someone else, and must not be able to plant an Authorization header on a download.
        Headers = DownloadStore.KeepableHeaders(entry.Headers),
    };

    /// <summary>Writes the visible list to a file the user picked.</summary>
    public void Export(string path) => DownloadStore.ExportTo(path, Items);

    public void Save() => _store.Save(Items);

    /// <summary>Gaps between automatic retries: long enough for a dropped connection or a
    /// busy server to recover, short enough that nobody is left waiting.</summary>
    private static readonly TimeSpan[] RetryDelays =
        [TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(10)];

    /// <summary>
    /// Tries a failed download again later, for failures that can plausibly fix themselves:
    /// timeouts, dropped connections, 5xx. Not for a refused or expired link (4xx) or a
    /// sign-in page, which fail the same way every time and need a person.
    /// </summary>
    private void ScheduleRetry(DownloadItem item, Exception exception) =>
        _ = InContext(() => RetryAfterDelayAsync(item, exception));

    /// <summary>
    /// Starts <paramref name="start"/> with the service's context current. An async method
    /// takes the context it will resume on from whatever is current at each await, so this is
    /// what makes every continuation after the first await land back on it. When the caller is
    /// already on that context, which is always the case in the app, this changes nothing.
    /// </summary>
    private Task InContext(Func<Task> start)
    {
        var caller = SynchronizationContext.Current;
        if (_context is null || caller == _context)
            return start();

        SynchronizationContext.SetSynchronizationContext(_context);
        try { return start(); }
        finally { SynchronizationContext.SetSynchronizationContext(caller); }
    }

    /// <summary>
    /// A Task, not async void: nothing awaits it, but an exception after the delay is caught
    /// and logged here instead of escaping onto the thread pool, where it ends the process.
    /// </summary>
    private async Task RetryAfterDelayAsync(DownloadItem item, Exception exception)
    {
        try
        {
            if (!_settings.AutoRetry || item.LooksExpired || RetryPolicy.IsPermanent(exception)
                || item.AutoRetries >= RetryDelays.Length)
                return;

            var delay = RetryDelays[item.AutoRetries];
            item.AutoRetries++;
            var wait = delay.TotalMinutes >= 1 ? $"{delay.TotalMinutes:0} min" : $"{delay.TotalSeconds:0} s";
            item.Error = $"{exception.Message} Retrying in {wait} ({item.AutoRetries} of {RetryDelays.Length}).";

            await Task.Delay(delay).ConfigureAwait(true);

            // Only if nothing else happened meanwhile: removed, restarted or resumed by hand.
            if (item.Status == DownloadStatus.Failed && Items.Contains(item))
            {
                item.Error = null;
                item.Status = DownloadStatus.Queued;
                Pump();
            }
        }
        catch (Exception retryFailure)
        {
            AppLog.Write($"Automatic retry of {item.Url} failed: {retryFailure}");
        }
    }

    /// <summary>Starts every item that is paused or waiting.</summary>
    public void ResumeAll()
    {
        foreach (var item in Items.Where(i => i.CanResume).ToList())
            Start(item);
    }

    /// <summary>Stops everything in flight, keeping each resume point.</summary>
    public void PauseAll()
    {
        foreach (var item in Items.Where(i => i.CanPause).ToList())
            Pause(item);
    }

    /// <summary>Marks an item as wanting to run. Whether it starts now depends on free slots.</summary>
    public void Start(DownloadItem item)
    {
        item.AutoRetries = 0;
        item.Error = null;
        item.Status = DownloadStatus.Queued;
        Pump();
    }

    /// <summary>Launches queued work up to the concurrency cap, oldest first.</summary>
    private void Pump()
    {
        var running = Items.Where(i => i.IsRunning).ToList();

        var slots = MaxConcurrent - running.Count;
        if (slots <= 0)
            return;

        // Prioritise puts running queues first in their arranged order, then everything
        // unqueued oldest-first.
        var eligible = Queues.Prioritise(Items.Where(i =>
            i.Status == DownloadStatus.Queued && !i.IsRunning));

        foreach (var item in eligible)
        {
            // Re-checked per item rather than filtered up front, because starting one
            // download from a queue makes the rest of that queue ineligible.
            if (!Queues.CanRun(item, running))
                continue;

            Launch(item);
            running.Add(item);

            if (--slots == 0)
                return;
        }
    }

    private void Launch(DownloadItem item)
    {
        item.IsRunning = true;
        item.Cancellation = new CancellationTokenSource();

        // Fire and forget by design: the item carries all the state the UI binds to,
        // and every outcome is written back onto it below.
        _ = InContext(() => RunAsync(item, item.Cancellation.Token));
    }

    private async Task RunAsync(DownloadItem item, CancellationToken ct)
    {
        // A per-download proxy override needs its own handler: proxy lives on
        // SocketsHttpHandler and cannot vary per request. Global downloads share
        // _network; overrides get a transient client that is disposed afterwards.
        // Either way the probe and transfer share one client, so they agree on routing.
        var (network, owned) = NetworkFor(item);

        // Progress<T> captures the service's SynchronizationContext, so handlers land back on
        // it (the UI thread, in the app) without any dispatcher juggling at the call sites.
        var progress = new Progress<ProgressSnapshot>(p =>
        {
            item.BytesCompleted = p.BytesCompleted;
            item.TotalBytes ??= p.TotalBytes;
            item.BytesPerSecond = p.BytesPerSecond;
            if (p.FractionOverride is { } fraction) item.ProgressOverride = fraction;
            if (item.Status == DownloadStatus.Queued)
                item.Status = DownloadStatus.Downloading;
        });

        try
        {
            // A playlist pasted into New download, or captured from the browser as a plain
            // download, used to go through the file engine and save the playlist text.
            var viaFfmpeg = item.IsStream || LooksLikePlaylist(item);
            StreamDownloader.Log ??= AppLog.Write;

            // Which engine took it, and what kind of address. Host and file ending only:
            // the rest of a media URL is usually a signed token.
            if (Uri.TryCreate(item.Url, UriKind.Absolute, out var logged))
                AppLog.Write($"Download start: {(viaFfmpeg ? "stream (ffmpeg)" : item.IsSiteVideo ? "site video (yt-dlp)" : "file")} "
                    + $"{logged.Host} ...{Path.GetExtension(logged.AbsolutePath)} mime={item.Mime ?? "-"}");

            if (viaFfmpeg)
            {
                await RunStreamAsync(item, progress, ct).ConfigureAwait(true);
                return;
            }

            if (item.IsSiteVideo)
            {
                await RunSiteVideoAsync(item, progress, ct).ConfigureAwait(true);
                return;
            }

            // FTP has its own client: no probe, no segments, and resume comes from REST
            // rather than from a sidecar.
            if (Uri.TryCreate(item.Url, UriKind.Absolute, out var address) && FtpDownloader.Handles(address))
            {
                await RunFtpAsync(item, progress, ct).ConfigureAwait(true);
                return;
            }

            // An extra probe ahead of the download costs one ranged one-byte request and
            // buys a correct filename and size in the list immediately, instead of after
            // the transfer finishes. It also decides the folder when sorting by type,
            // which cannot be known until the real filename is in hand.
            var probe = BuildRequest(item, item.TargetDirectory);
            var info = await HttpProbe.ProbeAsync(network.Http, probe, ct).ConfigureAwait(true);
            GuardAgainstSignInPage(item, info);

            item.Name = item.PreferredFileName ?? info.SuggestedFileName;
            item.TotalBytes = info.TotalLength;

            if (item.SortByType)
                item.TargetDirectory = DirectoryFor(item.Category);

            item.Status = DownloadStatus.Downloading;

            var request = BuildRequest(item, item.TargetDirectory);
            var result = await network.Downloader.DownloadAsync(request, progress, ct, info).ConfigureAwait(true);

            item.FilePath = result.FilePath;
            item.Name = Path.GetFileName(result.FilePath);
            item.TotalBytes = result.BytesWritten;
            item.BytesCompleted = result.BytesWritten;
            item.BytesPerSecond = 0;
            item.CompletedAt = DateTimeOffset.Now;
            item.AutoRetries = 0;
            item.Status = DownloadStatus.Complete;
            Completed?.Invoke(item);
        }
        catch (OperationCanceledException)
        {
            // A cancel is a pause: the engine has already written a resume point.
            item.BytesPerSecond = 0;
            item.Status = DownloadStatus.Paused;
        }
        catch (Exception exception)
        {
            item.BytesPerSecond = 0;
            item.Error = exception.Message;

            // Kept so the UI can tell "this link has expired" from "this download broke",
            // which are the same message to the user but different problems.
            item.FailureStatus = exception is HttpRequestException { StatusCode: { } status }
                ? (int)status
                : null;

            item.Status = DownloadStatus.Failed;
            AppLog.Write($"Download failed [{item.Url}]: {exception}");
            Failed?.Invoke(item);
            ScheduleRetry(item, exception);
        }
        finally
        {
            if (owned)
                network.Http.Dispose();

            // Each run gets a fresh source, so the finished one is released here. Cleared
            // as well as disposed: Pause and Remove call Cancel on it, which would throw
            // on a disposed source.
            item.Cancellation?.Dispose();
            item.Cancellation = null;

            // ConfigureAwait(true) above keeps this on the UI thread, so handing the freed
            // slot to the next queued item needs no marshalling.
            item.IsRunning = false;
            Pump();
        }
    }

    private DownloadRequest BuildRequest(DownloadItem item, string directory)
    {
        // Referer and User-Agent matter more than they look: plenty of hosts serve a
        // different response, or refuse outright, when a download is re-requested without
        // the context the browser had.
        //
        // The browser's own headers go in first, then the two that have dedicated fields
        // and so stay authoritative. The extension already strips Referer, User-Agent and
        // Cookie out of what it sends, but a download refreshed from the list can carry a
        // Referer that is newer than the capture, and case-insensitive keys keep a
        // replayed header from ending up alongside the same header set here.
        var headers = item.Headers is { Count: > 0 }
            ? new Dictionary<string, string>(item.Headers, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Never replay the slice the player asked for. The engine sets its own ranges, and a
        // captured Range (older extensions sent one) fetched only the player's first piece:
        // a few seconds of a video served as one file in byte ranges.
        foreach (var name in new[] { "Range", "If-Range", "If-Match", "If-None-Match", "If-Modified-Since", "If-Unmodified-Since" })
            headers.Remove(name);

        if (!string.IsNullOrEmpty(item.Referrer)) headers["Referer"] = item.Referrer;

        // The browser's own User-Agent wins when we have it; otherwise send the configured
        // one, because some hosts serve a different file, or refuse, without a plausible UA.
        headers["User-Agent"] = string.IsNullOrEmpty(item.UserAgent) ? _settings.UserAgent : item.UserAgent;

        return new DownloadRequest
        {
            Url = new Uri(item.Url),
            TargetDirectory = directory,
            FileName = item.PreferredFileName,
            // Capped here rather than when the download is added, so the limit also applies
            // to everything already in the list and to anything a refresh points elsewhere.
            Connections = _settings.ConnectionsFor(item.Url, item.Connections),
            Adaptive = _settings.AdaptiveConnections,
            BandwidthLimiter = _bandwidthLimiter,
            Headers = headers,
            Cookie = item.Cookie,
            Overwrite = _settings.OverwriteExisting,
            UseServerTimestamp = _settings.UseServerTimestamp,
        };
    }

    public void Pause(DownloadItem item)
    {
        if (!item.CanPause) return;

        if (item.IsRunning)
            item.Cancellation?.Cancel();
        else
            item.Status = DownloadStatus.Paused;   // still waiting for a slot; nothing to cancel
    }

    public void Resume(DownloadItem item)
    {
        if (!item.CanResume) return;
        Start(item);
    }

    /// <summary>
    /// Throws away everything fetched so far and downloads again from zero.
    ///
    /// Awaits the worker rather than just cancelling it: deleting the file while a segment
    /// still holds a handle on it either fails or, worse, succeeds and leaves the next
    /// attempt writing into a file nothing else knows about.
    /// </summary>
    public async Task RestartAsync(DownloadItem item)
    {
        if (item.IsRunning)
        {
            item.Cancellation?.Cancel();

            while (item.IsRunning)
                await Task.Delay(50).ConfigureAwait(true);
        }

        DeleteFilesFor(item);

        item.FilePath = null;
        item.BytesCompleted = 0;
        item.BytesPerSecond = 0;
        item.ProgressOverride = null;
        item.Error = null;
        item.FailureStatus = null;
        item.Status = DownloadStatus.Queued;

        Pump();
    }

    public void Remove(DownloadItem item, bool deleteFiles = false)
    {
        item.Cancellation?.Cancel();
        Items.Remove(item);

        if (deleteFiles)
            DeleteFilesFor(item);

        Pump();
    }

    /// <summary>Removes the downloaded file and its resume sidecar, if they are there.</summary>
    private static void DeleteFilesFor(DownloadItem item)
    {
        if (item.ResolvedPath is not { } path)
            return;

        foreach (var candidate in new[] { path, DownloadState.SidecarPathFor(path) })
        {
            try
            {
                if (File.Exists(candidate))
                    File.Delete(candidate);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                AppLog.Write($"Could not delete {candidate}: {exception.Message}");
            }
        }
    }

    public void RemoveCompleted()
    {
        foreach (var done in Items.Where(i => i.Status == DownloadStatus.Complete).ToList())
            Items.Remove(done);
    }

    private static string GuessName(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return url;

        var name = Path.GetFileName(Uri.UnescapeDataString(uri.AbsolutePath));
        return string.IsNullOrWhiteSpace(name) ? uri.Host : name;
    }
}
