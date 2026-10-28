using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Stretto.App.Dialogs;
using Stretto.App.Services;
using Stretto.Core;
using System.Diagnostics;

namespace Stretto.App;

/// <summary>Video: the page grabber, the browser media feed, and converting finished files.</summary>
public sealed partial class MainWindow
{
    // ═══════════════════════════════════════
    // ═══ Video download
    // ═══════════════════════════════════════

    private MediaGrabberWindow? _grabber;

    /// <summary>
    /// Opens the media grabber, or brings the open one forward. One window: a second copy
    /// of the same live feed would just be two lists saying the same thing.
    /// </summary>
    private void MediaGrabber_Click(object sender, RoutedEventArgs e)
    {
        if (_grabber is not null)
        {
            _grabber.Activate();
            return;
        }

        _grabber = new MediaGrabberWindow(_feed, _downloads, _settings, _downloads.Http);
        _grabber.Closed += (_, _) => _grabber = null;
        _grabber.Activate();
    }

    private async void VideoDownload_Click(object sender, RoutedEventArgs e)
        => await ShowVideoDownloadAsync(null);

    /// <summary>Opens the video finder, optionally starting from a browser sighting.</summary>
    private async Task ShowVideoDownloadAsync(string? address)
    {
        var dialog = new VideoDownloadDialog(_downloads.Http, _downloads.DefaultDirectory);
        if (!string.IsNullOrWhiteSpace(address))
            dialog.Prefill(address);

        var result = await ShowInWindowAsync(dialog, "Video download", 560, 380);
        if (result == ContentDialogResult.None)
            return;

        var chosen = dialog.Selected;
        if (chosen.Count == 0)
            return;

        // Streams are pulled by ffmpeg, so say so up front rather than failing per item later.
        if (chosen.Any(c => c.NeedsMuxing) && !await ToolBox.IsAvailableAsync(Tool.Ffmpeg))
        {
            await ShowMessageAsync(
                "Those are HLS or DASH streams, which need ffmpeg on your PATH to stitch "
                + "and remux. Install ffmpeg, or pick a direct file instead.");
            return;
        }

        // Same courtesy for the site pages: yt-dlp was there to list them, but it could
        // have gone away between the listing and the download.
        if (chosen.Any(c => c.Kind == MediaKind.Site) && !await ToolBox.IsAvailableAsync(Tool.YtDlp))
        {
            await ShowMessageAsync(
                "Those are video pages, which need yt-dlp on your PATH. Install yt-dlp and "
                + "Stretto will hand them over to it.");
            return;
        }

        ShowActive();

        foreach (var candidate in chosen)
        {
            _downloads.Add(candidate.Url.ToString(), MediaDownloadOptions.FromCandidate(
                candidate, _settings, startNow: result == ContentDialogResult.Primary));
        }
    }

    // ═══════════════════════════════════════
    // ═══ Conversion
    // ═══════════════════════════════════════

    private CancellationTokenSource? _conversion;

    private async void Convert_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem { Tag: string tag }
            || !Enum.TryParse<ConversionTarget>(tag, out var target)
            || Selection().FirstOrDefault() is not { ResolvedPath: { } source } item
            || !File.Exists(source))
            return;

        if (!await ToolBox.IsAvailableAsync(Tool.Ffmpeg))
        {
            await ShowMessageAsync(
                "Converting needs ffmpeg on your PATH. Install ffmpeg and this will work without any further setup.");
            return;
        }

        // One at a time. Two encodes at once just take twice as long each and make the
        // single progress line meaningless.
        if (_conversion is not null)
        {
            await ShowMessageAsync("A conversion is already running. Let that one finish first.");
            return;
        }

        _conversion = new CancellationTokenSource();
        var token = _conversion.Token;

        var name = item.Name;
        ShowNotice("Converting", $"{name} to {MediaConverter.DescribeTarget(target)}.", "Stop", () => _conversion?.Cancel());

        var progress = new Progress<double>(fraction =>
            Notice.Message = $"{name} to {MediaConverter.DescribeTarget(target)} — {fraction * 100:F0}%");

        try
        {
            var written = await new MediaConverter(ToolBox.Resolve(Tool.Ffmpeg))
                .ConvertAsync(source, target, progress, token);

            ShowNotice("Converted", Path.GetFileName(written), "Show file",
                () => Process.Start("explorer.exe", $"/select,\"{written}\""));
        }
        catch (OperationCanceledException)
        {
            ShowNotice("Conversion stopped", $"{name} was left as it was.", "Close", () => { });
        }
        catch (Exception exception)
        {
            App.Log($"Conversion failed: {exception}");
            await ShowMessageAsync(exception.Message);
        }
        finally
        {
            _conversion?.Dispose();
            _conversion = null;
        }
    }
}
