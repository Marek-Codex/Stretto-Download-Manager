using Stretto.Core;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace Stretto.App.Services;

/// <summary>
/// An editable row in the Password manager. Separate from the stored record because the
/// list binds to it and edits it in place, and a record is the wrong shape for that.
/// </summary>
public sealed class LoginRow : INotifyPropertyChanged
{
    private string _host = "";
    private string _username = "";

    public string Host
    {
        get => _host;
        set { if (_host == value) return; _host = value; Notify(); }
    }

    public string Username
    {
        get => _username;
        set { if (_username == value) return; _username = value; Notify(); }
    }

    /// <summary>Never shown in the list, only in the editor.</summary>
    public string Password { get; set; } = "";

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Notify([CallerMemberName] string? property = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
}

/// <summary>
/// Remembered HTTP logins, one per host.
///
/// Encrypted with DPAPI under the current user, so the file is useless on another account
/// or another machine. That is not a vault -- anything running as this user can read it
/// back -- but it does mean a password never sits in plain text on disk, which is the bar
/// XDM's "remember authentication for this website" fails to clear.
/// </summary>
public static class SiteCredentials
{
    public sealed record Login(string Username, string Password);

    private static readonly Lock Gate = new();
    private static Dictionary<string, Login>? _cache;

    private static string Path => System.IO.Path.Combine(DownloadStore.Directory, "credentials.dat");

    public static Login? For(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return null;

        lock (Gate)
        {
            return Load().GetValueOrDefault(uri.Host);
        }
    }

    public static void Remember(string url, string username, string password)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return;

        lock (Gate)
        {
            var all = Load();
            all[uri.Host] = new Login(username, password);
            Save(all);
        }
    }

    /// <summary>Every saved login, for the Password manager list.</summary>
    public static IReadOnlyList<Saved> All()
    {
        lock (Gate)
        {
            return [.. Load()
                .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(pair => new Saved(pair.Key, pair.Value.Username, pair.Value.Password))];
        }
    }

    /// <summary>Replaces the whole store, which is what Save in the Password manager does.</summary>
    public static void Replace(IEnumerable<Saved> logins)
    {
        lock (Gate)
        {
            var all = new Dictionary<string, Login>(StringComparer.OrdinalIgnoreCase);

            foreach (var saved in logins)
            {
                if (saved.Host.Trim().Length == 0)
                    continue;

                all[saved.Host.Trim()] = new Login(saved.Username, saved.Password);
            }

            Save(all);
            _cache = all;
        }
    }

    /// <summary>One row of the Password manager, host included.</summary>
    public sealed record Saved(string Host, string Username, string Password);

    private static Dictionary<string, Login> Load()
    {
        if (_cache is not null)
            return _cache;

        try
        {
            if (File.Exists(Path))
            {
                var plain = ProtectedData.Unprotect(
                    File.ReadAllBytes(Path), null, DataProtectionScope.CurrentUser);

                var saved = JsonSerializer.Deserialize<Dictionary<string, Login>>(plain);
                if (saved is not null)
                    _cache = new Dictionary<string, Login>(saved, StringComparer.OrdinalIgnoreCase);
            }
        }
        catch (Exception exception)
        {
            // A file written by another user account cannot be decrypted. Starting empty
            // beats refusing to run; the user just re-enters the login once.
            App.Log($"Could not read saved logins: {exception.Message}");
        }

        return _cache ??= new Dictionary<string, Login>(StringComparer.OrdinalIgnoreCase);
    }

    private static void Save(Dictionary<string, Login> all)
    {
        try
        {
            Directory.CreateDirectory(DownloadStore.Directory);

            var plain = JsonSerializer.SerializeToUtf8Bytes(all);
            AtomicFile.WriteAllBytes(Path, ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser));
        }
        catch (Exception exception)
        {
            App.Log($"Could not save logins: {exception.Message}");
        }
    }
}
