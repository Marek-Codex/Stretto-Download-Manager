using Stretto.Core;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Stretto.App.Services;

/// <summary>
/// The shared secret the browser extension must present, and the list of extensions allowed
/// to have it.
///
/// The handoff endpoint listens on loopback, but browser content can attempt local requests.
/// Pairing and request checks distinguish an allowed extension from an arbitrary caller.
/// A local address is a location, not an invitation.
///
/// The user never types the secret. The extension asks for it over <c>POST /pair</c>, and
/// Stretto hands it over once — silently when the caller is Stretto's own bundled extension,
/// otherwise after a single Allow.
/// </summary>
public static class Pairing
{
    private static readonly Lazy<string> LazyToken = new(LoadOrCreateToken);
    private static readonly Lock Gate = new();
    private static HashSet<string>? _approved;

    public static string Token => LazyToken.Value;

    private static string TokenPath => Path.Combine(DownloadStore.Directory, "pairing.txt");
    private static string ApprovedPath => Path.Combine(DownloadStore.Directory, "extensions.json");

    // ═══════════════════════════════════════
    // ═══ Approved extensions
    // ═══════════════════════════════════════

    public static bool IsApproved(string origin)
    {
        lock (Gate)
        {
            _approved ??= LoadApproved();
            return _approved.Contains(origin);
        }
    }

    public static void Approve(string origin)
    {
        lock (Gate)
        {
            _approved ??= LoadApproved();
            if (!_approved.Add(origin))
                return;

            try
            {
                Directory.CreateDirectory(DownloadStore.Directory);
                AtomicFile.WriteAllText(ApprovedPath, JsonSerializer.Serialize(_approved));
            }
            catch (Exception exception) when (exception is IOException or JsonException)
            {
                App.Log($"Could not persist approved extensions: {exception.Message}");
            }
        }
    }

    public static void Forget(string origin)
    {
        lock (Gate)
        {
            _approved ??= LoadApproved();
            if (_approved.Remove(origin))
            {
                try { AtomicFile.WriteAllText(ApprovedPath, JsonSerializer.Serialize(_approved)); }
                catch (Exception exception) when (exception is IOException or JsonException)
                {
                    App.Log($"Could not persist approved extensions: {exception.Message}");
                }
            }
        }
    }

    public static IReadOnlyCollection<string> Approved
    {
        get { lock (Gate) { return [.. _approved ??= LoadApproved()]; } }
    }

    private static HashSet<string> LoadApproved()
    {
        try
        {
            if (File.Exists(ApprovedPath)
                && JsonSerializer.Deserialize<HashSet<string>>(File.ReadAllText(ApprovedPath)) is { } saved)
                return saved;
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            App.Log($"Could not read approved extensions: {exception.Message}");
        }

        return new HashSet<string>(StringComparer.Ordinal);
    }

    // ═══════════════════════════════════════
    // ═══ Recognising our own extension
    // ═══════════════════════════════════════

    /// <summary>
    /// Whether this origin is the extension Stretto ships, loaded unpacked from a folder we
    /// know about. Chrome derives an unpacked extension's id deterministically from its
    /// absolute path, so the app can work out what its own copy will be called and skip the
    /// Allow prompt for it — nothing else gets that shortcut.
    /// </summary>
    public static bool IsOwnExtension(string origin)
    {
        var id = origin.Replace("chrome-extension://", "", StringComparison.OrdinalIgnoreCase)
                       .Replace("moz-extension://", "", StringComparison.OrdinalIgnoreCase)
                       .Trim('/');

        return CandidateIds().Contains(id, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Every id our bundled extension could have, across the folders it may sit in.</summary>
    public static IReadOnlyList<string> CandidateIds()
    {
        var ids = new List<string>();

        foreach (var folder in CandidateFolders())
        {
            // Chromium hashes the path in the platform's native string type, which is
            // UTF-16 on Windows. Both are computed because getting this wrong silently
            // costs the user a prompt rather than breaking anything.
            ids.Add(ComputeId(folder, Encoding.Unicode));
            ids.Add(ComputeId(folder, Encoding.UTF8));
        }

        return ids;
    }

    public static IReadOnlyList<string> CandidateFolders()
    {
        var folders = new List<string>();

        void Add(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            try
            {
                var full = Path.GetFullPath(path);
                if (Directory.Exists(full) && !folders.Contains(full, StringComparer.OrdinalIgnoreCase))
                    folders.Add(full);
            }
            catch (Exception exception) when (exception is IOException or ArgumentException)
            {
                // A malformed path is simply not a candidate.
            }
        }

        // Shipped next to the executable.
        Add(Path.Combine(BundledExtensions.Root, "chromium"));
        Add(Path.Combine(AppContext.BaseDirectory, "extensions", "chromium"));

        // And the source tree it was built from, found by walking up rather than counting
        // levels: the bin path depth changes with configuration and target framework, so a
        // fixed number of "../" is wrong the moment either does.
        var walk = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; walk is not null && depth < 8; depth++, walk = walk.Parent)
            Add(Path.Combine(walk.FullName, "extensions", "chromium"));

        return folders;
    }

    /// <summary>
    /// Chromium's unpacked-extension id: the first 16 bytes of SHA-256 over the absolute
    /// path, with each nibble mapped onto a..p.
    /// </summary>
    public static string ComputeId(string path, Encoding encoding)
    {
        var hash = SHA256.HashData(encoding.GetBytes(path));
        var id = new StringBuilder(32);

        foreach (var b in hash.AsSpan(0, 16))
        {
            id.Append((char)('a' + (b >> 4)));
            id.Append((char)('a' + (b & 0xF)));
        }

        return id.ToString();
    }

    // ═══════════════════════════════════════
    // ═══ Token
    // ═══════════════════════════════════════

    private static string LoadOrCreateToken()
    {
        try
        {
            if (File.Exists(TokenPath))
            {
                var existing = File.ReadAllText(TokenPath).Trim();
                if (existing.Length >= 32)
                    return existing;
            }

            var token = NewToken();
            Directory.CreateDirectory(DownloadStore.Directory);
        AtomicFile.WriteAllText(TokenPath, token);
            return token;
        }
        catch (IOException exception)
        {
            // Without a readable token file the endpoint still works for this session;
            // the extension just has to pair again next launch.
            App.Log($"Could not persist pairing token: {exception.Message}");
            return NewToken();
        }
    }

    private static string NewToken() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
}
