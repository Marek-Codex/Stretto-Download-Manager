using Stretto.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

using Stretto.App.Services;

namespace Stretto.App.Dialogs;

/// <summary>
/// The "that's finished" announcement: open the file, show it in its folder, or close.
///
/// Built in code because it is a short, fixed form.
/// </summary>
public sealed class DownloadCompleteDialog : UserControl, IWindowForm, IWindowFormChrome
{
    public bool DontShowAgain => _dontShowAgain.IsChecked == true;
    public string PrimaryText => "Open";
    public string? SecondaryText => "Open folder";
    public string? CancelText => "Close";
    public bool AccentPrimary => true;
    public bool CanPrimary => true;
    public bool CanSecondary => true;
    private readonly CheckBox _dontShowAgain = new() { Content = "Don't show again", FontSize = 12.5, MinWidth = 0 };

    // The checkbox sits in the footer, opposite the buttons, rather than as a line of its
    // own inside a card inside the window.
    public Thickness BodyPadding => new(18, 10, 18, 16);
    public UIElement? FooterStart => _dontShowAgain;

    public DownloadCompleteDialog(DownloadItem item)
    {
        var accent = (Brush)Application.Current.Resources["StrettoAccentBrush"];

        // The category tile says where the file went without reading a path: the same
        // glyph the Library filter and the list row use, on the same soft accent surface as
        // the app's primary buttons.
        var tile = new Border
        {
            Width = 46,
            Height = 46,
            CornerRadius = new CornerRadius(10),
            Background = (Brush)Application.Current.Resources["StrettoAccentSoftBrush"],
            VerticalAlignment = VerticalAlignment.Top,
            Child = new FontIcon
            {
                Glyph = Visuals.CategoryGlyph(item),
                FontSize = 22,
                Foreground = accent,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };

        // Size and category on one line, the folder on its own, so a long path never splits
        // the summary mid-phrase.
        var details = item.TotalBytes is { } size
            ? $"{DownloadItem.FormatBytes(size)}  ·  {item.Category}"
            : item.Category.ToString();

        var info = new StackPanel { Spacing = 4 };
        info.Children.Add(new TextBlock
        {
            Text = "Ready to open",
            FontSize = 18,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        });
        info.Children.Add(new TextBlock
        {
            Text = item.Name,
            FontSize = 14,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        });
        info.Children.Add(new TextBlock
        {
            Text = details,
            FontSize = 12,
            Opacity = 0.65,
            TextWrapping = TextWrapping.Wrap,
        });
        var location = new TextBlock
        {
            Text = item.TargetDirectory,
            FontSize = 12,
            Opacity = 0.5,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        ToolTipService.SetToolTip(location, item.TargetDirectory);
        info.Children.Add(location);

        var row = new Grid { ColumnSpacing = 12, MinWidth = 460, MaxWidth = 580 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.Children.Add(tile);
        Grid.SetColumn(info, 1);
        row.Children.Add(info);

        this.Compact();
        Content = row;
    }
}
