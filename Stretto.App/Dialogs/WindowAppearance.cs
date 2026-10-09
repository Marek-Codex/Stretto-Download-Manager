using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

namespace Stretto.App.Dialogs;

/// <summary>Keep native caption buttons and text on the same dark surface as Stretto.</summary>
internal static class WindowAppearance
{
    /// <summary>Minimum width of a button in a window footer.</summary>
    public const double ButtonWidth = 88;

    private static string IconPath => Path.Combine(AppContext.BaseDirectory, "Assets", "stretto.ico");

    public static void ApplyDarkTitleBar(Window window)
    {
        if (AppWindowTitleBar.IsCustomizationSupported())
            window.AppWindow.TitleBar.PreferredTheme = TitleBarTheme.Dark;
    }

    /// <summary>
    /// Applies the common Stretto shell used by every secondary window: Mica, the dark
    /// caption, and a title bar carrying the app icon like the main window's does.
    ///
    /// <paramref name="dialog"/> windows answer one question and go away, so they drop the
    /// minimise and maximise buttons; a maximised four-field form is never what anyone wants.
    /// Tool windows (the grabber, progress) keep them.
    /// </summary>
    public static TitleBar ConfigureUtilityWindow(Window window, string title, string? subtitle = null, bool dialog = true)
    {
        window.Title = title;
        ApplyDarkTitleBar(window);
        window.SystemBackdrop = new MicaBackdrop
        {
            Kind = Microsoft.UI.Composition.SystemBackdrops.MicaKind.BaseAlt,
        };
        window.ExtendsContentIntoTitleBar = true;
        window.AppWindow.SetIcon(IconPath);

        if (window.AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsMaximizable = false;
            if (dialog)
                presenter.IsMinimizable = false;
        }

        var titleBar = new TitleBar
        {
            Title = title,
            Subtitle = subtitle,
            IconSource = new BitmapIconSource
            {
                UriSource = new Uri("ms-appx:///Assets/stretto-icon.png"),
                ShowAsMonochrome = false,
            },
        };
        window.SetTitleBar(titleBar);
        return titleBar;
    }

    /// <summary>
    /// The button strip along the bottom of a window: a hairline above it and a surface one
    /// step up from the window, so the buttons read as a bar rather than as more content.
    /// <paramref name="left"/> holds anything that belongs at the far end (Advanced, Close).
    /// </summary>
    public static Border Footer(IEnumerable<UIElement> right, IEnumerable<UIElement>? left = null)
    {
        // One width for every footer button, so the same Cancel or Close is the same size
        // in every window. Callers used to pick 68 to 88 each, which made neighbours uneven.
        right = right.ToList();
        left = left?.ToList();
        foreach (var button in right.Concat(left ?? []).OfType<Button>())
            button.MinWidth = ButtonWidth;

        var grid = new Grid { ColumnSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        if (left is not null)
        {
            var start = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            foreach (var element in left) start.Children.Add(element);
            grid.Children.Add(start);
        }

        var end = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var element in right) end.Children.Add(element);
        Grid.SetColumn(end, 2);
        grid.Children.Add(end);

        return new Border
        {
            Padding = new Thickness(16, 10, 16, 12),
            BorderThickness = new Thickness(0, 1, 0, 0),
            BorderBrush = Brush("StrettoDividerBrush"),
            Background = Brush("StrettoFooterBrush"),
            Child = grid,
        };
    }

    public static Brush Brush(string key) => (Brush)Application.Current.Resources[key];

    /// <summary>Sizes a content-driven utility window and centers it on the available work area.</summary>
    public static async Task FitToContentAsync(Window window, FrameworkElement content, double maxWidth, double maxHeight)
    {
        if (content.XamlRoot is null)
        {
            var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            RoutedEventHandler handler = (_, _) => loaded.TrySetResult();
            content.Loaded += handler;
            await loaded.Task;
            content.Loaded -= handler;
        }

        var workArea = DisplayArea.GetFromWindowId(window.AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var scale = content.XamlRoot?.RasterizationScale ?? 1;
        var availableWidth = Math.Min(maxWidth, workArea.Width / scale);
        var availableHeight = Math.Min(maxHeight, workArea.Height / scale);
        content.Measure(new Windows.Foundation.Size(availableWidth, availableHeight));

        // AppWindow.Resize takes the outer size, while XAML measures the client area.
        var frameWidth = Math.Max(0, window.AppWindow.Size.Width - window.AppWindow.ClientSize.Width);
        var frameHeight = Math.Max(0, window.AppWindow.Size.Height - window.AppWindow.ClientSize.Height);
        var size = new SizeInt32(
            Math.Min(workArea.Width, (int)Math.Ceiling(content.DesiredSize.Width * scale) + frameWidth),
            Math.Min(workArea.Height, (int)Math.Ceiling(content.DesiredSize.Height * scale) + frameHeight));

        window.AppWindow.Resize(size);
        window.AppWindow.Move(new PointInt32(
            workArea.X + Math.Max(0, (workArea.Width - size.Width) / 2),
            workArea.Y + Math.Max(0, (workArea.Height - size.Height) / 2)));
    }
}
