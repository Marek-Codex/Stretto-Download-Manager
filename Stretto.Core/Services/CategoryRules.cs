using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace Stretto.Core;

/// <summary>
/// One row of the Download categories table: a name, the file types that land in it, and
/// where those files go.
/// </summary>
public sealed class CategoryRule : INotifyPropertyChanged
{
    private string _name = "";
    private List<string> _extensions = [];
    private string _folder = "";

    public string Name
    {
        get => _name;
        set { if (_name == value) return; _name = value; Notify(); Notify(nameof(FolderText)); }
    }

    /// <summary>Extensions without the dot, lower case.</summary>
    public List<string> Extensions
    {
        get => _extensions;
        set { _extensions = value; Notify(); Notify(nameof(ExtensionsText)); }
    }

    /// <summary>Empty means a subfolder named after the category, under the download folder.</summary>
    public string Folder
    {
        get => _folder;
        set { if (_folder == value) return; _folder = value; Notify(); Notify(nameof(FolderText)); }
    }

    /// <summary>Key into the icon dictionary. Custom categories get the generic file icon.</summary>
    public string Icon { get; set; } = "IconFile";

    [JsonIgnore]
    public string ExtensionsText
    {
        get => string.Join(", ", Extensions);
        set => Extensions = [.. value
            .Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(e => e.TrimStart('.').ToLowerInvariant())
            .Distinct()];
    }

    /// <summary>What the table shows in the folder column: the custom folder, or the
    /// real default with a suffix saying so rather than a parenthesised pseudo-path.</summary>
    [JsonIgnore]
    public string FolderText => Folder.Length > 0 ? Folder : $"Downloads\\{Name} (default)";

    public bool Matches(string extension) => Extensions.Contains(extension, StringComparer.OrdinalIgnoreCase);

    /// <summary>A detached copy, so Settings can be cancelled without having edited the
    /// live table underneath.</summary>
    public CategoryRule Clone() => new()
    {
        Name = Name,
        Extensions = [.. Extensions],
        Folder = Folder,
        Icon = Icon,
    };

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Notify([CallerMemberName] string? property = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
}

/// <summary>
/// The live category table.
///
/// Static because <see cref="Models.DownloadItem"/> classifies itself and has no settings
/// reference -- it is a row in a list, not a service consumer. One assignment at startup
/// keeps that true while still letting the table be edited.
/// </summary>
public static class CategoryRules
{
    /// <summary>Everything that matches no rule. Not in the table, because it is the
    /// absence of a match rather than a rule of its own.</summary>
    public const string Fallback = "Other";

    public static IReadOnlyList<CategoryRule> Active { get; private set; } = Defaults();

    public static void Use(IEnumerable<CategoryRule> rules)
    {
        var usable = rules.Where(r => r.Name.Length > 0).ToList();
        Active = usable.Count > 0 ? usable : Defaults();
    }

    /// <summary>The category for a file name, or <see cref="Fallback"/>.</summary>
    public static string Classify(string fileName, string? mime = null)
    {
        var extension = Path.GetExtension(fileName).TrimStart('.').ToLowerInvariant();

        // No extension in the name at all, which is what an API-style download endpoint
        // looks like: civitai.com/api/download/models/123. Until the probe returns the
        // server's own filename there is no other type to go on, so the Content-Type the
        // browser saw is used instead. A name that does carry an extension always wins.
        if (extension.Length == 0)
            extension = ExtensionFromMime(mime);

        if (extension.Length == 0)
            return Fallback;

        return Active.FirstOrDefault(r => r.Matches(extension))?.Name ?? Fallback;
    }

    /// <summary>
    /// A file extension for a Content-Type.
    ///
    /// The subtype is usually the extension already, once the vendor prefix and any
    /// structured suffix are dropped: image/jpeg is jpeg, application/dash+xml is dash,
    /// application/vnd.ms-excel is ms. The handful where that does not hold are listed
    /// rather than guessed at, and anything that comes out as nonsense is refused, since
    /// a wrong extension files a download under the wrong category just as surely as none.
    /// </summary>
    public static string ExtensionFromMime(string? mime)
    {
        if (string.IsNullOrWhiteSpace(mime))
            return "";

        var type = mime.Split(';')[0].Trim();
        if (MimeOverrides.TryGetValue(type, out var known))
            return known;

        var slash = type.IndexOf('/');
        if (slash < 0)
            return "";

        var subtype = type[(slash + 1)..].Split('+')[0];
        var dash = subtype.IndexOf('-');
        if (dash > 0)
            subtype = subtype[..dash];
        subtype = subtype.Trim().ToLowerInvariant();

        return subtype.Length is >= 2 and <= 5 && subtype.All(char.IsAsciiLetter) ? subtype : "";
    }

    private static readonly Dictionary<string, string> MimeOverrides = new(StringComparer.OrdinalIgnoreCase)
    {
        // "Unknown binary" says nothing about the type, so nothing is claimed from it.
        ["application/octet-stream"] = "",
        ["binary/octet-stream"] = "",

        // Would otherwise read as "form", a plausible-looking extension for a container
        // format that is never itself the download.
        ["multipart/form-data"] = "",

        ["application/x-msdownload"] = "exe",
        ["application/vnd.microsoft.portable-executable"] = "exe",
        ["application/x-msdos-program"] = "exe",
        ["application/x-msi"] = "msi",
        ["application/x-7z-compressed"] = "7z",
        ["application/x-rar-compressed"] = "rar",
        ["application/vnd.rar"] = "rar",
        ["application/x-gzip"] = "gz",
        ["application/gzip"] = "gz",
        ["application/x-tar"] = "tar",
        ["application/x-bzip"] = "bz2",
        ["application/x-bzip2"] = "bz2",
        ["application/vnd.apple.mpegurl"] = "m3u8",
        ["application/x-mpegurl"] = "m3u8",
        ["application/vnd.ms-excel"] = "xls",
        ["application/vnd.ms-powerpoint"] = "ppt",
        ["application/msword"] = "doc",
        ["application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"] = "xlsx",
        ["application/vnd.openxmlformats-officedocument.wordprocessingml.document"] = "docx",
        ["application/json"] = "json",
        ["text/html"] = "html",
        ["text/plain"] = "txt",
    };

    public static CategoryRule? Find(string name) =>
        Active.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));

    public static List<CategoryRule> Defaults() =>
    [
        new()
        {
            Name = "Document",
            Icon = "IconDocument",
            Extensions = ["pdf", "doc", "docx", "txt", "xls", "xlsx", "ppt", "pptx", "csv", "rtf", "odt", "md"],
        },
        new()
        {
            Name = "eBook",
            Icon = "IconEBook",
            // Comic archives are zip or rar underneath, but nobody looking for them looks
            // in Compressed.
            Extensions = ["epub", "mobi", "azw", "azw3", "fb2", "djvu", "cbz", "cbr"],
        },
        new()
        {
            Name = "Music",
            Icon = "IconMusic",
            Extensions = ["mp3", "flac", "wav", "m4a", "aac", "ogg", "opus", "wma"],
        },
        new()
        {
            Name = "Video",
            Icon = "IconVideo",
            Extensions = ["mp4", "mkv", "avi", "mov", "webm", "flv", "m4v", "wmv"],
        },
        new()
        {
            Name = "Image",
            Icon = "IconImage",
            Extensions = ["jpg", "jpeg", "png", "gif", "webp", "bmp", "svg", "avif", "heic", "tif", "tiff", "ico", "psd"],
        },
        new()
        {
            Name = "Compressed",
            Icon = "IconCompressed",
            Extensions = ["zip", "rar", "7z", "tar", "gz", "bz2", "xz", "iso"],
        },
        new()
        {
            Name = "Application",
            Icon = "IconApplication",
            Extensions = ["exe", "msi", "msix", "appx", "bat", "cmd", "apk"],
        },
    ];
}
