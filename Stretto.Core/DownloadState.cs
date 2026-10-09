using System.Text.Json;
using System.Text.Json.Serialization;

namespace Stretto.Core;

/// <summary>
/// Resume bookkeeping. Because workers write straight into the final file at their own
/// offsets, the only thing that needs persisting is how far each segment got --
/// there are no chunk files to track or merge.
/// </summary>
public sealed class DownloadState
{
    public const string SidecarExtension = ".stretto";

    public int Version { get; set; } = 1;
    public string Url { get; set; } = "";
    public long TotalLength { get; set; }
    public string? Validator { get; set; }
    public List<SegmentState> Segments { get; set; } = [];

    [JsonIgnore]
    public long BytesCompleted => Segments.Sum(s => s.Completed);

    public sealed class SegmentState
    {
        public long Start { get; set; }
        public long End { get; set; }
        public long Completed { get; set; }
    }

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

    public static string SidecarPathFor(string filePath) => filePath + SidecarExtension;

    /// <summary>Returns null when there is no usable resume point for this exact remote file.</summary>
    public static DownloadState? TryLoad(string filePath, RemoteFileInfo info)
    {
        var sidecar = SidecarPathFor(filePath);
        if (!File.Exists(sidecar) || !File.Exists(filePath))
            return null;

        try
        {
            var state = JsonSerializer.Deserialize<DownloadState>(File.ReadAllText(sidecar));

            // Anything less than an exact match on size and validator means the remote
            // file may have changed underneath us. Restarting beats silent corruption.
            if (state is null
                || state.Version != 1
                || state.Segments.Count == 0
                || info.Validator is null
                || !SameResourceAddress(state.Url, info.FinalUrl)
                || state.TotalLength != info.TotalLength
                || state.Validator != info.Validator)
                return null;

            // The file is only as long as the furthest byte any worker has written: Windows
            // preallocation reserves the space without extending the file. Demanding the full
            // length restarted every interrupted download from zero. What must hold is that
            // the file reaches every offset the sidecar claims is done, and never exceeds the
            // remote size.
            var fileLength = new FileInfo(filePath).Length;
            var written = state.Segments.Where(s => s.Completed > 0).Select(s => s.Start + s.Completed).DefaultIfEmpty(0).Max();
            if (fileLength > state.TotalLength || fileLength < written
                || !HasValidSegments(state.Segments, state.TotalLength))
                return null;

            return state;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return null;
        }
    }

    // Signed download links commonly refresh only their query string. Keep that supported,
    // while refusing to reuse byte ranges for a different endpoint that happens to have
    // the same size and validator.
    private static bool SameResourceAddress(string savedUrl, Uri current)
    {
        if (!Uri.TryCreate(savedUrl, UriKind.Absolute, out var saved))
            return false;

        return string.Equals(saved.Scheme, current.Scheme, StringComparison.OrdinalIgnoreCase)
               && string.Equals(saved.IdnHost, current.IdnHost, StringComparison.OrdinalIgnoreCase)
               && saved.Port == current.Port
               && string.Equals(saved.AbsolutePath, current.AbsolutePath, StringComparison.Ordinal);
    }

    private static bool HasValidSegments(IReadOnlyList<SegmentState> segments, long totalLength)
    {
        if (totalLength <= 0 || segments.Count == 0)
            return false;

        var ordered = segments.OrderBy(s => s.Start).ToArray();
        var next = 0L;

        foreach (var segment in ordered)
        {
            if (segment.Start != next
                || segment.End < segment.Start
                || segment.End >= totalLength
                || segment.Completed < 0
                || segment.Completed > segment.End - segment.Start + 1)
                return false;

            next = segment.End + 1;
        }

        return next == totalLength;
    }

    public void Save(string filePath)
    {
        var sidecar = SidecarPathFor(filePath);
        AtomicFile.WriteAllText(sidecar, JsonSerializer.Serialize(this, Options));
    }

    public static void Delete(string filePath)
    {
        foreach (var path in new[] { SidecarPathFor(filePath), SidecarPathFor(filePath) + ".tmp" })
        {
            try { File.Delete(path); } catch (IOException) { /* best effort */ }
        }
    }

    public List<Segment> ToSegments() =>
        [.. Segments.Select(s => new Segment { Start = s.Start, End = s.End, Completed = s.Completed })];

    public void UpdateFrom(IReadOnlyList<Segment> segments)
    {
        Segments = [.. segments.Select(s => new SegmentState
        {
            Start = s.Start, End = s.End, Completed = s.Completed,
        })];
    }
}
