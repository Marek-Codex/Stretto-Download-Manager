
namespace Stretto.Core;

/// <summary>Maps a detected media candidate to the shared download engine's options.</summary>
public static class MediaDownloadOptions
{
    public static DownloadOptions FromCandidate(
        MediaCandidate candidate,
        AppSettings settings,
        bool? startNow = null,
        string? directory = null) => new()
    {
        Directory = directory,
        FileName = candidate.FileName,
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
