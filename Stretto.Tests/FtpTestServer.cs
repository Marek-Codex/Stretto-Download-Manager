using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Stretto.Tests;

/// <summary>
/// Just enough FTP to exercise <see cref="Stretto.Core.FtpDownloader"/>: login, SIZE, MDTM,
/// passive mode, REST and RETR.
///
/// In process rather than a real server, because the point is to test our client against
/// the protocol, and a real FTP daemon would be a dependency this suite does not otherwise
/// need. The body is the same generated pattern the HTTP server uses, so the same byte
/// check catches the same class of bug.
/// </summary>
public sealed class FtpTestServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _loop;

    public long Size { get; }
    public int Port { get; }

    /// <summary>The fixed date MDTM reports, so a timestamp assertion has something to hit.</summary>
    public static readonly DateTime Modified = new(2019, 5, 17, 12, 34, 56, DateTimeKind.Utc);

    private FtpTestServer(TcpListener listener, int port, long size)
    {
        _listener = listener;
        Port = port;
        Size = size;
        _loop = Task.Run(AcceptLoop);
    }

    public static FtpTestServer Start(long size)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();

        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        return new FtpTestServer(listener, port, size);
    }

    public Uri Url(string name) => new($"ftp://127.0.0.1:{Port}/{name}");

    private async Task AcceptLoop()
    {
        while (!_stopping.IsCancellationRequested)
        {
            TcpClient client;

            try
            {
                client = await _listener.AcceptTcpClientAsync(_stopping.Token);
            }
            catch (Exception)
            {
                return;
            }

            _ = Task.Run(() => ServeAsync(client));
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.UTF8, false, 1024, leaveOpen: true);
            await using var writer = new StreamWriter(stream, Encoding.UTF8, 1024, leaveOpen: true) { AutoFlush = true };

            await writer.WriteAsync("220 stretto test ftp\r\n");

            TcpListener? passive = null;
            long restart = 0;

            try
            {
                while (await reader.ReadLineAsync(_stopping.Token) is { } line)
                {
                    var space = line.IndexOf(' ');
                    var command = (space < 0 ? line : line[..space]).ToUpperInvariant();
                    var argument = space < 0 ? "" : line[(space + 1)..];

                    switch (command)
                    {
                        case "USER":
                            await writer.WriteAsync("331 need password\r\n");
                            break;

                        case "PASS":
                            await writer.WriteAsync("230 logged in\r\n");
                            break;

                        case "TYPE":
                            await writer.WriteAsync("200 binary\r\n");
                            break;

                        case "SIZE":
                            await writer.WriteAsync($"213 {Size}\r\n");
                            break;

                        case "MDTM":
                            await writer.WriteAsync($"213 {Modified:yyyyMMddHHmmss}\r\n");
                            break;

                        case "REST":
                            restart = long.TryParse(argument, out var offset) ? offset : 0;
                            await writer.WriteAsync("350 restarting\r\n");
                            break;

                        case "PASV":
                            passive?.Stop();
                            passive = new TcpListener(IPAddress.Loopback, 0);
                            passive.Start();

                            var port = ((IPEndPoint)passive.LocalEndpoint).Port;
                            await writer.WriteAsync(
                                $"227 Entering Passive Mode (127,0,0,1,{port >> 8},{port & 0xFF})\r\n");
                            break;

                        case "RETR":
                            if (passive is null)
                            {
                                await writer.WriteAsync("425 use PASV first\r\n");
                                break;
                            }

                            await writer.WriteAsync("150 opening data connection\r\n");
                            await SendFileAsync(passive, restart);

                            passive.Stop();
                            passive = null;
                            restart = 0;

                            await writer.WriteAsync("226 transfer complete\r\n");
                            break;

                        case "QUIT":
                            await writer.WriteAsync("221 bye\r\n");
                            return;

                        default:
                            await writer.WriteAsync("502 not implemented\r\n");
                            break;
                    }
                }
            }
            catch (Exception)
            {
                // A client that hangs up mid conversation is normal here.
            }
            finally
            {
                passive?.Stop();
            }
        }
    }

    /// <summary>Writes the generated body from <paramref name="offset"/> to the end.</summary>
    private async Task SendFileAsync(TcpListener passive, long offset)
    {
        using var data = await passive.AcceptTcpClientAsync(_stopping.Token);
        await using var stream = data.GetStream();

        var buffer = new byte[64 * 1024];
        var position = offset;

        while (position < Size)
        {
            var count = (int)Math.Min(buffer.Length, Size - position);

            for (var i = 0; i < count; i++)
                buffer[i] = (byte)((position + i) % 251);

            await stream.WriteAsync(buffer.AsMemory(0, count), _stopping.Token);
            position += count;
        }

        await stream.FlushAsync(_stopping.Token);
    }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync();

        try
        {
            _listener.Stop();
            await _loop;
        }
        catch (Exception)
        {
            // Shutting down a listener that is mid accept throws; that is the shutdown.
        }

        _stopping.Dispose();
    }
}
