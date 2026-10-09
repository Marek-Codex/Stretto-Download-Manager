using System.Diagnostics;
using System.Globalization;

namespace Stretto.Core;

/// <summary>What a finished file can be turned into.</summary>
public enum ConversionTarget
{
    /// <summary>Audio only, re-encoded to MP3.</summary>
    Mp3,

    /// <summary>H.264 and AAC in MP4, which is the format everything plays.</summary>
    Mp4,

    /// <summary>
    /// The same streams rewrapped into MP4 without re-encoding. Near instant and lossless
    /// when the codecs already suit the container, and it simply fails when they do not.
    /// </summary>
    Mp4Remux,
}

/// <summary>
/// Converts a downloaded file with ffmpeg.
///
/// Separate from <see cref="StreamDownloader"/> even though both drive ffmpeg: that one
/// fetches a stream from the network, this one rewrites a file already on disk, and the
/// only thing they share is the executable.
/// </summary>
public sealed class MediaConverter(string? ffmpegPath = null)
{
    private readonly string _ffmpeg = ffmpegPath ?? "ffmpeg";

    public static string ExtensionFor(ConversionTarget target) =>
        target is ConversionTarget.Mp3 ? ".mp3" : ".mp4";

    public static string DescribeTarget(ConversionTarget target) => target switch
    {
        ConversionTarget.Mp3 => "MP3 audio",
        ConversionTarget.Mp4 => "MP4 video",
        _ => "MP4 (no re-encode)",
    };

    /// <summary>
    /// Converts <paramref name="input"/> and returns the path written.
    ///
    /// The output never overwrites the input, even when the extension matches, because a
    /// failed conversion that has already truncated the original is unrecoverable.
    /// </summary>
    public async Task<string> ConvertAsync(
        string input,
        ConversionTarget target,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        if (!File.Exists(input))
            throw new FileNotFoundException("Nothing to convert.", input);

        var output = UniquePath(input, target);
        var duration = await DurationAsync(input, ct).ConfigureAwait(false);

        var arguments = new List<string>
        {
            "-hide_banner", "-nostdin", "-y",
            "-i", input,
        };

        arguments.AddRange(target switch
        {
            ConversionTarget.Mp3 => ["-vn", "-c:a", "libmp3lame", "-q:a", "2"],
            ConversionTarget.Mp4 => ["-c:v", "libx264", "-preset", "medium", "-crf", "23", "-c:a", "aac", "-b:a", "192k"],
            _ => new[] { "-c", "copy", "-movflags", "+faststart" },
        });

        arguments.AddRange(["-progress", "pipe:1", output]);

        var startInfo = new ProcessStartInfo(_ffmpeg)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start ffmpeg.");

        // Drained on a separate task: ffmpeg writes a lot to stderr, and a full pipe buffer
        // deadlocks the process rather than failing visibly.
        var errors = process.StandardError.ReadToEndAsync(ct);

        await using (ct.Register(() => Kill(process)))
        {
            while (await process.StandardOutput.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
            {
                if (duration > 0 && line.StartsWith("out_time_us=", StringComparison.Ordinal)
                    && long.TryParse(line["out_time_us=".Length..], out var microseconds))
                {
                    progress?.Report(Math.Clamp(microseconds / 1_000_000d / duration, 0, 1));
                }
            }

            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }

        if (process.ExitCode != 0)
        {
            TryDelete(output);

            var message = (await errors.ConfigureAwait(false)).Trim();
            var lastLine = message.Split('\n').LastOrDefault()?.Trim();

            throw new InvalidOperationException(
                $"ffmpeg could not convert this file. {lastLine}");
        }

        progress?.Report(1);
        return output;
    }

    /// <summary>Reads the duration so progress means something. Zero when ffmpeg cannot say.</summary>
    private async Task<double> DurationAsync(string input, CancellationToken ct)
    {
        try
        {
            var startInfo = new ProcessStartInfo(_ffmpeg)
            {
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            };

            foreach (var argument in new[] { "-hide_banner", "-i", input })
                startInfo.ArgumentList.Add(argument);

            using var process = Process.Start(startInfo);
            if (process is null)
                return 0;

            var text = await process.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
            await process.WaitForExitAsync(ct).ConfigureAwait(false);

            // "  Duration: 00:04:13.37, start: ..."
            var marker = text.IndexOf("Duration:", StringComparison.Ordinal);
            if (marker < 0)
                return 0;

            var value = text[(marker + 9)..].Split(',')[0].Trim();

            return TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var parsed)
                ? parsed.TotalSeconds
                : 0;
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            return 0;
        }
    }

    private static string UniquePath(string input, ConversionTarget target)
    {
        var directory = Path.GetDirectoryName(input) ?? ".";
        var name = Path.GetFileNameWithoutExtension(input);
        var extension = ExtensionFor(target);

        var candidate = Path.Combine(directory, name + extension);
        var counter = 2;

        while (File.Exists(candidate))
            candidate = Path.Combine(directory, $"{name} ({counter++}){extension}");

        return candidate;
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

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { /* a half written output is not worth failing twice over */ }
    }
}
