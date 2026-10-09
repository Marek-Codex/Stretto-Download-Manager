using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Stretto.App.Services;
using Windows.Graphics;

namespace Stretto.App.Dialogs;

/// <summary>A shared, compact native window shell for all secondary Stretto forms.</summary>
public sealed class UtilityWindow : Window
{
    private readonly IWindowForm _form;
    private readonly Button _primary;
    private readonly Button? _secondary;
    private readonly TitleBar _titleBar;
    private readonly Border _footer;
    private readonly Border _body;
    private readonly TaskCompletionSource<ContentDialogResult> _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private ContentDialogResult _result;

    public UtilityWindow(FrameworkElement content, IWindowForm form, string title, int width = 0, int height = 0)
    {
        _form = form;
        _titleBar = WindowAppearance.ConfigureUtilityWindow(this, title);
        AppWindow.IsShownInSwitchers = true;

        var cancelText = form.CancelText;
        var cancel = cancelText is null ? null : new Button { Content = cancelText, MinWidth = 80 };
        if (cancel is not null)
        {
            cancel.Click += (_, _) => Finish(ContentDialogResult.None);
            cancel.KeyboardAccelerators.Add(new Microsoft.UI.Xaml.Input.KeyboardAccelerator
            {
                Key = Windows.System.VirtualKey.Escape,
            });
        }

        _primary = new Button
        {
            Content = form.PrimaryText,
            MinWidth = 80,
            Style = form.AccentPrimary ? (Style)Application.Current.Resources["AccentButtonStyle"] : null,
        };
        _primary.Click += (_, _) => Finish(ContentDialogResult.Primary);

        if (form.SecondaryText is { Length: > 0 } secondaryText)
        {
            _secondary = new Button { Content = secondaryText, MinWidth = 80 };
            _secondary.Click += (_, _) => Finish(ContentDialogResult.Secondary);
        }

        // Windows order: the action first, the way out last. The New Download window
        // already worked this way and the shared shell did not, so the same two buttons
        // swapped sides between windows.
        var buttons = new List<UIElement> { _primary };
        if (_secondary is not null) buttons.Add(_secondary);
        if (cancel is not null) buttons.Add(cancel);

        var chrome = form as IWindowFormChrome;
        _footer = WindowAppearance.Footer(buttons,
            chrome?.FooterStart is { } start ? [start] : null);

        _body = new Border
        {
            Padding = chrome?.BodyPadding ?? new Thickness(16, 8, 16, 14),
            Child = content,
        };

        var shell = new Grid { Background = (Brush)Application.Current.Resources["StrettoWindowBrush"] };
        shell.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        shell.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        shell.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        shell.Children.Add(_titleBar);
        Grid.SetRow(_body, 1);
        shell.Children.Add(_body);
        Grid.SetRow(_footer, 2);
        shell.Children.Add(_footer);
        Content = shell.Compact();

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = true;
        }

        if (form is IButtonStateWindowForm notifyingForm)
            notifyingForm.ButtonStateChanged += Form_ButtonStateChanged;
        if (form is ICloseRequestedWindowForm closeRequestingForm)
            closeRequestingForm.CloseRequested += Form_CloseRequested;
        Closed += (_, _) =>
        {
            if (form is IButtonStateWindowForm notifying)
                notifying.ButtonStateChanged -= Form_ButtonStateChanged;
            if (form is ICloseRequestedWindowForm closeRequesting)
                closeRequesting.CloseRequested -= Form_CloseRequested;
            _closed.TrySetResult(_result);
        };

        UpdateButtons();
        AppWindow.Resize(new SizeInt32(width > 0 ? width : 760, height > 0 ? height : 620));
        AppWindow.Move(new PointInt32(-20000, -20000));
        _ = SizeWhenReadyAsync(width, height);
    }

    public async Task<ContentDialogResult> ShowAsync()
    {
        Activate();
        // Batch captures from the browser land here while the browser is in front.
        Interop.WindowFocus.BringToFront(this);
        return await _closed.Task;
    }

    private void Finish(ContentDialogResult result)
    {
        _result = result;
        Close();
    }

    private void Form_ButtonStateChanged(object? sender, EventArgs e) => UpdateButtons();
    private void Form_CloseRequested(object? sender, WindowFormCloseEventArgs e) => Finish(e.Result);

    private void UpdateButtons()
    {
        _primary.IsEnabled = _form.CanPrimary;
        if (_secondary is not null)
            _secondary.IsEnabled = _form.CanSecondary;
    }

    private async Task SizeWhenReadyAsync(int width, int height)
    {
        var shell = (FrameworkElement)Content;
        if (shell.XamlRoot is null)
        {
            var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            RoutedEventHandler handler = (_, _) => loaded.TrySetResult();
            shell.Loaded += handler;
            await loaded.Task;
            shell.Loaded -= handler;
        }

        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var scale = shell.XamlRoot?.RasterizationScale ?? 1;
        var frameWidth = Math.Max(0, AppWindow.Size.Width - AppWindow.ClientSize.Width);
        var frameHeight = Math.Max(0, AppWindow.Size.Height - AppWindow.ClientSize.Height);
        SizeInt32 size;

        if (width > 0 && height > 0)
        {
            var headerHeight = Math.Max(0, _titleBar.DesiredSize.Height);
            _footer.Measure(new Windows.Foundation.Size(width + _body.Padding.Left + _body.Padding.Right, double.PositiveInfinity));
            var bodyVerticalPadding = _body.Padding.Top + _body.Padding.Bottom;
            size = new SizeInt32(
                Math.Min(area.Width, (int)Math.Ceiling((width + _body.Padding.Left + _body.Padding.Right) * scale) + frameWidth),
                Math.Min(area.Height, (int)Math.Ceiling((height + headerHeight + _footer.DesiredSize.Height + bodyVerticalPadding) * scale) + frameHeight));
        }
        else
        {
            var wantedWidth = Math.Min(680, Math.Max(420, area.Width / scale));
            shell.Measure(new Windows.Foundation.Size(wantedWidth, area.Height / scale));
            size = new SizeInt32(
                Math.Min(area.Width, (int)Math.Ceiling(shell.DesiredSize.Width * scale) + frameWidth),
                Math.Min(area.Height, (int)Math.Ceiling(shell.DesiredSize.Height * scale) + frameHeight));
        }

        AppWindow.Resize(size);
        AppWindow.Move(new PointInt32(
            area.X + Math.Max(0, (area.Width - size.Width) / 2),
            area.Y + Math.Max(0, (area.Height - size.Height) / 2)));
    }
}
