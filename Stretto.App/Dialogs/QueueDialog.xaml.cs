using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Stretto.App.Models;
using Stretto.App.Services;
using System.Collections.ObjectModel;
using System.ComponentModel;
using Windows.System;
using Windows.UI.Core;

namespace Stretto.App.Dialogs;

public sealed partial class QueueDialog : UserControl, IWindowForm
{
    private readonly DownloadService _downloads;
    private readonly ObservableCollection<DownloadItem> _files = [];

    private readonly bool _ready;
    private bool _loading;
    private DownloadQueue? _listened;

    // One Close button, no Cancel. Membership, order and start/stop all take effect the
    // moment they are made, so a Cancel that could not undo any of it was a false promise.
    public string PrimaryText => "Close";
    public string? SecondaryText => null;
    public string? CancelText => null;
    public bool AccentPrimary => false;
    public bool CanPrimary => true;
    public bool CanSecondary => false;

    public QueueDialog(DownloadService downloads)
    {
        InitializeComponent();
        _downloads = downloads;

        QueueList.ItemsSource = _downloads.Queues.Queues;

        _ready = true;

        if (_downloads.Queues.Queues.Count > 0)
            QueueList.SelectedIndex = 0;

        BuildFileTable();
        UpdateChrome();
    }

    private DownloadQueue? Current => QueueList.SelectedItem as DownloadQueue;

    // ═══════════════════════════════════════
    // ═══ Queue selection
    // ═══════════════════════════════════════

    private void Queue_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        LoadQueue();
        UpdateChrome();
    }

    private void Queue_StateChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Raised on the UI tick, so this is already on the UI thread.
        if (e.PropertyName == nameof(DownloadQueue.IsRunning))
            UpdateChrome();
    }

    private void LoadQueue()
    {
        if (_listened is not null)
            _listened.PropertyChanged -= Queue_StateChanged;

        _files.Clear();

        if (Current is not { } queue)
        {
            _listened = null;
            BuildFileTable();
            return;
        }

        // The scheduler can start or stop this queue while the dialog is open;
        // follow it so Start/Pause do not go stale behind the list text.
        _listened = queue;
        queue.PropertyChanged += Queue_StateChanged;

        DetailTitle.Text = queue.Name;
        _loading = true;

        foreach (var item in _downloads.ItemsIn(queue))
            _files.Add(item);

        SchedulerEnabled.IsOn = queue.SchedulerEnabled;
        SetScheduleEnabled(queue.SchedulerEnabled);

        StartTime.SelectedTime = queue.StartAt;
        StopTime.SelectedTime = queue.StopAt;

        SunCheck.IsChecked = queue.Days.Contains(DayOfWeek.Sunday);
        MonCheck.IsChecked = queue.Days.Contains(DayOfWeek.Monday);
        TueCheck.IsChecked = queue.Days.Contains(DayOfWeek.Tuesday);
        WedCheck.IsChecked = queue.Days.Contains(DayOfWeek.Wednesday);
        ThuCheck.IsChecked = queue.Days.Contains(DayOfWeek.Thursday);
        FriCheck.IsChecked = queue.Days.Contains(DayOfWeek.Friday);
        SatCheck.IsChecked = queue.Days.Contains(DayOfWeek.Saturday);

        DailyCheck.IsChecked = queue.Days.Count == 7;
        _loading = false;
        UpdateSummary();
        BuildFileTable();
    }

    private void UpdateChrome()
    {
        var has = Current is not null;

        var empty = _downloads.Queues.Queues.Count == 0;

        DetailPanel.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        NoQueues.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        QueueListEmpty.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        DeleteQueueButton.IsEnabled = has;

        if (Current is { } queue)
        {
            StartQueueButton.IsEnabled = !queue.IsRunning;
            PauseQueueButton.IsEnabled = queue.IsRunning;
            DetailState.Text = queue.StateText;
        }
    }

    private void UpdateSummary()
    {
        if (Current is not { } queue)
            return;

        var days = queue.Days.Count == 7
            ? "every day"
            : queue.Days.Count == 0
                ? "no days selected, so it will never run"
                : string.Join(", ", queue.Days.OrderBy(d => d).Select(d => d.ToString()[..3]));

        // Spelling out the midnight-crossing case matters: "23:00 to 06:00" is not obviously
        // a window that runs overnight rather than not at all.
        var wrap = queue.StopAt <= queue.StartAt ? ", carrying over past midnight" : "";

        ScheduleSummary.Text = $"Runs {days} from {queue.StartAt:hh\\:mm} to {queue.StopAt:hh\\:mm}{wrap}.";
    }

    // ═══════════════════════════════════════
    // ═══ Queue actions
    // ═══════════════════════════════════════

    private async void NewQueue_Click(object sender, RoutedEventArgs e)
    {
        var suggested = _downloads.Queues.Queues.Count == 0
            ? "Default queue"
            : $"Queue {_downloads.Queues.Queues.Count + 1}";

        var picker = new QueuePickerForm(Available(), suggested);
        if (await new UtilityWindow(picker, picker, "New queue", 420, 350).ShowAsync() != ContentDialogResult.Primary)
            return;

        var queue = _downloads.Queues.Create(picker.QueueName, picker.Chosen);
        QueueList.SelectedItem = queue;
        LoadQueue();
        UpdateChrome();
    }

    /// <summary>
    /// Only downloads that are not already spoken for, so an item cannot end up in two
    /// queues disagreeing about when it should run.
    /// </summary>
    private List<DownloadItem> Available() =>
        [.. _downloads.Items.Where(i => _downloads.Queues.QueueFor(i) is null && i.Status != DownloadStatus.Complete)];

    private void Tabs_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        var files = Tabs.SelectedItem == FilesTabItem;
        FilesPanel.Visibility = files ? Visibility.Visible : Visibility.Collapsed;
        SchedulePanel.Visibility = files ? Visibility.Collapsed : Visibility.Visible;
    }

    private void DeleteQueue_Click(object sender, RoutedEventArgs e)
    {
        if (Current is not { } queue) return;

        // Stop it first: removing the queue must not leave its downloads running under a
        // queue that no longer exists.
        _downloads.PauseQueue(queue);
        _downloads.Queues.Remove(queue);

        QueueList.SelectedIndex = _downloads.Queues.Queues.Count > 0 ? 0 : -1;
        LoadQueue();
        UpdateChrome();
    }

    private void StartQueue_Click(object sender, RoutedEventArgs e)
    {
        if (Current is not { } queue) return;
        _downloads.StartQueue(queue);
        LoadQueue();
        UpdateChrome();
    }

    private void PauseQueue_Click(object sender, RoutedEventArgs e)
    {
        if (Current is not { } queue) return;
        _downloads.PauseQueue(queue);
        LoadQueue();
        UpdateChrome();
    }

    // ═══════════════════════════════════════
    // ═══ Files in queue
    // ═══════════════════════════════════════

    // ═══════════════════════════════════════
    // ═══ Files in queue
    // ═══════════════════════════════════════
    //
    // One grid for headers and rows so the columns line up. Selection is a small
    // multi-select of our own (tap, Ctrl-tap, Shift-tap) now that the rows are not
    // ListViewItems anymore.

    private readonly Dictionary<DownloadItem, Border[]> _fileCells = [];
    private readonly HashSet<DownloadItem> _selectedFiles = [];
    private DownloadItem? _fileAnchor;

    private void BuildFileTable()
    {
        foreach (var item in _fileCells.Keys)
            item.PropertyChanged -= FileItemChanged;
        _fileCells.Clear();
        FileGrid.Children.Clear();
        FileGrid.RowDefinitions.Clear();
        _selectedFiles.Clear();
        _fileAnchor = null;

        AddFileTableHeader();

        foreach (var item in _files)
        {
            var row = FileGrid.RowDefinitions.Count;
            var firstDataRow = row == 1;
            FileGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(28) });

            var cells = new Border[3];
            var texts = new[] { item.Name, item.SizeText, item.StatusText };

            for (var column = 0; column < 3; column++)
            {
                var cell = new Border
                {
                    BorderThickness = firstDataRow ? new Thickness(0) : new Thickness(0, 1, 0, 0),
                    BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
                };
                var text = new TextBlock
                {
                    Text = texts[column],
                    FontSize = column == 0 ? 12.5 : 12,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                };
                if (column == 2)
                    text.Foreground = item.StatusBrush;
                else if (column == 1)
                    text.Opacity = 0.7;
                ToolTipService.SetToolTip(text, texts[column]);
                cell.Child = text;
                Grid.SetRow(cell, row);
                Grid.SetColumn(cell, column);
                FileGrid.Children.Add(cell);
                cells[column] = cell;

                var captured = item;
                cell.Tapped += (_, _) => FileRowTapped(captured);
            }

            _fileCells[item] = cells;
            item.PropertyChanged += FileItemChanged;
        }

        FileListEmpty.Visibility = _fileCells.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateFileSelection();
        UpdateFileButtons();
    }

    private void AddFileTableHeader()
    {
        FileGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var titles = new[] { "File", "Size", "Status" };
        for (var column = 0; column < titles.Length; column++)
        {
            var cell = new Border
            {
                BorderThickness = new Thickness(0, 0, 0, 1),
                BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
                Padding = new Thickness(0, 0, 0, 4),
            };
            cell.Child = new TextBlock
            {
                Text = titles[column],
                FontSize = 11.5,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"],
            };
            Grid.SetRow(cell, 0);
            Grid.SetColumn(cell, column);
            FileGrid.Children.Add(cell);
        }
    }

    private void FileItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is DownloadItem item && _fileCells.TryGetValue(item, out var cells))
        {
            var texts = new[] { item.Name, item.SizeText, item.StatusText };
            for (var i = 0; i < cells.Length; i++)
            {
                if (cells[i].Child is TextBlock text)
                {
                    text.Text = texts[i];
                    ToolTipService.SetToolTip(text, texts[i]);
                }
            }
            if (cells[2].Child is TextBlock status)
                status.Foreground = item.StatusBrush;
        }
    }

    private void FileRowTapped(DownloadItem item)
    {
        var ctrl = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)
            .HasFlag(CoreVirtualKeyStates.Down);
        var shift = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift)
            .HasFlag(CoreVirtualKeyStates.Down);

        if (shift && _fileAnchor is not null && _files.Contains(_fileAnchor) && _files.Contains(item))
        {
            var from = Math.Min(_files.IndexOf(_fileAnchor), _files.IndexOf(item));
            var to = Math.Max(_files.IndexOf(_fileAnchor), _files.IndexOf(item));
            _selectedFiles.Clear();
            for (var i = from; i <= to; i++)
                _selectedFiles.Add(_files[i]);
        }
        else if (ctrl)
        {
            if (!_selectedFiles.Add(item))
                _selectedFiles.Remove(item);
            _fileAnchor = item;
        }
        else
        {
            _selectedFiles.Clear();
            _selectedFiles.Add(item);
            _fileAnchor = item;
        }

        UpdateFileSelection();
        UpdateFileButtons();
    }

    private void UpdateFileSelection()
    {
        var highlight = (Brush)Application.Current.Resources["StrettoAccentSoftBrush"];
        foreach (var (item, cells) in _fileCells)
        {
            var selected = _selectedFiles.Contains(item);
            foreach (var cell in cells)
                cell.Background = selected ? highlight : null;
        }
    }

    private void UpdateFileButtons()
    {
        RemoveFileButton.IsEnabled = _selectedFiles.Count > 0;
        var single = _selectedFiles.Count == 1 ? _selectedFiles.First() : null;
        MoveUpButton.IsEnabled = single is not null && _files.IndexOf(single) > 0;
        MoveDownButton.IsEnabled = single is not null && _files.IndexOf(single) < _files.Count - 1;
    }

    private async void AddFiles_Click(object sender, RoutedEventArgs e)
    {
        if (Current is not { } queue) return;

        var picker = new QueuePickerForm(Available(), null);
        if (await new UtilityWindow(picker, picker, $"Add to {queue.Name}", 420, 300).ShowAsync() != ContentDialogResult.Primary)
            return;

        foreach (var item in picker.Chosen.Where(i => !queue.ItemIds.Contains(i.Id)))
            queue.ItemIds.Add(item.Id);

        // A running queue only releases what was Pending when it started, so just-added
        // files would otherwise sit until the next start.
        if (queue.IsRunning)
            _downloads.StartQueue(queue);

        LoadQueue();
        UpdateChrome();
    }

    private void RemoveFile_Click(object sender, RoutedEventArgs e)
    {
        if (Current is not { } queue) return;

        foreach (var item in _selectedFiles.ToList())
        {
            queue.ItemIds.Remove(item.Id);
            _files.Remove(item);
        }

        BuildFileTable();
    }

    private void MoveUp_Click(object sender, RoutedEventArgs e) => Move(-1);

    private void MoveDown_Click(object sender, RoutedEventArgs e) => Move(+1);

    private void Move(int delta)
    {
        if (Current is not { } queue) return;
        if (_selectedFiles.Count != 1) return;
        var item = _selectedFiles.First();

        var from = _files.IndexOf(item);
        var to = from + delta;
        if (to < 0 || to >= _files.Count) return;

        _files.Move(from, to);

        // The queue's own order is the one that matters; the list is just its view.
        queue.ItemIds.Clear();
        queue.ItemIds.AddRange(_files.Select(i => i.Id));

        BuildFileTable();
        _selectedFiles.Add(item);
        _fileAnchor = item;
        UpdateFileSelection();
        UpdateFileButtons();
    }

    // ═══════════════════════════════════════
    // ═══ Scheduler
    // ═══════════════════════════════════════

    private void Scheduler_Toggled(object sender, RoutedEventArgs e)
    {
        if (Current is not { } queue) return;

        if (_loading) return;
        queue.SchedulerEnabled = SchedulerEnabled.IsOn;
        SetScheduleEnabled(queue.SchedulerEnabled);
        UpdateSummary();
    }

    /// <summary>Greys out the timetable when the scheduler is off.
    /// StackPanel has no IsEnabled of its own in WinUI (it is a Control
    /// property, and putting it on a Panel kills XamlCompiler with no
    /// diagnostic), so each child control is toggled instead.</summary>
    private void SetScheduleEnabled(bool enabled)
    {
        StartTime.IsEnabled = enabled;
        StopTime.IsEnabled = enabled;
        DailyCheck.IsEnabled = enabled;

        foreach (var box in DayChecks())
            box.IsEnabled = enabled;
    }

    private void Daily_Click(object sender, RoutedEventArgs e)
    {
        var all = DailyCheck.IsChecked == true;

        foreach (var box in DayChecks())
            box.IsChecked = all;

        CollectDays();
    }

    private void Day_Click(object sender, RoutedEventArgs e)
    {
        DailyCheck.IsChecked = DayChecks().All(b => b.IsChecked == true);
        CollectDays();
    }

    private ToggleButton[] DayChecks() =>
        [SunCheck, MonCheck, TueCheck, WedCheck, ThuCheck, FriCheck, SatCheck];

    private void CollectDays()
    {
        if (Current is not { } queue) return;

        queue.Days = [.. DayChecks()
            .Select((box, index) => (box, day: (DayOfWeek)index))
            .Where(x => x.box.IsChecked == true)
            .Select(x => x.day)];

        UpdateSummary();
    }

    /// <summary>Pulls the time pickers back onto the queue; they have no per-change event
    /// worth wiring when Save is the commit point anyway.</summary>
    public void Commit()
    {
        if (Current is not { } queue) return;

        if (StartTime.SelectedTime is { } start) queue.StartAt = start;
        if (StopTime.SelectedTime is { } stop) queue.StopAt = stop;

        _downloads.Queues.Save();
    }
}
