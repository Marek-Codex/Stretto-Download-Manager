using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Stretto.Tests;

/// <summary>Serves generated HLS fixtures over real HTTP, including byte-range requests.</summary>
internal sealed class MediaTestServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly string _root;
    private readonly Task _loop;
    public Uri BaseUrl { get; }

    public MediaTestServer(string root)
    {
        _root = root;
        _listener.Start();
        BaseUrl = new Uri($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/");
        _loop = ServeAsync();
    }

    private async Task ServeAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                _ = RespondAsync(client);
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task RespondAsync(TcpClient client)
    {
        using (client)
        try
        {
            await using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
            var first = await reader.ReadLineAsync(_stop.Token);
            if (first is null) return;
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            while (await reader.ReadLineAsync(_stop.Token) is { Length: > 0 } line)
            {
                var split = line.IndexOf(':');
                if (split > 0) headers[line[..split]] = line[(split + 1)..].Trim();
            }
            var uri = new Uri(BaseUrl, first.Split(' ')[1]);
            var relative = Uri.UnescapeDataString(uri.AbsolutePath).TrimStart('/');
            var path = Path.GetFullPath(Path.Combine(_root, relative));
            var valid = path.StartsWith(Path.GetFullPath(_root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                && File.Exists(path);
            if (!valid)
            {
                Console.WriteLine($"Fixture not found: {relative}; files: {string.Join(", ", Directory.GetFiles(_root, "*", SearchOption.AllDirectories).Select(p => Path.GetRelativePath(_root, p)))}");
                await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"));
                return;
            }
            var data = await File.ReadAllBytesAsync(path);
            var start = 0;
            var end = data.Length - 1;
            var ranged = headers.TryGetValue("Range", out var range);
            if (ranged)
            {
                var bounds = range![6..].Split('-');
                start = int.Parse(bounds[0]);
                if (bounds[1].Length > 0) end = Math.Min(int.Parse(bounds[1]), end);
            }
            var mime = Path.GetExtension(path).ToLowerInvariant() switch
            {
                ".m3u8" or ".m3u" => "application/vnd.apple.mpegurl",
                ".ts" => "video/mp2t",
                ".mp4" or ".m4s" => "video/mp4",
                _ => "application/octet-stream",
            };
            var response = $"HTTP/1.1 {(ranged ? "206 Partial Content" : "200 OK")}\r\nContent-Type: {mime}\r\nContent-Length: {end - start + 1}\r\nConnection: close\r\n"
                + (ranged ? $"Content-Range: bytes {start}-{end}/{data.Length}\r\n" : "") + "\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(response));
            if (!first.StartsWith("HEAD ")) await stream.WriteAsync(data.AsMemory(start, end - start + 1));
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or SocketException) { }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Stop();
        await _loop;
        _stop.Dispose();
    }
}
