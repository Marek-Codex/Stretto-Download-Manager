namespace Stretto.Core;

/// <summary>Maps a detected media candidate to the shared download engine's options.</summary>
public static class MediaDownloadOptions
{
    public static DownloadOptions FromCandidate(
        MediaCandidate candidate,
        AppSettings settings,
        bool? startNow = null,
        string? directory = null)
    {
        // Named at download time, from the page's title, so a title that arrived after the
        // video was first seen is still used. Null leaves the name to the server.
        var titled = FileNaming.Choose(
            null, candidate.Url, candidate.PageTitle, settings.NameFrom, settings.VideoTypes);

        return new DownloadOptions
        {
            Directory = directory,
            FileName = titled,
            NamedFromTitle = titled is not null,
            IsStream = candidate.NeedsMuxing,
            IsSiteVideo = candidate.Kind == MediaKind.Site,
            FormatId = candidate.FormatId,
            Connections = settings.DefaultConnections,
            SortByType = settings.SortByType && directory is null,
            StartNow = startNow ?? settings.StartAutomatically,
            Cookie = candidate.Cookie,
            Referrer = candidate.Referrer,
            UserAgent = candidate.UserAgent,
            Headers = candidate.Headers,
        };
    }
}
