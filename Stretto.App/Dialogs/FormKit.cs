using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Stretto.App.Dialogs;

/// <summary>
/// The handful of shapes every settings-like surface in Stretto is built from, so the
/// windows stop inventing their own spacing.
///
/// A card groups related settings. Inside it, a row puts the setting's name and a short
/// explanation on the left and its control on the right, the way Windows Settings does;
/// a stacked row is for controls that need the full width (paths, lists). Rows are
/// separated by hairlines rather than gaps, which is what keeps a long page compact
/// without every line crowding the next.
/// </summary>
internal static class FormKit
{
    public static Brush Res(string key) => (Brush)Application.Current.Resources[key];

    /// <summary>The small heading above a card.</summary>
    public static TextBlock GroupTitle(string text) => new()
    {
        Text = text,
        FontSize = 12.5,
        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        Margin = new Thickness(2, 14, 0, 6),
    };

    /// <summary>The page heading at the top of a settings section.</summary>
    public static TextBlock PageTitle(string text) => new()
    {
        Text = text,
        FontSize = 20,
        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        Margin = new Thickness(2, 0, 0, 0),
    };

    public static TextBlock Hint(string text) => new()
    {
        Text = text,
        FontSize = 11.5,
        TextWrapping = TextWrapping.Wrap,
        Foreground = Res("TextFillColorSecondaryBrush"),
    };

    /// <summary>A bordered group of rows with hairlines between them.</summary>
    public static Border Card(params UIElement[] rows)
    {
        var stack = new StackPanel();

        for (var i = 0; i < rows.Length; i++)
        {
            if (i > 0)
                stack.Children.Add(new Border { Height = 1, Background = Res("StrettoDividerBrush") });

            stack.Children.Add(rows[i]);
        }

        return new Border
        {
            Background = Res("StrettoCardBrush"),
            BorderBrush = Res("StrettoCardBorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Child = stack,
        };
    }

    /// <summary>Name and explanation on the left, the control on the right.</summary>
    public static Grid Row(string title, string? hint, FrameworkElement control)
    {
        var grid = new Grid { Padding = new Thickness(14, 6, 14, 6), ColumnSpacing = 16, MinHeight = 44 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        grid.Children.Add(Label(title, hint));

        control.VerticalAlignment = VerticalAlignment.Center;
        control.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(control, 1);
        grid.Children.Add(control);
        return grid;
    }

    /// <summary>Name and explanation above, a full-width control below.</summary>
    public static StackPanel StackRow(string title, string? hint, FrameworkElement control)
    {
        var panel = new StackPanel { Padding = new Thickness(14, 10, 14, 12), Spacing = 8 };
        panel.Children.Add(Label(title, hint));
        panel.Children.Add(control);
        return panel;
    }

    private static StackPanel Label(string title, string? hint)
    {
        var label = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        label.Children.Add(new TextBlock { Text = title, FontSize = 13, TextWrapping = TextWrapping.Wrap });
        if (!string.IsNullOrEmpty(hint))
            label.Children.Add(Hint(hint));
        return label;
    }

    /// <summary>A switch with no On/Off caption beside it: the row already says what it is.</summary>
    public static ToggleSwitch Toggle(bool isOn) => new()
    {
        IsOn = isOn,
        OnContent = "",
        OffContent = "",
        MinWidth = 0,
    };

    /// <summary>A text box with a button beside it, for paths.</summary>
    public static Grid WithButton(FrameworkElement box, FrameworkElement button)
    {
        var grid = new Grid { ColumnSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(box);
        Grid.SetColumn(button, 1);
        grid.Children.Add(button);
        return grid;
    }

    public static Button PushButton(string text, RoutedEventHandler click, bool accent = false)
    {
        var button = new Button { Content = text, MinWidth = 72 };
        if (accent) button.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
        button.Click += click;
        return button;
    }

    /// <summary>A flat icon-and-text button for the tool row under a table.</summary>
    public static Button ToolButton(string glyph, string text, RoutedEventHandler click)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        content.Children.Add(new FontIcon { Glyph = glyph, FontSize = 12 });
        content.Children.Add(new TextBlock { Text = text });

        var button = new Button
        {
            Content = content,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(8, 5, 8, 5),
            FontSize = 12,
        };
        button.Click += click;
        return button;
    }

    public static StackPanel Horizontal(params UIElement[] children)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var child in children) panel.Children.Add(child);
        return panel;
    }
}

/// <summary>
/// A small selectable table: one Grid for header and rows, so the columns line up by
/// construction. A header above a ListView drifts, because the item chrome leaves the rows
/// narrower than the header and a star column turns the difference into a visible offset.
/// </summary>
internal sealed class SimpleTable<T> where T : class
{
    private readonly Grid _grid = new() { ColumnSpacing = 12 };
    private readonly Func<T, string[]> _cells;
    private readonly string[] _headers;
    private readonly Dictionary<T, Border[]> _rows = [];

    public SimpleTable(string[] headers, GridLength[] widths, Func<T, string[]> cells)
    {
        _headers = headers;
        _cells = cells;
        foreach (var width in widths)
            _grid.ColumnDefinitions.Add(new ColumnDefinition { Width = width });

        View = new Border
        {
            Background = FormKit.Res("StrettoCardBrush"),
            BorderBrush = FormKit.Res("StrettoCardBorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 6, 12, 6),
            Child = new ScrollViewer
            {
                Content = _grid,
                MaxHeight = 232,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            },
        };
    }

    public Border View { get; }

    public T? Selected { get; private set; }

    public event Action? SelectionChanged;

    /// <summary>Raised on double click, which is the obvious way to edit a row.</summary>
    public event Action<T>? Activated;

    public void Show(IEnumerable<T> items, string emptyText)
    {
        _rows.Clear();
        _grid.Children.Clear();
        _grid.RowDefinitions.Clear();

        _grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (var column = 0; column < _headers.Length; column++)
        {
            var header = new TextBlock
            {
                Text = _headers[column],
                FontSize = 11.5,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = FormKit.Res("TextFillColorTertiaryBrush"),
                Margin = new Thickness(0, 2, 0, 6),
            };
            Grid.SetColumn(header, column);
            _grid.Children.Add(header);
        }

        foreach (var item in items)
        {
            var row = _grid.RowDefinitions.Count;
            _grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(30) });

            var texts = _cells(item);
            var cells = new Border[texts.Length];

            for (var column = 0; column < texts.Length; column++)
            {
                var text = new TextBlock
                {
                    Text = texts[column],
                    FontSize = 12.5,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Opacity = column == 0 ? 1 : 0.75,
                    // The cell's highlight reaches into the column gap (below), so the text
                    // keeps that gap itself or trimmed text runs into the next column.
                    Margin = new Thickness(0, 0, column < texts.Length - 1 ? 12 : 0, 0),
                };
                ToolTipService.SetToolTip(text, texts[column]);

                var cell = new Border
                {
                    BorderThickness = new Thickness(0, 1, 0, 0),
                    BorderBrush = FormKit.Res("StrettoDividerBrush"),
                    Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                    Child = text,
                };

                // Cells sit in a Grid with a column gap; stretching the highlight into the
                // gap keeps a selected row reading as one bar rather than separate boxes.
                if (column < texts.Length - 1)
                    cell.Margin = new Thickness(0, 0, -12, 0);

                var captured = item;
                cell.Tapped += (_, _) => Select(captured);
                cell.DoubleTapped += (_, _) => { Select(captured); Activated?.Invoke(captured); };

                Grid.SetRow(cell, row);
                Grid.SetColumn(cell, column);
                _grid.Children.Add(cell);
                cells[column] = cell;
            }

            _rows[item] = cells;
        }

        if (_rows.Count == 0)
        {
            _grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(44) });
            var empty = new TextBlock
            {
                Text = emptyText,
                FontSize = 12,
                Opacity = 0.5,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetRow(empty, 1);
            Grid.SetColumnSpan(empty, _headers.Length);
            _grid.Children.Add(empty);
        }

        if (Selected is not null && !_rows.ContainsKey(Selected))
            Selected = null;

        Paint();
        SelectionChanged?.Invoke();
    }

    public void Select(T? item)
    {
        Selected = item;
        Paint();
        SelectionChanged?.Invoke();
    }

    private void Paint()
    {
        var highlight = FormKit.Res("StrettoAccentSoftBrush");
        var clear = new SolidColorBrush(Microsoft.UI.Colors.Transparent);

        foreach (var (item, cells) in _rows)
            foreach (var cell in cells)
                cell.Background = ReferenceEquals(item, Selected) ? highlight : clear;
    }
}
