using Microsoft.UI.Xaml;
using Stretto.App.Dialogs;
using Stretto.App.Services;
using Stretto.Core;
using System.Diagnostics;
using Windows.ApplicationModel.DataTransfer;

namespace Stretto.App;

/// <summary>
/// The two Browser monitoring settings that surface something to the user rather than
/// changing how a download runs: watching the clipboard for links, and saying when the
/// extension has spotted media.
/// </summary>
public sealed partial class MainWindow
{
    private Action? _noticeAction;
    private string _lastClipboardLink = "";
    private bool _readingClipboard;

    /// <summary>Shows the bar with a single action. Replaces whatever was there.</summary>
    private void ShowNotice(string title, string message, string actionText, Action action)
    {
        Notice.Title = title;
        Notice.Message = message;
        NoticeAction.Content = actionText;
        _noticeAction = action;
        Notice.IsOpen = true;
    }

    private void Notice_Click(object sender, RoutedEventArgs e)
    {
        Notice.IsOpen = false;
        var action = _noticeAction;
        _noticeAction = null;
        action?.Invoke();
    }

    // ═══════════════════════════════════════
    // ═══ Updates
    // ═══════════════════════════════════════

    /// <summary>
    /// The once-a-day check. Silent unless there is something newer, and a failure is only
    /// logged: being offline is not worth interrupting anyone over.
    /// </summary>
    private async Task CheckForUpdatesQuietlyAsync()
    {
        if (!Updates.IsConfigured || !_settings.CheckForUpdates)
            return;

        if (DateTime.UtcNow - _settings.LastUpdateCheck < TimeSpan.FromDays(1))
            return;

        try
        {
            var found = await Updates.CheckAsync(_downloads.Http);
            _settings.LastUpdateCheck = DateTime.UtcNow;
            _settings.Save();

            if (!found.IsNewer || found.PageUrl.Length == 0)
                return;

            ShowNotice($"Stretto {found.Tag} is available",
                $"You have {Updates.CurrentText}.",
                "See what's new",
                () => WebLinks.Open(found.PageUrl));
        }
        catch (Exception exception)
        {
            App.Log($"Automatic update check failed: {exception.Message}");
        }
    }

    // ═══════════════════════════════════════
    // ═══ Finished downloads
    // ═══════════════════════════════════════

    private bool _announcing;

    /// <summary>
    /// Offers the refresh when a download dies of something that looks like a stale link.
    /// Without this the feature is invisible: nobody right clicks a failed row hoping for a
    /// menu item they have never seen.
    /// </summary>
    private void OnDownloadFailed(DownloadItem item)
    {
        if (!item.LooksExpired || !item.CanRefresh)
            return;

        ShowNotice(
            "That link has expired",
            $"{item.Name} was refused with {item.FailureStatus}. The site can issue a new link for it.",
            "Refresh link",
            () =>
            {
                DownloadList.SelectedItem = item;
                RefreshLink_Click(this, new RoutedEventArgs());
            });
    }

    /// <summary>Everything that happens once a download lands.</summary>
    private void OnDownloadCompleted(DownloadItem item)
    {
        // The progress window's job ends with the transfer: the announcement takes over
        // from here. Left open, it just sat at 100%.
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_progressWindows.TryGetValue(item.Id, out var window))
                window.Close();
        });

        ScanIfAsked(item);
        AnnounceComplete(item);
    }

    /// <summary>
    /// Hands the finished file to the configured scanner. Fire and forget: Stretto does not
    /// read the verdict, because a scanner that finds something quarantines or deletes the
    /// file itself, and one that does not has nothing to say.
    /// </summary>
    private void ScanIfAsked(DownloadItem item)
    {
        if (!_settings.ScanWithAntivirus
            || _settings.AntivirusPath.Length == 0
            || item.ResolvedPath is not { } path
            || !File.Exists(path))
            return;

        try
        {
            var template = _settings.AntivirusArguments;

            // {file} lets the path go in the middle, which some scanners need; without it
            // the path is simply appended, which is what most of them expect.
            var arguments = template.Contains("{file}", StringComparison.OrdinalIgnoreCase)
                ? template.Replace("{file}", $"\"{path}\"", StringComparison.OrdinalIgnoreCase)
                : $"{template} \"{path}\"".TrimStart();

            Process.Start(new ProcessStartInfo(_settings.AntivirusPath, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            });
        }
        catch (Exception exception)
        {
            App.Log($"Antivirus scan could not start: {exception.Message}");
        }
    }

    /// <summary>
    /// Says a download has finished, if the setting asks for it. One at a time: a batch
    /// finishing would otherwise stack a window per file.
    /// </summary>
    private async void AnnounceComplete(DownloadItem item)
    {
        if (!_settings.ShowCompleteDialog)
            return;

        if (_announcing)
            return;

        _announcing = true;

        try
        {
            var dialog = new DownloadCompleteDialog(item);
            var result = await ShowInWindowAsync(dialog, "Finished");

            if (dialog.DontShowAgain)
            {
                _settings.ShowCompleteDialog = false;
                _settings.Save();
            }

            switch (result)
            {
                case Microsoft.UI.Xaml.Controls.ContentDialogResult.Primary:
                    OpenPath(item.ResolvedPath);
                    break;

                case Microsoft.UI.Xaml.Controls.ContentDialogResult.Secondary:
                    RevealPath(item);
                    break;
            }
        }
        catch (Exception exception)
        {
            App.Log($"Complete dialog failed: {exception}");
        }
        finally
        {
            _announcing = false;
        }
    }

    // ═══════════════════════════════════════
    // ═══ Clipboard watching
    // ═══════════════════════════════════════

    /// <summary>
    /// Polled rather than event-driven: the clipboard has no reliable change notification for
    /// an unfocused desktop app, and polling a few times a minute costs nothing.
    /// </summary>
    private async Task CheckClipboardAsync()
    {
        if (!_settings.MonitorClipboard || _readingClipboard)
            return;

        _readingClipboard = true;

        try
        {
            var content = Clipboard.GetContent();
            if (!content.Contains(StandardDataFormats.Text))
                return;

            var text = (await content.GetTextAsync())?.Trim() ?? "";

            // Only react to a link that is new, otherwise the bar reappears every poll for
            // as long as the URL stays on the clipboard.
            if (text == _lastClipboardLink)
                return;

            if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
                return;

            _lastClipboardLink = text;

            // Nothing to offer if it is already in the list.
            if (_downloads.Items.Any(i => i.Url == text))
                return;

            ShowNotice("Link copied", text, "Download", () =>
            {
                ShowActive();
                _downloads.Add(text);
            });
        }
        catch (Exception exception)
        {
            // Another process can hold the clipboard open; that is normal, not an error.
            App.Log($"Clipboard read skipped: {exception.Message}");
        }
        finally
        {
            _readingClipboard = false;
        }
    }

    // ═══════════════════════════════════════
    // ═══ Media sightings
    // ═══════════════════════════════════════

    private void WatchMediaFeed()
    {
        _feed.Items.CollectionChanged += (_, e) =>
        {
            if (!_settings.NotifyOnMedia || e.NewItems is null || e.NewItems.Count == 0)
                return;

            var newest = e.NewItems[^1] as MediaReport;
            if (newest is null)
                return;

            var name = string.IsNullOrWhiteSpace(newest.Name) ? "A video" : newest.Name;

            ShowNotice("Video detected", $"{name} is playing in your browser.", "Grab it",
                () => _ = ShowVideoDownloadAsync(newest.Url));
        };
    }
}
