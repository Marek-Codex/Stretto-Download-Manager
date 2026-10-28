using Stretto.Core;
using System.Runtime.InteropServices;
using System.Text;

namespace Stretto.App.Services;

/// <summary>
/// A plain text report for pasting into a bug report: versions, the settings that change
/// behaviour, and the end of the log.
///
/// Settings omit credentials and the download list. Logs can still contain download URLs,
/// paths and error details; the report asks the user to review these before sharing.
/// </summary>
public static class Diagnostics
{
    private const int LogLines = 300;

    public static string Build(AppSettings settings)
    {
        var text = new StringBuilder();

        text.AppendLine($"Stretto {Updates.CurrentText}");
        text.AppendLine($"Windows {Environment.OSVersion.Version} ({RuntimeInformation.OSArchitecture})");
        text.AppendLine($".NET {Environment.Version}");
        text.AppendLine($"ffmpeg: {ToolBox.Describe(Tool.Ffmpeg)}");
        text.AppendLine($"yt-dlp: {ToolBox.Describe(Tool.YtDlp)}");
        text.AppendLine();

        text.AppendLine("Settings");
        text.AppendLine($"  connections {settings.DefaultConnections}, simultaneous {settings.MaxConcurrentDownloads}, timeout {settings.ConnectionTimeoutSeconds}s, retries {settings.MaxRetriesPerSegment}");
        text.AppendLine($"  speed limit {(settings.SpeedLimitBytesPerSecond > 0 ? $"{settings.SpeedLimitBytesPerSecond / 1024} KB/s" : "none")}, adaptive {settings.AdaptiveConnections}, auto retry {settings.AutoRetry}");
        text.AppendLine($"  proxy {settings.ProxyMode} ({settings.ProxyProtocol}), Windows credentials {settings.UseWindowsCredentials}");
        text.AppendLine($"  browser monitoring {settings.BrowserMonitoring}, port {settings.HandoffPort}, confirm captures {settings.ConfirmCaptures}, clipboard {settings.MonitorClipboard}");
        text.AppendLine($"  antivirus scan {settings.ScanWithAntivirus}");
        text.AppendLine();

        text.AppendLine($"Log (last {LogLines} lines; review URLs, paths and error details before sharing)");
        text.Append(TailOfLog());

        return Anonymise(text.ToString());
    }

    public static string LogPath => App.StartupLogPath;

    private static string TailOfLog()
    {
        try
        {
            if (!File.Exists(LogPath))
                return "  (no log yet)" + Environment.NewLine;

            // Shared read: the app may be writing to it at this moment.
            using var stream = new FileStream(LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            var lines = new Queue<string>(LogLines);

            while (reader.ReadLine() is { } line)
            {
                if (lines.Count == LogLines) lines.Dequeue();
                lines.Enqueue(line);
            }

            return lines.Count == 0 ? "  (log is empty)" + Environment.NewLine : string.Join(Environment.NewLine, lines) + Environment.NewLine;
        }
        catch (Exception exception)
        {
            return $"  (could not read the log: {exception.Message})" + Environment.NewLine;
        }
    }

    private static string Anonymise(string text)
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        text = profile.Length > 0 ? text.Replace(profile, "~", StringComparison.OrdinalIgnoreCase) : text;

        var user = Environment.UserName;
        return user.Length > 2 ? text.Replace(user, "<user>", StringComparison.OrdinalIgnoreCase) : text;
    }
}
