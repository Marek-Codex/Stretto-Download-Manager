using System.Text.Json;
using Stretto.Core;

namespace Stretto.Tests;

public static partial class Program
{
    private static YtDlp FixtureYtDlp() => new(Path.Combine(AppContext.BaseDirectory,
        OperatingSystem.IsWindows() ? "Stretto.Tests.exe" : "Stretto.Tests"));

    private static async Task YtDlpFinalPath()
    {
        using var folder = new TempFolder();
        var directory = Path.Combine(folder.Path, "100% test folder");
        var tool = FixtureYtDlp();
        var first = await tool.DownloadAsync("https://stretto.invalid/yt-dlp-fixture/merged", directory);
        Assert.Equal(Path.Combine(directory, "finished α [merged].mkv"), first, "uses the actual merged path");
        Assert.Equal("finished", File.ReadAllText(first), "final result exists");
        var existing = await tool.DownloadAsync("https://stretto.invalid/yt-dlp-fixture/existing", directory);
        Assert.Equal(first, existing, "already-downloaded result uses its explicit final path");
    }

    private static async Task YtDlpRefusesUnknownOutput()
    {
        using var folder = new TempFolder();
        var unrelated = Path.Combine(folder.Path, "unrelated.mp4");
        File.WriteAllText(unrelated, "original user file");
        await ExpectInvalidOutput("silent", folder.Path);
        await ExpectInvalidOutput("human-logs", folder.Path);
        Assert.Equal("original user file", File.ReadAllText(unrelated), "unrelated file is not altered");
    }

    private static async Task YtDlpValidatesOutput()
    {
        using var folder = new TempFolder();
        foreach (var mode in new[] { "missing", "multiple", "outside", "invalid-json", "relative", "failed" })
            await ExpectInvalidOutput(mode, Path.Combine(folder.Path, mode));
    }

    private static async Task ExpectInvalidOutput(string mode, string directory)
    {
        try
        {
            await FixtureYtDlp().DownloadAsync($"https://stretto.invalid/yt-dlp-fixture/{mode}", directory);
        }
        catch (Exception exception) when (exception is InvalidDataException or InvalidOperationException)
        { return; }
        throw new AssertionException($"{mode} must not be reported as a completed file");
    }

    private static async Task YtDlpConcurrentResults()
    {
        using var folder = new TempFolder();
        var results = await Task.WhenAll(new[] { "first", "second" }.Select(mode =>
            FixtureYtDlp().DownloadAsync($"https://stretto.invalid/yt-dlp-fixture/{mode}", folder.Path)));
        Assert.True(results[0] != results[1], "parallel invocations retain distinct output paths");
        Assert.Equal("first", File.ReadAllText(results[0]), "first invocation owns its result");
        Assert.Equal("second", File.ReadAllText(results[1]), "second invocation owns its result");
    }

    private static async Task RealYtDlpOutput()
    {
        RequireFfmpeg();
        using var folder = new TempFolder();
        var source = Path.Combine(folder.Path, "source.mp4");
        await RunMediaTool("ffmpeg", "-hide_banner", "-nostdin", "-y", "-f", "lavfi", "-i",
            "testsrc=duration=1:size=160x120:rate=10", "-c:v", "libx264", "-pix_fmt", "yuv420p", source);
        await using var server = new MediaTestServer(folder.Path);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var directory = Path.Combine(folder.Path, "100% real output");
        var tool = new YtDlp(Environment.GetEnvironmentVariable("STRETTO_TEST_YTDLP"));
        var path = await tool.DownloadAsync(new Uri(server.BaseUrl, "source.mp4").AbsoluteUri,
            directory, ct: timeout.Token);
        Assert.True(File.ReadAllBytes(source).SequenceEqual(File.ReadAllBytes(path)), "real yt-dlp output matches source");
        var again = await tool.DownloadAsync(new Uri(server.BaseUrl, "source.mp4").AbsoluteUri,
            directory, ct: timeout.Token);
        Assert.Equal(path, again, "real already-downloaded response returns the same explicit file");
    }

    // A real child process, selected only by the test runner's private fixture URL. This
    // exercises process arguments, stdout framing, exit codes and concurrent execution
    // without relying on a video site or installing yt-dlp on the build machine.
    private static async Task<int> RunYtDlpFixtureAsync(string[] args)
    {
        var print = Array.IndexOf(args, "--print");
        if (print < 0 || args[print + 1] != "after_move:STRETTO_OUTPUT:%(filepath)j"
            || !args.Contains("--no-simulate") || !args.Contains("--ignore-config")) return 12;
        var output = args[Array.IndexOf(args, "-o") + 1];
        var directory = Path.GetDirectoryName(output)!.Replace("%%", "%");
        var mode = new Uri(args[^1]).Segments[^1];
        var path = Path.Combine(directory, mode is "merged" or "existing" ? "finished α [merged].mkv" : mode + ".mp4");
        Directory.CreateDirectory(directory);
        if (mode == "silent") return 0;
        if (mode == "failed") return 7;
        if (mode == "human-logs")
        {
            Console.WriteLine("[download] Destination: " + Path.Combine(directory, "unrelated.mp4"));
            Console.WriteLine("[Merger] Merging formats into \"" + Path.Combine(directory, "unrelated.mp4") + "\"");
            return 0;
        }
        if (mode == "outside")
            path = Path.Combine(Path.GetDirectoryName(directory)!, "outside.mp4");
        if (mode != "missing" && mode != "existing") await File.WriteAllTextAsync(path, mode is "merged" ? "finished" : mode);
        if (mode == "existing" && !File.Exists(path)) return 13;
        Console.WriteLine("[download] 50.0% of test data");
        if (mode == "invalid-json") Console.WriteLine("STRETTO_OUTPUT:not json");
        else if (mode == "relative") Console.WriteLine("STRETTO_OUTPUT:\"relative.mp4\"");
        else Console.WriteLine("STRETTO_OUTPUT:" + JsonSerializer.Serialize(path));
        if (mode == "multiple") Console.WriteLine("STRETTO_OUTPUT:" + JsonSerializer.Serialize(path));
        return 0;
    }
}
