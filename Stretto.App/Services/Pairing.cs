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
/// Stretto hands it over after an explicit Allow. Already-approved extensions can reconnect
/// silently; the bundled extension does not bypass a user's revocation.
/// </summary>
public static class Pairing
{
    private static readonly Lock Gate = new();
    private static HashSet<string>? _approved;
    private static string? _token;
    private static long _approvalGeneration;

    public static long ApprovalGeneration { get { lock (Gate) return _approvalGeneration; } }

    public static string? ApprovedToken(string origin)
    {
        lock (Gate)
        {
            EnsureLoaded();
            return _approved!.Contains(origin) ? _token : null;
        }
    }

    public static string Token
    {
        get { lock (Gate) { EnsureLoaded(); return _token!; } }
    }

    private static string TokenPath => Path.Combine(DownloadStore.Directory, "pairing.txt");
    private static string ApprovedPath => Path.Combine(DownloadStore.Directory, "extensions.json");
    private static string StatePath => Path.Combine(DownloadStore.Directory, "pairing-state.json");

    private sealed class SavedState
    {
        public int Version { get; set; } = 1;
        public string Token { get; set; } = "";
        public HashSet<string> Approved { get; set; } = [];
    }

    private static void EnsureLoaded()
    {
        if (_approved is not null && _token is not null) return;
        var hasState = File.Exists(StatePath);
        if (hasState)
        {
            try
            {
                var state = JsonSerializer.Deserialize<SavedState>(File.ReadAllText(StatePath));
                if (state is { Version: 1, Token.Length: >= 32, Approved: not null })
                {
                    _approved = new(state.Approved, StringComparer.Ordinal);
                    _token = state.Token;
                    return;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            { App.Log($"Could not read pairing state: {ex.Message}"); }
        }

        // Migrate the old two-file layout once. An unreadable/new-format state never
        // falls back to old approvals or an obsolete token that revocation replaced.
        _approved = hasState ? new(StringComparer.Ordinal) : LoadApproved();
        _token = hasState ? NewToken() : LoadOrCreateToken();
        try { SaveState(_approved, _token); }
        catch (IOException ex) { App.Log(ex.Message); }
    }

    private static void SaveState(HashSet<string> approved, string token)
    {
        try { AtomicFile.WriteAllText(StatePath, JsonSerializer.Serialize(new SavedState { Approved = approved, Token = token })); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { throw new IOException("Could not save browser pairing permissions. The requested permission change was not applied.", ex); }
    }

    // ═══════════════════════════════════════
    // ═══ Approved extensions
    // ═══════════════════════════════════════

    public static bool IsApproved(string origin)
    {
        lock (Gate)
        {
            EnsureLoaded();
            return _approved!.Contains(origin);
        }
    }

    public static string? ApproveAndGetToken(string origin, long expectedGeneration)
    {
        lock (Gate)
        {
            // A dialog opened before a revocation cannot issue the newly rotated secret.
            if (_approvalGeneration != expectedGeneration) return null;
            EnsureLoaded();
            if (_approved!.Contains(origin)) return _token;
            var approved = new HashSet<string>(_approved, StringComparer.Ordinal) { origin };
            SaveState(approved, _token!);
            _approved = approved;
            return _token;
        }
    }

    public static void Forget(string origin) => ForgetMany([origin]);

    public static void ForgetMany(IEnumerable<string> origins)
    {
        lock (Gate)
        {
            EnsureLoaded();
            var approved = new HashSet<string>(_approved!, StringComparer.Ordinal);
            foreach (var origin in origins) approved.Remove(origin);
            if (approved.SetEquals(_approved!)) return;
            var token = NewToken();
            SaveState(approved, token);
            _approved = approved;
            _token = token;
            _approvalGeneration++;

            // The revoked extension still holds the old token, and taking it off the list
            // alone would leave it working. A fresh token shuts it out; the extensions still
            // approved notice the failed ping and re-pair without a prompt.
            PersistToken(_token);
        }
    }

    public static IReadOnlyCollection<string> Approved
    {
        get { lock (Gate) { EnsureLoaded(); return [.. _approved!]; } }
    }

    private static HashSet<string> LoadApproved()
    {
        try
        {
            if (File.Exists(ApprovedPath)
                && JsonSerializer.Deserialize<HashSet<string>>(File.ReadAllText(ApprovedPath)) is { } saved)
                return saved;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
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
    /// absolute path. A folder-derived match identifies the bundled copy; it does not grant
    /// approval or override revocation. Authorization uses the saved approval state.
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
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            App.Log($"Could not read pairing token: {exception.Message}");
        }

        var token = NewToken();
        PersistToken(token);
        return token;
    }

    private static void PersistToken(string token)
    {
        try
        {
            Directory.CreateDirectory(DownloadStore.Directory);
            AtomicFile.WriteAllText(TokenPath, token);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Without a writable token file the endpoint still works for this session;
            // the extension just has to pair again next launch.
            App.Log($"Could not persist pairing token: {exception.Message}");
        }
    }

    private static string NewToken() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
}
