using System.IO.Compression;

namespace Stretto.App.Services;

/// <summary>The two external programs Stretto can use, and where to get them.</summary>
public enum Tool
{
    /// <summary>Stitches HLS and DASH streams, and does the converting.</summary>
    Ffmpeg,

    /// <summary>Resolves video pages and playlists.</summary>
    YtDlp,
}

/// <summary>
/// Optional helper programs, kept in Stretto's own folder.
///
/// Neither is bundled. ffmpeg is tens of megabytes against an app that is a couple, and a
/// bundled yt-dlp would be stale within weeks -- the sites it handles change constantly, so
/// a frozen copy is worse than none at all. Fetching them on request keeps the app small
/// and lets yt-dlp be updated whenever it needs to be.
///
/// A copy already on PATH always wins, so anyone who manages these themselves is left alone.
/// </summary>
public static class ToolBox
{
    /// <summary>Beside the settings rather than beside the executable, which may be read only.</summary>
    public static string Directory { get; } = Path.Combine(DownloadStore.Directory, "tools");

    /// <summary>The single file yt-dlp release. Public domain, so no licence to carry.</summary>
    private const string YtDlpUrl = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe";

    /// <summary>A build of ffmpeg for Windows, as a zip holding the executables.</summary>
    private const string FfmpegUrl = "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip";

    public static string FileName(Tool tool) => tool is Tool.Ffmpeg ? "ffmpeg.exe" : "yt-dlp.exe";

    public static string SourceUrl(Tool tool) => tool is Tool.Ffmpeg ? FfmpegUrl : YtDlpUrl;

    public static string DisplayName(Tool tool) => tool is Tool.Ffmpeg ? "ffmpeg" : "yt-dlp";

    /// <summary>The copy Stretto fetched, if there is one.</summary>
    public static string? Local(Tool tool)
    {
        var path = Path.Combine(Directory, FileName(tool));
        return File.Exists(path) ? path : null;
    }

    /// <summary>
    /// What to actually run: our copy when we have one, otherwise the bare name so the
    /// system looks along PATH.
    /// </summary>
    public static string Resolve(Tool tool) =>
        Local(tool) ?? Path.GetFileNameWithoutExtension(FileName(tool));

    /// <summary>True when the tool can be run at all, from either place.</summary>
    public static bool IsAvailable(Tool tool) => tool switch
    {
        Tool.Ffmpeg => Stretto.Core.StreamDownloader.IsAvailable(Resolve(tool)),
        _ => Stretto.Core.YtDlp.IsAvailable(Resolve(tool)),
    };

    /// <summary>Where a tool is coming from, for the Settings list.</summary>
    public static string Describe(Tool tool)
    {
        if (Local(tool) is not null)
            return "Installed by Stretto";

        return IsAvailable(tool) ? "Found on your PATH" : "Not installed";
    }

    /// <summary>
    /// Fetches the tool into <see cref="Directory"/>, replacing any copy already there.
    ///
    /// Downloaded to a temporary name and moved into place only once it is complete, so an
    /// interrupted install cannot leave a truncated executable that fails in confusing ways.
    /// </summary>
    public static async Task InstallAsync(
        Tool tool,
        HttpClient http,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        System.IO.Directory.CreateDirectory(Directory);

        var temporary = Path.Combine(Directory, FileName(tool) + ".part");

        try
        {
            await FetchAsync(http, SourceUrl(tool), temporary, progress, ct).ConfigureAwait(false);

            if (tool is Tool.YtDlp)
            {
                File.Move(temporary, Path.Combine(Directory, FileName(tool)), overwrite: true);
                return;
            }

            ExtractFfmpeg(temporary);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                try { File.Delete(temporary); }
                catch (IOException) { /* it will be overwritten next time */ }
            }
        }
    }

    private static async Task FetchAsync(
        HttpClient http, string url, string destination, IProgress<double>? progress, CancellationToken ct)
    {
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength;

        await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var target = new FileStream(destination, FileMode.Create, FileAccess.Write,
            FileShare.None, 1 << 16, useAsync: true);

        var buffer = new byte[1 << 16];
        long written = 0;

        while (true)
        {
            var read = await source.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read == 0)
                break;

            await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            written += read;

            if (total is > 0)
                progress?.Report(Math.Clamp((double)written / total.Value, 0, 1));
        }
    }

    /// <summary>
    /// Pulls the executables out of the ffmpeg zip. The archive nests everything under a
    /// versioned folder, so the entries are matched by file name rather than by path.
    /// </summary>
    private static void ExtractFfmpeg(string archivePath)
    {
        using var archive = ZipFile.OpenRead(archivePath);

        var wanted = new[] { "ffmpeg.exe", "ffprobe.exe" };
        var found = 0;

        foreach (var entry in archive.Entries)
        {
            if (!wanted.Contains(entry.Name, StringComparer.OrdinalIgnoreCase))
                continue;

            entry.ExtractToFile(Path.Combine(Directory, entry.Name), overwrite: true);
            found++;
        }

        if (found == 0)
            throw new InvalidOperationException("That archive did not contain ffmpeg.");
    }

    // ═══ winget ═══

    /// <summary>
    /// The winget package for each tool. Installing through winget puts it on PATH for
    /// everything, not just Stretto, and winget then keeps it updated -- which is the
    /// better outcome for anyone who already manages their machine that way.
    /// </summary>
    public static string WingetId(Tool tool) => tool is Tool.Ffmpeg ? "Gyan.FFmpeg" : "yt-dlp.yt-dlp";

    public static bool HasWinget()
    {
        try
        {
            using var probe = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("winget", "--version")
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
    /// Installs through winget. Returns what winget said, so a refusal can be shown rather
    /// than turned into a shrug.
    ///
    /// User scope is asked for first because a machine wide install needs elevation, and a
    /// consent prompt appearing from a settings dialog is a poor way to find that out.
    /// </summary>
    public static async Task<(bool Ok, string Message)> InstallWithWingetAsync(Tool tool, CancellationToken ct = default)
    {
        var (ok, message) = await RunWingetAsync(tool, userScope: true, ct).ConfigureAwait(false);

        if (ok)
            return (true, message);

        // Not every package offers a user scope install; those need the default one.
        return await RunWingetAsync(tool, userScope: false, ct).ConfigureAwait(false);
    }

    private static async Task<(bool Ok, string Message)> RunWingetAsync(Tool tool, bool userScope, CancellationToken ct)
    {
        var info = new System.Diagnostics.ProcessStartInfo("winget")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        foreach (var argument in new[]
                 {
                     "install", "--id", WingetId(tool), "--exact", "--silent",
                     "--accept-package-agreements", "--accept-source-agreements",
                 })
        {
            info.ArgumentList.Add(argument);
        }

        if (userScope)
        {
            info.ArgumentList.Add("--scope");
            info.ArgumentList.Add("user");
        }

        try
        {
            using var process = System.Diagnostics.Process.Start(info);
            if (process is null)
                return (false, "winget could not be started.");

            var output = process.StandardOutput.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct).ConfigureAwait(false);

            var text = (await output.ConfigureAwait(false)).Trim();

            return process.ExitCode == 0
                ? (true, "Installed with winget. It may take a new sign in before it is on PATH.")
                : (false, LastMeaningfulLine(text));
        }
        catch (Exception exception)
        {
            return (false, exception.Message);
        }
    }

    /// <summary>winget is chatty and its progress lines are noise; the last real line is the reason.</summary>
    private static string LastMeaningfulLine(string output)
    {
        // The spinner characters winget draws progress with, backslash included by code
        // point so this literal carries no escape sequence of its own.
        var noise = "-|/. " + (char)92;

        var line = output
            .ReplaceLineEndings()
            .Split(Environment.NewLine)
            .Select(l => l.Trim())
            .LastOrDefault(l => l.Length > 0 && l.Any(c => !noise.Contains(c)));

        return string.IsNullOrEmpty(line) ? "winget could not install it." : line;
    }

    /// <summary>Removes Stretto's own copy, leaving any on PATH alone.</summary>
    public static void Remove(Tool tool)
    {
        foreach (var name in tool is Tool.Ffmpeg
                     ? new[] { "ffmpeg.exe", "ffprobe.exe" }
                     : [FileName(tool)])
        {
            var path = Path.Combine(Directory, name);

            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch (IOException exception)
            {
                App.Log($"Could not remove {name}: {exception.Message}");
            }
        }
    }
}
