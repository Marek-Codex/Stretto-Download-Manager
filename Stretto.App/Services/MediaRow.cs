using Stretto.Core;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Stretto.App.Services;

/// <summary>
/// One form a piece of media is available in: an HLS program in its master playlist,
/// or a yt-dlp format, which is a name only yt-dlp can turn into one.
/// </summary>
public sealed record FormatOption(string Label, Uri? Url = null, string? FormatId = null, int? HlsProgramIndex = null)
{
    /// <summary>The entry that means "whatever the source picks".</summary>
    public static FormatOption Auto { get; } = new("Auto");

    public bool IsAuto => Url is null && FormatId is null && HlsProgramIndex is null;
}

/// <summary>
/// A row in the media grabber or the video finder, with the qualities it can be had in.
///
/// Shared by both windows so the lazy loading below exists once. Fetching formats means a
/// network round trip or a yt-dlp process, so it happens when a row is actually looked at,
/// never for a whole list at once -- a playlist of two hundred entries would otherwise be
/// two hundred process launches before anything appeared.
/// </summary>
public sealed class MediaRow(MediaCandidate candidate) : INotifyPropertyChanged
{
    private FormatOption? _chosen;

    public MediaCandidate Candidate { get; private set; } = candidate;

    public void UpdateCandidate(MediaCandidate value)
    {
        Candidate = value;
        Notify(nameof(Name));
        Notify(nameof(Address));
    }

    public ObservableCollection<FormatOption> Formats { get; } = [];

    public bool FormatsLoaded { get; private set; }

    public string Name => Candidate.Label;

    public string Address => Candidate.Url.ToString();

    /// <summary>What the Quality column shows before anything has been looked up.</summary>
    public FormatOption? Chosen
    {
        get => _chosen;
        set
        {
            if (_chosen == value) return;
            _chosen = value;
            Notify();
            Notify(nameof(QualityText));
        }
    }

    public string QualityText => Chosen is { IsAuto: false } chosen ? chosen.Label : "Auto";

    /// <summary>What actually gets downloaded once a quality has been picked.</summary>
    public MediaCandidate Effective => Chosen is not { IsAuto: false } chosen
        ? Candidate
        : Candidate with
        {
            Url = chosen.Url ?? Candidate.Url,
            FormatId = chosen.FormatId ?? Candidate.FormatId,
            HlsProgramIndex = chosen.HlsProgramIndex ?? Candidate.HlsProgramIndex,
            Label = $"{Candidate.Label} · {chosen.Label}",
        };

    /// <summary>
    /// Fetches the qualities, once. A direct file has only one form, so only playlists and
    /// site pages have anything to ask about.
    /// </summary>
    public async Task LoadFormatsAsync(HttpClient http, CancellationToken ct = default)
    {
        if (FormatsLoaded)
            return;

        FormatsLoaded = true;

        try
        {
            switch (Candidate.Kind)
            {
                case MediaKind.Hls:
                    var variants = await HlsPlaylist.FetchVariantsAsync(
                        http, Candidate.Url, Candidate.Cookie, Candidate.Referrer, ct,
                        Candidate.UserAgent, Candidate.Headers);

                    // One rendition is not a choice, so it is not offered as one.
                    if (variants.Count > 1)
                    {
                        Formats.Add(FormatOption.Auto);

                        foreach (var variant in variants)
                            Formats.Add(new FormatOption(variant.Label, HlsProgramIndex: variant.ProgramIndex));
                    }

                    break;

                case MediaKind.Site when await ToolBox.IsAvailableAsync(Tool.YtDlp):
                    var formats = await new YtDlp(ToolBox.Resolve(Tool.YtDlp))
                        .ListFormatsAsync(Candidate.Url.ToString(), ct);

                    if (formats.Count > 0)
                    {
                        Formats.Add(FormatOption.Auto);

                        foreach (var format in formats)
                            Formats.Add(new FormatOption(format.Label, null, format.Id));
                    }

                    break;
            }
        }
        catch (Exception exception)
        {
            // No qualities is a usable answer: the row downloads at whatever the source
            // picks, which is what happened before any of this existed.
            App.Log($"Could not read formats for {Address}: {exception.Message}");
        }

        Notify(nameof(HasFormats));
        Notify(nameof(FormatsVisibility));
    }

    public bool HasFormats => Formats.Count > 0;

    /// <summary>The quality picker only appears once there is something to pick between;
    /// an empty drop-down on every direct file read as broken.</summary>
    public Microsoft.UI.Xaml.Visibility FormatsVisibility => HasFormats
        ? Microsoft.UI.Xaml.Visibility.Visible
        : Microsoft.UI.Xaml.Visibility.Collapsed;

    /// <summary>What kind of thing this is, for rows that need explaining.</summary>
    public string Badge => Candidate.Kind switch
    {
        MediaKind.Hls => "HLS",
        MediaKind.Dash => "DASH",
        MediaKind.Site => "SITE",
        _ => "",
    };

    /// <summary>A plain file needs no badge. Exposed as Visibility because the row
    /// templates use Binding, which has no bool conversion of its own.</summary>
    public Microsoft.UI.Xaml.Visibility BadgeVisibility => Badge.Length > 0
        ? Microsoft.UI.Xaml.Visibility.Visible
        : Microsoft.UI.Xaml.Visibility.Collapsed;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Notify([CallerMemberName] string? property = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
}
