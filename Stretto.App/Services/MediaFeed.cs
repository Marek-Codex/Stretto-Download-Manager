using Stretto.Core;
using System.Collections.ObjectModel;

namespace Stretto.App.Services;

/// <summary>Media the extension has watched pass by, kept for the media grabber.
///
/// In-memory only: sightings go stale the moment the page is gone, so persisting
/// them would just show yesterday's videos. The extension re-reports anything
/// still playing.</summary>
public sealed record FedMedia
{
    public required string Url { get; init; }
    public required string Name { get; init; }

    /// <summary>stream, video or audio, as classified by the extension.</summary>
    public required string Kind { get; init; }

    public string? Mime { get; init; }
    public long Size { get; init; }
    public string? Cookie { get; init; }
    public string? Referrer { get; init; }
    public string? UserAgent { get; init; }
    public Dictionary<string, string>? Headers { get; init; }

    /// <summary>As a grabber row. A reported stream goes to ffmpeg even when its URL
    /// carries no telling extension, which direct master links often do not.</summary>
    public MediaCandidate ToCandidate()
    {
        var kind = Kind == "stream" && Url.EndsWith(".mpd", StringComparison.OrdinalIgnoreCase)
            ? MediaKind.Dash
            : Kind == "stream" ? MediaKind.Hls
            : MediaKind.Direct;

        return new MediaCandidate
        {
            Url = new Uri(Url),
            Label = Name,
            Kind = kind,
            Source = "browser",
            Cookie = Cookie,
            Referrer = Referrer,
            UserAgent = UserAgent,
            Headers = Headers,
        };
    }
}

/// <summary>Newest first, deduplicated by URL, capped. Add on the UI thread:
/// the dialog binds straight onto <see cref="Items"/>.</summary>
public sealed class MediaFeed
{
    public ObservableCollection<FedMedia> Items { get; } = [];

    public void Add(MediaReport report)
    {
        if (!Uri.TryCreate(report.Url, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https"))
            return;

        if (Items.Any(m => m.Url == report.Url))
            return;

        Items.Insert(0, new FedMedia
        {
            Url = report.Url,
            Name = string.IsNullOrWhiteSpace(report.Name) ? uri.Host : report.Name,
            Kind = report.Kind ?? "video",
            Mime = report.Mime,
            Size = report.Size ?? 0,
            Cookie = report.Cookie,
            Referrer = report.Referrer,
            UserAgent = report.UserAgent,
            Headers = report.Headers,
        });

        while (Items.Count > 100)
            Items.RemoveAt(Items.Count - 1);
    }

    public void Clear() => Items.Clear();
}
