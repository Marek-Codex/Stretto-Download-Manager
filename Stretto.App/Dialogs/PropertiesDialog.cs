using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Stretto.App.Models;
using Stretto.Core;
using System.Text;

namespace Stretto.App.Dialogs;

/// <summary>
/// Everything Stretto knows about one download. Lives in the main window's details pane;
/// the window form is kept for the debug capture path.
///
/// A two-column sheet of selectable text rather than a column of read-only text boxes: the
/// point of the window is that the address and headers can be copied when something needs
/// debugging, and selectable text does that without nine input borders stacked on top of
/// one another. Empty fields are left out rather than shown blank.
/// </summary>
public sealed class PropertiesDialog : UserControl, IWindowForm, IWindowFormChrome
{
    public string PrimaryText => "Close";
    public string? SecondaryText => null;
    public string? CancelText => null;
    public bool AccentPrimary => false;
    public bool CanPrimary => true;
    public bool CanSecondary => false;
    public Thickness BodyPadding => new(16, 8, 16, 14);
    public UIElement? FooterStart { get; }

    /// <param name="embedded">For the details pane: fills its width, stacks each label
    /// over its value, and carries its own copy button since there is no footer.</param>
    public PropertiesDialog(DownloadItem item, bool embedded = false)
    {
        _embedded = embedded;

        var accent = FormKit.Res("StrettoAccentBrush");

        // Same category tile as the completion announcement, so the type reads first.
        var tile = new Border
        {
            Width = 40,
            Height = 40,
            CornerRadius = new CornerRadius(9),
            Background = FormKit.Res("StrettoAccentSoftBrush"),
            VerticalAlignment = VerticalAlignment.Top,
            Child = new FontIcon { Glyph = item.CategoryGlyph, FontSize = 20, Foreground = accent },
        };

        var title = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        title.Children.Add(Selectable(item.Name, 14, semiBold: true));
        title.Children.Add(new TextBlock
        {
            Text = $"{item.Category}  ·  {item.StatusLabel}"
                 + (item.TotalBytes is { } size ? $"  ·  {DownloadItem.FormatBytes(size)}" : ""),
            FontSize = 12,
            Foreground = item.StatusBrush,
        });

        if (embedded)
            title.Children[0].SetValue(TextBlock.MaxLinesProperty, 4);

        var header = new Grid { ColumnSpacing = 12 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.Children.Add(tile);
        Grid.SetColumn(title, 1);
        header.Children.Add(title);

        var sheet = new Grid { ColumnSpacing = 14, RowSpacing = 0 };
        sheet.ColumnDefinitions.Add(new ColumnDefinition { Width = embedded ? new GridLength(0) : new GridLength(78) });
        sheet.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        if (embedded)
            Add(sheet, "Status", item.Status == DownloadStatus.Complete ? null : item.DetailLine);
        Add(sheet, "Saved in", item.TargetDirectory);
        Add(sheet, "Address", item.Url, maxLines: 3);
        Add(sheet, "Added", item.Added.LocalDateTime.ToString("d MMMM yyyy, HH:mm"));
        Add(sheet, "Kind", item.IsStream ? "Streaming media, through ffmpeg" : $"HTTP, {item.Connections} connections");
        Add(sheet, "Proxy", item.ProxyDescription);
        Add(sheet, "Referrer", item.Referrer, maxLines: 2);
        Add(sheet, "Cookies", string.IsNullOrEmpty(item.Cookie) ? null : $"{item.Cookie.Split(';').Length} sent from the browser");
        Add(sheet, "Headers", DescribeHeaders(item), mono: true);

        var copy = new Button { Content = "Copy address", MinWidth = 80 };
        copy.Click += (_, _) =>
        {
            var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
            package.SetText(item.Url);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
        };
        var panel = new StackPanel { Spacing = 14, Width = embedded ? double.NaN : 480 };
        panel.Children.Add(header);
        panel.Children.Add(new Border
        {
            Background = FormKit.Res("StrettoCardBrush"),
            BorderBrush = FormKit.Res("StrettoCardBorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14, 4, 14, 4),
            Child = sheet,
        });

        if (BuildVerify(item) is { } verify)
            panel.Children.Add(verify);

        if (embedded)
        {
            // The pane scrolls itself, and has no footer for the button to live in.
            copy.HorizontalAlignment = HorizontalAlignment.Left;
            panel.Children.Add(copy);
            Content = panel;
            return;
        }

        FooterStart = copy;
        Content = new ScrollViewer
        {
            Content = panel,
            MaxHeight = 520,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
    }

    /// <summary>
    /// "Is this the file the site meant to give me?" for a finished download.
    ///
    /// Paste the checksum the site published and it says whether the file matches, picking
    /// MD5, SHA-1, SHA-256 or SHA-512 from the length. Leave it empty and it works out the
    /// SHA-256, which is what to copy back into a forum post or compare by eye. Offered
    /// only while the file is still on disk.
    /// </summary>
    private static UIElement? BuildVerify(DownloadItem item)
    {
        if (item.Status != DownloadStatus.Complete || item.ResolvedPath is not { Length: > 0 } path || !File.Exists(path))
            return null;

        var box = new TextBox
        {
            PlaceholderText = "Paste a checksum, or leave empty",
            FontFamily = new FontFamily("Consolas"),
            FontSize = 11.5,
        };
        var verify = new Button { Content = "Verify" };
        var result = new TextBlock
        {
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
            Visibility = Visibility.Collapsed,
        };

        void Show(string text, string? brush = null)
        {
            result.Text = text;
            result.Foreground = brush is null ? FormKit.Res("TextFillColorSecondaryBrush") : FormKit.Res(brush);
            result.Visibility = Visibility.Visible;
        }

        async Task RunAsync()
        {
            var pasted = box.Text;
            var kind = string.IsNullOrWhiteSpace(pasted) ? Checksum.Kind.Sha256 : Checksum.Detect(pasted);
            if (kind is null)
            {
                Show("That is not an MD5, SHA-1, SHA-256 or SHA-512 checksum.", "SystemFillColorCriticalBrush");
                return;
            }

            verify.IsEnabled = false;
            try
            {
                // Progress reports are posted to the UI thread, so the last one can land after
                // the result and overwrite it; once the hash is in, they are ignored.
                var finished = false;
                var progress = new Progress<double>(f =>
                {
                    if (!finished) Show($"Checking… {f * 100:F0}%");
                });
                var actual = await Checksum.ComputeAsync(path, kind.Value, progress);
                finished = true;
                var label = Checksum.Label(kind.Value);

                if (string.IsNullOrWhiteSpace(pasted))
                    Show($"{label}: {actual}");
                else if (Checksum.Matches(pasted, actual))
                    Show($"Matches. The file is exactly what the {label} checksum describes.", "SystemFillColorSuccessBrush");
                else
                    Show($"Does not match. This file's {label} is {actual}. Download it again from the original site.", "SystemFillColorCriticalBrush");
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Show($"Could not read the file: {exception.Message}", "SystemFillColorCriticalBrush");
            }
            finally
            {
                verify.IsEnabled = true;
            }
        }

        verify.Click += async (_, _) => await RunAsync();
        box.KeyDown += async (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter && verify.IsEnabled)
                await RunAsync();
        };

        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(box);
        Grid.SetColumn(verify, 1);
        row.Children.Add(verify);

        var block = new StackPanel { Spacing = 6 };
        block.Children.Add(new TextBlock
        {
            Text = "Check the file",
            FontSize = 11.5,
            Foreground = FormKit.Res("TextFillColorSecondaryBrush"),
        });
        block.Children.Add(row);
        block.Children.Add(result);
        return block;
    }

    /// <summary>
    /// The headers this download actually sends.
    ///
    /// The Authorization value is named but never printed. A properties window is the sort
    /// of thing people screenshot when asking for help, and a base64 Basic header is the
    /// password in a thin disguise. Cookies are counted rather than shown for the same reason.
    /// </summary>
    private static string DescribeHeaders(DownloadItem item)
    {
        var headers = new StringBuilder();

        if (!string.IsNullOrEmpty(item.UserAgent))
            headers.AppendLine($"User-Agent: {item.UserAgent}");

        if (!string.IsNullOrEmpty(item.Referrer))
            headers.AppendLine($"Referer: {item.Referrer}");

        if (!string.IsNullOrEmpty(item.Username) || Services.SiteCredentials.For(item.Url) is not null)
            headers.AppendLine("Authorization: sent on server challenge (credentials not shown)");

        // Captured from the browser. Names always, values only for the few known to carry
        // nothing secret -- the same list that is allowed to be saved to disk.
        if (item.Headers is { Count: > 0 } captured)
        {
            var safe = Services.DownloadStore.KeepableHeaders(captured) ?? [];
            foreach (var (name, value) in captured.OrderBy(h => h.Key, StringComparer.OrdinalIgnoreCase))
                headers.AppendLine(safe.ContainsKey(name) ? $"{name}: {value}" : $"{name}: (from the browser, not shown)");
        }

        return headers.ToString().TrimEnd();
    }

    /// <summary>Adds one label and value row to the sheet, skipping an empty value.</summary>
    /// <param name="sheet">The two-column grid the rows go into.</param>
    /// <param name="label">The left-hand caption.</param>
    /// <param name="value">What is shown; nothing is added when it is blank.</param>
    /// <param name="mono">Monospaced, for addresses and headers.</param>
    /// <param name="maxLines">Signed download links run to a dozen lines; three is enough to
    /// recognise one, and Copy address in the footer takes the whole thing.</param>
    private readonly bool _embedded;

    private void Add(Grid sheet, string label, string? value, bool mono = false, int maxLines = 0)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;

        // The pane is too narrow for a label column, so each label sits over its value.
        if (_embedded)
        {
            var at = sheet.RowDefinitions.Count;
            sheet.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var block = new StackPanel { Spacing = 2, Margin = new Thickness(0, 8, 0, 8) };
            block.Children.Add(new TextBlock
            {
                Text = label,
                FontSize = 11.5,
                Foreground = FormKit.Res("TextFillColorSecondaryBrush"),
            });
            var shown = Selectable(value, 12.5);
            if (mono) { shown.FontFamily = new FontFamily("Consolas"); shown.FontSize = 11.5; }
            if (maxLines > 0)
            {
                shown.MaxLines = maxLines;
                shown.TextTrimming = TextTrimming.CharacterEllipsis;
                ToolTipService.SetToolTip(shown, value);
            }
            block.Children.Add(shown);
            Grid.SetRow(block, at);
            Grid.SetColumnSpan(block, 2);
            sheet.Children.Add(block);

            if (at > 0)
            {
                var rule = new Border { Height = 1, VerticalAlignment = VerticalAlignment.Top, Background = FormKit.Res("StrettoDividerBrush") };
                Grid.SetRow(rule, at);
                Grid.SetColumnSpan(rule, 2);
                sheet.Children.Add(rule);
            }
            return;
        }

        var row = sheet.RowDefinitions.Count;
        sheet.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var name = new TextBlock
        {
            Text = label,
            FontSize = 12,
            Foreground = FormKit.Res("TextFillColorSecondaryBrush"),
            Margin = new Thickness(0, 8, 0, 8),
        };
        Grid.SetRow(name, row);
        sheet.Children.Add(name);

        var text = Selectable(value, 12.5);
        text.Margin = new Thickness(0, 8, 0, 8);
        if (mono) { text.FontFamily = new FontFamily("Consolas"); text.FontSize = 11.5; }
        if (maxLines > 0)
        {
            text.MaxLines = maxLines;
            text.TextTrimming = TextTrimming.CharacterEllipsis;
            ToolTipService.SetToolTip(text, value);
        }
        Grid.SetRow(text, row);
        Grid.SetColumn(text, 1);
        sheet.Children.Add(text);

        // A hairline above every row but the first, spanning both columns.
        if (row > 0)
        {
            var line = new Border { Height = 1, VerticalAlignment = VerticalAlignment.Top, Background = FormKit.Res("StrettoDividerBrush"), Margin = new Thickness(0, 0, 0, 0) };
            Grid.SetRow(line, row);
            Grid.SetColumnSpan(line, 2);
            sheet.Children.Add(line);
        }
    }

    private static TextBlock Selectable(string text, double size, bool semiBold = false) => new()
    {
        Text = text,
        FontSize = size,
        FontWeight = semiBold ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
        TextWrapping = TextWrapping.Wrap,
        IsTextSelectionEnabled = true,
    };
}
