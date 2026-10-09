namespace Stretto.Core;

/// <summary>Manual SOCKS5 hop for a connection created by the HTTP transport.</summary>
public sealed record Socks5ProxyOptions(string Host, int Port, string? Username = null, string? Password = null);

/// <summary>What the caller wants downloaded. Headers/cookies come from the browser handoff.</summary>
public sealed class DownloadRequest
{
    public required Uri Url { get; init; }

    /// <summary>Directory the file lands in. The name is taken from the server unless <see cref="FileName"/> is set.</summary>
    public required string TargetDirectory { get; init; }

    public string? FileName { get; init; }

    /// <summary>Extra request headers (Referer, User-Agent, Authorization) forwarded from the browser.</summary>
    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>();

    /// <summary>Raw Cookie header value, so authenticated URLs can be re-requested outside the browser.</summary>
    public string? Cookie { get; init; }

    /// <summary>Desired parallel connections. Clamped, and ignored when the server refuses ranges.
    /// With <see cref="Adaptive"/> it is the most the download may grow to, not the number it
    /// starts with.</summary>
    public int Connections { get; init; } = 8;

    /// <summary>
    /// Start with a few connections and add more only while the total speed keeps improving,
    /// up to <see cref="Connections"/>. A fixed fan-out is too many for a site that refuses
    /// parallel ranges and too few for one that throttles each connection; measuring settles
    /// it per download. A speed limit needs no special case: growth stops once the limit is what holds the speed.
    /// </summary>
    public bool Adaptive { get; init; }
    public IBandwidthLimiter? BandwidthLimiter { get; init; }

    /// <summary>False keeps an existing file and writes alongside it as "name (2).ext".
    /// A part-finished download is resumed rather than renamed, since its sidecar is the
    /// proof that the bytes on disk belong to this transfer.</summary>
    public bool Overwrite { get; init; } = true;

    /// <summary>Stamp the finished file with the server's Last-Modified date.</summary>
    public bool UseServerTimestamp { get; init; }
}

/// <summary>What a probe learned about the remote file before any bytes are committed.</summary>
public sealed record RemoteFileInfo
{
    public required Uri FinalUrl { get; init; }
    public long? TotalLength { get; init; }
    public bool SupportsRanges { get; init; }
    public required string SuggestedFileName { get; init; }
    public string? ContentType { get; init; }

    /// <summary>ETag or Last-Modified, used to decide whether a resume is still valid.</summary>
    public string? Validator { get; init; }

    /// <summary>The server's own date for the file, when it sent one.</summary>
    public DateTimeOffset? LastModified { get; init; }

    /// <summary>A download can only be segmented when we know the size AND the server honours Range.</summary>
    public bool CanSegment => SupportsRanges && TotalLength is > 0;
}

/// <summary>One contiguous byte range owned by a single worker.</summary>
public sealed class Segment
{
    private long _end;
    private long _completed;

    public long Start { get; init; }

    /// <summary>Inclusive. Can shrink while a worker runs, when an idle worker takes over
    /// the back half, so it is read and written with barriers.</summary>
    public long End
    {
        get => Volatile.Read(ref _end);
        set => Volatile.Write(ref _end, value);
    }

    /// <summary>Bytes written so far within this segment. Read by the progress reporter
    /// and by the splitter while the worker updates it.</summary>
    public long Completed
    {
        get => Volatile.Read(ref _completed);
        set => Volatile.Write(ref _completed, value);
    }

    /// <summary>
    /// Set when a connection added mid-download was turned away before it delivered a byte.
    /// The range is then nobody's until another worker takes it, and the splitter must not
    /// cut it up in the meantime.
    /// </summary>
    public bool Orphaned
    {
        get => Volatile.Read(ref _orphaned);
        set => Volatile.Write(ref _orphaned, value);
    }
    private bool _orphaned;

    public long Length => End - Start + 1;
    public long Remaining => Length - Completed;
    public bool IsDone => Remaining <= 0;

    /// <summary>Absolute file offset the next write for this segment goes to.</summary>
    public long CurrentOffset => Start + Completed;
}

public sealed record ProgressSnapshot
{
    public required long BytesCompleted { get; init; }
    public long? TotalBytes { get; init; }
    public required double BytesPerSecond { get; init; }
    public required int ActiveConnections { get; init; }

    /// <summary>Progress that cannot be derived from bytes. A stream has no known size, so
    /// its only honest measure is position through the timeline.</summary>
    public double? FractionOverride { get; init; }

    public double? Fraction => FractionOverride
        ?? (TotalBytes is > 0 ? (double)BytesCompleted / TotalBytes.Value : null);

    public TimeSpan? Eta => TotalBytes is > 0 && BytesPerSecond > 1
        ? TimeSpan.FromSeconds((TotalBytes.Value - BytesCompleted) / BytesPerSecond)
        : null;
}

public sealed record DownloadResult
{
    public required string FilePath { get; init; }
    public required long BytesWritten { get; init; }
    public required TimeSpan Elapsed { get; init; }
    public required int ConnectionsUsed { get; init; }
    public required bool WasResumed { get; init; }

    /// <summary>The most connections that were open at once.</summary>
    public int PeakConnections { get; init; }

    /// <summary>The server turned away a connection added mid-download, so growth stopped there.</summary>
    public bool ExtraConnectionsRefused { get; init; }
}
