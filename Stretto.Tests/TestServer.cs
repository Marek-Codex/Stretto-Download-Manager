using System.Diagnostics;
using System.Net;

namespace Stretto.Tests;

/// <summary>
/// Runs tools/testserver.js for the life of one test.
///
/// The node server rather than an in-process one on purpose: it is the same server the
/// engine has always been exercised against by hand, its body is generated from a known
/// pattern, and it can be told to misbehave in the specific ways that matter (refuse Range,
/// die partway, throttle).
/// </summary>
public sealed class TestServer : IAsyncDisposable
{
    private readonly Process _process;

    public int Port { get; }
    public long Size { get; }

    private TestServer(Process process, int port, long size)
    {
        _process = process;
        Port = port;
        Size = size;
    }

    /// <summary>A URL for this server. The path becomes the suggested filename.</summary>
    public Uri Url(string name, string? query = null) =>
        new($"http://127.0.0.1:{Port}/{name}{(query is null ? "" : "?" + query)}");

    public static async Task<TestServer> StartAsync(int port, long size, params string[] extra)
    {
        var script = FindScript();

        var arguments = new List<string> { script, "--port", port.ToString(), "--size", size.ToString() };
        arguments.AddRange(extra);

        var info = new ProcessStartInfo("node")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        foreach (var argument in arguments)
            info.ArgumentList.Add(argument);

        var process = Process.Start(info)
            ?? throw new InvalidOperationException("Could not start node. Is it on PATH?");

        var server = new TestServer(process, port, size);
        await server.WaitUntilReadyAsync();
        return server;
    }

    /// <summary>
    /// Polls until the server answers. Without this a fast test races the node startup and
    /// fails on a connection refused that has nothing to do with the engine.
    /// </summary>
    private async Task WaitUntilReadyAsync()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var deadline = DateTime.UtcNow.AddSeconds(20);

        while (DateTime.UtcNow < deadline)
        {
            if (_process.HasExited)
                throw new InvalidOperationException($"testserver exited early: {await _process.StandardError.ReadToEndAsync()}");

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Head, Url("ready.bin"));
                using var response = await http.SendAsync(request);

                if (response.StatusCode is HttpStatusCode.OK or HttpStatusCode.PartialContent)
                    return;
            }
            catch (HttpRequestException)
            {
                // Not listening yet.
            }

            await Task.Delay(100);
        }

        throw new TimeoutException($"testserver on {Port} never became ready.");
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!_process.HasExited)
                _process.Kill(entireProcessTree: true);

            await _process.WaitForExitAsync();
        }
        catch (Exception)
        {
            // A test that has already failed should not be reported as a teardown failure.
        }

        _process.Dispose();
    }

    /// <summary>Walks up from the binaries to the repo, so the runner works from anywhere.</summary>
    private static string FindScript()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "tools", "testserver.js");
            if (File.Exists(candidate))
                return candidate;

            directory = directory.Parent;
        }

        throw new FileNotFoundException("Could not find tools/testserver.js above " + AppContext.BaseDirectory);
    }
}
