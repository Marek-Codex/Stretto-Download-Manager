using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Stretto.Core;

/// <summary>
/// Pulls HLS and DASH streams by driving ffmpeg.
///
/// These cannot go through the segmented engine: a playlist is not a file with byte ranges,
/// it is a list of hundreds of small segments that then have to be stitched and remuxed into
/// a container. ffmpeg already does all of that correctly, including the awkward parts
/// (discontinuities, AAC ADTS to ASC conversion, variant selection), so shelling out to it
/// is both far less code and far more correct than reimplementing it.
/// </summary>
public sealed partial class StreamDownloader
{
    private readonly string _ffmpeg;

    public StreamDownloader(string? ffmpegPath = null)
        => _ffmpeg = ffmpegPath ?? "ffmpeg";

    /// <summary>ffmpeg is an external dependency, so the UI has to be able to say it is missing.</summary>
    public static bool IsAvailable(string executable = "ffmpeg")
    {
        try
        {
            using var probe = Process.Start(new ProcessStartInfo(executable, "-version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });

            if (probe is null) return false;
            _ = probe.StandardOutput.ReadToEndAsync();
            _ = probe.StandardError.ReadToEndAsync();
            if (!probe.WaitForExit(4000))
            {
                Kill(probe);
                return false;
            }
            return probe.ExitCode == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Fetches the playlist the way ffmpeg will and logs its shape: segment count, total
    /// length, byte ranges, end tag, variants. Tells apart "ffmpeg read it wrong" from "the
    /// site served ffmpeg a different playlist than the browser got".
    /// </summary>
    private static async Task DescribePlaylistAsync(Uri playlist, List<string> headers, CancellationToken ct)
    {
        if (Log is null || IsDash(playlist)) return;

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            using var request = new HttpRequestMessage(HttpMethod.Get, playlist);
            foreach (var header in headers)
            {
                var split = header.IndexOf(':');
                if (split > 0) request.Headers.TryAddWithoutValidation(header[..split].Trim(), header[(split + 1)..].Trim());
            }

            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var lines = text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

            var durations = lines.Where(l => l.StartsWith("#EXTINF:", StringComparison.Ordinal))
                .Select(l => double.TryParse(l[8..].Split(',')[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0)
                .ToList();

            Log($"playlist: HTTP {(int)response.StatusCode}, {text.Length} chars, {durations.Count} segments, "
                + $"{durations.Sum():F1} s, byteranges={lines.Count(l => l.StartsWith("#EXT-X-BYTERANGE", StringComparison.Ordinal))}, "
                + $"endlist={lines.Contains("#EXT-X-ENDLIST")}, variants={lines.Count(l => l.StartsWith("#EXT-X-STREAM-INF", StringComparison.Ordinal))}, "
                + $"media={lines.Count(l => l.StartsWith("#EXT-X-MEDIA:", StringComparison.Ordinal))}, "
                + $"keys={lines.Count(l => l.StartsWith("#EXT-X-KEY", StringComparison.Ordinal))}, "
                + $"uris={lines.Where(l => !l.StartsWith('#')).Select(l => Redact(l)).Distinct().Count()}");
        }
        catch (Exception exception)
        {
            Log($"playlist: could not read it ({exception.Message})");
        }
    }

    /// <summary>Where diagnostic lines go. Set by the app to its log.</summary>
    public static Action<string>? Log { get; set; }

    /// <summary>ffmpeg prints segment URLs, whose query strings are usually signed tokens.</summary>
    private static string Redact(string line) => QueryString().Replace(line, "?…");

    [GeneratedRegex(@"\?[^\s'""]+")]
    private static partial Regex QueryString();

    /// <summary>Headers worth handing to ffmpeg. Transport ones (Range, Host, encodings,
    /// lengths) would break its own requests, so they are left out.</summary>
    [GeneratedRegex(@"^(?!(?:host|range|connection|content-length|accept-encoding|if-range|if-none-match|if-modified-since|upgrade-insecure-requests|sec-fetch-.*)$)[A-Za-z0-9-]+$", RegexOptions.IgnoreCase)]
    private static partial Regex ForwardedHeaders { get; }

    private static bool IsDash(Uri playlist) =>
        playlist.AbsolutePath.EndsWith(".mpd", StringComparison.OrdinalIgnoreCase);

    private static readonly Dictionary<string, bool> PickySupport = [];

    /// <summary>Whether this ffmpeg knows -extension_picky (7.1 and later). Asked once per
    /// executable: an older one rejects the unknown option and fails the whole run.</summary>
    private static bool SupportsExtensionPicky(string executable)
    {
        lock (PickySupport)
        {
            if (PickySupport.TryGetValue(executable, out var known)) return known;

            var supported = false;
            try
            {
                using var help = Process.Start(new ProcessStartInfo(executable, "-hide_banner -h demuxer=hls")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });
                if (help is not null)
                {
                    var output = help.StandardOutput.ReadToEndAsync();
                    var errors = help.StandardError.ReadToEndAsync();
                    if (!help.WaitForExit(4000))
                    {
                        Kill(help);
                        return false;
                    }
                    var text = output.GetAwaiter().GetResult() + errors.GetAwaiter().GetResult();
                    supported = text.Contains("extension_picky", StringComparison.Ordinal);
                }
            }
            catch (Exception)
            {
                // Not found or would not run; the download itself will report that.
            }

            return PickySupport[executable] = supported;
        }
    }

    [GeneratedRegex(@"Duration:\s*(\d+):(\d{2}):(\d{2})\.(\d+)")]
    private static partial Regex DurationLine();

    public async Task<DownloadResult> DownloadAsync(
        Uri playlist,
        string targetDirectory,
        string fileName,
        string? cookie = null,
        string? referrer = null,
        string? userAgent = null,
        IProgress<ProgressSnapshot>? progress = null,
        CancellationToken ct = default,
        IReadOnlyDictionary<string, string>? extraHeaders = null,
        bool overwrite = false,
        bool tagClashes = false,
        Action<string>? outputFileOpened = null,
        int? hlsProgramIndex = null,
        bool isDash = false)
    {
        Directory.CreateDirectory(targetDirectory);

        fileName = SafeFileName(fileName);
        if (!Path.HasExtension(fileName) || Path.GetExtension(fileName).ToLowerInvariant() is ".m3u8" or ".m3u" or ".mpd")
            fileName = Path.ChangeExtension(fileName, ".mp4");

        var output = ResolveOutputPath(targetDirectory, fileName, overwrite, tagClashes ? SourceTag.For(playlist) : null);
        var stopwatch = Stopwatch.StartNew();
        isDash |= IsDash(playlist);

        var headers = new List<string>();
        if (!string.IsNullOrEmpty(cookie)) headers.Add($"Cookie: {cookie}");
        if (!string.IsNullOrEmpty(referrer)) headers.Add($"Referer: {referrer}");
        if (!string.IsNullOrEmpty(userAgent)) headers.Add($"User-Agent: {userAgent}");

        // The rest of what the browser sent: Origin, Authorization, signed-token headers.
        // Plenty of video CDNs refuse a segment without the Origin the player sent.
        foreach (var (name, value) in extraHeaders ?? new Dictionary<string, string>())
        {
            if (!ForwardedHeaders.IsMatch(name) || string.IsNullOrEmpty(value)) continue;
            if (headers.Any(h => h.StartsWith(name + ":", StringComparison.OrdinalIgnoreCase))) continue;
            headers.Add($"{name}: {value}");
        }

        // The output is reserved atomically below, before ffmpeg may overwrite it.
        var arguments = new List<string> { "-hide_banner", "-y", "-nostdin" };
        if (headers.Count > 0)
            arguments.AddRange(["-headers", string.Concat(headers.Select(h => h + "\r\n"))]);

        // ffmpeg 7.1 onwards refuses HLS segments whose extension it does not expect, and a
        // good share of sites serve theirs as .jpg, .png, .txt or with no extension at all,
        // so those downloads failed outright. The playlist is what says what a segment is.
        // A playlist without #EXT-X-ENDLIST is taken for live, and ffmpeg then starts three
        // segments from the end: about six seconds of a video that browsers play from the
        // top. Plenty of on-demand sites leave the tag off. Start at the first segment.
        if (!isDash)
            arguments.AddRange(["-live_start_index", "0"]);

        // Sites that serve the whole video as one file, cut up by #EXT-X-BYTERANGE, came
        // out as the first segment only: ffmpeg treats each range as a seekable file,
        // reads one, and stops without an error. Not seeking within a segment costs
        // nothing for ordinary playlists and fixes those. Reproduced with ffmpeg 9.
        if (!isDash)
            arguments.AddRange(["-http_seekable", "0"]);

        // Segments come one request at a time, and a CDN dropping one connection used to
        // cost that segment: ffmpeg skips it and carries on.
        arguments.AddRange(["-reconnect", "1", "-reconnect_on_network_error", "1", "-reconnect_delay_max", "10"]);

        if (!isDash && await Task.Run(() => SupportsExtensionPicky(_ffmpeg), ct).ConfigureAwait(false))
            arguments.AddRange(["-extension_picky", "0", "-allowed_extensions", "ALL"]);
        else if (!isDash)
            arguments.AddRange(["-allowed_extensions", "ALL"]);

        arguments.AddRange([
            "-i", playlist.ToString(),
        ]);
        // Keep the master input: its program contains both this rendition and any
        // external audio group. Opening the rendition URL alone drops that audio.
        if (hlsProgramIndex is >= 0)
            arguments.AddRange(["-map", $"0:p:{hlsProgramIndex}:v?", "-map", $"0:p:{hlsProgramIndex}:a?"]);
        arguments.AddRange([
            "-c", "copy",
            // The MP4 muxer inserts AAC's ADTS conversion when needed. Forcing an AAC
            // filter on every stream rejects otherwise valid MP3/AC-3 audio.
            "-movflags", "+faststart",
            "-progress", "pipe:1",
            "-nostats",
            output,
        ]);

        var startInfo = new ProcessStartInfo(_ffmpeg)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        Log?.Invoke("ffmpeg " + string.Join(' ', arguments
            .Where((a, i) => i == 0 || arguments[i - 1] != "-headers")
            .Select(a => Redact(a).Contains(' ') ? $"\"{Redact(a)}\"" : Redact(a))));
        await DescribePlaylistAsync(playlist, headers, ct).ConfigureAwait(false);

        // Reserve the output atomically before letting ffmpeg overwrite our reservation.
        // An unrelated file that appeared after name selection must remain untouched.
        using (var reserved = new FileStream(output, overwrite ? FileMode.Create : FileMode.CreateNew, FileAccess.Write)) { }
        outputFileOpened?.Invoke(output);
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start ffmpeg.");
        using var killOnCancel = ct.Register(() => Kill(process));

        double? durationSeconds = null;
        long bytes = 0;
        var errorTail = new Queue<string>();
        var skipped = new List<string>();

        // ffmpeg reports the media duration on stderr and machine-readable progress on
        // stdout, so both have to be pumped or the pipes fill and the process stalls.
        var stderr = Task.Run(async () =>
        {
            while (await process.StandardError.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
            {
                if (durationSeconds is null && DurationLine().Match(line) is { Success: true } m)
                {
                    durationSeconds = int.Parse(m.Groups[1].Value) * 3600
                                    + int.Parse(m.Groups[2].Value) * 60
                                    + int.Parse(m.Groups[3].Value)
                                    + double.Parse("0." + m.Groups[4].Value, CultureInfo.InvariantCulture);
                }

                // The HLS reader does not fail on a segment it cannot open; it logs it,
                // skips it, and finishes "successfully" with whatever it did get. Those
                // lines are the only sign, so they are collected rather than ignored.
                if (line.Contains("Failed to open segment", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("Server returned 4", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("Server returned 5", StringComparison.OrdinalIgnoreCase))
                    skipped.Add(Redact(line));

                errorTail.Enqueue(Redact(line));
                if (errorTail.Count > 12) errorTail.Dequeue();
            }
        }, ct);

        var lastBytes = 0L;
        var lastTick = Stopwatch.GetTimestamp();
        var speed = 0d;

        try
        {
            while (await process.StandardOutput.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
            {
                var split = line.IndexOf('=');
                if (split <= 0) continue;

                var key = line[..split];
                var value = line[(split + 1)..];

                if (key == "total_size" && long.TryParse(value, out var size))
                    bytes = size;

                if (key != "out_time_us" || !long.TryParse(value, out var micros))
                    continue;

                var elapsed = Stopwatch.GetElapsedTime(lastTick);
                if (elapsed.TotalMilliseconds < 250)
                    continue;

                lastTick = Stopwatch.GetTimestamp();
                var instant = (bytes - lastBytes) / elapsed.TotalSeconds;
                speed = speed == 0 ? instant : (speed * 0.7) + (instant * 0.3);
                lastBytes = bytes;

                progress?.Report(new ProgressSnapshot
                {
                    BytesCompleted = bytes,
                    TotalBytes = null,      // a stream has no known size until it is finished
                    BytesPerSecond = speed,
                    ActiveConnections = 1,
                    // Position through the timeline is the only honest progress a stream has.
                    FractionOverride = durationSeconds is > 0
                        ? Math.Clamp(micros / 1_000_000d / durationSeconds.Value, 0, 1)
                        : null,
                });
            }

            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            await stderr.ConfigureAwait(false);
        }
        catch
        {
            // Disposing Process does not stop its child. Stop the entire ffmpeg process
            // tree, then reap it and the stderr reader before letting pause/failure return.
            Kill(process);
            try { await process.WaitForExitAsync().ConfigureAwait(false); }
            catch (Exception) { /* cleanup must not replace the original failure */ }
            try { await stderr.ConfigureAwait(false); }
            catch (Exception) { /* cleanup must not replace the original failure */ }
            throw;
        }

        Log?.Invoke($"ffmpeg exit {process.ExitCode}; {skipped.Count} segment problems. Last output: "
            + string.Join(" | ", errorTail.TakeLast(6)));

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"ffmpeg failed: {string.Join(" | ", errorTail.TakeLast(3))}");

        // A file made of the first few segments is not a download, it is a truncation that
        // looks like success until it is played.
        if (skipped.Count > 0)
            throw new InvalidOperationException(
                $"The site refused {skipped.Count} part(s) of the stream, so only some of it arrived. {skipped[0]}");

        var written = new FileInfo(output).Length;

        return new DownloadResult
        {
            FilePath = output,
            BytesWritten = written,
            Elapsed = stopwatch.Elapsed,
            ConnectionsUsed = 1,
            WasResumed = false,
        };
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
            // The process may have exited while cancellation was being delivered.
        }
    }

    private static string ResolveOutputPath(string directory, string name, bool overwrite, string? tag) =>
        FilePaths.ClashFree(directory, name, overwrite, tag: tag);

    private static string SafeFileName(string name)
    {
        name = Path.GetFileName(name.Trim());
        foreach (var invalid in Path.GetInvalidFileNameChars())
            name = name.Replace(invalid, '_');

        return string.IsNullOrWhiteSpace(name) || name is "." or ".." ? "video.mp4" : name;
    }
}
