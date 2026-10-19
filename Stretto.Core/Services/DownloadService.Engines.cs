
namespace Stretto.Core;

/// <summary>The three engines besides the segmented HTTP one: yt-dlp, FTP and ffmpeg streams.</summary>
public sealed partial class DownloadService
{
    /// <summary>
    /// Hands a video page to yt-dlp. No progress in bytes here: yt-dlp reports a percentage
    /// of something it knows and we do not, so the item carries a fraction instead.
    /// </summary>
    private async Task RunSiteVideoAsync(DownloadItem item, IProgress<ProgressSnapshot> progress, CancellationToken ct)
    {
        item.Status = DownloadStatus.Downloading;

        if (item.SortByType)
            item.TargetDirectory = DirectoryFor("Video");

        var fractions = new Progress<double>(fraction => progress.Report(new ProgressSnapshot
        {
            BytesCompleted = 0,
            BytesPerSecond = 0,
            ActiveConnections = 1,
            FractionOverride = fraction,
        }));

        var path = await new YtDlp(ToolBox.Resolve(Tool.YtDlp))
            .DownloadAsync(item.Url, item.TargetDirectory, fractions, ct, item.FormatId)
            .ConfigureAwait(true);

        item.FilePath = path;
        item.Name = Path.GetFileName(path);
        item.TotalBytes = new FileInfo(path).Length;
        item.BytesCompleted = item.TotalBytes ?? 0;
        item.ProgressOverride = 1;
        item.BytesPerSecond = 0;
        item.CompletedAt = DateTimeOffset.Now;
        item.AutoRetries = 0;
        item.Status = DownloadStatus.Complete;
        Completed?.Invoke(item);
    }

    private async Task RunFtpAsync(DownloadItem item, IProgress<ProgressSnapshot> progress, CancellationToken ct)
    {
        item.Status = DownloadStatus.Downloading;

        if (item.SortByType)
            item.TargetDirectory = DirectoryFor(item.Category);

        var login = string.IsNullOrEmpty(item.Username)
            ? SiteCredentials.For(item.Url)
            : new SiteCredentials.Login(item.Username, item.Password ?? "");

        var result = await _ftp.DownloadAsync(new DownloadRequest
        {
            Url = new Uri(item.Url),
            TargetDirectory = item.TargetDirectory,
            FileName = item.PreferredFileName,
            BandwidthLimiter = _bandwidthLimiter,
            // FTP carries no cookies, so this field ferries the login instead.
            Cookie = login is null ? null : $"{login.Username}:{login.Password}",
            Overwrite = _settings.OverwriteExisting,
            UseServerTimestamp = _settings.UseServerTimestamp,
        }, progress, ct).ConfigureAwait(true);

        item.FilePath = result.FilePath;
        item.Name = Path.GetFileName(result.FilePath);
        item.TotalBytes = result.BytesWritten;
        item.BytesCompleted = result.BytesWritten;
        item.BytesPerSecond = 0;
        item.CompletedAt = DateTimeOffset.Now;
        item.AutoRetries = 0;
        item.Status = DownloadStatus.Complete;
        Completed?.Invoke(item);
    }

    /// <summary>Streams have no byte ranges to resume from, so ffmpeg owns the whole pull.</summary>
    /// <summary>An HLS or DASH playlist by its address or by the type the browser saw.</summary>
    private static bool LooksLikePlaylist(DownloadItem item)
    {
        var mime = item.Mime ?? "";
        if (mime.Contains("mpegurl", StringComparison.OrdinalIgnoreCase)
            || mime.Contains("dash+xml", StringComparison.OrdinalIgnoreCase))
            return true;

        return Uri.TryCreate(item.Url, UriKind.Absolute, out var url)
            && (url.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase)
                || url.AbsolutePath.EndsWith(".mpd", StringComparison.OrdinalIgnoreCase));
    }

    private async Task RunStreamAsync(DownloadItem item, IProgress<ProgressSnapshot> progress, CancellationToken ct)
    {
        item.Status = DownloadStatus.Downloading;

        // A remuxed stream is always a video file, whatever the playlist URL looked like.
        if (item.SortByType)
            item.TargetDirectory = DirectoryFor("Video");

        // A pasted playlist may not have a name yet, and "master.m3u8" says nothing, so
        // fall back to the address's last part and let the downloader swap the extension.
        var url = new Uri(item.Url);
        var name = item.PreferredFileName ?? item.Name;
        if (string.IsNullOrWhiteSpace(name))
            name = Path.GetFileName(url.AbsolutePath) is { Length: > 0 } last ? last : url.Host;

        var result = await _streams.DownloadAsync(
            url,
            item.TargetDirectory,
            name,
            item.Cookie,
            item.Referrer,
            item.UserAgent,
            progress,
            ct,
            item.Headers,
            overwrite: _settings.OverwriteExisting).ConfigureAwait(true);

        AppLog.Write($"Stream finished: {result.BytesWritten / 1024} KB in {result.Elapsed.TotalSeconds:F0} s");

        item.FilePath = result.FilePath;
        item.Name = Path.GetFileName(result.FilePath);
        item.TotalBytes = result.BytesWritten;
        item.BytesCompleted = result.BytesWritten;
        item.ProgressOverride = 1;
        item.BytesPerSecond = 0;
        item.CompletedAt = DateTimeOffset.Now;
        item.AutoRetries = 0;
        item.Status = DownloadStatus.Complete;
        Completed?.Invoke(item);
    }

    /// <summary>
    /// A download link that answers with a web page has almost always bounced to a sign-in
    /// wall — civitai and most model hosts do exactly this when the session cookie is
    /// missing. Saving that HTML under the file's name is worse than failing, because it
    /// looks like it worked until the file is opened.
    /// </summary>
    private static void GuardAgainstSignInPage(DownloadItem item, RemoteFileInfo info)
    {
        if (info.ContentType is not { } type
            || !type.StartsWith("text/html", StringComparison.OrdinalIgnoreCase))
            return;

        // Unless the user actually asked for a web page.
        var wanted = (item.PreferredFileName ?? item.Name).ToLowerInvariant();
        if (wanted.EndsWith(".html") || wanted.EndsWith(".htm"))
            return;

        var redirected = !string.Equals(info.FinalUrl.Host, new Uri(item.Url).Host,
            StringComparison.OrdinalIgnoreCase);

        throw new InvalidOperationException(redirected
            ? $"Redirected to {info.FinalUrl.Host} and got a web page instead of a file. "
              + "This link needs a signed-in session, so download it through the browser extension."
            : "The server returned a web page instead of a file. The link may need a signed-in session.");
    }
}
