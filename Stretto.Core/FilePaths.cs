namespace Stretto.Core;

public static class FilePaths
{
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>
    /// A name a server, page or extension suggested, made safe to put in a folder. Names
    /// arrive from places that do not get to choose the path: no separators, no "..", no
    /// device names, nothing Windows would quietly trim into a different file.
    /// </summary>
    public static string SafeName(string? name, string fallback = "download")
    {
        if (string.IsNullOrWhiteSpace(name))
            return fallback;

        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = string.Concat(name.Trim().Select(c => invalid.Contains(c) ? '_' : c))
                            .TrimEnd('.', ' ');

        if (cleaned.Length == 0)
            return fallback;

        var stem = cleaned.Split('.')[0].TrimEnd(' ');
        if (ReservedNames.Contains(stem))
            cleaned = "_" + cleaned;

        // NTFS caps a component at 255 UTF-16 units. Keep the extension when trimming.
        if (cleaned.Length > 200)
        {
            var extension = Path.GetExtension(cleaned);
            if (extension.Length > 20) extension = "";
            cleaned = cleaned[..(200 - extension.Length)].TrimEnd('.', ' ') + extension;
        }

        return cleaned;
    }

    /// <summary>
    /// Picks the file a download writes to. Without <paramref name="overwrite"/>, an existing
    /// file pushes the download to "name (2).ext", "name (3).ext" and so on -- unless
    /// <paramref name="isOwnPartial"/> says the file already there is this download's own work
    /// in progress, in which case it is resumed rather than avoided.
    ///
    /// With a <paramref name="tag"/> the first try is "name_tag.ext" -- a mark that says
    /// which source it came from -- and numbering only starts if that is taken as well.
    /// </summary>
    public static string ClashFree(
        string directory, string name, bool overwrite, Func<string, bool>? isOwnPartial = null,
        string? tag = null)
    {
        name = SafeName(name);
        var path = Path.Combine(directory, name);
        if (overwrite || !File.Exists(path) || isOwnPartial?.Invoke(path) == true)
            return path;

        var stem = Path.GetFileNameWithoutExtension(name);
        var extension = Path.GetExtension(name);

        if (!string.IsNullOrEmpty(tag))
        {
            stem = $"{stem}_{tag}";
            var tagged = Path.Combine(directory, stem + extension);
            if (!File.Exists(tagged))
                return tagged;
        }

        for (var n = 2; n < 10_000; n++)
        {
            var candidate = Path.Combine(directory, $"{stem} ({n}){extension}");
            if (!File.Exists(candidate))
                return candidate;
        }

        // Ten thousand copies of one name is not a real folder. A unique name still beats
        // quietly overwriting somebody's file, which is what returning the path would do.
        return Path.Combine(directory, $"{stem} ({Guid.NewGuid():N}){extension}");
    }

    /// <summary>
    /// Writes the Mark of the Web a browser would: the Zone.Identifier stream that makes
    /// SmartScreen check a program and Office open a document in Protected View. Without it,
    /// a download manager quietly launders internet files into "local" ones. The addresses
    /// are kept to scheme, host and path, since query strings often carry access tokens.
    /// Volumes without alternate streams (FAT, some network shares) simply go unmarked.
    /// </summary>
    public static void MarkFromInternet(string path, string? url, string? referrer)
    {
        static string? Bare(string? address)
        {
            if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https" or "ftp"))
                return null;
            var publicAddress = new UriBuilder(uri) { UserName = "", Password = "", Query = "", Fragment = "" };
            return publicAddress.Uri.GetLeftPart(UriPartial.Path);
        }

        var text = new System.Text.StringBuilder("[ZoneTransfer]\r\nZoneId=3\r\n");
        if (Bare(referrer) is { } from) text.Append($"ReferrerUrl={from}\r\n");
        if (Bare(url) is { } host) text.Append($"HostUrl={host}\r\n");

        try
        {
            if (File.Exists(path))
                File.WriteAllText(path + ":Zone.Identifier", text.ToString());
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            AppLog.Write($"Could not mark {Path.GetFileName(path)} as downloaded: {exception.Message}");
        }
    }
}
