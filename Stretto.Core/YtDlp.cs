using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Stretto.Core;

/// <summary>One entry of a playlist, or a single video.</summary>
public sealed record PlaylistEntry
{
    public required string Url { get; init; }
    public required string Title { get; init; }
    public TimeSpan? Duration { get; init; }
}

/// <summary>One downloadable form of a video, as yt-dlp reports it.</summary>
public sealed record MediaFormat
{
    public required string Id { get; init; }
    public int? Height { get; init; }
    public string? Extension { get; init; }
    public long? Size { get; init; }

    /// <summary>True when the format carries no video, which is how audio only is offered.</summary>
    public bool AudioOnly { get; init; }

    /// <summary>True when it carries no audio, so it has to be merged with an audio track.</summary>
    public bool VideoOnly { get; init; }

    public string? Note { get; init; }

    public string Label
    {
        get
        {
            var quality = AudioOnly
                ? "Audio only"
                : Height is { } height ? $"{height}p" : Note ?? "Unknown";

            var parts = new List<string> { quality };

            if (!string.IsNullOrEmpty(Extension))
                parts.Add(Extension);

            if (Size is { } bytes and > 0)
                parts.Add($"{bytes / 1024d / 1024:0.#} MB");

            return string.Join(" · ", parts);
        }
    }
}

/// <summary>
/// Site aware video downloading, delegated to yt-dlp.
///
/// Deliberately not reimplemented. Pulling a video off a site like YouTube means parsing
/// whatever the page ships today and undoing whatever signature scheme it uses, and both
/// change without notice; yt-dlp exists precisely because that is a full time job. Writing
/// our own would mean a feature that works the week it ships and rots quietly after.
///
/// So this shells out, exactly as <see cref="StreamDownloader"/> does for ffmpeg, and the
/// feature simply reports itself unavailable when yt-dlp is not installed.
/// </summary>
public sealed partial class YtDlp(string? executable = null)
{
    private readonly string _exe = executable ?? "yt-dlp";

    public static bool IsAvailable(string executable = "yt-dlp")
    {
        try
        {
            using var probe = Process.Start(new ProcessStartInfo(executable, "--version")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            });

            if (probe is null)
                return false;

            probe.WaitForExit(5000);
            return probe.HasExited && probe.ExitCode == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Lists what is behind a URL: every video of a playlist, or the single video itself.
    ///
    /// Flat listing, so this asks the site for the index only and does not resolve every
    /// entry. A long playlist would otherwise take a minute before showing anything.
    /// </summary>
    public async Task<IReadOnlyList<PlaylistEntry>> ListAsync(string url, CancellationToken ct = default)
    {
        var (output, _, exitCode) = await RunAsync(
            ["--flat-playlist", "--dump-single-json", "--no-warnings", url], ct).ConfigureAwait(false);

        if (exitCode != 0 || output.Length == 0)
            return [];

        try
        {
            using var document = JsonDocument.Parse(output);
            var root = document.RootElement;

            if (root.TryGetProperty("entries", out var entries) && entries.ValueKind == JsonValueKind.Array)
            {
                return [.. entries.EnumerateArray()
                    .Select(ToEntry)
                    .Where(e => e is not null)
                    .Select(e => e!)];
            }

            // Not a playlist: a single video described by the same shape.
            return ToEntry(root) is { } single ? [single] : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static PlaylistEntry? ToEntry(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;

        // Flat entries carry "url"; a fully resolved one carries "webpage_url".
        var url = Text(element, "webpage_url") ?? Text(element, "url");
        if (string.IsNullOrWhiteSpace(url))
            return null;

        var duration = element.TryGetProperty("duration", out var seconds)
                       && seconds.ValueKind == JsonValueKind.Number
            ? TimeSpan.FromSeconds(seconds.GetDouble())
            : (TimeSpan?)null;

        return new PlaylistEntry
        {
            Url = url,
            Title = Text(element, "title") ?? url,
            Duration = duration,
        };

        static string? Text(JsonElement source, string name) =>
            source.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
    }

    /// <summary>
    /// Lists the forms one video is available in. Empty when yt-dlp cannot say, which the
    /// caller treats as "no choice to offer" rather than as a failure.
    ///
    /// Done per video and on demand: a playlist of two hundred entries would otherwise mean
    /// two hundred process launches before anything could be shown.
    /// </summary>
    public async Task<IReadOnlyList<MediaFormat>> ListFormatsAsync(string url, CancellationToken ct = default)
    {
        var (output, _, exitCode) = await RunAsync(
            ["--dump-single-json", "--no-playlist", "--no-warnings", url], ct).ConfigureAwait(false);

        return exitCode == 0 ? ParseFormats(output) : [];
    }

    /// <summary>
    /// Reads the formats out of yt-dlp's JSON. Separated from the process call so it can be
    /// tested without yt-dlp installed.
    /// </summary>
    public static IReadOnlyList<MediaFormat> ParseFormats(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];

        try
        {
            using var document = JsonDocument.Parse(json);

            if (!document.RootElement.TryGetProperty("formats", out var formats)
                || formats.ValueKind != JsonValueKind.Array)
                return [];

            var parsed = new List<MediaFormat>();

            foreach (var element in formats.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object)
                    continue;

                var id = Text(element, "format_id");
                if (string.IsNullOrEmpty(id))
                    continue;

                var video = Text(element, "vcodec");
                var audio = Text(element, "acodec");

                var audioOnly = video is null or "none";
                var videoOnly = audio is null or "none";

                // Storyboards are neither, and are listed alongside the real formats.
                if (audioOnly && videoOnly)
                    continue;

                parsed.Add(new MediaFormat
                {
                    Id = id,
                    Height = Number(element, "height") is { } height ? (int)height : null,
                    Extension = Text(element, "ext"),
                    Size = (long?)(Number(element, "filesize") ?? Number(element, "filesize_approx")),
                    AudioOnly = audioOnly,
                    VideoOnly = videoOnly,
                    Note = Text(element, "format_note"),
                });
            }

            // Best video first, then the audio only entries, which is the order someone
            // scanning for "the good one" expects.
            return [.. parsed
                .OrderBy(f => f.AudioOnly)
                .ThenByDescending(f => f.Height ?? 0)
                .ThenByDescending(f => f.Size ?? 0)];
        }
        catch (JsonException)
        {
            return [];
        }

        static string? Text(JsonElement source, string name) =>
            source.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

        static double? Number(JsonElement source, string name) =>
            source.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
                ? value.GetDouble()
                : null;
    }

    /// <summary>
    /// Downloads one video into <paramref name="directory"/> and returns the file written.
    ///
    /// yt-dlp does the whole transfer here rather than handing a URL back to our engine:
    /// most sites now serve video and audio as separate streams that have to be muxed, and
    /// the direct URLs are short lived and tied to the session that asked for them.
    /// </summary>
    public async Task<string> DownloadAsync(
        string url,
        string directory,
        IProgress<double>? progress = null,
        CancellationToken ct = default,
        string? formatId = null)
    {
        Directory.CreateDirectory(directory);

        var arguments = new List<string>
        {
            "--newline",                       // progress on its own line, not carriage returns
            "--no-warnings",
            "--no-playlist",                   // one entry per download item
            "--restrict-filenames",
            "-o", Path.Combine(directory, "%(title)s.%(ext)s"),
        };

        if (!string.IsNullOrEmpty(formatId))
        {
            // A chosen video stream often carries no audio, so ask for it merged with the
            // best audio, falling back to the format alone when it already has some.
            arguments.Add("-f");
            arguments.Add($"{formatId}+bestaudio/{formatId}");
        }

        arguments.Add(url);

        var destination = "";
        var merged = "";

        var exitCode = await RunAsync(arguments, ct, line =>
        {
            if (ProgressLine().Match(line) is { Success: true } match
                && double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var percent))
            {
                progress?.Report(Math.Clamp(percent / 100, 0, 1));
                return;
            }

            // The final name is whatever it last said it was writing. A merge happens after
            // the streams are fetched, so it wins over the per stream destinations.
            if (DestinationLine().Match(line) is { Success: true } destinationMatch)
                destination = destinationMatch.Groups[1].Value.Trim();

            if (MergeLine().Match(line) is { Success: true } mergeMatch)
                merged = mergeMatch.Groups[1].Value.Trim();
        }).ConfigureAwait(false);

        if (exitCode != 0)
            throw new InvalidOperationException("yt-dlp could not download this video.");

        var path = merged.Length > 0 ? merged : destination;

        if (path.Length > 0 && File.Exists(path))
            return path;

        // It can report a name it then post-processes into another extension. Falling back
        // to the newest file in the folder beats failing after a successful download.
        var newest = new DirectoryInfo(directory)
            .EnumerateFiles()
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .FirstOrDefault();

        return newest?.FullName
            ?? throw new InvalidOperationException("yt-dlp finished but wrote no file.");
    }

    private async Task<int> RunAsync(List<string> arguments, CancellationToken ct, Action<string> onLine)
    {
        var info = NewStartInfo(arguments);

        using var process = Process.Start(info)
            ?? throw new InvalidOperationException("Could not start yt-dlp.");

        var errors = process.StandardError.ReadToEndAsync(ct);

        await using (ct.Register(() => Kill(process)))
        {
            while (await process.StandardOutput.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
                onLine(line);

            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }

        await errors.ConfigureAwait(false);
        return process.ExitCode;
    }

    private async Task<(string Output, string Errors, int ExitCode)> RunAsync(
        List<string> arguments, CancellationToken ct)
    {
        var info = NewStartInfo(arguments);

        using var process = Process.Start(info)
            ?? throw new InvalidOperationException("Could not start yt-dlp.");

        var output = process.StandardOutput.ReadToEndAsync(ct);
        var errors = process.StandardError.ReadToEndAsync(ct);

        await using (ct.Register(() => Kill(process)))
            await process.WaitForExitAsync(ct).ConfigureAwait(false);

        return (await output.ConfigureAwait(false), await errors.ConfigureAwait(false), process.ExitCode);
    }

    private ProcessStartInfo NewStartInfo(List<string> arguments)
    {
        var info = new ProcessStartInfo(_exe)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        foreach (var argument in arguments)
            info.ArgumentList.Add(argument);

        return info;
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (Exception)
        {
            // Already gone.
        }
    }

    [GeneratedRegex(@"^\[download\]\s+([\d.]+)%", RegexOptions.CultureInvariant)]
    private static partial Regex ProgressLine();

    [GeneratedRegex(@"^\[download\]\s+Destination:\s*(.+)$", RegexOptions.CultureInvariant)]
    private static partial Regex DestinationLine();

    [GeneratedRegex(@"Merging formats into\s+""(.+)""", RegexOptions.CultureInvariant)]
    private static partial Regex MergeLine();
}
