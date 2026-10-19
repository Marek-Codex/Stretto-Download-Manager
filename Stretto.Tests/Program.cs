using Stretto.Core;

namespace Stretto.Tests;

/// <summary>
/// The engine's test suite. Run with: dotnet run --project Stretto.Tests
///
/// Every test drives the real <see cref="SegmentedDownloader"/> against a real HTTP server
/// and checks the bytes on disk, because the failures worth catching here are not "did it
/// throw" but "did a segment land at the wrong offset" and "did a resume append to a file
/// it should have restarted".
/// </summary>
public static class Program
{
    private static int _port = 8110;
    private static int NextPort() => Interlocked.Increment(ref _port);

    public static async Task<int> Main()
    {
        var runner = new Runner();

        await runner.Run("downloads a file correctly over many connections", DownloadsCorrectly);
        await runner.Run("takes over the work of a slow connection", StealsFromSlowConnection);
        await runner.Run("reconnects a connection that stalls without closing", RecoversFromStall);
        await runner.Run("resumes a mostly finished download on several connections", ResumesOnSeveralConnections);
        await runner.Run("uses supplied probe metadata without a second probe", UsesSuppliedProbeMetadata);
        await runner.Run("resumes when the link is replaced with a fresh one", ResumesAcrossUrlChange);
        await runner.Run("falls back to one connection when Range is refused", FallsBackWhenRangeRefused);
        await runner.Run("recovers when connections die partway", RecoversFromFlakyConnections);
        await runner.Run("restarts instead of resuming when the remote file changed", RestartsWhenRemoteFileChanged);
        await runner.Run("stamps the file with the server's Last-Modified", StampsServerTimestamp);
        await runner.Run("reads the formats out of an HLS master playlist", ReadsHlsVariants);
        await runner.Run("treats a media playlist as having no alternatives", IgnoresMediaPlaylist);
        await runner.Run("builds a curl command reproducing a request", BuildsCurlCommand);
        await runner.Run("downloads over FTP", DownloadsOverFtp);
        await runner.Run("does not mistake an unmarked FTP file for a partial download", ProtectsExistingFtpFile);
        await runner.Run("converts a video to MP3", ConvertsToMp3);
        await runner.Run("rewraps to MP4 without re-encoding", RemuxesToMp4);
        await runner.Run("reports a clear error for a file ffmpeg cannot read", RefusesNonMedia);
        await runner.Run("adds connections while a per-connection cap is the bottleneck", RampsUpUnderPerConnectionCap);
        await runner.Run("stops adding connections and finishes when the server refuses extras", BacksOffWhenRefused);
        await runner.Run("stops adding connections once a speed limit is what holds the speed", RampStopsAtSpeedLimit);
        await runner.Run("picks a free name instead of overwriting", PicksFreeFileName);
        await runner.Run("reuses a file that is its own partial download", ReusesOwnPartial);
        await runner.Run("retries what can fix itself and not what cannot", ClassifiesRetries);
        await runner.Run("runs the download service with no window", ServiceRunsHeadless);
        await runner.Run("tells two sources apart with a short tag", TagsSources);
        await runner.Run("tags a clashing name before numbering it", TagsClashingNames);
        await runner.Run("opens every connection at once when not adaptive", FixedFanOutIsUnchanged);
        await runner.Run("hashes a file with each supported algorithm", HashesFile);
        await runner.Run("reads the algorithm and value out of a pasted checksum", ReadsPastedChecksum);

        return runner.Report();
    }

    /// <summary>
    /// Each connection is held to 500 KB/s, the way many CDNs do it, so the only way to go
    /// faster is more connections. Four connections would take 20 seconds for this file.
    /// </summary>
    private static async Task RampsUpUnderPerConnectionCap()
    {
        const long size = 40L * 1024 * 1024;

        await using var server = await TestServer.StartAsync(NextPort(), size, "--throttle", "500000");
        using var folder = new TempFolder();

        var original = SegmentedDownloader.RampInterval;
        SegmentedDownloader.RampInterval = TimeSpan.FromSeconds(1);
        try
        {
            // The app always passes a limiter, set to "no limit" by default. It must not be
            // mistaken for a speed limit, or the ramp would never run in the real program.
            var result = await Download(server.Url("capped.bin"), folder.Path, connections: 16,
                adaptive: true, limiter: new BandwidthLimiter());

            VerifyPattern(result.FilePath, size);
            Assert.True(result.PeakConnections >= 9, $"expected growth past the starting four, peaked at {result.PeakConnections}");
            Assert.False(result.ExtraConnectionsRefused, "nothing refused the extra connections");
            Assert.True(result.Elapsed < TimeSpan.FromSeconds(16),
                $"took {result.Elapsed.TotalSeconds:0.0}s; four fixed connections would need about 20");
        }
        finally
        {
            SegmentedDownloader.RampInterval = original;
        }
    }

    /// <summary>
    /// The server answers 503 to anything beyond four parallel ranges. The download must
    /// still finish with the right bytes: the turned-away range goes back to a worker that
    /// frees up, rather than failing the file or being left as a hole.
    /// </summary>
    private static async Task BacksOffWhenRefused()
    {
        const long size = 24L * 1024 * 1024;

        await using var server = await TestServer.StartAsync(NextPort(), size, "--throttle", "1000000", "--max-concurrent", "4");
        using var folder = new TempFolder();

        var original = SegmentedDownloader.RampInterval;
        SegmentedDownloader.RampInterval = TimeSpan.FromSeconds(1);
        try
        {
            var result = await Download(server.Url("limited.bin"), folder.Path, connections: 16, adaptive: true);

            VerifyPattern(result.FilePath, size);
            Assert.Equal(size, new FileInfo(result.FilePath).Length, "final size");
            Assert.True(result.ExtraConnectionsRefused, "the refused extra connection should have been noticed");
        }
        finally
        {
            SegmentedDownloader.RampInterval = original;
        }
    }

    private static async Task RampStopsAtSpeedLimit()
    {
        const long size = 30L * 1024 * 1024;

        // The limit is 3 MB/s and each connection gets 500 KB/s, so six connections reach it.
        // Growth must stop there instead of running on to the ceiling of sixteen.
        await using var server = await TestServer.StartAsync(NextPort(), size, "--throttle", "500000");
        using var folder = new TempFolder();

        var original = SegmentedDownloader.RampInterval;
        SegmentedDownloader.RampInterval = TimeSpan.FromSeconds(1);
        try
        {
            var result = await Download(server.Url("limited-speed.bin"), folder.Path, connections: 16,
                adaptive: true, limiter: new BandwidthLimiter(3_000_000));

            VerifyPattern(result.FilePath, size);
            Assert.True(result.PeakConnections > SegmentedDownloader.RampStart, $"should grow from the start, peaked at {result.PeakConnections}");
            // The ramp goes 4 -> 6 -> 9 and reaches the limit there, so 9 is the usual peak. The
            // window before the plateau is still climbing into the limit (about 2.8 of 3.0 MB/s),
            // so the one measured at the plateau sometimes lands just over the 8% bar and the
            // ramp opens one more batch: measured over 14 runs, 9 eleven times and 13 three times.
            // It cannot go on from 13, because that would take a speed 10% over a limiter's
            // ceiling. So 13 is the real upper bound, and 16 would mean it never stopped.
            Assert.True(result.PeakConnections <= 13, $"should stop once the limit binds, peaked at {result.PeakConnections}");
        }
        finally
        {
            SegmentedDownloader.RampInterval = original;
        }
    }

    private static async Task FixedFanOutIsUnchanged()
    {
        const long size = 8L * 1024 * 1024;

        await using var server = await TestServer.StartAsync(NextPort(), size);
        using var folder = new TempFolder();

        var result = await Download(server.Url("fixed.bin"), folder.Path, connections: 8);

        VerifyPattern(result.FilePath, size);
        Assert.True(result.PeakConnections >= 8, $"a fixed download opens all eight at once, peaked at {result.PeakConnections}");
    }

    // The published test vectors for the three-byte input "abc".
    private const string AbcMd5 = "900150983cd24fb0d6963f7d28e17f72";
    private const string AbcSha1 = "a9993e364706816aba3e25717850c26c9cd0d89d";
    private const string AbcSha256 = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";

    private static Task PicksFreeFileName()
    {
        using var folder = new TempFolder();
        File.WriteAllText(System.IO.Path.Combine(folder.Path, "a.zip"), "x");
        File.WriteAllText(System.IO.Path.Combine(folder.Path, "a (2).zip"), "x");

        Assert.Equal(System.IO.Path.Combine(folder.Path, "a (3).zip"),
            FilePaths.ClashFree(folder.Path, "a.zip", overwrite: false), "next free number");
        Assert.Equal(System.IO.Path.Combine(folder.Path, "a.zip"),
            FilePaths.ClashFree(folder.Path, "a.zip", overwrite: true), "overwrite keeps the name");
        Assert.Equal(System.IO.Path.Combine(folder.Path, "b.zip"),
            FilePaths.ClashFree(folder.Path, "b.zip", overwrite: false), "a free name is left alone");
        return Task.CompletedTask;
    }

    private static Task ReusesOwnPartial()
    {
        using var folder = new TempFolder();
        var path = System.IO.Path.Combine(folder.Path, "a.zip");
        File.WriteAllText(path, "x");

        Assert.Equal(path, FilePaths.ClashFree(folder.Path, "a.zip", false, p => p == path), "own partial is resumed");
        Assert.True(FilePaths.ClashFree(folder.Path, "a.zip", false, _ => false) != path, "someone else's file is avoided");
        return Task.CompletedTask;
    }

    private static Task ClassifiesRetries()
    {
        Assert.False(RetryPolicy.IsPermanent(new HttpRequestException("x", null, System.Net.HttpStatusCode.BadGateway)), "5xx can recover");
        Assert.False(RetryPolicy.IsPermanent(new IOException("dropped")), "a dropped connection can recover");
        Assert.True(RetryPolicy.IsPermanent(new HttpRequestException("x", null, System.Net.HttpStatusCode.Forbidden)), "403 will repeat");
        Assert.True(RetryPolicy.IsPermanent(new HttpRequestException("x", null, System.Net.HttpStatusCode.NotFound)), "404 will repeat");
        Assert.True(RetryPolicy.IsPermanent(new InvalidOperationException("html page")), "a sign-in page will repeat");
        return Task.CompletedTask;
    }

    /// <summary>
    /// The whole service, as a CLI or a watch-folder feature would host it: no UI thread, its
    /// own data folder, and a serial context standing in for the dispatcher. The completion
    /// event has to arrive on that context, which is what lets the service avoid locking.
    /// </summary>
    private static async Task ServiceRunsHeadless()
    {
        const long size = 3L * 1024 * 1024;

        await using var server = await TestServer.StartAsync(NextPort(), size);
        using var folder = new TempFolder();
        using var context = new SerialSynchronizationContext();

        var originalData = DownloadStore.Directory;
        DownloadStore.Directory = System.IO.Path.Combine(folder.Path, "data");
        try
        {
            var finished = new TaskCompletionSource<(DownloadItem Item, bool OnContext)>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            var item = await context.InvokeAsync(() =>
            {
                var settings = new AppSettings { DownloadFolder = folder.Path, SortByType = false };
                var service = new DownloadService(settings, context);
                service.Completed += done => finished.TrySetResult((done, SynchronizationContext.Current == context));
                service.Failed += failed => finished.TrySetException(new Exception(failed.Error ?? "failed"));

                return service.Add(server.Url("headless.bin").ToString(),
                    new DownloadOptions { Directory = folder.Path, Connections = 4 });
            });

            var (completed, onContext) = await finished.Task.WaitAsync(TimeSpan.FromSeconds(30));

            Assert.Equal(DownloadStatus.Complete, completed.Status, "final status");
            Assert.True(ReferenceEquals(item, completed), "the event reports the item that was added");
            Assert.True(onContext, "Completed should be raised on the service's context");
            VerifyPattern(System.IO.Path.Combine(completed.TargetDirectory, completed.Name), size);
        }
        finally
        {
            DownloadStore.Directory = originalData;
        }
    }

    private static Task TagsSources()
    {
        Assert.Equal("a8f3k2", SourceTag.For(new Uri("https://cdn.example.com/v/a8f3k2xq9.mp4?token=1")), "a real file name gives its start");
        Assert.Equal(SourceTag.For(new Uri("https://x.com/v/abc/master.m3u8?sig=1")), SourceTag.For(new Uri("https://x.com/v/abc/master.m3u8?sig=2")), "a rotating token must not change the tag");
        Assert.True(SourceTag.For(new Uri("https://x.com/v/abc/master.m3u8")) != SourceTag.For(new Uri("https://x.com/v/def/master.m3u8")), "two playlists need different tags");
        Assert.Equal(6, SourceTag.For(new Uri("https://x.com/v/abc/master.m3u8")).Length, "a hash tag is six characters");
        Assert.Equal(6, SourceTag.For(new Uri("https://x.com/a.mp4")).Length, "a name too short to use falls back to a hash");
        return Task.CompletedTask;
    }

    private static Task TagsClashingNames()
    {
        using var folder = new TempFolder();
        File.WriteAllText(System.IO.Path.Combine(folder.Path, "Cat.mp4"), "x");

        var first = FilePaths.ClashFree(folder.Path, "Cat.mp4", false, tag: "a8f3k2");
        Assert.Equal(System.IO.Path.Combine(folder.Path, "Cat [a8f3k2].mp4"), first, "tag, not a number");

        File.WriteAllText(first, "x");
        Assert.Equal(System.IO.Path.Combine(folder.Path, "Cat [a8f3k2] (2).mp4"),
            FilePaths.ClashFree(folder.Path, "Cat.mp4", false, tag: "a8f3k2"), "numbering only after the tagged name is taken too");
        Assert.Equal(System.IO.Path.Combine(folder.Path, "Cat (2).mp4"),
            FilePaths.ClashFree(folder.Path, "Cat.mp4", false), "no tag keeps the number");
        Assert.Equal(System.IO.Path.Combine(folder.Path, "Free.mp4"),
            FilePaths.ClashFree(folder.Path, "Free.mp4", false, tag: "a8f3k2"), "a free name is left alone");
        return Task.CompletedTask;
    }

    private static async Task HashesFile()
    {
        using var folder = new TempFolder();
        var path = System.IO.Path.Combine(folder.Path, "abc.txt");
        await File.WriteAllTextAsync(path, "abc");

        Assert.Equal(AbcMd5, await Checksum.ComputeAsync(path, Checksum.Kind.Md5), "md5");
        Assert.Equal(AbcSha1, await Checksum.ComputeAsync(path, Checksum.Kind.Sha1), "sha1");
        Assert.Equal(AbcSha256, await Checksum.ComputeAsync(path, Checksum.Kind.Sha256), "sha256");
        Assert.True(Checksum.Matches(AbcSha256.ToUpperInvariant(), AbcSha256), "case should not matter");
        Assert.False(Checksum.Matches(AbcMd5, AbcSha256), "different digests must not match");
    }

    private static Task ReadsPastedChecksum()
    {
        Assert.Equal(Checksum.Kind.Md5, Checksum.Detect(AbcMd5)!.Value, "32 hex is md5");
        Assert.Equal(Checksum.Kind.Sha1, Checksum.Detect(AbcSha1)!.Value, "40 hex is sha1");
        Assert.Equal(Checksum.Kind.Sha256, Checksum.Detect(AbcSha256)!.Value, "64 hex is sha256");

        // sha256sum prints "<hash>  <file>"; the file name must not matter.
        Assert.Equal(AbcSha256, Checksum.Normalize($"  {AbcSha256.ToUpperInvariant()}  *abc.txt")!, "hash then file name");
        Assert.True(Checksum.Detect("not a checksum") is null, "words are not a checksum");
        Assert.True(Checksum.Detect("abcd") is null, "wrong length is not a checksum");
        return Task.CompletedTask;
    }

    // ═══════════════════════════════════════
    // ═══ Tests
    // ═══════════════════════════════════════

    private static async Task DownloadsCorrectly()
    {
        const long size = 4 * 1024 * 1024;

        await using var server = await TestServer.StartAsync(NextPort(), size);
        using var folder = new TempFolder();

        var result = await Download(server.Url("plain.bin"), folder.Path, connections: 8);

        Assert.Equal(size, result.BytesWritten, "bytes written");
        Assert.True(result.ConnectionsUsed > 1, "should have used more than one connection");
        Assert.False(result.WasResumed, "a first download is not a resume");
        VerifyPattern(result.FilePath, size);
    }

    /// <summary>
    /// One connection in four runs at 100 KB/s. Left alone its 2 MB quarter takes about
    /// 20 seconds; with idle workers taking over its remaining range it should take a
    /// fraction of that.
    /// </summary>
    private static async Task StealsFromSlowConnection()
    {
        const long size = 8 * 1024 * 1024;

        await using var server = await TestServer.StartAsync(NextPort(), size, "--slow-first", "100000");
        using var folder = new TempFolder();

        var result = await Download(server.Url("slow.bin"), folder.Path, connections: 4);

        VerifyPattern(result.FilePath, size);
        Assert.True(result.ConnectionsUsed > 4, $"expected extra ranges to be split off, got {result.ConnectionsUsed}");
        Assert.True(result.Elapsed < TimeSpan.FromSeconds(8),
            $"took {result.Elapsed.TotalSeconds:0.0}s; the slow range was not taken over");
    }

    /// <summary>
    /// The first connection sends 64 KB and then goes silent without closing. Before the
    /// stall timeout existed this hung forever.
    /// </summary>
    private static async Task RecoversFromStall()
    {
        const long size = 4 * 1024 * 1024;

        await using var server = await TestServer.StartAsync(NextPort(), size, "--stall-first");
        using var folder = new TempFolder();

        var original = SegmentedDownloader.StallTimeout;
        SegmentedDownloader.StallTimeout = TimeSpan.FromSeconds(2);

        try
        {
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var result = await Download(server.Url("stall.bin"), folder.Path, connections: 4, ct: limit.Token);
            VerifyPattern(result.FilePath, size);
        }
        finally
        {
            SegmentedDownloader.StallTimeout = original;
        }
    }

    /// <summary>
    /// A sidecar where the first segment is finished and only the last quarter remains.
    /// That quarter used to go down a single connection; now it is split back up.
    /// </summary>
    private static async Task ResumesOnSeveralConnections()
    {
        const long size = 8 * 1024 * 1024;
        const long done = 6 * 1024 * 1024;

        await using var server = await TestServer.StartAsync(NextPort(), size);
        using var folder = new TempFolder();

        using var http = SegmentedDownloader.CreateDefaultClient();
        var request = new DownloadRequest { Url = server.Url("resume.bin"), TargetDirectory = folder.Path, Connections = 4 };
        var info = await HttpProbe.ProbeAsync(http, request, CancellationToken.None);

        var path = Path.Combine(folder.Path, "resume.bin");
        var bytes = new byte[size];
        for (var i = 0; i < done; i++)
            bytes[i] = (byte)(i % 251);
        File.WriteAllBytes(path, bytes);

        new DownloadState
        {
            Url = info.FinalUrl.ToString(),
            TotalLength = size,
            Validator = info.Validator,
            Segments =
            [
                new() { Start = 0, End = done - 1, Completed = done },
                new() { Start = done, End = size - 1, Completed = 0 },
            ],
        }.Save(path);

        var result = await new SegmentedDownloader(http).DownloadAsync(request, probedInfo: info);

        Assert.True(result.WasResumed, "should have resumed from the sidecar");
        Assert.True(result.ConnectionsUsed > 2, $"the remaining quarter should be split, got {result.ConnectionsUsed} ranges");
        VerifyPattern(result.FilePath, size);
    }

    private static async Task UsesSuppliedProbeMetadata()
    {
        const long size = 2 * 1024 * 1024;

        await using var server = await TestServer.StartAsync(NextPort(), size);
        using var folder = new TempFolder();
        using var http = SegmentedDownloader.CreateDefaultClient();
        var request = new DownloadRequest { Url = server.Url("known.bin"), TargetDirectory = folder.Path, Connections = 4 };
        var info = await HttpProbe.ProbeAsync(http, request, CancellationToken.None);
        var downloader = new SegmentedDownloader(http);

        var result = await downloader.DownloadAsync(request, probedInfo: info);

        Assert.Equal(size, result.BytesWritten, "bytes written");
        Assert.Equal("known.bin", Path.GetFileName(result.FilePath), "filename from probe");
        VerifyPattern(result.FilePath, size);
    }

    /// <summary>
    /// The guarantee the Refresh link feature rests on: a download can be pointed at a new
    /// URL and still pick up where it stopped, as long as the file behind it is the same.
    /// </summary>
    private static async Task ResumesAcrossUrlChange()
    {
        const long size = 8 * 1024 * 1024;

        // Throttled so there is time to stop it partway rather than racing the whole file.
        await using var server = await TestServer.StartAsync(NextPort(), size, "--throttle", "2000000");
        using var folder = new TempFolder();

        var stopped = 0L;
        using var cancellation = new CancellationTokenSource();

        var progress = new Progress<ProgressSnapshot>(snapshot =>
        {
            stopped = snapshot.BytesCompleted;

            if (snapshot.BytesCompleted > size / 4)
                cancellation.Cancel();
        });

        try
        {
            await Download(server.Url("movie.bin", "token=FIRST"), folder.Path, 4, progress, cancellation.Token);
            throw new AssertionException("the download was supposed to be interrupted");
        }
        catch (OperationCanceledException)
        {
            // Expected: this is the "link died halfway" case.
        }

        Assert.True(stopped > 0, "should have transferred something before stopping");

        // A different address for the same file, which is what a reissued token looks like.
        var result = await Download(server.Url("movie.bin", "token=SECOND"), folder.Path, connections: 4);

        Assert.True(result.WasResumed, "the new link should have resumed, not restarted");
        Assert.Equal(size, new FileInfo(result.FilePath).Length, "final size");
        VerifyPattern(result.FilePath, size);
    }

    private static async Task FallsBackWhenRangeRefused()
    {
        const long size = 2 * 1024 * 1024;

        await using var server = await TestServer.StartAsync(NextPort(), size, "--no-range");
        using var folder = new TempFolder();

        var result = await Download(server.Url("single.bin"), folder.Path, connections: 8);

        Assert.Equal(1, result.ConnectionsUsed, "a server without Range can only be read once");
        Assert.Equal(size, result.BytesWritten, "bytes written");
        VerifyPattern(result.FilePath, size);
    }

    private static async Task RecoversFromFlakyConnections()
    {
        const long size = 4 * 1024 * 1024;

        await using var server = await TestServer.StartAsync(NextPort(), size, "--flaky", "0.35");
        using var folder = new TempFolder();

        var result = await Download(server.Url("flaky.bin"), folder.Path, connections: 6);

        Assert.Equal(size, new FileInfo(result.FilePath).Length, "final size");
        VerifyPattern(result.FilePath, size);
    }

    /// <summary>
    /// The anti-corruption guarantee. A resume point is only good for the file it was taken
    /// from; if the remote file has changed, appending to what is on disk would produce a
    /// file that is the right length and quietly wrong.
    /// </summary>
    private static async Task RestartsWhenRemoteFileChanged()
    {
        const long firstSize = 8 * 1024 * 1024;
        const long secondSize = 3 * 1024 * 1024;

        using var folder = new TempFolder();
        var port = NextPort();

        // Stop a download partway, leaving a sidecar that describes an 8MB file.
        await using (var server = await TestServer.StartAsync(port, firstSize, "--throttle", "2000000"))
        {
            using var cancellation = new CancellationTokenSource();

            var progress = new Progress<ProgressSnapshot>(snapshot =>
            {
                if (snapshot.BytesCompleted > firstSize / 4)
                    cancellation.Cancel();
            });

            try
            {
                await Download(server.Url("changed.bin"), folder.Path, 4, progress, cancellation.Token);
            }
            catch (OperationCanceledException)
            {
            }
        }

        // Same name and address, different file behind it.
        await using var replaced = await TestServer.StartAsync(port, secondSize);

        var result = await Download(replaced.Url("changed.bin"), folder.Path, connections: 4);

        Assert.False(result.WasResumed, "a changed file must not be resumed into");
        Assert.Equal(secondSize, new FileInfo(result.FilePath).Length, "final size");
        VerifyPattern(result.FilePath, secondSize);
    }

    private static async Task StampsServerTimestamp()
    {
        const long size = 512 * 1024;

        await using var server = await TestServer.StartAsync(NextPort(), size);
        using var folder = new TempFolder();

        var result = await Download(server.Url("dated.bin"), folder.Path, connections: 4, timestamp: true);

        // The fixed date the test server reports.
        var expected = new DateTime(2019, 5, 17, 12, 34, 56, DateTimeKind.Utc);

        Assert.Equal(expected, File.GetLastWriteTimeUtc(result.FilePath), "file timestamp");
    }

    // ═══════════════════════════════════════
    // ═══ HLS
    // ═══════════════════════════════════════

    private static Task ReadsHlsVariants()
    {
        var text = string.Join(Environment.NewLine, MasterPlaylist);
        var variants = HlsPlaylist.ParseMaster(text, new Uri("https://example.com/video/master.m3u8"));

        Assert.Equal(3, variants.Count, "variant count");

        // Best first, so the top of the list is the one most people want.
        Assert.Equal(1080, variants[0].Height ?? 0, "first variant height");
        Assert.Equal(720, variants[1].Height ?? 0, "second variant height");
        Assert.Equal(480, variants[2].Height ?? 0, "third variant height");

        // Relative URIs resolve against the playlist; absolute ones are left alone.
        Assert.Equal("https://example.com/video/high/index.m3u8", variants[0].Url.ToString(), "relative URI resolved");
        Assert.Equal("https://cdn.example.com/mid/index.m3u8", variants[1].Url.ToString(), "absolute URI kept");

        Assert.Equal(4500000L, variants[0].Bandwidth ?? 0, "bandwidth");

        // CODECS holds a comma inside quotes, which a naive split on commas would break.
        Assert.Equal("avc1.640028,mp4a.40.2", variants[0].Codecs ?? "", "quoted codecs survived");
        Assert.True(variants[0].Label.Contains("1080p"), "a readable label");

        return Task.CompletedTask;
    }

    private static Task IgnoresMediaPlaylist()
    {
        var text = string.Join(Environment.NewLine, MediaPlaylist);
        var variants = HlsPlaylist.ParseMaster(text, new Uri("https://example.com/v/index.m3u8"));

        Assert.Equal(0, variants.Count, "a media playlist offers no alternatives");

        return Task.CompletedTask;
    }

    /// <summary>
    /// The Copy-as-cURL builder has to quote exactly once: a quote or backslash in a token
    /// that reaches the shell unescaped either breaks the command or changes the URL.
    /// </summary>
    private static Task BuildsCurlCommand()
    {
        var command = CurlCommand.Build(
            new Uri("https://files.example.com/setup.exe?token=a\"b\\c"),
            new Dictionary<string, string>
            {
                ["Referer"] = "https://example.com/",
                ["User-Agent"] = "unit",
            },
            "session=abc");

        Assert.Equal(
            "curl --fail --location --remote-name \"https://files.example.com/setup.exe?token=a\\\"b\\\\c\" "
            + "--header \"Referer: https://example.com/\" "
            + "--header \"User-Agent: unit\" "
            + "--header \"Cookie: session=abc\"",
            command, "curl command");

        var bare = CurlCommand.Build(new Uri("https://files.example.com/plain.bin"));
        Assert.Equal(
            "curl --fail --location --remote-name \"https://files.example.com/plain.bin\"",
            bare, "a request with no extra context stays one line");

        return Task.CompletedTask;
    }

    private static readonly string[] MasterPlaylist =
    [
        @"#EXTM3U",
        @"#EXT-X-STREAM-INF:BANDWIDTH=1280000,RESOLUTION=854x480,CODECS=""avc1.4d401e,mp4a.40.2""",
        @"low/index.m3u8",
        @"#EXT-X-STREAM-INF:BANDWIDTH=4500000,RESOLUTION=1920x1080,CODECS=""avc1.640028,mp4a.40.2""",
        @"high/index.m3u8",
        @"#EXT-X-STREAM-INF:BANDWIDTH=2500000,RESOLUTION=1280x720",
        @"https://cdn.example.com/mid/index.m3u8",
    ];

    private static readonly string[] MediaPlaylist =
    [
        @"#EXTM3U",
        @"#EXT-X-TARGETDURATION:6",
        @"#EXTINF:6.0,",
        @"segment0.ts",
        @"#EXT-X-ENDLIST",
    ];

    // ═══════════════════════════════════════
    // ═══ FTP
    // ═══════════════════════════════════════

    private static async Task DownloadsOverFtp()
    {
        const long size = 2 * 1024 * 1024;

        await using var server = FtpTestServer.Start(size);
        using var folder = new TempFolder();

        var result = await new FtpDownloader().DownloadAsync(new DownloadRequest
        {
            Url = server.Url("archive.bin"),
            TargetDirectory = folder.Path,
            UseServerTimestamp = true,
        });

        Assert.Equal(size, result.BytesWritten, "bytes written");
        Assert.False(result.WasResumed, "a first download is not a resume");
        Assert.Equal(FtpTestServer.Modified, File.GetLastWriteTimeUtc(result.FilePath), "MDTM timestamp");
        VerifyPattern(result.FilePath, size);
    }

    /// <summary>
    /// A file is not an FTP resume point based on its length alone. Without Stretto's
    /// matching marker, preserve it and download alongside it.
    /// </summary>
    private static async Task ProtectsExistingFtpFile()
    {
        const long size = 2 * 1024 * 1024;
        const long already = 700 * 1024;

        await using var server = FtpTestServer.Start(size);
        using var folder = new TempFolder();

        var existing = Enumerable.Repeat((byte)0xA5, (int)already).ToArray();

        var path = System.IO.Path.Combine(folder.Path, "archive.bin");
        await File.WriteAllBytesAsync(path, existing);

        var result = await new FtpDownloader().DownloadAsync(new DownloadRequest
        {
            Url = server.Url("archive.bin"),
            TargetDirectory = folder.Path,
            Overwrite = false,
        });

        Assert.False(result.WasResumed, "an unmarked existing file must not be resumed");
        Assert.Equal("archive (2).bin", Path.GetFileName(result.FilePath), "safe collision name");
        var preserved = await File.ReadAllBytesAsync(path);
        Assert.True(existing.SequenceEqual(preserved), "existing file preserved");
        Assert.Equal(size, new FileInfo(result.FilePath).Length, "final size");
        VerifyPattern(result.FilePath, size);
    }

    // ═══════════════════════════════════════
    // ═══ Conversion
    // ═══════════════════════════════════════

    private static async Task ConvertsToMp3()
    {
        RequireFfmpeg();

        using var folder = new TempFolder();
        var source = await MakeClip(folder.Path);

        var seen = 0d;
        var progress = new Progress<double>(fraction => seen = Math.Max(seen, fraction));

        var output = await new MediaConverter().ConvertAsync(source, ConversionTarget.Mp3, progress);

        Assert.Equal(".mp3", System.IO.Path.GetExtension(output), "output extension");
        Assert.True(File.Exists(output), "the converted file to exist");
        Assert.True(new FileInfo(output).Length > 1024, "the MP3 to contain audio");
        Assert.True(File.Exists(source), "the original to be left alone");
        Assert.True(seen > 0, "progress to have been reported");
    }

    private static async Task RemuxesToMp4()
    {
        RequireFfmpeg();

        using var folder = new TempFolder();
        var source = await MakeClip(folder.Path);

        var output = await new MediaConverter().ConvertAsync(source, ConversionTarget.Mp4Remux);

        Assert.True(File.Exists(output), "the remuxed file to exist");

        // A remux copies the streams, so it lands within a hair of the original size. A
        // re-encode would come out markedly different, which is how this catches the
        // arguments having silently stopped being a copy.
        var ratio = (double)new FileInfo(output).Length / new FileInfo(source).Length;
        Assert.True(ratio is > 0.8 and < 1.2, $"a copy to be about the same size (was {ratio:F2}x)");
    }

    /// <summary>
    /// The failure path matters as much as the happy one: ffmpeg exiting non-zero has to
    /// surface as a readable message and leave no half written file behind.
    /// </summary>
    private static async Task RefusesNonMedia()
    {
        RequireFfmpeg();

        using var folder = new TempFolder();
        var source = System.IO.Path.Combine(folder.Path, "notes.txt");
        await File.WriteAllTextAsync(source, "this is not a video");

        try
        {
            await new MediaConverter().ConvertAsync(source, ConversionTarget.Mp3);
            throw new AssertionException("converting a text file was supposed to fail");
        }
        catch (InvalidOperationException exception)
        {
            Assert.True(exception.Message.Length > 20, "a message that says something");
            Assert.False(File.Exists(System.IO.Path.Combine(folder.Path, "notes.mp3")),
                "no leftover output from the failed conversion");
        }
    }

    private static void RequireFfmpeg()
    {
        if (!StreamDownloader.IsAvailable())
            throw new SkippedException("ffmpeg is not on PATH");
    }

    /// <summary>Synthesises a short clip with ffmpeg, so no media file has to be checked in.</summary>
    private static async Task<string> MakeClip(string folder)
    {
        var path = System.IO.Path.Combine(folder, "clip.mp4");

        var arguments = new[]
        {
            "-hide_banner", "-nostdin", "-y",
            "-f", "lavfi", "-i", "testsrc=duration=2:size=320x240:rate=15",
            "-f", "lavfi", "-i", "sine=frequency=440:duration=2",
            "-c:v", "libx264", "-preset", "ultrafast", "-c:a", "aac", "-shortest",
            path,
        };

        var info = new System.Diagnostics.ProcessStartInfo("ffmpeg")
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        };

        foreach (var argument in arguments)
            info.ArgumentList.Add(argument);

        using var process = System.Diagnostics.Process.Start(info)!;
        await process.WaitForExitAsync();

        if (!File.Exists(path))
            throw new SkippedException("ffmpeg could not build a test clip here");

        return path;
    }

    // ═══════════════════════════════════════
    // ═══ Helpers
    // ═══════════════════════════════════════

    private static async Task<DownloadResult> Download(
        Uri url,
        string directory,
        int connections,
        IProgress<ProgressSnapshot>? progress = null,
        CancellationToken ct = default,
        bool timestamp = false,
        bool adaptive = false,
        IBandwidthLimiter? limiter = null)
    {
        var downloader = new SegmentedDownloader(SegmentedDownloader.CreateDefaultClient(), maxRetriesPerSegment: 5);

        return await downloader.DownloadAsync(new DownloadRequest
        {
            Url = url,
            TargetDirectory = directory,
            Connections = connections,
            UseServerTimestamp = timestamp,
            Adaptive = adaptive,
            BandwidthLimiter = limiter,
        }, progress, ct);
    }

    /// <summary>
    /// Checks every byte against the server's generated pattern. This is the assertion that
    /// actually matters: a segment written to the wrong offset produces a file of exactly
    /// the right length, so only the contents give it away.
    /// </summary>
    private static void VerifyPattern(string path, long expectedSize)
    {
        var info = new FileInfo(path);
        Assert.Equal(expectedSize, info.Length, "file length");

        using var stream = File.OpenRead(path);
        var buffer = new byte[64 * 1024];
        long offset = 0;

        while (true)
        {
            var read = stream.Read(buffer);
            if (read == 0)
                break;

            for (var i = 0; i < read; i++)
            {
                var expected = (byte)((offset + i) % 251);

                if (buffer[i] != expected)
                    throw new AssertionException(
                        $"byte {offset + i} is {buffer[i]}, expected {expected} — a segment landed at the wrong offset");
            }

            offset += read;
        }
    }
}

/// <summary>A directory that cleans itself up, so a failing run leaves no gigabytes behind.</summary>
public sealed class TempFolder : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), "stretto-tests", Guid.NewGuid().ToString("n"));

    public TempFolder() => Directory.CreateDirectory(Path);

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); }
        catch (IOException) { /* a handle lingering is not a test failure */ }
    }
}
