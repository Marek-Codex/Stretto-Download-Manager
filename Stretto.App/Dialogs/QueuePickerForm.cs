using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Stretto.App.Models;

namespace Stretto.App.Dialogs;

/// <summary>
/// Picks downloads for a queue, and names the queue when it is a new one.
///
/// Its own window rather than a panel that slid over the queue editor: the overlay hid the
/// very queue it was adding to, and its OK and Cancel sat right above the editor's own Save
/// and Cancel, two button rows meaning different things on one screen.
/// </summary>
public sealed class QueuePickerForm : UserControl, IWindowForm
{
    private readonly TextBox? _name;
    private readonly ListView _list = new() { SelectionMode = ListViewSelectionMode.Multiple };

    public string PrimaryText { get; }
    public string? SecondaryText => null;
    public string? CancelText => "Cancel";
    public bool AccentPrimary => true;
    public bool CanPrimary => true;
    public bool CanSecondary => false;

    public string QueueName => _name?.Text.Trim() is { Length: > 0 } name ? name : "Queue";

    public IReadOnlyList<DownloadItem> Chosen => [.. _list.SelectedItems.OfType<DownloadItem>()];

    /// <param name="available">The downloads that can be put in the queue.</param>
    /// <param name="newQueueName">A suggested name when creating a queue; null when adding
    /// to an existing one, which hides the name field.</param>
    public QueuePickerForm(IReadOnlyList<DownloadItem> available, string? newQueueName)
    {
        PrimaryText = newQueueName is null ? "Add" : "Create";

        var panel = new Grid { Width = 420, Height = newQueueName is null ? 300 : 350, RowSpacing = 10 };
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        if (newQueueName is not null)
        {
            _name = new TextBox { Header = "Queue name", Text = newQueueName };
            _name.Loaded += (_, _) => { _name.Focus(FocusState.Programmatic); _name.SelectAll(); };
            panel.Children.Add(_name);
        }

        var selectAll = new CheckBox { Content = "Select all", MinWidth = 0 };
        selectAll.Click += (_, _) =>
        {
            if (selectAll.IsChecked == true) _list.SelectAll();
            else _list.SelectedItems.Clear();
        };

        var heading = new Grid();
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        heading.Children.Add(new TextBlock
        {
            Text = newQueueName is null ? "Downloads not in any queue" : "Start it with these downloads (optional)",
            FontSize = 12.5,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = WindowAppearance.Brush("TextFillColorSecondaryBrush"),
        });
        Grid.SetColumn(selectAll, 1);
        heading.Children.Add(selectAll);
        Grid.SetRow(heading, 1);
        panel.Children.Add(heading);

        ScrollViewer.SetHorizontalScrollMode(_list, ScrollMode.Disabled);
        _list.Style = (Style)Application.Current.Resources["StrettoDialogList"];
        _list.ItemTemplate = (DataTemplate)XamlReader.Load("""
            <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
              <StackPanel Spacing="1" Padding="0,3">
                <TextBlock Text="{Binding Name}" FontSize="12.5" TextTrimming="CharacterEllipsis" />
                <TextBlock Text="{Binding SizeText}" FontSize="10.5" Opacity="0.55" />
              </StackPanel>
            </DataTemplate>
            """);
        _list.ItemsSource = available;
        _list.SelectionChanged += (_, _) => selectAll.IsChecked = _list.SelectedItems.Count switch
        {
            0 => false,
            var n when n == available.Count => true,
            _ => null,
        };

        var listArea = new Grid();
        listArea.Children.Add(_list);
        if (available.Count == 0)
        {
            selectAll.Visibility = Visibility.Collapsed;
            listArea.Children.Add(new TextBlock
            {
                Text = "Every unfinished download is already in a queue.",
                FontSize = 12,
                Opacity = 0.55,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            });
        }

        Grid.SetRow(listArea, 2);
        panel.Children.Add(listArea);

        Content = panel;
    }
}
