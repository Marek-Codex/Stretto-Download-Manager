namespace Stretto.App.Services;

/// <summary>How a new download should be set up. Mirrors the choices on the New dialog.</summary>
public sealed record DownloadOptions
{
    /// <summary>Explicit folder. Ignored when <see cref="SortByType"/> is set.</summary>
    public string? Directory { get; init; }

    /// <summary>Route into a per-category subfolder of Downloads, as XDM's
    /// "automatically select based on file type" does.</summary>
    public bool SortByType { get; init; } = true;

    /// <summary>Overrides the filename the server suggests.</summary>
    public string? FileName { get; init; }

    public int Connections { get; init; } = 8;

    /// <summary>False is "Download Later": it sits in the list until asked to start.</summary>
    public bool StartNow { get; init; } = true;

    /// <summary>Request context handed over by the browser extension.</summary>
    public string? Cookie { get; init; }
    public string? Referrer { get; init; }
    public string? UserAgent { get; init; }

    /// <summary>
    /// The browser's remaining request headers, for media the extension watched go out.
    /// Needed only by sites that gate a file behind something other than a cookie. In
    /// memory only, like <see cref="Cookie"/>.
    /// </summary>
    public Dictionary<string, string>? Headers { get; init; }

    /// <summary>
    /// The browser's Content-Type, for a download whose address has no extension. Only
    /// consulted for the category before the probe returns the server's filename.
    /// </summary>
    public string? Mime { get; init; }

    /// <summary>An HLS or DASH playlist, which ffmpeg handles instead of the engine.</summary>
    public bool IsStream { get; init; }

    /// <summary>A video site page, which yt-dlp handles instead of the engine.</summary>
    public bool IsSiteVideo { get; init; }

    /// <summary>The yt-dlp format picked in the grabber, if any.</summary>
    public string? FormatId { get; init; }

    /// <summary>Credentials for a site that challenges for HTTP authentication.</summary>
    public string? Username { get; init; }
    public string? Password { get; init; }

    /// <summary>
    /// Per-download proxy override. Null means "use the global settings", which is the
    /// default: a proxy is normally a property of how this machine reaches the internet,
    /// not of one file. Set to <see cref="ProxyMode.None"/> for direct or
    /// <see cref="ProxyMode.Manual"/> with <see cref="ProxyHost"/> etc. for this download only.
    /// </summary>
    public ProxyMode? ProxyModeOverride { get; init; }
    public ProxyProtocol? ProxyProtocolOverride { get; init; }
    public string? ProxyHost { get; init; }
    public int ProxyPort { get; init; }
    public string? ProxyUsername { get; init; }
    public string? ProxyPassword { get; init; }
}
