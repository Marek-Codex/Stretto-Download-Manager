using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Stretto.Core;

public enum DownloadStatus { Pending, Queued, Downloading, Paused, Complete, Failed }

/// <summary>One row in the list. Also the view model -- the app is small enough that a
/// separate model/VM split would be ceremony rather than clarity.</summary>
public sealed class DownloadItem : INotifyPropertyChanged
{
    private long _bytesCompleted;
    private long? _totalBytes;
    private double _bytesPerSecond;
    private DownloadStatus _status = DownloadStatus.Pending;
    private string? _error;

    /// <summary>Stable identity, so a queue can reference an item across restarts.</summary>
    public string Id { get; init; } = Guid.NewGuid().ToString("n");

    // Settable for the same reason as the request context below: a link refresh replaces
    // the address on an existing download rather than starting a second one.
    public required string Url { get; set; }
    /// <summary>Settable because "sort by file type" can only pick the folder once the
    /// probe has told us the real filename.</summary>
    public required string TargetDirectory { get; set; }
    public int Connections { get; init; } = 8;
    private readonly DateTimeOffset _created = DateTimeOffset.Now;

    /// <summary>When restored from disk, the original add time is carried over so the list
    /// keeps its order across restarts.</summary>
    public DateTimeOffset? RestoredAt { get; init; }

    public DateTimeOffset Added => RestoredAt ?? _created;

    /// <summary>When it finished. Older lists never recorded this, so they fall back to
    /// the add time.</summary>
    public DateTimeOffset? CompletedAt
    {
        get => _completedAt;
        set { _completedAt = value; Notify(); Notify(nameof(DetailLine)); }
    }
    private DateTimeOffset? _completedAt;

    /// <summary>Automatic retries used since the last success or manual start.</summary>
    public int AutoRetries { get; set; }

    /// <summary>Route into a per-category subfolder once the real filename is known.</summary>
    // Settable because "Save as" picks an explicit path, which has to win over sorting.
    public bool SortByType { get; set; }

    /// <summary>User-supplied name that wins over whatever the server suggests.</summary>
    // Settable so a link refresh can pin it: see DownloadService.RefreshLink.
    public string? PreferredFileName { get; set; }

    /// <summary>The name came from a page title, so a clash is told apart with a tag.</summary>
    public bool NamedFromTitle { get; set; }

    /// <summary>Cookie header forwarded from the browser, so an authenticated URL can be
    /// re-requested outside it. Held in memory only and never written to the list file.</summary>
    public string? Cookie { get; set; }

    // Settable rather than init-only: a link refresh replaces the request context on an
    // existing download instead of creating a second one.
    public string? Referrer { get; set; }
    public string? UserAgent { get; set; }

    /// <summary>
    /// The rest of the browser's request headers, for the media the extension watched go
    /// out. This is what makes a URL the site gates behind an Authorization header, a
    /// CSRF token or a signature fetchable outside the browser at all.
    ///
    /// In memory only, like the cookie: a session token has no business in a list file on
    /// disk, so a download restored from the list goes back to Cookie, Referer and
    /// User-Agent alone. Anything replayable is a header the browser could have set, and
    /// nothing here is a credential the user did not already hand the site.
    /// </summary>
    public Dictionary<string, string>? Headers { get; set; }

    /// <summary>
    /// The Content-Type the browser saw, for a download whose address carries no
    /// extension. Only used until the probe returns the server's own filename, after
    /// which the name has the type in it. In memory, and not a secret either way: it is a
    /// response header the browser already had.
    /// </summary>
    public string? Mime { get; set; }

    /// <summary>The HTTP status the last attempt failed on, when it failed on one.</summary>
    public int? FailureStatus { get; set; }

    /// <summary>
    /// Whether the failure looks like an expired link rather than a broken one.
    ///
    /// Tokenised download URLs go stale and then answer 400 or 403 rather than anything
    /// that names the real problem, so this is a guess -- but it is the right guess often
    /// enough to be worth offering, and refreshing a healthy link costs nothing.
    /// </summary>
    public bool LooksExpired => FailureStatus is 400 or 401 or 403 or 404 or 410;

    /// <summary>A refresh needs a page to go back to.</summary>
    public bool CanRefresh => Status is DownloadStatus.Failed or DownloadStatus.Paused
        && !string.IsNullOrEmpty(Referrer);

    /// <summary>A video page handed to yt-dlp rather than fetched directly.</summary>
    public bool IsSiteVideo { get; init; }

    /// <summary>Which yt-dlp format to ask for. Null lets yt-dlp choose.</summary>
    public string? FormatId { get; init; }

    /// <summary>HTTP credentials, when the site asks for them.</summary>
    public string? Username { get; init; }
    public string? Password { get; init; }

    /// <summary>
    /// Per-download proxy override. Null is "use the global settings". Set for direct or
    /// manual proxying of this download only; the password lives in memory and is never
    /// written to the download list file.
    /// </summary>
    public ProxyMode? ProxyModeOverride { get; set; }
    public ProxyProtocol? ProxyProtocolOverride { get; set; }
    public string? ProxyHost { get; set; }
    public int ProxyPort { get; set; }
    public string? ProxyUsername { get; set; }
    public string? ProxyPassword { get; set; }

    /// <summary>Human-readable proxy routing, without secrets. For Properties.</summary>
    public string ProxyDescription => ProxyModeOverride switch
    {
        null => "Global settings",
        ProxyMode.None => "Direct (no proxy)",
        _ => string.IsNullOrWhiteSpace(ProxyHost) ? "Manual (incomplete)"
            : $"{ProxyProtocolOverride ?? ProxyProtocol.Http} via {ProxyHost}:{ProxyPort}",
    };

    /// <summary>An HLS or DASH playlist, pulled by ffmpeg rather than by byte ranges.</summary>
    public bool IsStream { get; init; }

    private double? _progressOverride;

    /// <summary>Progress a stream reports by timeline position, since it has no known size.</summary>
    public double? ProgressOverride
    {
        get => _progressOverride;
        set
        {
            if (_progressOverride == value) return;
            _progressOverride = value;
            Notify(); Notify(nameof(Progress)); Notify(nameof(PercentText)); Notify(nameof(DetailLine));
        }
    }

    private string _name = "";
    public string Name
    {
        get => _name;
        set
        {
            if (_name == value) return;
            _name = value;
            Notify(); Notify(nameof(Category)); Notify(nameof(DetailLine));
        }
    }

    public string? FilePath { get; set; }

    /// <summary>Where the bytes are, or will be. A part-finished download has no FilePath
    /// yet but its data is already on disk under the name the probe resolved.</summary>
    public string? ResolvedPath => FilePath
        ?? (Name.Length > 0 ? Path.Combine(TargetDirectory, Name) : null);

    /// <summary>Cancels the in-flight download. Pausing is just cancelling: the engine
    /// leaves a resume point behind, so resume is a fresh call with the same arguments.</summary>
    public CancellationTokenSource? Cancellation { get; set; }

    /// <summary>True while a transfer is actually in flight. Distinguishes "queued and
    /// running" from "queued, waiting for a free slot", which look the same to the user.</summary>
    public bool IsRunning { get; set; }

    public long BytesCompleted
    {
        get => _bytesCompleted;
        set
        {
            if (_bytesCompleted == value) return;
            _bytesCompleted = value;
            Notify(); Notify(nameof(Progress)); Notify(nameof(PercentText)); Notify(nameof(SizeText)); Notify(nameof(DetailLine)); Notify(nameof(ShowsProgress));
        }
    }

    public long? TotalBytes
    {
        get => _totalBytes;
        set
        {
            if (_totalBytes == value) return;
            _totalBytes = value;
            Notify(); Notify(nameof(Progress)); Notify(nameof(PercentText)); Notify(nameof(SizeText)); Notify(nameof(DetailLine)); Notify(nameof(ShowsProgress));
        }
    }

    public double BytesPerSecond
    {
        get => _bytesPerSecond;
        set { if (Math.Abs(_bytesPerSecond - value) < 1) return; _bytesPerSecond = value; Notify(); Notify(nameof(StatusText)); Notify(nameof(DetailLine)); }
    }

    public DownloadStatus Status
    {
        get => _status;
        set
        {
            if (_status == value) return;
            _status = value;
            Notify(); Notify(nameof(StatusText));
            Notify(nameof(IsActive)); Notify(nameof(CanPause)); Notify(nameof(CanResume));
            Notify(nameof(DetailLine)); Notify(nameof(ShowsProgress));
            Notify(nameof(RowActionLabel));
        }
    }

    public string? Error
    {
        get => _error;
        set { _error = value; Notify(); Notify(nameof(StatusText)); Notify(nameof(DetailLine)); }
    }

    public double Progress => ProgressOverride is { } fraction
        ? fraction * 100
        : TotalBytes is > 0 ? BytesCompleted * 100d / TotalBytes.Value : 0;

    /// <summary>
    /// Everything that is not finished, including failures. Leaving Failed out meant a
    /// download that errored vanished from the UI entirely: Active excluded it, Complete
    /// only shows finished work, and the categories are subviews of Complete. It stays in
    /// Active so the error is visible and Retry is reachable.
    /// </summary>
    public bool IsActive => Status is DownloadStatus.Pending or DownloadStatus.Queued
        or DownloadStatus.Downloading or DownloadStatus.Paused or DownloadStatus.Failed;
    public bool CanPause => Status is DownloadStatus.Downloading or DownloadStatus.Queued;
    public bool CanResume => Status is DownloadStatus.Pending or DownloadStatus.Paused or DownloadStatus.Failed;

    public string SizeText => TotalBytes is > 0 ? FormatBytes(TotalBytes.Value) : "—";

    public string PercentText => ProgressOverride is not null || TotalBytes is > 0 ? $"{Progress:F0}%" : "—";

    public string StatusText => Status switch
    {
        DownloadStatus.Pending => "Waiting",
        DownloadStatus.Queued => "Queued",
        DownloadStatus.Downloading => BytesPerSecond > 1 ? $"{FormatBytes((long)BytesPerSecond)}/s" : "Connecting",
        DownloadStatus.Paused => "Paused",
        DownloadStatus.Complete => "Complete",
        DownloadStatus.Failed => Error is null ? "Failed" : $"Failed — {Error}",
        _ => "",
    };

    /// <summary>The state as a plain verb. The list's StatusText shows live speed instead,
    /// which reads wrong next to a byte count.</summary>
    public string StatusLabel => Status switch
    {
        DownloadStatus.Pending => "Waiting",
        DownloadStatus.Queued => "Queued",
        DownloadStatus.Downloading => "Downloading",
        DownloadStatus.Paused => "Paused",
        DownloadStatus.Complete => "Completed",
        DownloadStatus.Failed => "Failed",
        _ => "",
    };

    /// <summary>
    /// The row's one line of detail: everything the old status, size and date columns said,
    /// in the order it matters for the state the download is in. Time left lives here too,
    /// so it can be read without opening a window.
    /// </summary>
    public string DetailLine
    {
        get
        {
            var size = TotalBytes is > 0
                ? $"{FormatBytes(BytesCompleted)} of {FormatBytes(TotalBytes.Value)}"
                : BytesCompleted > 0 ? FormatBytes(BytesCompleted) : null;

            return Status switch
            {
                DownloadStatus.Downloading => Join(
                    size,
                    BytesPerSecond > 1 ? $"{FormatBytes((long)BytesPerSecond)}/s" : "Connecting",
                    TimeLeft is { } left ? $"{FormatDuration(left)} left" : null),
                DownloadStatus.Paused => Join($"Paused at {PercentText}", size),
                DownloadStatus.Queued => Join("Queued", size),
                DownloadStatus.Pending => Join("Waiting to start", size),
                DownloadStatus.Failed => Error is null ? "Failed" : $"Failed: {Error}",
                DownloadStatus.Complete => Join(
                    TotalBytes is > 0 ? FormatBytes(TotalBytes.Value) : null,
                    Category,
                    $"finished {FormatWhen(CompletedAt ?? Added)}"),
                _ => "",
            };

            static string Join(params string?[] parts) =>
                string.Join("  ·  ", parts.Where(p => !string.IsNullOrEmpty(p)));
        }
    }

    /// <summary>Remaining time at the current rate, when both are known.</summary>
    public TimeSpan? TimeLeft
    {
        get
        {
            if (Status != DownloadStatus.Downloading || BytesPerSecond < 1 || TotalBytes is not > 0)
                return null;

            var remaining = TotalBytes.Value - BytesCompleted;
            return remaining <= 0 ? TimeSpan.Zero : TimeSpan.FromSeconds(remaining / BytesPerSecond);
        }
    }

    /// <summary>
    /// A bar only where it says something: while bytes are moving, or once some have
    /// arrived. A finished file or an empty track just read as a stray divider.
    /// </summary>
    public bool ShowsProgress =>
        Status != DownloadStatus.Complete && (Status == DownloadStatus.Downloading || Progress > 0);

    public string RowActionLabel => Status switch
    {
        DownloadStatus.Downloading or DownloadStatus.Queued => "Pause",
        DownloadStatus.Failed => "Retry",
        DownloadStatus.Complete => "Show in folder",
        _ => "Resume",
    };

    public static string FormatDuration(TimeSpan span) =>
        span.TotalHours >= 1 ? $"{(int)span.TotalHours} h {span.Minutes} min"
        : span.TotalMinutes >= 1 ? $"{(int)span.TotalMinutes} min"
        : $"{Math.Max(1, (int)span.TotalSeconds)} s";

    private static string FormatWhen(DateTimeOffset when)
    {
        var local = when.LocalDateTime;
        return local.Date == DateTime.Today ? local.ToString("HH:mm")
            : local.Date == DateTime.Today.AddDays(-1) ? $"yesterday {local:HH:mm}"
            : local.ToString("d MMM");
    }

    /// <summary>
    /// The category this file files under, by name rather than by enum: the table in
    /// Settings is editable, so the set of categories is data now and not a closed list.
    /// </summary>
    public string Category => CategoryRules.Classify(Name, Mime);

    public static string FormatBytes(long value)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double size = value;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1) { size /= 1024; unit++; }
        return unit == 0 ? $"{size:F0} {units[unit]}" : $"{size:F1} {units[unit]}";
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Notify([CallerMemberName] string? property = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
}
