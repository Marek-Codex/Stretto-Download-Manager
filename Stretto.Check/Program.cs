using System.Diagnostics;
using Stretto.Core;

// Headless harness for the engine. Everything the UI will eventually do runs through
// here first, so engine bugs surface without a XAML window in the way.

if (args.Length == 0 || args[0] is "-h" or "--help")
{
    Console.WriteLine("""
        Stretto.Check - headless driver for the download engine

          stretto <url> [-o <dir>] [-n <connections>]
          stretto <url> --benchmark [-n <connections>]

        Options:
          -o <dir>    output directory        (default: ./downloads)
          -n <count>  parallel connections    (default: 8, max 32)
          --benchmark download twice, 1 connection vs N, and compare
        """);
    return 0;
}

if (args[0] == "stream")
{
    // stretto stream <playlistUrl> [-o dir]
    var dir = ArgValue("-o") ?? Path.Combine(Environment.CurrentDirectory, "downloads");
    var streams = new StreamDownloader();

    if (!StreamDownloader.IsAvailable())
    {
        Console.Error.WriteLine("ffmpeg not found on PATH.");
        return 1;
    }

    var seen = 0;
    var reporter = new Progress<ProgressSnapshot>(p =>
    {
        seen++;
        var pct = p.Fraction is { } f ? $"{f * 100,5:F1}%" : "   ?  ";
        Console.Write($"\r  {pct}  {Bytes(p.BytesCompleted),10}  {Bytes((long)p.BytesPerSecond),9}/s   ");
    });

    try
    {
        var done = await streams.DownloadAsync(new Uri(args[1]), dir, "stream-test.mp4",
            progress: reporter, ct: CancellationToken.None);

        Console.WriteLine($"\n  saved     {done.FilePath}");
        Console.WriteLine($"  size      {Bytes(done.BytesWritten)}");
        Console.WriteLine($"  elapsed   {done.Elapsed.TotalSeconds:F2}s");
        Console.WriteLine($"  progress  {seen} updates reported");
        return 0;
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine($"\nfailed: {exception.Message}");
        return 1;
    }
}

if (args[0] == "media")
{
    // stretto media <pageUrl>
    using var http = SegmentedDownloader.CreateDefaultClient();
    try
    {
        var found = await MediaExtractor.ExtractAsync(http, new Uri(args[1]), ct: CancellationToken.None);

        Console.WriteLine($"{found.Count} candidate(s) on {args[1]}\n");
        foreach (var item in found)
            Console.WriteLine($"  {item.Label,-16} {item.Kind,-6} via {item.Source,-10} {item.Url}");

        return found.Count > 0 ? 0 : 1;
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine($"failed: {exception.Message}");
        return 1;
    }
}

if (args[0] == "batch")
{
    // stretto batch <pattern> <numbers|letters> <from> <to> [pad]
    try
    {
        var kind = args.Length > 2 && args[2].StartsWith('l') ? BatchWildcard.Letters : BatchWildcard.Numbers;
        var pad = args.Length > 5 ? int.Parse(args[5]) : 0;
        var expanded = BatchExpander.Expand(args[1], kind, args[3], args[4], pad);

        Console.WriteLine($"{expanded.Count} file(s)");
        foreach (var entry in expanded.Take(3)) Console.WriteLine($"  {entry}");
        if (expanded.Count > 4) Console.WriteLine($"  ... ({expanded.Count - 4} more)");
        if (expanded.Count > 3) Console.WriteLine($"  {expanded[^1]}");
        return 0;
    }
    catch (ArgumentException exception)
    {
        Console.WriteLine($"rejected: {exception.Message}");
        return 1;
    }
}

var url = args[0];
var outputDir = ArgValue("-o") ?? Path.Combine(Environment.CurrentDirectory, "downloads");
var connections = int.TryParse(ArgValue("-n"), out var n) ? n : 8;
var benchmark = args.Contains("--benchmark");

if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
{
    Console.Error.WriteLine($"Not a valid absolute URL: {url}");
    return 1;
}

using var cancel = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancel.Cancel(); };

var downloader = new SegmentedDownloader();

try
{
    if (benchmark)
    {
        Console.WriteLine($"Benchmarking {uri}\n");
        var single = await RunAsync(1, Path.Combine(outputDir, "bench-1"));
        var multi = await RunAsync(connections, Path.Combine(outputDir, $"bench-{connections}"));

        var ratio = single.Elapsed.TotalSeconds / multi.Elapsed.TotalSeconds;
        Console.WriteLine();
        Console.WriteLine($"  1 connection   {single.Elapsed.TotalSeconds,7:F2}s   {Rate(single)}");
        Console.WriteLine($"  {multi.ConnectionsUsed,2} connections  {multi.Elapsed.TotalSeconds,7:F2}s   {Rate(multi)}");
        Console.WriteLine($"  speedup        {ratio,7:F2}x");
    }
    else
    {
        var result = await RunAsync(connections, outputDir);
        Console.WriteLine();
        Console.WriteLine($"  saved     {result.FilePath}");
        Console.WriteLine($"  size      {Bytes(result.BytesWritten)}");
        Console.WriteLine($"  elapsed   {result.Elapsed.TotalSeconds:F2}s   {Rate(result)}");
        Console.WriteLine($"  conns     {result.ConnectionsUsed}{(result.WasResumed ? "   (resumed)" : "")}");
    }
}
catch (OperationCanceledException)
{
    Console.WriteLine("\ncancelled - resume state kept, rerun the same command to continue");
    return 130;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"\nfailed: {ex.Message}");
    return 1;
}

return 0;

async Task<DownloadResult> RunAsync(int conns, string dir)
{
    var request = new DownloadRequest
    {
        Url = uri!,
        TargetDirectory = dir,
        Connections = conns,
        Overwrite = !args.Contains("--no-overwrite"),
        UseServerTimestamp = args.Contains("--server-time"),
    };

    var lastDraw = 0L;
    var progress = new Progress<ProgressSnapshot>(p =>
    {
        // Throttle redraws; the reporter ticks faster than a console can usefully repaint.
        if (Stopwatch.GetElapsedTime(lastDraw).TotalMilliseconds < 200) return;
        lastDraw = Stopwatch.GetTimestamp();
        Draw(p, conns);
    });

    var result = await downloader.DownloadAsync(request, progress, cancel.Token);
    Console.Write($"\r{new string(' ', Math.Min(Console.IsOutputRedirected ? 80 : Console.WindowWidth - 1, 120))}\r");
    return result;
}

static void Draw(ProgressSnapshot p, int conns)
{
    var pct = p.Fraction is { } f ? $"{f * 100,5:F1}%" : "  ?  ";
    var bar = p.Fraction is { } bf ? Bar(bf) : new string('-', 24);
    var eta = p.Eta is { } e ? $"ETA {e:mm\\:ss}" : "ETA   --";

    Console.Write($"\r  [{bar}] {pct}  {Bytes(p.BytesCompleted),10}  {Bytes((long)p.BytesPerSecond),9}/s  {p.ActiveConnections}/{conns} conn  {eta}   ");
}

static string Bar(double fraction)
{
    var filled = (int)Math.Round(fraction * 24);
    return new string('#', filled) + new string('-', 24 - filled);
}

static string Bytes(long value)
{
    string[] units = ["B", "KB", "MB", "GB", "TB"];
    double size = value;
    var unit = 0;
    while (size >= 1024 && unit < units.Length - 1) { size /= 1024; unit++; }
    return $"{size:F1} {units[unit]}";
}

static string Rate(DownloadResult r) =>
    r.Elapsed.TotalSeconds > 0 ? $"{Bytes((long)(r.BytesWritten / r.Elapsed.TotalSeconds))}/s" : "-";

string? ArgValue(string flag)
{
    var i = Array.IndexOf(args, flag);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}
