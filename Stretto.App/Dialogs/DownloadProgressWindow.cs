using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Stretto.App.Models;
using Stretto.App.Services;
using System.ComponentModel;
using Windows.Graphics;

namespace Stretto.App.Dialogs;

/// <summary>
/// A small window that follows one download, for anyone who likes to watch a big one
/// arrive. Off by default: every row in the list already shows speed and time left. Built
/// in code rather than XAML to match <see cref="BrowserSetupWindow"/>, and because it is
/// one fixed layout that never branches.
///
/// It watches a single <see cref="DownloadItem"/> and mirrors it. The item stays the source
/// of truth, so closing this window has no effect on the transfer.
/// </summary>
public sealed class DownloadProgressWindow : Window
{
    private readonly DownloadItem _item;
    private readonly DownloadService _downloads;

    private readonly TextBlock _name = Text("", 14, 1);
    private readonly TextBlock _url = Text("", 11.5, .55);
    private readonly TextBlock _percent = Text("", 34, 1);
    private readonly TextBlock _state = Text("", 12.5, .8);
    private readonly TextBlock _speed = Text("", 15, 1);
    private readonly TextBlock _timeLeft = Text("", 12.5, .7);
    private readonly TextBlock _location = Text("", 11.5, .6);
    private readonly ProgressBar _bar = new() { MinHeight = 4, Minimum = 0, Maximum = 100 };
    private readonly Button _pause = new() { Content = "Pause", MinWidth = 84 };
    private readonly FontIcon _glyph = new() { FontSize = 18 };
    private readonly TitleBar _titleBar;
    private Border _footer = null!;

    public DownloadProgressWindow(AppWindow owner, DownloadItem item, DownloadService downloads)
    {
        _item = item;
        _downloads = downloads;

        _titleBar = WindowAppearance.ConfigureUtilityWindow(this, item.Name, dialog: false);
        var width = Math.Clamp((int)Math.Ceiling(_item.Name.Length * 7.5 + 200), 460, 700);
        var height = 300;
        AppWindow.Resize(new SizeInt32(width, height));
        AppWindow.Move(new PointInt32(owner.Position.X + Math.Max(0, (owner.Size.Width - width) / 2), owner.Position.Y + Math.Max(0, (owner.Size.Height - height) / 2)));

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = true;
            presenter.IsMaximizable = false;
        }

        var shell = new Grid { Background = (Brush)Application.Current.Resources["StrettoWindowBrush"] };
        shell.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        shell.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var body = BuildLayout();
        Grid.SetRow(body, 1);
        shell.Children.Add(_titleBar);
        shell.Children.Add(body);

        Content = shell.Compact();

        // Mirror the item while the window is open, and let go cleanly when it closes.
        _item.PropertyChanged += OnItemChanged;
        Closed += (_, _) => _item.PropertyChanged -= OnItemChanged;

        Update();
    }

    /// <summary>
    /// The number first, because it is what anyone opening this window came to see; the
    /// rate and time left beside it; where it came from and where it is going underneath,
    /// quieter.
    /// </summary>
    private FrameworkElement BuildLayout()
    {
        var accent = (Brush)Application.Current.Resources["StrettoAccentBrush"];
        _speed.Foreground = accent;
        _glyph.Foreground = accent;
        _name.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        _percent.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        _speed.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        _name.TextTrimming = TextTrimming.CharacterEllipsis;
        _name.TextWrapping = TextWrapping.NoWrap;
        _url.TextTrimming = TextTrimming.CharacterEllipsis;
        _url.TextWrapping = TextWrapping.NoWrap;
        _location.TextTrimming = TextTrimming.CharacterEllipsis;
        _location.TextWrapping = TextWrapping.NoWrap;

        // The same category tile as the list row, so the window reads as that row enlarged.
        var tile = new Border
        {
            Width = 36,
            Height = 36,
            CornerRadius = new CornerRadius(8),
            Background = (Brush)Application.Current.Resources["StrettoAccentSoftBrush"],
            Child = _glyph,
        };

        var titles = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(_name);
        titles.Children.Add(_url);

        var heading = new Grid { ColumnSpacing = 12 };
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        heading.Children.Add(tile);
        Grid.SetColumn(titles, 1);
        heading.Children.Add(titles);

        var left = new StackPanel { Spacing = 0 };
        left.Children.Add(_percent);
        left.Children.Add(_state);

        var right = new StackPanel { Spacing = 2, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom };
        _speed.HorizontalAlignment = HorizontalAlignment.Right;
        _timeLeft.HorizontalAlignment = HorizontalAlignment.Right;
        right.Children.Add(_speed);
        right.Children.Add(_timeLeft);

        var figures = new Grid();
        figures.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        figures.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        figures.Children.Add(left);
        Grid.SetColumn(right, 1);
        figures.Children.Add(right);

        var close = new Button { Content = "Close", MinWidth = 84 };
        close.Click += (_, _) => Close();
        ToolTipService.SetToolTip(close, "The download carries on without this window");

        var folder = new Button { Content = "Show in folder", MinWidth = 84 };
        folder.Click += (_, _) => Reveal();

        // One button that follows the transfer: Pause while it runs, Resume once paused,
        // Open once finished.
        _pause.Click += (_, _) =>
        {
            if (_item.Status == DownloadStatus.Complete) Open();
            else if (_item.CanPause) _downloads.Pause(_item);
            else if (_item.CanResume) _downloads.Resume(_item);
        };

        _footer = WindowAppearance.Footer([_pause, close], [folder]);

        var content = new StackPanel { Spacing = 12, Margin = new Thickness(20, 8, 20, 16) };
        content.Children.Add(heading);
        content.Children.Add(figures);
        content.Children.Add(_bar);
        content.Children.Add(_location);

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(content);
        Grid.SetRow(_footer, 1);
        root.Children.Add(_footer);

        return root;
    }

    private void OnItemChanged(object? sender, PropertyChangedEventArgs e)
        => DispatcherQueue.TryEnqueue(Update);

    private void Update()
    {
        _name.Text = _item.Name;
        _url.Text = _item.Url;
        _glyph.Glyph = _item.CategoryGlyph;

        var percent = _item.Progress;
        var known = _item.TotalBytes is > 0 || _item.ProgressOverride is not null;
        _bar.Value = percent;
        _bar.IsIndeterminate = !known && _item.Status == DownloadStatus.Downloading;
        _bar.Foreground = _item.StatusBrush;

        // An unknown total is real for streams, so show what is known rather than a fake total.
        _percent.Text = known ? $"{percent:F0}%" : _item.BytesCompleted > 0 ? DownloadItem.FormatBytes(_item.BytesCompleted) : "0%";
        _state.Text = _item.TotalBytes is > 0
            ? $"{_item.StatusLabel}  ·  {DownloadItem.FormatBytes(_item.BytesCompleted)} of {DownloadItem.FormatBytes(_item.TotalBytes.Value)}"
            : _item.StatusLabel;

        _speed.Text = _item.Status == DownloadStatus.Downloading && _item.BytesPerSecond > 1
            ? $"{DownloadItem.FormatBytes((long)_item.BytesPerSecond)}/s"
            : "";
        _timeLeft.Text = _item.TimeLeft is { } left ? $"{DownloadItem.FormatDuration(left)} left" : "";

        // The limit is only worth a line when there is one.
        var limited = _downloads.SpeedLimitLabel.StartsWith("Limit", StringComparison.Ordinal);
        _location.Text = limited
            ? $"Saving to {_item.TargetDirectory}  ·  {_downloads.SpeedLimitLabel}"
            : $"Saving to {_item.TargetDirectory}";

        var done = _item.Status == DownloadStatus.Complete;
        _pause.Content = done ? "Open" : _item.CanResume ? "Resume" : "Pause";
        _pause.IsEnabled = done || _item.CanPause || _item.CanResume;
        _pause.Style = done ? (Style)Application.Current.Resources["AccentButtonStyle"] : null;

        // Window.Title drives the taskbar entry; the TitleBar control draws what is on
        // screen. Both have to be set or the visible caption never updates.
        var caption = known ? $"{percent:F0}%  {_item.Name}" : _item.Name;

        Title = caption;
        _titleBar.Title = caption;
    }

    private void Open()
    {
        if (_item.ResolvedPath is not { } path) return;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception exception) { App.Log($"Could not open {path}: {exception.Message}"); }
    }

    /// <summary>Opens Explorer with the file selected, or just the folder until it exists.</summary>
    private void Reveal()
    {
        try
        {
            var start = _item.ResolvedPath is { } path && File.Exists(path)
                ? new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{path}\"")
                : new System.Diagnostics.ProcessStartInfo(_item.TargetDirectory) { UseShellExecute = true };
            System.Diagnostics.Process.Start(start);
        }
        catch (Exception exception)
        {
            App.Log($"Could not open the folder: {exception.Message}");
        }
    }

    private static TextBlock Text(string value, double size, double opacity) => new()
    {
        Text = value,
        FontSize = size,
        Opacity = opacity,
        TextWrapping = TextWrapping.Wrap,
    };
}
