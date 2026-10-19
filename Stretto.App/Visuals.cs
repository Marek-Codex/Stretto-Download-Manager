using Stretto.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Stretto.App.Services;

namespace Stretto.App;

/// <summary>
/// How a download looks: the brushes and glyphs that used to be properties on the item itself.
/// The item says what state it is in; this resolves that against the app's resource
/// dictionary, so the model carries nothing from WinUI.
/// </summary>
public static class Visuals
{
    public static Brush StatusBrush(DownloadStatus status) => Brush(status switch
    {
        DownloadStatus.Complete => "StrettoDoneBrush",
        DownloadStatus.Paused => "StrettoPausedBrush",
        DownloadStatus.Pending => "StrettoPendingBrush",
        DownloadStatus.Failed => "StrettoErrorBrush",
        _ => "StrettoAccentBrush",
    });

    /// <summary>The one thing the row's own button does for this state.</summary>
    public static string RowActionGlyph(DownloadStatus status) => Text(status switch
    {
        DownloadStatus.Downloading or DownloadStatus.Queued => "IconPause",
        DownloadStatus.Failed => "IconRetry",
        DownloadStatus.Complete => "IconFolder",
        _ => "IconResume",
    });

    /// <summary>Resolved through the icon dictionary so a font swap stays a one-file change.</summary>
    public static string CategoryGlyph(string category) =>
        Application.Current.Resources[CategoryRules.Find(category)?.Icon ?? "IconFile"] as string ?? "";

    public static string CategoryGlyph(DownloadItem item) => CategoryGlyph(item.Category);

    private static Brush Brush(string key) =>
        Application.Current.Resources[key] as Brush ?? new SolidColorBrush();

    private static string Text(string key) =>
        (Application.Current.Resources[key] as string)!;
}

/// <summary>Binds a <see cref="DownloadStatus"/> to its colour.</summary>
public sealed class StatusBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        Visuals.StatusBrush(value is DownloadStatus status ? status : DownloadStatus.Pending);

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

/// <summary>Binds a <see cref="DownloadStatus"/> to the glyph of the row's button.</summary>
public sealed class RowActionGlyphConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        Visuals.RowActionGlyph(value is DownloadStatus status ? status : DownloadStatus.Pending);

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

/// <summary>Binds a category name to its icon glyph.</summary>
public sealed class CategoryGlyphConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        Visuals.CategoryGlyph(value as string ?? "");

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

/// <summary>Visible for true, collapsed for false.</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
