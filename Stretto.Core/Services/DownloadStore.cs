using System.Text.Json;
using System.Text.Json.Serialization;

namespace Stretto.Core;

/// <summary>
/// Persists the download list between runs. Bytes live in the destination files; per-file
/// .stretto sidecars track resumable progress. This list remembers which downloads exist and what
/// the user last did with them. The guest list, not the luggage. Please keep both.
/// </summary>
public sealed class DownloadStore
{
    /// <summary>
    /// Where settings, the download list, queues, logins and tools live. Settable so a test
    /// or a headless host can use its own folder; set it before anything reads or writes.
    /// </summary>
    public static string Directory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Stretto");

    private static string ListPath => Path.Combine(Directory, "downloads.json");

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public sealed class Entry
    {
        public string Id { get; set; } = "";
        public string Url { get; set; } = "";
        public string Name { get; set; } = "";
        public string TargetDirectory { get; set; } = "";
        public string? FilePath { get; set; }
        public string? PreferredFileName { get; set; }
        public bool SortByType { get; set; }
        public bool IsStream { get; set; }
        public bool IsSiteVideo { get; set; }
        public string? FormatId { get; set; }
        public int? HlsProgramIndex { get; set; }
        public string? Mime { get; set; }
        public bool NamedFromTitle { get; set; }
        public int Connections { get; set; } = 8;
        public ProxyMode? ProxyModeOverride { get; set; }
        public ProxyProtocol? ProxyProtocolOverride { get; set; }
        public string? ProxyHost { get; set; }
        public int ProxyPort { get; set; }
        public string? ProxyUsername { get; set; }
        public DateTimeOffset Added { get; set; }
        public DateTimeOffset? CompletedAt { get; set; }
        public DownloadStatus Status { get; set; }
        public long BytesCompleted { get; set; }
        public long? TotalBytes { get; set; }

        /// <summary>Why a failed download failed, so the row still says after a restart.</summary>
        public string? Error { get; set; }

        /// <summary>The captured request headers that are safe to keep. See <see cref="Keepable"/>.</summary>
        public Dictionary<string, string>? Headers { get; set; }
    }

    /// <summary>
    /// Request headers that may be written to disk. An allowlist rather than a blocklist:
    /// sites name their tokens anything (x-auth, x-csrf-token, x-amz-security-token,
    /// x-goog-...), so we retain a small set of ordinary request preferences and origin
    /// context instead of trying to enumerate every possible credential header. Other
    /// request headers die with the session; Refresh link can supply fresh context.
    /// This is not a guarantee that the saved URL or arbitrary header values are public.
    /// </summary>
    private static readonly HashSet<string> Keepable = new(StringComparer.OrdinalIgnoreCase)
    {
        "origin", "accept", "accept-language", "x-requested-with", "cache-control", "pragma", "dnt",
    };

    public static Dictionary<string, string>? KeepableHeaders(IReadOnlyDictionary<string, string>? headers)
    {
        if (headers is not { Count: > 0 })
            return null;

        var kept = headers.Where(h => Keepable.Contains(h.Key))
            .ToDictionary(h => h.Key, h => h.Value, StringComparer.OrdinalIgnoreCase);
        return kept.Count > 0 ? kept : null;
    }

    public IReadOnlyList<Entry> Load()
    {
        try
        {
            if (!File.Exists(ListPath))
                return [];

            return JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(ListPath)) ?? [];
        }
        catch (Exception exception) when (exception is JsonException or IOException)
        {
            // A corrupt list must not stop the app launching; starting empty is recoverable.
            AppLog.Write($"Could not read download list: {exception.Message}");
            return [];
        }
    }

    /// <summary>
    /// The list in its persisted shape. Shared by the on disk list and by Export, so the
    /// two cannot drift -- and note what an Entry leaves out: cookies, referrer, user agent,
    /// saved logins and any request header outside the keepable few never reach it.
    /// URLs can still contain access tokens, and errors can contain private details.
    /// An export is useful, not magically safe to paste into a public issue. Slow blink.
    /// </summary>
    public static List<Entry> ToEntries(IEnumerable<DownloadItem> items) =>
        [.. items.Select(i => new Entry
        {
            Id = i.Id,
            Url = i.Url,
            Name = i.Name,
            TargetDirectory = i.TargetDirectory,
            FilePath = i.FilePath,
            PreferredFileName = i.PreferredFileName,
            SortByType = i.SortByType,
            IsStream = i.IsStream,
            IsSiteVideo = i.IsSiteVideo,
            FormatId = i.FormatId,
            HlsProgramIndex = i.HlsProgramIndex,
            Mime = i.Mime,
            NamedFromTitle = i.NamedFromTitle,
            Connections = i.Connections,
            ProxyModeOverride = i.ProxyModeOverride,
            ProxyProtocolOverride = i.ProxyProtocolOverride,
            ProxyHost = i.ProxyHost,
            ProxyPort = i.ProxyPort,
            ProxyUsername = i.ProxyUsername,
            // ProxyPassword deliberately omitted: session secret, like cookies.
            Added = i.Added,
            CompletedAt = i.CompletedAt,
            // Record queued/running work as paused. The next process has no live workers yet;
            // claiming "downloading" would be very confident of a JSON file.
            Status = i.Status is DownloadStatus.Downloading or DownloadStatus.Queued
                ? DownloadStatus.Paused
                : i.Status,
            BytesCompleted = i.BytesCompleted,
            TotalBytes = i.TotalBytes,
            Error = i.Status is DownloadStatus.Failed ? i.Error : null,
            Headers = KeepableHeaders(i.Headers),
        })];

    /// <summary>What was last written, so an unchanged list is not rewritten every 10 seconds.</summary>
    private string? _lastWritten;
    private readonly object _saveGate = new();
    private long _saveRevision;
    private long _committedRevision;

    public Task SaveAsync(IEnumerable<DownloadItem> items)
    {
        // Snapshot UI-owned items before dispatching serialization and IO.
        // The background worker gets a photograph, not permission to rearrange the room.
        var revision = Interlocked.Increment(ref _saveRevision);
        var entries = ToEntries(items);
        var path = ListPath;
        return Task.Run(() => SaveEntries(entries, path, revision));
    }

    public void Save(IEnumerable<DownloadItem> items)
    {
        var revision = Interlocked.Increment(ref _saveRevision);
        SaveEntries(ToEntries(items), ListPath, revision);
    }

    private void SaveEntries(List<Entry> entries, string path, long revision)
    {
        lock (_saveGate)
        try
        {
            // A slower old save must not overwrite a newer one. Past-me can wait his turn.
            if (revision < _committedRevision) return;
            var json = JsonSerializer.Serialize(entries, Options);
            if (json == _lastWritten)
            {
                _committedRevision = revision;
                return;
            }

            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            AtomicFile.WriteAllText(path, json);
            _lastWritten = json;
            _committedRevision = revision;
        }
        catch (Exception exception) when (exception is JsonException or IOException)
        {
            AppLog.Write($"Could not save download list: {exception.Message}");
        }
    }

    /// <summary>Writes the list somewhere the user chose. Throws, so the caller can say so.</summary>
    public static void ExportTo(string path, IEnumerable<DownloadItem> items) =>
        AtomicFile.WriteAllText(path, JsonSerializer.Serialize(ToEntries(items), Options));

    /// <summary>Reads a list written by Export, or by another copy of Stretto.</summary>
    public static List<Entry> ImportFrom(string path) =>
        JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(path)) ?? [];
}
