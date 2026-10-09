using Microsoft.UI.Xaml.Controls;
using Stretto.App.Dialogs;

namespace Stretto.App;

public sealed partial class MainWindow
{
    /// <summary>
    /// Opens a form in the shared Stretto utility window shell.
    /// </summary>
    private async Task<ContentDialogResult> ShowInWindowAsync(
        IWindowForm form, string title, int width = 0, int height = 0)
    {
        var window = new UtilityWindow((Microsoft.UI.Xaml.FrameworkElement)form, form, title, width, height);
        return await window.ShowAsync();
    }
}
