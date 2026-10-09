using Microsoft.UI.Xaml;

namespace Stretto.App.Services;

/// <summary>
/// WinUI's compact control metrics, applied to one window at a time.
///
/// Every window except the main one is compact. The main window is somewhere you sit and
/// read a list; the rest are somewhere you pass through to answer a question, and a row of
/// full height controls makes a four field dialog feel like a form to fill in.
///
/// This is WinUI's own density dictionary rather than hand shrunk paddings, so control
/// heights, list rows and spacing all move together and keep agreeing with each other.
/// </summary>
public static class CompactDensity
{
    private static readonly Uri Source = new("ms-appx:///Microsoft.UI.Xaml/DensityStyles/Compact.xaml");

    /// <summary>
    /// Scopes compact metrics to <paramref name="root"/> and everything inside it. Merged
    /// into the element rather than the application so the main window keeps its breathing
    /// room.
    /// </summary>
    public static T Compact<T>(this T root) where T : FrameworkElement
    {
        root.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = Source });
        return root;
    }
}
