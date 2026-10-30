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

        if (item.ResolvedPath is { Length: > 0 } path && File.Exists(path))
            info.Children.Add(BuildHash(path));

        var row = new Grid { ColumnSpacing = 12, MinWidth = 460, MaxWidth = 580 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.Children.Add(tile);
        Grid.SetColumn(info, 1);
        row.Children.Add(info);

        this.Compact();
        Content = row;
    }

    /// <summary>
    /// The file's SHA-256 on one small line, for comparing with the one a download page lists.
    /// Worked out after the window is up, so a large file never holds the announcement back,
    /// and dropped if the window closes first: nobody waits on a hash they walked away from.
    /// </summary>
    private UIElement BuildHash(string path)
    {
        var text = new TextBlock
        {
            Text = "SHA-256  working it out…",
            FontFamily = new FontFamily("Consolas"),
            FontSize = 11,
            Opacity = 0.5,
            TextTrimming = TextTrimming.CharacterEllipsis,
            IsTextSelectionEnabled = true,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var copy = new Button
        {
            Content = new FontIcon { Glyph = "", FontSize = 11 },
            Padding = new Thickness(5, 2, 5, 2),
            MinWidth = 0,
            MinHeight = 0,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            Visibility = Visibility.Collapsed,
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTipService.SetToolTip(copy, "Copy SHA-256");

        var line = new Grid { ColumnSpacing = 2 };
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        line.Children.Add(text);
        Grid.SetColumn(copy, 1);
        line.Children.Add(copy);

        string? digest = null;
        copy.Click += (_, _) =>
        {
            if (digest is null) return;
            var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
            package.SetText(digest);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
            ToolTipService.SetToolTip(copy, "Copied");
        };

        var cts = new CancellationTokenSource();
        Unloaded += (_, _) => cts.Cancel();
        var started = false;
        Loaded += async (_, _) =>
        {
            if (started) return;
            started = true;
            try
            {
                digest = await Task.Run(() => Checksum.ComputeAsync(path, Checksum.Kind.Sha256, ct: cts.Token));
                text.Text = $"SHA-256  {digest}";
                ToolTipService.SetToolTip(text, digest);
                copy.Visibility = Visibility.Visible;
            }
            catch (OperationCanceledException) { }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Moved, opened elsewhere or scanned by antivirus: not worth more than a shrug.
                text.Text = "SHA-256  not available";
                App.Log($"Could not hash {path}: {exception.Message}");
            }
        };

        return line;
    }
}
