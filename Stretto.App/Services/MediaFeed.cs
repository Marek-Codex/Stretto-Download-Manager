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

    /// <summary>The page's title, when the extension had one. The saved file's name is chosen
    /// from it when the video is downloaded.</summary>
    public string? PageTitle { get; init; }

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
        var kind = Kind == "stream" && ((Mime?.Contains("dash+xml", StringComparison.OrdinalIgnoreCase) ?? false)
            || new Uri(Url).AbsolutePath.EndsWith(".mpd", StringComparison.OrdinalIgnoreCase))
            ? MediaKind.Dash
            : Kind == "stream" ? MediaKind.Hls
            : MediaKind.Direct;

        return new MediaCandidate
        {
            Url = new Uri(Url),
            Label = Name,
            PageTitle = PageTitle,
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

        // The same video reported again is usually the extension catching up: the page's
        // title arrives after the first sighting. Take the newer name and title, keep
        // everything else.
        var seen = Items.ToList().FindIndex(m => m.Url == report.Url);
        if (seen >= 0)
        {
            var current = Items[seen];
            var name = !string.IsNullOrWhiteSpace(report.Name) && report.Name != uri.Host ? report.Name : current.Name;
            var title = string.IsNullOrWhiteSpace(report.PageTitle) ? current.PageTitle : report.PageTitle;

            Items[seen] = current with
            {
                Name = name, PageTitle = title,
                Cookie = report.Cookie ?? current.Cookie,
                Referrer = report.Referrer ?? current.Referrer,
                UserAgent = report.UserAgent ?? current.UserAgent,
                Headers = report.Headers ?? current.Headers,
                Mime = report.Mime ?? current.Mime,
            };

            return;
        }

        Items.Insert(0, new FedMedia
        {
            Url = report.Url,
            Name = string.IsNullOrWhiteSpace(report.Name) ? uri.Host : report.Name,
            PageTitle = string.IsNullOrWhiteSpace(report.PageTitle) ? null : report.PageTitle,
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
