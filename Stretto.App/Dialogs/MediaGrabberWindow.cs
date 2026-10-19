using Stretto.Core;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Stretto.App.Services;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Windows.Graphics;

namespace Stretto.App.Dialogs;

/// <summary>
/// The media the extension has seen, in a window of its own.
///
/// Its own window rather than a tab beside the address finder, because the two are used at
/// different moments: this one sits open while you browse and fills up as videos play, and
/// that is also why it can be kept above other windows. Sharing a dialog meant guessing
/// which half you wanted and getting it wrong.
/// </summary>
public sealed class MediaGrabberWindow : Window
{
    private readonly MediaFeed _feed;
    private readonly DownloadService _downloads;
    private readonly AppSettings _settings;
    private readonly HttpClient _http;

    private readonly ObservableCollection<MediaRow> _rows = [];
    private readonly ListView _list = new()
    {
        SelectionMode = ListViewSelectionMode.Multiple,
        Margin = new Thickness(0, 8, 0, 8),
    };

    private readonly TextBlock _empty = Dim("Nothing detected yet. Play a video in your browser and it will appear here.");
    private readonly Button _download = new() { Content = "Download", MinWidth = 88 };

    public MediaGrabberWindow(MediaFeed feed, DownloadService downloads, AppSettings settings, HttpClient http)
    {
        _feed = feed;
        _downloads = downloads;
        _settings = settings;
        _http = http;

        var titleBar = WindowAppearance.ConfigureUtilityWindow(this, "Media grabber", "Detected while you browse", dialog: false);
        AppWindow.Resize(new SizeInt32(640, 440));
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        AppWindow.Move(new PointInt32(
            area.X + Math.Max(0, (area.Width - 640) / 2),
            area.Y + Math.Max(0, (area.Height - 440) / 2)));
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = true;
            presenter.IsMaximizable = true;
        }

        Content = BuildShell(titleBar).Compact();

        Sync();
        _feed.Items.CollectionChanged += Feed_Changed;
        Closed += (_, _) => _feed.Items.CollectionChanged -= Feed_Changed;
    }

    private FrameworkElement BuildShell(TitleBar titleBar)
    {
        var onTop = new CheckBox { Content = "Keep above browser", IsChecked = true };
        onTop.Checked += (_, _) => SetAlwaysOnTop(true);
        onTop.Unchecked += (_, _) => SetAlwaysOnTop(false);
        SetAlwaysOnTop(true);

        _list.ItemTemplate = BuildRowTemplate();
        _list.ItemsSource = _rows;
        _list.SelectionChanged += async (_, _) => await SelectionChangedAsync();

        _download.Click += Download_Click;
        _download.IsEnabled = false;

        var clear = new Button { Content = "Clear list" };
        clear.Click += (_, _) => { _feed.Clear(); Sync(); };

        var close = new Button { Content = "Close" };
        close.Click += (_, _) => Close();

        // The shared footer, like every other window: the action first, the way out last,
        // and the "keep above" switch at the far end where Complete's checkbox sits.
        _download.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
        var footer = WindowAppearance.Footer([_download, clear, close], [onTop]);

        _empty.HorizontalAlignment = HorizontalAlignment.Center;
        _empty.TextAlignment = TextAlignment.Center;
        _empty.MaxWidth = 380;

        var listArea = new Grid();
        _list.Style = (Style)Application.Current.Resources["StrettoDialogList"];
        listArea.Children.Add(_list);
        listArea.Children.Add(_empty);

        var body = new Grid { Padding = new Thickness(16, 2, 16, 12), RowSpacing = 6 };
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var header = new Grid { Margin = new Thickness(14, 0, 14, 0) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var name = Dim("Name");
        var quality = Dim("Quality");
        Grid.SetColumn(quality, 1);
        header.Children.Add(name);
        header.Children.Add(quality);

        Grid.SetRow(listArea, 1);
        body.Children.Add(header);
        body.Children.Add(listArea);

        var shell = new Grid { Background = (Brush)Application.Current.Resources["StrettoWindowBrush"] };
        shell.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        shell.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        shell.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        Grid.SetRow(body, 1);
        Grid.SetRow(footer, 2);
        shell.Children.Add(titleBar);
        shell.Children.Add(body);
        shell.Children.Add(footer);

        return shell;
    }

    /// <summary>
    /// Name on the left, a quality picker on the right.
    ///
    /// The only markup in this window: a DataTemplate has no code-first form, so it has to
    /// be parsed from a string. Kept to the shape of the row and nothing else.
    /// </summary>
    private static DataTemplate BuildRowTemplate()
    {
        const string markup = """
            <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
              <Grid Padding="0,4">
              <Grid.ColumnDefinitions>
                <ColumnDefinition Width="*" />
                <ColumnDefinition Width="Auto" />
              </Grid.ColumnDefinitions>
                <StackPanel Spacing="1">
                  <TextBlock Text="{Binding Name}" FontSize="13" FontWeight="SemiBold"
                             TextTrimming="CharacterEllipsis" />
                  <TextBlock Text="{Binding Address}" FontSize="11" Opacity="0.55"
                             TextTrimming="CharacterEllipsis" />
                </StackPanel>
                <ComboBox Grid.Column="1" VerticalAlignment="Center" HorizontalAlignment="Stretch" MinWidth="150"
                          ItemsSource="{Binding Formats}" SelectedItem="{Binding Chosen, Mode=TwoWay}"
                          PlaceholderText="Auto" DisplayMemberPath="Label"
                          Visibility="{Binding FormatsVisibility}" />
              </Grid>
            </DataTemplate>
            """;

        return (DataTemplate)XamlReader.Load(markup);
    }

    private void SetAlwaysOnTop(bool onTop)
    {
        if (AppWindow.Presenter is OverlappedPresenter presenter)
            presenter.IsAlwaysOnTop = onTop;
    }

    private void Feed_Changed(object? sender, NotifyCollectionChangedEventArgs e) =>
        DispatcherQueue.TryEnqueue(Sync);

    /// <summary>Adds what is new, keeping the rows already there and their chosen quality.</summary>
    private void Sync()
    {
        var known = _rows.Select(r => r.Address).ToHashSet();

        foreach (var item in _feed.Items.Where(i => !known.Contains(i.Url)))
            _rows.Insert(0, new MediaRow(item.ToCandidate()));

        foreach (var gone in _rows.Where(r => _feed.Items.All(i => i.Url != r.Address)).ToList())
            _rows.Remove(gone);

        _empty.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _download.IsEnabled = _list.SelectedItems.Count > 0;
    }

    /// <summary>
    /// Reads the qualities for the row just selected. On selection rather than up front,
    /// so a list filling up while you browse never sets off a burst of lookups.
    /// </summary>
    private async Task SelectionChangedAsync()
    {
        _download.IsEnabled = _list.SelectedItems.Count > 0;

        if (_list.SelectedItems.Count > 0 && _list.SelectedItems[^1] is MediaRow row)
            await row.LoadFormatsAsync(_http);
    }

    private void Download_Click(object sender, RoutedEventArgs e)
    {
        var chosen = _rows.Where(r => _list.SelectedItems.Contains(r)).Select(r => r.Effective).ToList();
        if (chosen.Count == 0)
            return;

        foreach (var candidate in chosen)
        {
            _downloads.Add(candidate.Url.ToString(), MediaDownloadOptions.FromCandidate(
                candidate, _settings));
        }

        // Left open on purpose: the usual next thing is to grab another one.
        _list.SelectedItems.Clear();
    }

    private static TextBlock Dim(string text) => new()
    {
        Text = text,
        FontSize = 12,
        Opacity = 0.6,
        TextWrapping = TextWrapping.Wrap,
        HorizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Center,
    };
}
