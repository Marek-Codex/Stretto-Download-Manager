using System.Net;
using System.Diagnostics;
using System.Text.Json;
using Stretto.Core;

namespace Stretto.Tests;

public static partial class Program
{
    private static async Task RunReleaseRegressions(Runner runner)
    {
        await runner.Run("does not delete an existing file for an unstarted download", ProtectsUnownedFiles);
        await runner.Run("preserves video engine, format and stream metadata across restart", RestoresVideoMetadata);
        await runner.Run("retains the actual collision path through pause and resume", RetainsPartialPath);
        await runner.Run("downloads conventional and unconventional HLS without losing audio", DownloadsHlsFixtures);
        await runner.Run("forwards browser context when fetching HLS qualities", ForwardsVariantContext);
        await runner.Run("finds relative and escaped playlists in player scripts", FindsPlayerPlaylists);
        await runner.Run("snapshots autosaves and refuses stale background writes", AutosaveOrdering);
        await runner.Run("uses yt-dlp's final post-processing path and handles existing output", YtDlpFinalPath);
        await runner.Run("does not guess yt-dlp output from unrelated files or human logs", YtDlpRefusesUnknownOutput);
        await runner.Run("rejects missing, ambiguous and out-of-folder yt-dlp results", YtDlpValidatesOutput);
        await runner.Run("keeps concurrent yt-dlp results separate", YtDlpConcurrentResults);
        await runner.Run("protects proxy passwords and migrates old settings", ProtectsProxySettings);
        await runner.Run("keeps suggested file names inside the target folder", ConfinesSuggestedNames);
        await runner.Run("reads helper checksums from publisher listings", ReadsToolChecksums);
        await runner.Run("marks finished files as downloaded from the internet", MarksFromInternet);
        await runner.Run("keeps browser cookies on their own site across redirects", ScopesSessionToSite);
        await runner.Run("confines captured credentials to the exact origin", RedirectCredentialBoundaries);
        await runner.Run("requires fresh approval after revoking an extension", PairingRevocation);
        await runner.Run("keeps https cookies off http links in a batch", ScopesBatchCookies);
        await runner.Run("refuses FTP addresses that smuggle a second command", RefusesFtpLineBreaks);
        await runner.Run("imports lists paused and without file ownership", ImportsWithoutAuthority);
        if (Environment.GetEnvironmentVariable("STRETTO_TEST_YTDLP") is { Length: > 0 })
            await runner.Run("downloads and reopens a local fixture with the real yt-dlp", RealYtDlpOutput);
    }

    private static Task ConfinesSuggestedNames()
    {
        using var folder = new TempFolder();
        foreach (var hostile in new[] { @"..\..\evil.exe", "../evil.exe", @"C:\Windows\evil.exe", "..", "CON", "nul.txt", "name. . ." })
        {
            var path = FilePaths.ClashFree(folder.Path, hostile, overwrite: true);
            Assert.Equal(Path.GetFullPath(folder.Path), Path.GetDirectoryName(Path.GetFullPath(path)), $"'{hostile}' stays in the folder");
            var name = Path.GetFileName(path);
            Assert.True(name.Split('.')[0] is not ("CON" or "nul") && !name.EndsWith('.'), $"'{hostile}' became a plain file name, not '{name}'");
        }

        Assert.Equal("report.pdf", FilePaths.SafeName("report.pdf"), "an ordinary name is untouched");
        Assert.Equal("download", FilePaths.SafeName("  "), "a blank name falls back");
        return Task.CompletedTask;
    }

    private sealed class RedirectingHandler : HttpMessageHandler
    {
        public List<(Uri Url, string? Cookie, string? Token)> Seen { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Seen.Add((request.RequestUri!,
                request.Headers.TryGetValues("Cookie", out var c) ? c.Single() : null,
                request.Headers.TryGetValues("X-Signed", out var t) ? t.Single() : null));

            var response = new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request };
            var next = request.RequestUri!.AbsolutePath switch
            {
                "/start" => "https://dl.example.test/hop",
                "/hop" => "https://cdn.other.test/file",
                "/file" => "https://www.example.test/back",
                _ => null,
            };
            if (next is not null)
            {
                response.StatusCode = HttpStatusCode.Found;
                response.Headers.Location = new Uri(next);
            }
            return Task.FromResult(response);
        }
    }

    private static async Task ScopesSessionToSite()
    {
        var inner = new RedirectingHandler();
        using var client = new HttpClient(new SafeRedirects(inner));
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://www.example.test/start");
        request.Headers.TryAddWithoutValidation("Cookie", "session=SECRET");
        request.Headers.TryAddWithoutValidation("X-Signed", "abc");
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode, "followed to the end");
        Assert.Equal(4, inner.Seen.Count, "four hops");
        Assert.True(inner.Seen[1].Cookie is null, "a sibling subdomain does not inherit captured cookies");
        Assert.True(inner.Seen[2].Cookie is null && inner.Seen[2].Token is null, "another site gets no session");
        Assert.True(inner.Seen[3].Cookie is null, "coming back does not restore it");
        Assert.True(!SafeRedirects.SameOrigin(new Uri("https://example.test/"), new Uri("http://example.test/")), "no downgrade");
    }

    private static Task MarksFromInternet()
    {
        using var folder = new TempFolder();
        var path = Path.Combine(folder.Path, "setup.exe");
        File.WriteAllText(path, "x");
        FilePaths.MarkFromInternet(path, "https://cdn.example.test/setup.exe?token=secret", "https://example.test/page");
        var zone = File.ReadAllText(path + ":Zone.Identifier");
        Assert.True(zone.Contains("ZoneId=3"), "internet zone");
        Assert.True(zone.Contains("HostUrl=https://cdn.example.test/setup.exe") && !zone.Contains("secret"), "host recorded without its token");
        foreach (var url in new[] { "ftp://user:password@example.test/file.zip", "https://user:p%40ss@example.test/file.zip?secret=yes#private", "https://user@example.test/file.zip" })
        {
            FilePaths.MarkFromInternet(path, url, url);
            zone = File.ReadAllText(path + ":Zone.Identifier");
            Assert.True(zone.Contains("ZoneId=3") && zone.Contains("example.test/file.zip"), "mark and public path retained");
            Assert.True(!zone.Contains("user") && !zone.Contains("password") && !zone.Contains("p%40ss") && !zone.Contains("secret") && !zone.Contains("private"), "host and referrer metadata omit credentials, query and fragment");
        }
        return Task.CompletedTask;
    }

    private sealed class CredentialRedirectHandler(Uri destination) : HttpMessageHandler
    {
        public Dictionary<string, string>? FinalHeaders;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request };
            if (request.RequestUri!.AbsolutePath == "/start")
            {
                response.StatusCode = HttpStatusCode.Found;
                response.Headers.Location = destination;
            }
            else FinalHeaders = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
            return Task.FromResult(response);
        }
    }

    private static async Task RedirectCredentialBoundaries()
    {
        var origin = new Uri("https://www.example.test/start");
        foreach (var target in new[] { "https://www.example.test/file", "https://WWW.example.test:443/file", "https://dl.example.test/file", "https://example.test/file", "https://www.example.test:8443/file", "https://unrelated.test/file" })
        {
            var handler = new CredentialRedirectHandler(new Uri(target));
            using var http = new HttpClient(new SafeRedirects(handler));
            var download = new DownloadRequest { Url = origin, TargetDirectory = ".", Cookie = "session=FAKE", Headers = new Dictionary<string, string> {
                ["Authorization"] = "Bearer FAKE", ["X-Token"] = "FAKE", ["Session-Token"] = "FAKE",
                ["Referer"] = "https://www.example.test/page?token=FAKE", ["Range"] = "bytes=1-2", ["User-Agent"] = "Stretto-test"
            } };
            using var request = HttpProbe.BuildRequest(download);
            using var response = await http.SendAsync(request);
            var headers = handler.FinalHeaders!;
            var same = SafeRedirects.SameOrigin(origin, new Uri(target));
            foreach (var secret in new[] { "Cookie", "Authorization", "X-Token", "Session-Token", "Referer" })
                Assert.Equal(same, headers.ContainsKey(secret), $"{secret} follows only its original origin: {target}");
            Assert.Equal("bytes=1-2", headers["Range"], "redirect preserves ranged transfer semantics");
            Assert.Equal("Stretto-test", headers["User-Agent"], "ordinary client identification survives");

            // Segments rebuild the request for a probe's final URL. That path must use
            // the very same boundary, not quietly put the browser credentials back.
            using var rebuilt = HttpProbe.BuildRequest(download, new Uri(target));
            Assert.Equal(same, rebuilt.Headers.Contains("Authorization"), "direct final-URL request uses the origin boundary");
            Assert.Equal(same, rebuilt.Headers.Contains("Session-Token"), "non-X credentials cannot bypass the boundary");
        }
        Assert.False(SafeRedirects.SameOrigin(new Uri("http://example.test/"), new Uri("https://example.test/")), "a scheme change is another origin too");
    }

    private static Task ScopesBatchCookies()
    {
        var current = new BatchHandoff
        {
            Urls = [],
            Cookies = new() { ["https://img.example.test"] = "secure=1", ["http://plain.example.test"] = "plain=1" },
        };
        Assert.Equal("secure=1", current.CookieFor("https://img.example.test/a.jpg"), "same origin gets its cookies");
        Assert.True(current.CookieFor("http://img.example.test/a.jpg") is null, "http does not get the https jar");
        Assert.Equal("plain=1", current.CookieFor("http://plain.example.test/a.jpg"), "an http origin gets its own");

        // What an older extension sends: bare host, read from the https:// jar.
        var legacy = new BatchHandoff { Urls = [], Cookies = new() { ["img.example.test"] = "secure=1" } };
        Assert.Equal("secure=1", legacy.CookieFor("https://img.example.test/a.jpg"), "legacy keys still serve https");
        Assert.True(legacy.CookieFor("http://img.example.test/a.jpg") is null, "legacy keys never serve http");
        return Task.CompletedTask;
    }

    private static async Task RefusesFtpLineBreaks()
    {
        await using var server = FtpTestServer.Start(1024);
        using var folder = new TempFolder();
        var smuggled = new Uri(server.Url("a.bin") + "%0D%0ADELE%20important.txt");
        try
        {
            await new FtpDownloader().DownloadAsync(new DownloadRequest { Url = smuggled, TargetDirectory = folder.Path });
            throw new Exception("a line break in the path was sent to the server");
        }
        catch (IOException exception)
        {
            Assert.True(exception.Message.Contains("line break"), "refused for the right reason: " + exception.Message);
        }
    }

    private static Task ImportsWithoutAuthority()
    {
        using var folder = new TempFolder();
        var originalData = DownloadStore.Directory;
        DownloadStore.Directory = Path.Combine(folder.Path, "data");
        try
        {
            var victim = Path.Combine(folder.Path, "thesis.docx");
            File.WriteAllText(victim, "years of work");
            var service = new DownloadService(new AppSettings());
            service.Import([
                new DownloadStore.Entry { Url = "https://example.test/a.bin", Name = "a.bin", TargetDirectory = folder.Path,
                    FilePath = victim, Status = DownloadStatus.Queued, BytesCompleted = 10 },
                new DownloadStore.Entry { Url = "https://example.test/b.bin", Name = "b.bin", TargetDirectory = folder.Path,
                    Status = DownloadStatus.Failed, Error = "old" },
            ]);
            Assert.True(service.Items.All(i => i.Status == DownloadStatus.Paused), "everything lands paused, queued included");
            Assert.True(service.Items.All(i => i.FilePath is null), "a list cannot claim a file on this machine");
            Assert.True(service.Items.All(i => i.BytesCompleted == 0), "no progress is claimed either");
            Assert.Equal("years of work", File.ReadAllText(victim), "nothing touched on import");
        }
        finally { DownloadStore.Directory = originalData; }
        return Task.CompletedTask;
    }

    private static Task ReadsToolChecksums()
    {
        var hash = new string('a', 64);
        var other = new string('b', 64);
        Assert.Equal(hash, ToolBox.ExpectedHash($"{other}  yt-dlp_x86.exe\n{hash}  yt-dlp.exe\n", "yt-dlp.exe", single: false), "named line");
        Assert.Equal(hash, ToolBox.ExpectedHash($"{hash}\n", "ffmpeg.exe", single: true), "single-hash file");
        Assert.True(ToolBox.ExpectedHash($"{other}  yt-dlp.tar.gz", "yt-dlp.exe", single: false) is null, "absent file");
        return Task.CompletedTask;
    }

    private static async Task AutosaveOrdering()
    {
        using var folder = new TempFolder();
        var originalData = DownloadStore.Directory;
        DownloadStore.Directory = folder.Path;
        try
        {
            var store = new DownloadStore();
            var item = new DownloadItem { Url = "https://example.test/a", TargetDirectory = folder.Path, Name = "before" };
            var saving = store.SaveAsync([item]);
            item.Name = "after";
            await saving;
            Assert.Equal("before", store.Load().Single().Name, "background save uses its UI snapshot");

            IEnumerable<DownloadItem> DelayedOldSnapshot()
            {
                store.Save([item]);
                yield return new DownloadItem { Url = item.Url, TargetDirectory = folder.Path, Name = "stale" };
            }
            await store.SaveAsync(DelayedOldSnapshot());
            Assert.Equal("after", store.Load().Single().Name, "late older snapshot cannot replace a newer save");
            var pending = Enumerable.Range(0, 20).Select(_ => store.SaveAsync([item])).ToArray();
            item.Name = "latest";
            store.Save([item]);
            await Task.WhenAll(pending);
            Assert.Equal("latest", store.Load().Single().Name, "shutdown save wins over pending periodic writes");
        }
        finally { DownloadStore.Directory = originalData; }
    }

    private static async Task ProtectsUnownedFiles()
    {
        using var folder = new TempFolder();
        var originalData = DownloadStore.Directory;
        DownloadStore.Directory = Path.Combine(folder.Path, "data");
        try
        {
            var path = Path.Combine(folder.Path, "existing.bin");
            File.WriteAllText(path, "original user file");
            var service = new DownloadService(new AppSettings());
            var item = service.Add("https://example.invalid/existing.bin", new DownloadOptions
                { Directory = folder.Path, FileName = "existing.bin", StartNow = false });
            service.Remove(item, deleteFiles: true);
            Assert.Equal("original user file", File.ReadAllText(path), "unstarted remove preserves the original");

            await using var server = await TestServer.StartAsync(NextPort(), 1024);
            using var context = new SerialSynchronizationContext();
            var finished = new TaskCompletionSource<DownloadItem>(TaskCreationOptions.RunContinuationsAsynchronously);
            await context.InvokeAsync(() =>
            {
                var downloads = new DownloadService(new AppSettings { OverwriteExisting = false }, context);
                var pending = downloads.Add(server.Url("existing.bin").ToString(), new DownloadOptions
                    { Directory = folder.Path, FileName = "existing.bin", StartNow = false });
                downloads.Completed += done => finished.TrySetResult(done);
                downloads.Failed += failed => finished.TrySetException(new Exception(failed.Error));
                _ = downloads.RestartAsync(pending);
                return true;
            });
            var result = await finished.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal("original user file", File.ReadAllText(path), "unstarted restart preserves the original");
            Assert.True(result.FilePath != path, "restart downloads beside the existing file");
        }
        finally { DownloadStore.Directory = originalData; }
    }

    private static Task RestoresVideoMetadata()
    {
        using var folder = new TempFolder();
        var originalData = DownloadStore.Directory;
        DownloadStore.Directory = folder.Path;
        try
        {
            var service = new DownloadService(new AppSettings());
            service.Add("https://example.invalid/watch?v=1", new DownloadOptions
            {
                StartNow = false, IsSiteVideo = true, FormatId = "137", NamedFromTitle = true,
                Mime = "application/dash+xml", HlsProgramIndex = 2,
            });
            service.Save();
            var restored = new DownloadService(new AppSettings());
            restored.Restore();
            var item = restored.Items.Single();
            Assert.True(item.IsSiteVideo, "site video engine survives");
            Assert.Equal("137", item.FormatId, "selected site format survives");
            Assert.Equal(2, item.HlsProgramIndex, "HLS program survives");
            Assert.Equal("application/dash+xml", item.Mime, "manifest type survives");
            Assert.True(item.NamedFromTitle, "title naming survives");
        }
        finally { DownloadStore.Directory = originalData; }
        return Task.CompletedTask;
    }

    private static async Task RetainsPartialPath()
    {
        const long size = 4 * 1024 * 1024;
        await using var server = await TestServer.StartAsync(NextPort(), size, "--throttle", "1000000");
        using var folder = new TempFolder();
        using var context = new SerialSynchronizationContext();
        var originalData = DownloadStore.Directory;
        DownloadStore.Directory = Path.Combine(folder.Path, "data");
        try
        {
            var original = Path.Combine(folder.Path, "collision.bin");
            File.WriteAllText(original, "keep me");
            DownloadService service = null!;
            var item = await context.InvokeAsync(() =>
            {
                service = new DownloadService(new AppSettings { OverwriteExisting = false, AutoRetry = false }, context);
                return service.Add(server.Url("collision.bin").ToString(), new DownloadOptions { Directory = folder.Path, Connections = 2 });
            });
            await WaitUntil(() => item.BytesCompleted > 200000);
            await context.InvokeAsync(() => { service.Pause(item); return true; });
            await WaitUntil(() => !item.IsRunning);
            var actual = item.FilePath;
            Assert.True(actual is not null && actual != original && File.Exists(actual), "actual partial path is retained");
            service.Save();
            var restored = new DownloadService(new AppSettings());
            restored.Restore();
            Assert.Equal(actual, restored.Items.Single().FilePath, "actual partial path survives restart");
            await context.InvokeAsync(() => { service.Resume(item); return true; });
            await WaitUntil(() => !item.IsRunning && item.Status != DownloadStatus.Queued);
            Assert.Equal(DownloadStatus.Complete, item.Status, item.Error ?? "download completes");
            Assert.Equal(actual, item.FilePath, "resume stays at the numbered path");
            VerifyPattern(item.FilePath!, size);
            Assert.Equal("keep me", File.ReadAllText(original), "unrelated file stays intact");
            await context.InvokeAsync(() => { service.Remove(item, deleteFiles: true); return true; });
            Assert.False(File.Exists(actual), "remove deletes the owned file");
            Assert.True(File.Exists(original), "remove preserves the original");
            var active = await context.InvokeAsync(() => service.Add(server.Url("collision.bin").ToString(),
                new DownloadOptions { Directory = folder.Path, Connections = 2 }));
            await WaitUntil(() => active.BytesCompleted > 200000);
            var activePath = active.FilePath;
            await context.InvokeAsync(() => { service.Remove(active, deleteFiles: true); return true; });
            await WaitUntil(() => !active.IsRunning && !File.Exists(activePath));
            Assert.False(File.Exists(activePath + DownloadState.SidecarExtension), "active removal waits for final sidecar before deleting");
            Assert.Equal("keep me", File.ReadAllText(original), "active removal preserves unrelated file");
        }
        finally { DownloadStore.Directory = originalData; }
    }

    private static async Task WaitUntil(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (!predicate()) await Task.Delay(30, timeout.Token);
    }

    private static async Task<string> RunMediaTool(string exe, params string[] arguments)
    {
        var start = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardError = true, RedirectStandardOutput = true,
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(arguments[^1]))! };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var kill = timeout.Token.Register(() => { try { process.Kill(true); } catch { } });
        await process.WaitForExitAsync(timeout.Token);
        var output = await stdout;
        var errors = await stderr;
        if (process.ExitCode != 0) throw new Exception($"{exe} failed: {errors}");
        return output;
    }

    private static async Task DownloadsHlsFixtures()
    {
        RequireFfmpeg();
        using var folder = new TempFolder();
        var source = Path.Combine(folder.Path, "source.mp4");
        await RunMediaTool("ffmpeg", "-hide_banner", "-nostdin", "-y",
            "-f", "lavfi", "-i", "testsrc=duration=3:size=320x240:rate=15",
            "-f", "lavfi", "-i", "sine=frequency=440:duration=3",
            "-c:v", "libx264", "-preset", "ultrafast", "-g", "15", "-sc_threshold", "0",
            "-c:a", "aac", "-shortest", source);
        foreach (var name in new[] { "ts", "fmp4", "range", "video", "audio", "mp3", "encrypted" })
            Directory.CreateDirectory(Path.Combine(folder.Path, name));
        async Task Generate(string name, params string[] options)
        {
            var args = new List<string> { "-hide_banner", "-nostdin", "-y", "-i", source };
            args.AddRange(options);
            args.AddRange(["-f", "hls", "-hls_time", "1", "-hls_list_size", "0", Path.Combine(folder.Path, name, "index.m3u8")]);
            await RunMediaTool("ffmpeg", args.ToArray());
        }
        await Generate("ts", "-c", "copy");
        await Generate("fmp4", "-c", "copy", "-hls_segment_type", "fmp4");
        await Generate("range", "-c", "copy", "-hls_flags", "single_file");
        await Generate("video", "-c:v", "copy", "-an");
        await Generate("audio", "-vn", "-c:a", "copy");
        await Generate("mp3", "-vn", "-c:a", "libmp3lame");
        var key = Path.Combine(folder.Path, "encrypted", "key.key");
        File.WriteAllBytes(key, Enumerable.Range(0, 16).Select(i => (byte)i).ToArray());
        var keyInfo = Path.Combine(folder.Path, "keyinfo.txt");
        File.WriteAllText(keyInfo, "key.key\n" + key + "\n");
        await Generate("encrypted", "-c", "copy", "-hls_key_info_file", keyInfo);
        var odd = Path.Combine(folder.Path, "odd");
        Directory.CreateDirectory(odd);
        foreach (var file in Directory.GetFiles(Path.Combine(folder.Path, "ts")))
            File.Copy(file, Path.Combine(odd, Path.GetFileName(file).Replace(".ts", ".jpg")));
        var oddPlaylist = Path.Combine(odd, "index.m3u8");
        File.WriteAllText(oddPlaylist, File.ReadAllText(oddPlaylist).Replace(".ts", ".jpg"));
        File.WriteAllText(Path.Combine(folder.Path, "master.m3u8"), """
            #EXTM3U
            #EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID="sound",NAME="English",DEFAULT=YES,AUTOSELECT=YES,URI="audio/index.m3u8"
            #EXT-X-STREAM-INF:BANDWIDTH=500000,RESOLUTION=320x240,AUDIO="sound"
            video/index.m3u8
            #EXT-X-STREAM-INF:BANDWIDTH=600000,RESOLUTION=320x240,AUDIO="sound"
            video/index.m3u8
            """);
        await using var server = new MediaTestServer(folder.Path);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var output = Path.Combine(folder.Path, "output");
        foreach (var name in new[] { "ts", "fmp4", "range", "odd", "mp3", "master", "encrypted" })
        {
            var url = new Uri(server.BaseUrl, name == "master" ? "master.m3u8" : name + "/index.m3u8");
            var result = await new StreamDownloader().DownloadAsync(url, output, name + ".mp4", ct: timeout.Token,
                hlsProgramIndex: name == "master" ? 1 : null);
            var probe = await RunMediaTool("ffprobe", "-v", "error", "-show_streams", "-show_format", "-of", "json", result.FilePath);
            using var json = JsonDocument.Parse(probe);
            var streams = json.RootElement.GetProperty("streams").EnumerateArray().ToArray();
            Assert.True(streams.Any(s => s.GetProperty("codec_type").GetString() == "audio"), name + " retains audio");
            if (name != "mp3") Assert.True(streams.Any(s => s.GetProperty("codec_type").GetString() == "video"), name + " retains video");
            var duration = double.Parse(json.RootElement.GetProperty("format").GetProperty("duration").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
            Assert.True(duration >= 2.9 && duration < 4, name + " contains all three seconds, not just the first segment");
        }
        var variants = HlsPlaylist.ParseMaster(File.ReadAllText(Path.Combine(folder.Path, "master.m3u8")), new Uri(server.BaseUrl, "master.m3u8"));
        Assert.Equal(1, variants[0].ProgramIndex, "quality sorting preserves source program order");
    }

    private sealed class ContextHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal("Bearer test", request.Headers.GetValues("Authorization").Single(), "authorization forwarded");
            Assert.Equal("Browser/1", request.Headers.UserAgent.ToString(), "user agent forwarded");
            Assert.Equal("session=test", request.Headers.GetValues("Cookie").Single(), "cookie forwarded");
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            { Content = new StringContent("\uFEFF#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=42\nrelative.m3u8\n"), RequestMessage = request });
        }
    }

    private static async Task ForwardsVariantContext()
    {
        using var http = new HttpClient(new ContextHandler());
        var result = await HlsPlaylist.FetchVariantsAsync(http, new Uri("https://example.test/master.m3u8"), "session=test",
            userAgent: "Browser/1", headers: new Dictionary<string, string> { ["Authorization"] = "Bearer test" });
        Assert.Equal(1, result.Count, "BOM-prefixed authenticated master is parsed");
    }

    private static Task FindsPlayerPlaylists()
    {
        var page = new Uri("https://example.test/watch/page");
        var result = MediaExtractor.Extract("""
            <script>player.loadSource('../hls/master.m3u8?one=1&amp;two=2');
            var other = "https:\/\/cdn.example.test\/stream.m3u8";</script>
            """, page);
        Assert.Equal(2, result.Count, "relative and escaped playlists found");
        Assert.True(result.Any(c => c.Url.ToString() == "https://example.test/hls/master.m3u8?one=1&two=2"), "HTML entities decoded");
        Assert.True(result.Any(c => c.Url.Host == "cdn.example.test"), "escaped slashes decoded");
        return Task.CompletedTask;
    }
}
