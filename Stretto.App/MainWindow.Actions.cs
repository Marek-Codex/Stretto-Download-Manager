using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Stretto.App.Dialogs;
using Stretto.App.Models;
using Stretto.App.Services;
using Stretto.Core;
using System.Diagnostics;
using Windows.ApplicationModel.DataTransfer;
using WinRT.Interop;

namespace Stretto.App;

/// <summary>What can be done to downloads in the list: bulk actions, the per-row context menu, and import and export.</summary>
public sealed partial class MainWindow
{
    // ═══════════════════════════════════════
    // ═══ Bulk actions
    // ═══════════════════════════════════════

    /// <summary>Honours the General setting rather than assuming Open.</summary>
    private void DownloadList_DoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
    {
        switch (_settings.DoubleClick)
        {
            case DoubleClickAction.OpenFile:
                OpenFile_Click(sender, e);
                break;
            case DoubleClickAction.OpenFolder:
                OpenContainingFolder_Click(sender, e);
                break;
        }
    }

    /// <summary>
    /// Opens the per-download progress window. Only ever called for one download at a time:
    /// a batch opening one window per file would bury the screen.
    /// </summary>
    private void ShowProgressFor(DownloadItem item)
    {
        try
        {
            if (_progressWindows.TryGetValue(item.Id, out var open))
            {
                open.Activate();
                return;
            }

            var window = new DownloadProgressWindow(AppWindow, item, _downloads);
            _progressWindows[item.Id] = window;
            window.Closed += (_, _) => _progressWindows.Remove(item.Id);
            window.Activate();
            Interop.WindowFocus.BringToFront(window);
        }
        catch (Exception exception)
        {
            App.Log($"Could not open the progress window: {exception}");
        }
    }

    private void ShowProgress_Click(object sender, RoutedEventArgs e)
    {
        if (Selection().FirstOrDefault() is { } item)
            ShowProgressFor(item);
    }

    private void ResumeAll_Click(object sender, RoutedEventArgs e) => _downloads.ResumeAll();

    private void PauseAll_Click(object sender, RoutedEventArgs e) => _downloads.PauseAll();

    /// <summary>Queues are a page now. Still a Task, so the callers that awaited the old
    /// window read the same.</summary>
    private Task ShowQueuesAsync()
    {
        ShowView("Queues");
        return Task.CompletedTask;
    }

    // ═══════════════════════════════════════
    // ═══ Row context menu
    // ═══════════════════════════════════════

    private void DownloadList_RightTapped(object sender, Microsoft.UI.Xaml.Input.RightTappedRoutedEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source)
            return;

        var row = FindAncestor<ListViewItem>(source);
        if (row?.Content is not DownloadItem)
            return;

        // Right-clicking an already selected row preserves an extended selection;
        // right-clicking another row makes that row the new context-menu target.
        if (!row.IsSelected)
        {
            DownloadList.SelectedItems.Clear();
            row.IsSelected = true;
        }

        e.Handled = true;
        RowMenu.ShowAt(row, new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions
        {
            Position = e.GetPosition(row),
            ShowMode = Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowMode.Standard,
        });
    }

    /// <summary>
    /// The menu key and Shift+F10. The row menu is not the list's ContextFlyout, because
    /// that opened on empty space too, so the keyboard route is wired here by hand.
    /// </summary>
    private void DownloadList_ContextRequested(UIElement sender, Microsoft.UI.Xaml.Input.ContextRequestedEventArgs e)
    {
        e.Handled = true;
        if (Selection().Count == 0) return;

        var target = DownloadList.ContainerFromItem(Selection()[0]) as FrameworkElement ?? DownloadList;
        RowMenu.ShowAt(target);
    }

    /// <summary>
    /// List shortcuts. A key handler rather than KeyboardAccelerators, which pin a
    /// tooltip naming the key to the list on hover and double click.
    /// </summary>
    private void DownloadList_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        var ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

        switch (e.Key)
        {
            case Windows.System.VirtualKey.Delete:
                e.Handled = true;
                Delete_Click(this, new RoutedEventArgs());
                break;
            case Windows.System.VirtualKey.V when ctrl:
                e.Handled = true;
                NewDownload_Click(this, new RoutedEventArgs());
                break;
            case Windows.System.VirtualKey.Escape when DownloadList.SelectedItems.Count > 0:
                e.Handled = true;
                DownloadList.SelectedItems.Clear();
                break;
        }
    }

    private static T? FindAncestor<T>(DependencyObject? element) where T : DependencyObject
    {
        while (element is not null)
        {
            if (element is T match)
                return match;

            element = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(element);
        }

        return null;
    }

    /// <summary>
    /// Shows only the items that mean something for what is selected. A finished download
    /// has nothing to pause; one that has not started has nothing to open.
    /// </summary>
    private void DownloadMenu_Opening(object sender, object e)
    {
        var selection = Selection().ToList();

        var single = selection.Count == 1 ? selection[0] : null;
        var finished = selection.Count > 0 && selection.All(i => i.Status == DownloadStatus.Complete);

        Show(MenuOpenFile, finished && single is not null);
        Show(MenuOpenFolder, finished);
        Show(MenuPause, !finished && selection.Any(i => i.CanPause));
        Show(MenuResume, !finished && selection.Any(i => i.CanResume));

        Show(MenuSaveAs, single is not null && !finished && !single.IsRunning);
        Show(MenuRefreshLink, single?.CanRefresh == true);
        Show(MenuShowProgress, !finished && single is not null);
        Show(MenuMoveTo, !finished && selection.Count > 0);

        Show(MenuCopyLink, selection.Count > 0);
        Show(MenuCopyCurl, selection.Count > 0);
        Show(MenuCopyFile, finished && single is not null);

        // Converting only means anything for a file that is actually there.
        Show(MenuConvert, finished && single?.ResolvedPath is { } convertible && File.Exists(convertible));
        Show(MenuRestart, selection.Count > 0);
        Show(MenuRemove, selection.Count > 0);
        Show(MenuProperties, single is not null);

        // Separators only earn their space when something is showing on both sides.
        Show(MenuSeparatorOne, MenuOpenFile.Visibility == Visibility.Visible
                            || MenuPause.Visibility == Visibility.Visible
                            || MenuResume.Visibility == Visibility.Visible);
        Show(MenuSeparatorTwo, MenuSaveAs.Visibility == Visibility.Visible
                            || MenuRefreshLink.Visibility == Visibility.Visible
                            || MenuShowProgress.Visibility == Visibility.Visible
                            || MenuMoveTo.Visibility == Visibility.Visible);

        BuildMoveToMenu(selection);

        static void Show(FrameworkElement element, bool visible) =>
            element.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Rebuilt each time, because queues can be created and deleted meanwhile.</summary>
    private void BuildMoveToMenu(List<DownloadItem> selection)
    {
        MenuMoveTo.Items.Clear();

        foreach (var queue in _downloads.Queues.Queues)
        {
            var entry = new MenuFlyoutItem { Text = queue.Name };
            var target = queue;

            entry.Click += (_, _) =>
            {
                _downloads.Queues.MoveTo(target, selection);
                Refresh();
            };

            MenuMoveTo.Items.Add(entry);
        }

        MenuMoveTo.Items.Add(new MenuFlyoutSeparator());

        var none = new MenuFlyoutItem { Text = "No queue" };

        none.Click += (_, _) =>
        {
            _downloads.Queues.MoveTo(null, selection);
            Refresh();
        };

        MenuMoveTo.Items.Add(none);
    }

    /// <summary>Opens the details pane on this row rather than a window per download.</summary>
    private void Properties_Click(object sender, RoutedEventArgs e)
    {
        if (Selection().FirstOrDefault() is not { } item)
            return;

        DownloadList.SelectedItems.Clear();
        DownloadList.SelectedItems.Add(item);

        if (DetailsToggle.IsChecked != true)
        {
            DetailsToggle.IsChecked = true;
            DetailsToggle_Click(DetailsToggle, e);
        }
    }

    private async void Restart_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in Selection().ToList())
            await _downloads.RestartAsync(item);

        ShowActive();
    }

    /// <summary>
    /// Puts the finished file itself on the clipboard, so it can be pasted into Explorer or
    /// an upload box. StorageItems rather than a path string: pasting a path gives you text
    /// where a file was wanted.
    /// </summary>
    private async void CopyFile_Click(object sender, RoutedEventArgs e)
    {
        if (Selection().FirstOrDefault()?.ResolvedPath is not { } path || !File.Exists(path))
            return;

        try
        {
            var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);

            var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
            package.SetStorageItems([file]);
            Clipboard.SetContent(package);
        }
        catch (Exception exception)
        {
            App.Log($"Copy file failed: {exception.Message}");
        }
    }

    /// <summary>Chooses where an unfinished download should land instead.</summary>
    private async void SaveAs_Click(object sender, RoutedEventArgs e)
    {
        if (Selection().FirstOrDefault() is not { } item || item.IsRunning)
            return;

        try
        {
            var picker = new Windows.Storage.Pickers.FileSavePicker
            {
                SuggestedFileName = Path.GetFileNameWithoutExtension(item.Name),
            };

            var extension = Path.GetExtension(item.Name);
            picker.FileTypeChoices.Add(extension.Length > 1 ? extension.TrimStart('.').ToUpperInvariant() : "File",
                [extension.Length > 1 ? extension : "."]);

            // Unpackaged apps have no implicit window for the picker to parent itself to.
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));

            if (await picker.PickSaveFileAsync() is not { } chosen)
                return;

            item.TargetDirectory = Path.GetDirectoryName(chosen.Path) ?? item.TargetDirectory;
            item.PreferredFileName = Path.GetFileName(chosen.Path);

            // An explicit destination has to beat the category sorting, or the file would
            // be quietly filed somewhere else than the one just chosen.
            item.SortByType = false;

            Refresh();
        }
        catch (Exception exception)
        {
            App.Log($"Save as failed: {exception.Message}");
        }
    }

    private void OpenFile_Click(object sender, RoutedEventArgs e) =>
        OpenPath(Selection().FirstOrDefault()?.ResolvedPath);

    private void OpenContainingFolder_Click(object sender, RoutedEventArgs e) =>
        RevealPath(Selection().FirstOrDefault());

    /// <summary>Opens a finished file with whatever Windows associates with it.</summary>
    private static void OpenPath(string? path)
    {
        if (path is null || !File.Exists(path))
            return;

        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            App.Log($"Open file failed: {exception.Message}");
        }
    }

    /// <summary>Shows the file in Explorer, falling back to its folder when it is gone.</summary>
    private static void RevealPath(DownloadItem? item)
    {
        if (item is null) return;

        try
        {
            // /select, highlights the file rather than just opening the folder.
            if (item.ResolvedPath is { } path && File.Exists(path))
                Process.Start("explorer.exe", $"/select,\"{path}\"");
            else if (Directory.Exists(item.TargetDirectory))
                Process.Start(new ProcessStartInfo(item.TargetDirectory) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            App.Log($"Open folder failed: {exception.Message}");
        }
    }

    // ═══════════════════════════════════════
    // ═══ Import and export
    // ═══════════════════════════════════════

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_downloads.Items.Count == 0)
        {
            await ShowMessageAsync("There are no downloads to export yet.");
            return;
        }

        try
        {
            var picker = new Windows.Storage.Pickers.FileSavePicker
            {
                SuggestedFileName = $"stretto-downloads-{DateTime.Now:yyyy-MM-dd}",
            };

            picker.FileTypeChoices.Add("Stretto download list", [".json"]);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));

            if (await picker.PickSaveFileAsync() is not { } file)
                return;

            _downloads.Export(file.Path);

            ShowNotice("List exported",
                $"{_downloads.Items.Count} downloads written. Cookies and saved logins are not included.",
                "Show file", () => Process.Start("explorer.exe", $"/select,\"{file.Path}\""));
        }
        catch (Exception exception)
        {
            App.Log($"Export failed: {exception}");
            await ShowMessageAsync($"That list could not be written. {exception.Message}");
        }
    }

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            picker.FileTypeFilter.Add(".json");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));

            if (await picker.PickSingleFileAsync() is not { } file)
                return;

            var added = _downloads.Import(DownloadStore.ImportFrom(file.Path));
            Refresh();

            await ShowMessageAsync(added == 0
                ? "Nothing new to add: every download in that list is already here."
                : $"Added {added} download{(added == 1 ? "" : "s")}, paused. "
                  + "Select them and Resume when you want them to run.");
        }
        catch (Exception exception)
        {
            App.Log($"Import failed: {exception}");
            await ShowMessageAsync($"That file could not be read as a download list. {exception.Message}");
        }
    }

    /// <summary>The log, for anyone reporting a problem.</summary>
    private void OpenLog_Click(object sender, RoutedEventArgs e) => OpenPath(App.StartupLogPath);

    private void CopyLink_Click(object sender, RoutedEventArgs e)
    {
        var links = string.Join(Environment.NewLine, Selection().Select(i => i.Url));
        if (links.Length > 0)
            SetClipboard(links);
    }

    /// <summary>
    /// One curl command per selected download, reproducing its request context: same URL,
    /// Referer/User-Agent, login challenge behavior, and cookies. For taking an
    /// expired link apart somewhere else.
    /// </summary>
    private void CopyCurl_Click(object sender, RoutedEventArgs e)
    {
        var commands = Selection()
            .Where(item => Uri.TryCreate(item.Url, UriKind.Absolute, out _))
            .Select(item =>
            {
                // Same context the engine sends: plenty of hosts refuse a download
                // re-requested without the browser's Referer and User-Agent.
                var headers = new Dictionary<string, string>();
                if (!string.IsNullOrEmpty(item.Referrer)) headers["Referer"] = item.Referrer;
                headers["User-Agent"] = string.IsNullOrEmpty(item.UserAgent) ? _settings.UserAgent : item.UserAgent;

                var login = string.IsNullOrEmpty(item.Username)
                    ? SiteCredentials.For(item.Url)
                    : new SiteCredentials.Login(item.Username, item.Password ?? "");
                return CurlCommand.Build(new Uri(item.Url), headers, item.Cookie, login?.Username, login?.Password);
            });

        var text = string.Join(Environment.NewLine, commands);
        if (text.Length > 0)
            SetClipboard(text);
    }

    private static void SetClipboard(string text)
    {
        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);
    }
}
