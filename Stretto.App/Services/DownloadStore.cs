using Stretto.App.Models;
using Stretto.Core;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Stretto.App.Services;

/// <summary>
/// Persists the download list between runs. The bytes themselves already survive via the
/// engine's per-file .stretto sidecars, so this only has to remember which downloads exist
/// and what the user last did with them.
/// </summary>
public sealed class DownloadStore
{
    public static string Directory { get; } = Path.Combine(
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
    /// x-goog-...), so only headers known to carry no secret survive a restart. These are the
    /// ones hotlink and CORS checks actually look at; anything else a download needed dies
    /// with the session, like cookies do, and the Refresh link flow gets it back.
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
            App.Log($"Could not read download list: {exception.Message}");
            return [];
        }
    }

    /// <summary>
    /// The list in its persisted shape. Shared by the on disk list and by Export, so the
    /// two cannot drift -- and note what an Entry leaves out: cookies, referrer, user agent,
    /// saved logins and any request header outside the keepable few never reach it, so an
    /// exported list carries no session secrets.
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
            Connections = i.Connections,
            ProxyModeOverride = i.ProxyModeOverride,
            ProxyProtocolOverride = i.ProxyProtocolOverride,
            ProxyHost = i.ProxyHost,
            ProxyPort = i.ProxyPort,
            ProxyUsername = i.ProxyUsername,
            // ProxyPassword deliberately omitted: session secret, like cookies.
            Added = i.Added,
            CompletedAt = i.CompletedAt,
            // Anything mid-flight is recorded as paused: on next launch it can be
            // resumed from its sidecar, whereas "downloading" would be a lie.
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

    public void Save(IEnumerable<DownloadItem> items)
    {
        try
        {
            var json = JsonSerializer.Serialize(ToEntries(items), Options);
            if (json == _lastWritten)
                return;

            System.IO.Directory.CreateDirectory(Directory);
            AtomicFile.WriteAllText(ListPath, json);
            _lastWritten = json;
        }
        catch (Exception exception) when (exception is JsonException or IOException)
        {
            App.Log($"Could not save download list: {exception.Message}");
        }
    }

    /// <summary>Writes the list somewhere the user chose. Throws, so the caller can say so.</summary>
    public static void ExportTo(string path, IEnumerable<DownloadItem> items) =>
        AtomicFile.WriteAllText(path, JsonSerializer.Serialize(ToEntries(items), Options));

    /// <summary>Reads a list written by Export, or by another copy of Stretto.</summary>
    public static List<Entry> ImportFrom(string path) =>
        JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(path)) ?? [];
}
