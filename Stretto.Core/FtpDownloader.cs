using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Stretto.Core;

/// <summary>
/// A minimal FTP client, enough to fetch a file and resume one.
///
/// Hand rolled rather than using FtpWebRequest, which is obsolete in modern .NET and slated
/// to go. It is also less code than it looks: FTP is line based commands on one socket and
/// raw bytes on another.
///
/// Single stream, unlike the HTTP engine. FTP has no equivalent of a range request -- the
/// closest is REST, which sets the start offset for the whole transfer -- so segmenting
/// means opening several logins to the same server, which a great many FTP servers refuse
/// outright. Resume still works, because REST is exactly what resume needs.
/// </summary>
public sealed class FtpDownloader
{
    private const int DefaultPort = 21;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    public static bool Handles(Uri url) => url.Scheme is "ftp";

    public async Task<DownloadResult> DownloadAsync(
        DownloadRequest request,
        IProgress<ProgressSnapshot>? progress = null,
        CancellationToken ct = default)
    {
        if (!Handles(request.Url))
            throw new ArgumentException($"{request.Url.Scheme} is not FTP.", nameof(request));

        var stopwatch = Stopwatch.StartNew();

        using var control = new FtpControlChannel(request.Url, request.Cookie);
        await control.ConnectAsync(ct).ConfigureAwait(false);

        var remotePath = Uri.UnescapeDataString(request.Url.AbsolutePath);
        var total = await control.SizeAsync(remotePath, ct).ConfigureAwait(false);
        var modified = await control.ModifiedAtAsync(remotePath, ct).ConfigureAwait(false);

        Directory.CreateDirectory(request.TargetDirectory);

        var name = SafeName(request.FileName is { Length: > 0 } chosen
            ? chosen
            : Path.GetFileName(remotePath));
        var identity = new FtpResumeState
        {
            UrlHash = HashUrl(request.Url),
            Length = total,
            ModifiedUtcTicks = modified?.UtcDateTime.Ticks,
        };
        var path = ResolvePath(request, name, identity);
        var statePath = StatePathFor(path);
        var prior = TryLoadResumeState(path, identity);
        var existing = prior is null ? 0 : new FileInfo(path).Length;
        var canResume = prior is not null;

        // A matching Stretto sidecar identifies a partial we own. A bare same-named file
        // is somebody else's file until proven otherwise, so it never becomes a resume point.
        if (canResume && total > 0 && existing == total)
        {
            try { File.Delete(statePath); }
            catch (IOException) { /* the completed file does not depend on its marker */ }
            return Finish(path, total, stopwatch, resumed: true, modified, request);
        }

        if (total > 0)
            AtomicFile.WriteAllText(statePath, JsonSerializer.Serialize(identity));

        await using (var file = new FileStream(path, existing > 0 ? FileMode.Append : FileMode.Create,
                                               FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
        {
            await using var data = await control.OpenDataStreamAsync(remotePath, existing, ct).ConfigureAwait(false);

            var buffer = new byte[1 << 16];
            var completed = existing;
            var lastReport = Stopwatch.StartNew();
            var sinceReport = 0L;

            while (true)
            {
                var read = await data.ReadAsync(buffer, ct).ConfigureAwait(false);
                if (read == 0)
                    break;

                if (total > 0 && completed + read > total)
                    throw new IOException("The FTP server sent more bytes than its advertised file size.");

                await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);

                completed += read;
                sinceReport += read;

                if (request.BandwidthLimiter is { } limiter)
                    await limiter.ThrottleAsync(read, ct).ConfigureAwait(false);

                if (lastReport.ElapsedMilliseconds >= 250)
                {
                    progress?.Report(new ProgressSnapshot
                    {
                        BytesCompleted = completed,
                        TotalBytes = total > 0 ? total : null,
                        BytesPerSecond = sinceReport / lastReport.Elapsed.TotalSeconds,
                        ActiveConnections = 1,
                    });

                    sinceReport = 0;
                    lastReport.Restart();
                }
            }

            if (total > 0 && completed != total)
                throw new IOException($"The FTP server ended the transfer at {completed} of {total} bytes.");

            if (total <= 0)
                total = completed;
        }

        await control.FinishTransferAsync(ct).ConfigureAwait(false);

        try { File.Delete(statePath); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            /* a completed file does not depend on its resume marker */
        }

        return Finish(path, total, stopwatch, resumed: existing > 0, modified, request);
    }

    private static string ResolvePath(DownloadRequest request, string name, FtpResumeState identity) =>
        FilePaths.ClashFree(request.TargetDirectory, name, request.Overwrite,
            path => TryLoadResumeState(path, identity) is not null);

    private static string StatePathFor(string path) => path + ".stretto.ftp";

    private static string HashUrl(Uri url) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(url.AbsoluteUri)));

    private static FtpResumeState? TryLoadResumeState(string path, FtpResumeState expected)
    {
        if (!File.Exists(path) || !File.Exists(StatePathFor(path)))
            return null;

        try
        {
            var saved = JsonSerializer.Deserialize<FtpResumeState>(File.ReadAllText(StatePathFor(path)));
            if (saved is null
                || saved.Version != expected.Version
                || saved.UrlHash != expected.UrlHash
                || saved.Length != expected.Length
                || saved.ModifiedUtcTicks != expected.ModifiedUtcTicks
                || expected.Length <= 0
                || new FileInfo(path).Length > expected.Length)
                return null;

            return saved;
        }
        catch (Exception exception) when (exception is JsonException or IOException)
        {
            return null;
        }
    }

    private sealed class FtpResumeState
    {
        public int Version { get; init; } = 1;
        public string UrlHash { get; init; } = "";
        public long Length { get; init; }
        public long? ModifiedUtcTicks { get; init; }
    }

    private static DownloadResult Finish(
        string path, long total, Stopwatch stopwatch, bool resumed, DateTimeOffset? modified, DownloadRequest request)
    {
        if (request.UseServerTimestamp && modified is { } when)
        {
            try { File.SetLastWriteTimeUtc(path, when.UtcDateTime); }
            catch (IOException) { /* a timestamp is not worth failing a download over */ }
        }

        return new DownloadResult
        {
            FilePath = path,
            BytesWritten = total,
            Elapsed = stopwatch.Elapsed,
            ConnectionsUsed = 1,
            WasResumed = resumed,
        };
    }

    /// <summary>A server controls this name, so it does not get to pick the path.</summary>
    private static string SafeName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "download";

        foreach (var invalid in Path.GetInvalidFileNameChars())
            name = name.Replace(invalid, '_');

        return name;
    }

    /// <summary>The command socket, and the PASV dance that opens data sockets.</summary>
    private sealed class FtpControlChannel(Uri url, string? credentials) : IDisposable
    {
        private readonly TcpClient _client = new();
        private StreamReader? _reader;
        private StreamWriter? _writer;

        public async Task ConnectAsync(CancellationToken ct)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(Timeout);

            await _client.ConnectAsync(url.Host, url.Port > 0 ? url.Port : DefaultPort, timeout.Token)
                .ConfigureAwait(false);

            var stream = _client.GetStream();
            _reader = new StreamReader(stream, Encoding.UTF8, false, 1024, leaveOpen: true);
            _writer = new StreamWriter(stream, Encoding.UTF8, 1024, leaveOpen: true) { AutoFlush = true };

            await ReadReplyAsync(ct).ConfigureAwait(false);   // greeting

            var (user, password) = Credentials();

            var userReply = await SendAsync($"USER {user}", ct).ConfigureAwait(false);

            // 331 means it wants a password; 230 means it let us straight in.
            if (userReply.Code == 331)
                await ExpectAsync($"PASS {password}", 230, ct).ConfigureAwait(false);
            else if (userReply.Code != 230)
                throw new IOException($"FTP login refused: {userReply.Text}");

            await ExpectAsync("TYPE I", 200, ct).ConfigureAwait(false);
        }

        /// <summary>
        /// Credentials from the URL when it carries them, otherwise whatever the caller
        /// passed, otherwise anonymous -- which is what most public FTP expects.
        /// </summary>
        private (string User, string Password) Credentials()
        {
            if (!string.IsNullOrEmpty(url.UserInfo))
            {
                var parts = url.UserInfo.Split(':', 2);
                return (Uri.UnescapeDataString(parts[0]),
                        parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : "");
            }

            if (!string.IsNullOrEmpty(credentials))
            {
                var parts = credentials.Split(':', 2);
                return (parts[0], parts.Length > 1 ? parts[1] : "");
            }

            return ("anonymous", "stretto@localhost");
        }

        public async Task<long> SizeAsync(string path, CancellationToken ct)
        {
            var reply = await SendAsync($"SIZE {path}", ct).ConfigureAwait(false);

            return reply.Code == 213 && long.TryParse(reply.Text.Trim(), out var size) ? size : 0;
        }

        public async Task<DateTimeOffset?> ModifiedAtAsync(string path, CancellationToken ct)
        {
            var reply = await SendAsync($"MDTM {path}", ct).ConfigureAwait(false);

            // 213 YYYYMMDDHHMMSS, always UTC per the spec.
            return reply.Code == 213
                && DateTime.TryParseExact(reply.Text.Trim(), "yyyyMMddHHmmss",
                       CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                       out var parsed)
                ? new DateTimeOffset(parsed, TimeSpan.Zero)
                : null;
        }

        /// <summary>Opens a passive data connection and starts the transfer.</summary>
        public async Task<Stream> OpenDataStreamAsync(string path, long offset, CancellationToken ct)
        {
            var pasv = await SendAsync("PASV", ct).ConfigureAwait(false);

            if (pasv.Code != 227)
                throw new IOException($"FTP server refused passive mode: {pasv.Text}");

            var (host, port) = ParsePassive(pasv.Text);

            var data = new TcpClient();
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                timeout.CancelAfter(Timeout);
                await data.ConnectAsync(host, port, timeout.Token).ConfigureAwait(false);
            }

            if (offset > 0)
                await ExpectAsync($"REST {offset}", 350, ct).ConfigureAwait(false);

            var retr = await SendAsync($"RETR {path}", ct).ConfigureAwait(false);

            // 125 already open, 150 about to open.
            if (retr.Code is not (125 or 150))
            {
                data.Dispose();
                throw new IOException($"FTP server refused the transfer: {retr.Text}");
            }

            return new DataStream(data);
        }

        /// <summary>Reads the completion reply that follows the data connection closing.</summary>
        public async Task FinishTransferAsync(CancellationToken ct)
        {
            try
            {
                await ReadReplyAsync(ct).ConfigureAwait(false);
            }
            catch (IOException)
            {
                // The bytes are already on disk; a missing trailer is not worth failing on.
            }
        }

        /// <summary>227 Entering Passive Mode (h1,h2,h3,h4,p1,p2).</summary>
        private static (string Host, int Port) ParsePassive(string text)
        {
            var open = text.IndexOf('(');
            var close = text.IndexOf(')');

            if (open < 0 || close < open)
                throw new IOException($"Could not read the passive mode reply: {text}");

            var parts = text[(open + 1)..close].Split(',');

            if (parts.Length != 6)
                throw new IOException($"Could not read the passive mode reply: {text}");

            var host = string.Join('.', parts.Take(4));
            var port = (int.Parse(parts[4]) << 8) + int.Parse(parts[5]);

            return (host, port);
        }

        private async Task<Reply> ExpectAsync(string command, int expected, CancellationToken ct)
        {
            var reply = await SendAsync(command, ct).ConfigureAwait(false);

            if (reply.Code != expected)
                throw new IOException($"FTP {command.Split(' ')[0]} failed: {reply.Code} {reply.Text}");

            return reply;
        }

        private async Task<Reply> SendAsync(string command, CancellationToken ct)
        {
            await _writer!.WriteAsync(command.AsMemory(), ct).ConfigureAwait(false);
            await _writer.WriteAsync("\r\n".AsMemory(), ct).ConfigureAwait(false);

            return await ReadReplyAsync(ct).ConfigureAwait(false);
        }

        /// <summary>
        /// Reads one reply, including the multi-line form where the first line ends in a
        /// dash and the last repeats the code followed by a space.
        /// </summary>
        private async Task<Reply> ReadReplyAsync(CancellationToken ct)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(Timeout);

            var line = await _reader!.ReadLineAsync(timeout.Token).ConfigureAwait(false)
                ?? throw new IOException("The FTP server closed the connection.");

            if (line.Length < 4 || !int.TryParse(line[..3], out var code))
                throw new IOException($"Unexpected FTP reply: {line}");

            var text = line[4..];

            if (line[3] == '-')
            {
                var terminator = line[..3] + " ";

                while (true)
                {
                    var next = await _reader.ReadLineAsync(timeout.Token).ConfigureAwait(false)
                        ?? throw new IOException("The FTP server closed the connection.");

                    if (next.StartsWith(terminator, StringComparison.Ordinal))
                    {
                        text = next[4..];
                        break;
                    }
                }
            }

            return new Reply(code, text);
        }

        public void Dispose()
        {
            try
            {
                if (_client.Connected)
                    _writer?.Write("QUIT\r\n");
            }
            catch (IOException)
            {
                // Closing politely is best effort.
            }

            _reader?.Dispose();
            _writer?.Dispose();
            _client.Dispose();
        }

        private readonly record struct Reply(int Code, string Text);
    }

    /// <summary>Owns the data socket so disposing the stream closes the connection.</summary>
    private sealed class DataStream(TcpClient client) : Stream
    {
        private readonly NetworkStream _inner = client.GetStream();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
            _inner.ReadAsync(buffer, ct);

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
                client.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
