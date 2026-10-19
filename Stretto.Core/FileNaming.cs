using System.Text.RegularExpressions;

namespace Stretto.Core;

/// <summary>
/// Decides what a captured video is called. The browser extension only reports what it
/// saw -- the file's own name and the title of the page -- and this is the one place that
/// chooses between them, so the rule is in one language, next to the settings, and testable.
/// </summary>
public static partial class FileNaming
{
    /// <summary>Two real words, or a space: a name somebody wrote rather than a server generated.</summary>
    [GeneratedRegex(@"\s|[A-Za-z]{3,}[_.\-][A-Za-z]{3,}")]
    private static partial Regex ReadsLikeWords();

    /// <summary>A playlist's stock name, which says nothing about the video.</summary>
    [GeneratedRegex(@"^(master|index|playlist|manifest|chunklist|video|audio|media|stream)[\w-]*$", RegexOptions.IgnoreCase)]
    private static partial Regex StockPlaylistName();

    /// <summary>A name this long with no readable word in it is an id, not something anyone wrote.</summary>
    private const int ChaoticLength = 12;

    /// <summary>
    /// Whether a file name is one of the wildly random ones some servers hand out: a long
    /// string with no word in it. Also a stream playlist's stock name, or no extension at all.
    /// Deliberately strict, because the page's title replaces the name: <c>movie.mp4</c> and
    /// <c>clip01.mp4</c> are somebody's names and stay.
    /// </summary>
    public static bool IsGeneric(string name)
    {
        var extension = Path.GetExtension(name);
        if (extension.Length is < 3 or > 6)
            return true;

        var stem = Path.GetFileNameWithoutExtension(name);
        if (extension is ".m3u8" or ".m3u" or ".mpd" && StockPlaylistName().IsMatch(stem))
            return true;

        return stem.Length >= ChaoticLength && !ReadsLikeWords().IsMatch(stem);
    }

    /// <summary>
    /// The name to save a captured file under, or <paramref name="original"/> when the title
    /// should not be used. Only video is renamed, since a zip or a pdf from a video page has
    /// nothing to do with the page's title. Smart keeps a name that already reads like words;
    /// Title always uses the page; File never does.
    /// </summary>
    public static string? Choose(
        string? original, Uri url, string? pageTitle, NameSource source, IEnumerable<string> videoTypes)
    {
        if (source == NameSource.File || string.IsNullOrWhiteSpace(pageTitle))
            return original;

        var name = !string.IsNullOrWhiteSpace(original)
            ? original
            : Path.GetFileName(Uri.UnescapeDataString(url.AbsolutePath));

        var extension = Path.GetExtension(name).TrimStart('.').ToLowerInvariant();
        if (extension.Length == 0 || !videoTypes.Any(t => string.Equals(t, extension, StringComparison.OrdinalIgnoreCase)))
            return original;

        if (source != NameSource.Title && !IsGeneric(name))
            return original;

        var title = Sanitize(pageTitle);
        return title.Length == 0 ? original : $"{title}.{extension}";
    }

    /// <summary>
    /// A page title as a file name that no program will complain about: letters, digits and
    /// hyphens stay (in any language), apostrophes disappear, and any other run of characters
    /// -- spaces, punctuation, symbols -- becomes one underscore. "Funny Cat Video, Part 2!"
    /// is "Funny_Cat_Video_Part_2". Not too long, and never starting or ending on a separator.
    /// </summary>
    public static string Sanitize(string title)
    {
        // " - " between words is a separator, not part of a word ("Cats - Dogs" is Cats_Dogs).
        var text = Regex.Replace(title.Replace("'", "").Replace("\u2019", ""), @"\s+[-\u2013\u2014]\s+", "_");

        var builder = new System.Text.StringBuilder(text.Length);
        foreach (var c in text)
            builder.Append(char.IsLetterOrDigit(c) || c == '-' ? c : '_');

        var cleaned = Regex.Replace(builder.ToString(), "_{2,}", "_").Trim('_', '-');
        return cleaned.Length > 120 ? cleaned[..120].TrimEnd('_', '-') : cleaned;
    }
}
