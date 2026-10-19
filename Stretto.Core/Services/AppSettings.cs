using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Stretto.Core;

/// <summary>How downloads reach the network.</summary>
public enum ProxyMode { System, None, Manual }
public enum ProxyProtocol { Http, Socks5 }

/// <summary>What double clicking a finished download does.</summary>
public enum DoubleClickAction { OpenFile, OpenFolder, Nothing }

/// <summary>Where a captured video's file name comes from.</summary>
public enum NameSource { Smart, File, Title }

/// <summary>
/// Persisted preferences.
///
/// Most of these were hardcoded constants until now, so exposing them is less about adding
/// features than about handing over control of behaviour that already existed.
/// </summary>
public sealed class AppSettings
{
    // ═══ Browser monitoring ═══

    public bool BrowserMonitoring { get; set; } = true;
    public int HandoffPort { get; set; } = HandoffListener.DefaultPort;

    /// <summary>Watch the clipboard for links and offer to take them.</summary>
    public bool MonitorClipboard { get; set; }

    /// <summary>Toast when the extension spots media on a page. Off by default: every
    /// video tab raised one, and the extension popup already lists what it found.</summary>
    public bool NotifyOnMedia { get; set; }

    // ═══ Capture rules ═══
    //
    // These live here rather than in the extension's own options because the app is the
    // single source of truth: the extension fetches them from GET /rules on startup, so
    // changing them in one place cannot leave the two disagreeing.

    /// <summary>File types taken over from the browser. An allowlist, as XDM has.</summary>
    public List<string> CaptureTypes { get; set; } = [.. DefaultCaptureTypes];

    /// <summary>Media types the extension reports as grabbable when played.</summary>
    public List<string> VideoTypes { get; set; } = [.. DefaultVideoTypes];

    /// <summary>
    /// Content-Type fragments that mean media even when the address has no extension.
    ///
    /// A video served from /play?id=9182 as video/mp4 is invisible to an extension that
    /// only looks at file extensions, and a good number of sites serve media as
    /// application/octet-stream, which is on its own useless -- hence a list rather than a
    /// check. Matched as a substring of the response's Content-Type, as XDM's media types
    /// are. Empty falls back to the extension's own built-in table.
    /// </summary>
    public List<string> MediaTypes { get; set; } = [.. DefaultMediaTypes];

    /// <summary>Ignore media smaller than this. Only applies when the browser reports a size.</summary>
    public int MinVideoSizeKb { get; set; } = 1024;

    /// <summary>
    /// Show the New Download window for a download the browser hands over, instead of
    /// queueing it silently. On by default: a capture is the one moment where the choice
    /// of folder, name and login is still open, and some sites need to be waved through
    /// to the browser entirely.
    /// </summary>
    public bool ConfirmCaptures { get; set; } = true;

    /// <summary>Hosts never captured from, matched against the hostname.</summary>
    public List<string> SkipHosts { get; set; } = [.. DefaultSkipHosts];

    /// <summary>
    /// Hosts whose downloads are taken whatever their type, as XDM's matching hosts are.
    ///
    /// The counterpart to the skip list: a site that is not on it is still filtered
    /// through the type allowlist, and adding a host here says "this one I trust, take
    /// whatever it serves". An address with no extension is captured either way, so this
    /// is about a host serving a known type that is deliberately off the list.
    /// </summary>
    public List<string> AlwaysHosts { get; set; } = [];

    /// <summary>Stamp the saved file with the server's Last-Modified date.</summary>
    public bool UseServerTimestamp { get; set; } = true;

    public static readonly string[] DefaultCaptureTypes =
    [
        "3gp", "7z", "avi", "bz2", "deb", "doc", "docx", "exe", "gz", "iso", "msi", "pdf",
        "ppt", "pptx", "rar", "rpm", "sit", "sitx", "tar", "jar", "xls", "xlsx", "xz", "zip",
        // Not in XDM's list, but these are large model files people fetch from APIs and
        // there is no reason a download manager should ignore them.
        "safetensors", "gguf", "ckpt", "pt", "pth", "onnx", "bin", "appimage", "dmg",
    ];

    public static readonly string[] DefaultVideoTypes =
    [
        "mp4", "m3u8", "mpd", "f4m", "webm", "ogg", "mp3", "aac", "flv", "mkv",
        "divx", "mov", "mpg", "mpeg", "opus",
    ];

    /// <summary>
    /// Content-Type fragments that mean media. Substrings, not exact types, so a charset
    /// or a codec parameter cannot stop the match.
    ///
    /// Deliberately not application/octet-stream, even though plenty of sites serve video
    /// that way: so does every unknown binary blob, and listing it would report every
    /// archive and installer fetched by script as media. A CDNe that hides its media
    /// behind octet-stream is not worth that trade.
    /// </summary>
    public static readonly string[] DefaultMediaTypes =
    [
        "video/", "audio/", "mpegurl", "dash+xml", "application/vnd.apple.mpegurl",
        "application/x-mpegurl", "application/dash+xml",
    ];

    public static readonly string[] DefaultSkipHosts =
    [
        "update.microsoft.com", "windowsupdate.com", "thawte.com", "uupdump.net",
        // IPFS gateways that unpack the file in a service worker inside the browser. Fetched
        // directly they answer with a web page, so only the browser can download from them.
        "dweb.link", "inbrowser.link",
    ];

    // ═══ General ═══

    /// <summary>Empty means the user profile's Downloads folder.</summary>
    public string DownloadFolder { get; set; } = "";

    /// <summary>File a finished download into a per-category subfolder.</summary>
    public bool SortByType { get; set; } = true;

    /// <summary>
    /// The Download categories table: which file types belong to which category, and where
    /// each one is filed. Also what Library filters by, so editing this changes both.
    /// </summary>
    public List<CategoryRule> Categories { get; set; } = CategoryRules.Defaults();

    /// <summary>Announce when a download finishes, with buttons to open it.</summary>
    public bool ShowCompleteDialog { get; set; } = true;

    /// <summary>Whether New and browser handoffs start straight away.</summary>
    public bool StartAutomatically { get; set; } = true;

    /// <summary>False means a clashing name gets " (2)" rather than being replaced.</summary>
    public bool OverwriteExisting { get; set; }

    /// <summary>A clashing name gets a short tag from where it came from ("Title [a8f3k2]")
    /// rather than a number. Only matters when <see cref="OverwriteExisting"/> is off.</summary>
    public bool TagClashingNames { get; set; }

    /// <summary>Where a browser-captured video's name comes from. Smart uses the page's
    /// title only when the address's own name is generic, like master.m3u8.</summary>
    public NameSource NameFrom { get; set; } = NameSource.Smart;

    public DoubleClickAction DoubleClick { get; set; } = DoubleClickAction.OpenFile;

    /// <summary>Open a progress window when a single download starts. Batches never open
    /// one per file, which would bury the screen.
    ///
    /// Off by default: every row shows speed and time left now, so a window per download
    /// is something to ask for rather than something to dismiss. A saved settings file
    /// keeps whatever it already says.</summary>
    public bool ShowProgressWindow { get; set; }

    /// <summary>The details pane beside the list. Remembered, because someone who wants it
    /// open wants it open every time.</summary>
    public bool ShowDetailsPane { get; set; }

    /// <summary>How Library is ordered: Newest, Oldest, Name, Size or Type.</summary>
    public string LibrarySort { get; set; } = "Newest";

    /// <summary>Transfers allowed to run at once. The rest wait in Queued.</summary>
    public int MaxConcurrentDownloads { get; set; } = 4;

    // ═══ Network ═══

    /// <summary>The most parallel byte ranges one download may use. With
    /// <see cref="AdaptiveConnections"/> it only gets there while the speed keeps improving.</summary>
    public int DefaultConnections { get; set; } = 16;

    public int ConnectionTimeoutSeconds { get; set; } = 30;

    public int MaxRetriesPerSegment { get; set; } = 5;

    /// <summary>Global transfer ceiling; zero means unlimited.</summary>
    public long SpeedLimitBytesPerSecond { get; set; }

    /// <summary>
    /// Open a few connections and add more only while the speed keeps improving, up to
    /// <see cref="DefaultConnections"/>. Replaces a blanket cap of four on every unlisted
    /// site, which held a fast line to what four throttled connections could carry.
    /// </summary>
    public bool AdaptiveConnections { get; set; } = true;

    /// <summary>Retry a failed download by itself, a few times with growing gaps.</summary>
    public bool AutoRetry { get; set; } = true;

    /// <summary>Resume paused downloads when Stretto starts. Off by default: an app that
    /// silently resumes six downloads the moment it opens is a nasty surprise.</summary>
    public bool ResumeOnStartup { get; set; }

    /// <summary>Days a finished download stays under All in Library. It stays under its
    /// file-type category regardless, and nothing is deleted. 0 keeps it forever.</summary>
    public int KeepInCompleteDays { get; set; } = 30;

    /// <summary>Schema version, for moving an existing settings file onto new defaults
    /// once. Absent in older files, so it reads as 0.</summary>
    public int SettingsVersion { get; set; }

    /// <summary>Ticked "Don't ask me again" on the "start with Windows?" question, which is
    /// otherwise asked on each launch until the answer is yes. A new name rather than the
    /// old once-only AskedAboutStartup, so people who said "Not now" get the checkbox.</summary>
    public bool NeverAskAboutStartup { get; set; }

    /// <summary>
    /// Segment counts for hosts that do not cope with the default.
    ///
    /// Plenty of sites hand out a one-time token and then throttle or reject the extra
    /// range requests, so more segments makes a download slower or kills it outright.
    /// Keyed by hostname; anything not listed uses <see cref="DefaultConnections"/>.
    /// </summary>
    public Dictionary<string, int> HostConnectionLimits { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The segment count to use for a URL, after any per-host cap.</summary>
    public int ConnectionsFor(string url, int wanted)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return wanted;

        if (HostConnectionLimits.TryGetValue(uri.Host, out var capped))
            return Math.Clamp(Math.Min(wanted, capped), 1, SegmentedDownloader.MaxConnections);

        // Unlisted hosts get the full ceiling. Whether they can use it is measured, not
        // assumed: an adaptive download starts small and a refused connection stops growth.
        return Math.Clamp(wanted, 1, SegmentedDownloader.MaxConnections);
    }

    // ═══ Proxy ═══
    //
    // The default route for every download. Individual downloads can override it from the
    // New Download window's Advanced pane, for the one host that needs a different way out.

    public ProxyMode ProxyMode { get; set; } = ProxyMode.System;
    public ProxyProtocol ProxyProtocol { get; set; } = ProxyProtocol.Http;
    public string ProxyHost { get; set; } = "";
    public int ProxyPort { get; set; }
    public string ProxyUsername { get; set; } = "";
    public string ProxyPassword { get; set; } = "";

    /// <summary>Null means "let the handler use the system proxy".</summary>
    [JsonIgnore]
    public IWebProxy? ResolvedProxy
    {
        get
        {
            if (ProxyMode == ProxyMode.None)
                return SegmentedDownloader.NoProxy;

            if (ProxyMode != ProxyMode.Manual
                || ProxyProtocol == ProxyProtocol.Socks5
                || string.IsNullOrWhiteSpace(ProxyHost))
                return null;

            var proxy = new WebProxy(ProxyHost, ProxyPort);

            if (!string.IsNullOrEmpty(ProxyUsername))
                proxy.Credentials = new NetworkCredential(ProxyUsername, ProxyPassword);

            return proxy;
        }
    }

    [JsonIgnore]
    public Socks5ProxyOptions? ResolvedSocks5Proxy =>
        ProxyMode == ProxyMode.Manual && ProxyProtocol == ProxyProtocol.Socks5
            && !string.IsNullOrWhiteSpace(ProxyHost)
            ? new Socks5ProxyOptions(ProxyHost.Trim(), Math.Clamp(ProxyPort, 1, 65535),
                string.IsNullOrWhiteSpace(ProxyUsername) ? null : ProxyUsername,
                string.IsNullOrWhiteSpace(ProxyUsername) ? null : ProxyPassword)
            : null;

    // ═══ Advanced ═══

    /// <summary>
    /// Authenticate with the signed in Windows account when a server or proxy asks
    /// (NTLM or Kerberos). Off by default, because it means offering those credentials to
    /// whatever host challenges for them.
    /// </summary>
    public bool UseWindowsCredentials { get; set; }

    /// <summary>
    /// Start with Windows. The real state lives in the registry, which the user can change
    /// behind our back from Task Manager; this is the last thing Stretto asked for.
    /// </summary>
    public bool StartWithWindows { get; set; }

    /// <summary>Come up hidden, leaving only the tray icon.</summary>
    public bool StartMinimized { get; set; }

    /// <summary>Show an icon in the notification area.</summary>
    public bool ShowTrayIcon { get; set; } = true;

    /// <summary>Minimising hides the window rather than leaving it on the taskbar.</summary>
    public bool MinimizeToTray { get; set; }

    /// <summary>Closing the window leaves Stretto running in the notification area.</summary>
    public bool CloseToTray { get; set; } = true;

    /// <summary>Keep the machine awake while anything is transferring.</summary>
    public bool PreventSleep { get; set; } = true;

    /// <summary>Shut down once everything finishes. Windows shows its own cancellable notice.</summary>
    public bool ShutdownWhenDone { get; set; }

    /// <summary>Program to run once the queue drains. Empty disables it.</summary>
    public string RunWhenDone { get; set; } = "";

    /// <summary>Hand each finished file to a scanner before it is called done.</summary>
    public bool ScanWithAntivirus { get; set; }

    /// <summary>The scanner to run. Empty disables the scan whatever the checkbox says.</summary>
    public string AntivirusPath { get; set; } = "";

    /// <summary>
    /// Arguments for the scanner. The file path is appended, quoted, unless the arguments
    /// already say where it goes with {file}.
    /// </summary>
    public string AntivirusArguments { get; set; } = "";

    /// <summary>Sent when the browser did not supply one of its own.</summary>
    public string UserAgent { get; set; } = DefaultUserAgent;

    /// <summary>Looks for a newer release at most once a day. It only tells; it never installs.</summary>
    public bool CheckForUpdates { get; set; } = true;

    /// <summary>When the automatic check last ran, so it stays once a day across restarts.</summary>
    public DateTime LastUpdateCheck { get; set; }
    public const string DefaultUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36";

    // ═══ Derived ═══

    [JsonIgnore]
    public string ResolvedDownloadFolder => string.IsNullOrWhiteSpace(DownloadFolder)
        ? System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads")
        : DownloadFolder;

    private static string Path => System.IO.Path.Combine(DownloadStore.Directory, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            var loaded = File.Exists(Path)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Path)) ?? new AppSettings()
                : new AppSettings();

            loaded.Migrate();
            loaded.Clamp();
            CategoryRules.Use(loaded.Categories);
            return loaded;
        }
        catch (Exception exception) when (exception is JsonException or IOException)
        {
            AppLog.Write($"Could not read settings: {exception.Message}");
            return new AppSettings();
        }
    }

    /// <summary>
    /// Moves a settings file written by an older build onto defaults that have since
    /// changed. Runs once per version: afterwards the user's own choice stands.
    /// </summary>
    private void Migrate()
    {
        if (SettingsVersion < 2)
        {
            // Version 2: media notices off (noisy, and the popup lists media anyway), and
            // unknown hosts start gently, since some refuse eight parallel ranges.
            NotifyOnMedia = false;
            SettingsVersion = 2;
        }

        if (SettingsVersion < 3)
        {
            // Version 3: connections ramp up on their own, so the ceiling can be higher. An
            // untouched default of 8 moves to 16; a value the user chose stands.
            if (DefaultConnections == 8)
                DefaultConnections = 16;
            SettingsVersion = 3;
        }

        if (SettingsVersion < 4)
        {
            // Version 4: the default skip list gained IPFS gateways, and a default only seeds
            // a new file. Add what is missing; a host the user removes later stays removed.
            foreach (var host in DefaultSkipHosts)
                if (!SkipHosts.Contains(host, StringComparer.OrdinalIgnoreCase))
                    SkipHosts.Add(host);
            SettingsVersion = 4;
        }
    }

    /// <summary>A hand-edited settings file must not be able to wedge the downloader.</summary>
    private void Clamp()
    {
        DefaultConnections = Math.Clamp(DefaultConnections, 1, SegmentedDownloader.MaxConnections);
        MaxConcurrentDownloads = Math.Clamp(MaxConcurrentDownloads, 1, 16);
        ConnectionTimeoutSeconds = Math.Clamp(ConnectionTimeoutSeconds, 5, 300);
        MaxRetriesPerSegment = Math.Clamp(MaxRetriesPerSegment, 0, 20);
        SpeedLimitBytesPerSecond = Math.Max(0, SpeedLimitBytesPerSecond);
        HandoffPort = Math.Clamp(HandoffPort, 1024, 65535);
        ProxyPort = Math.Clamp(ProxyPort, 0, 65535);
        KeepInCompleteDays = Math.Clamp(KeepInCompleteDays, 0, 3650);

        foreach (var host in HostConnectionLimits.Keys.ToList())
            HostConnectionLimits[host] = Math.Clamp(HostConnectionLimits[host], 1, SegmentedDownloader.MaxConnections);

        if (string.IsNullOrWhiteSpace(UserAgent))
            UserAgent = DefaultUserAgent;
    }

    public void Save()
    {
        try
        {
            Clamp();
            CategoryRules.Use(Categories);
            Directory.CreateDirectory(DownloadStore.Directory);
            AtomicFile.WriteAllText(Path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception exception) when (exception is JsonException or IOException)
        {
            AppLog.Write($"Could not save settings: {exception.Message}");
        }
    }
}
