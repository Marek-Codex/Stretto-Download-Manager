using Microsoft.UI.Xaml.Controls;

namespace Stretto.App.Dialogs;

/// <summary>Button state supplied by a form shown in a Stretto utility window.</summary>
public interface IWindowForm
{
    string PrimaryText { get; }
    string? SecondaryText { get; }
    string? CancelText { get; }
    bool AccentPrimary { get; }
    bool CanPrimary { get; }
    bool CanSecondary { get; }
}

/// <summary>
/// Optional layout hooks. A form with its own edge-to-edge layout (Settings, with its
/// sidebar) asks for no body padding; a form with a secondary action that does not close
/// the window (Advanced…) puts it at the far end of the footer.
/// </summary>
public interface IWindowFormChrome : IWindowForm
{
    Microsoft.UI.Xaml.Thickness BodyPadding { get; }
    Microsoft.UI.Xaml.UIElement? FooterStart { get; }
}

public interface IButtonStateWindowForm : IWindowForm
{
    event EventHandler? ButtonStateChanged;
}

public interface ICloseRequestedWindowForm : IWindowForm
{
    event EventHandler<WindowFormCloseEventArgs>? CloseRequested;
}

public sealed class WindowFormCloseEventArgs(ContentDialogResult result) : EventArgs
{
    public ContentDialogResult Result { get; } = result;
}
