using Stretto.Core;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Stretto.App.Services;
using Windows.Graphics;
using WinRT.Interop;

namespace Stretto.App.Dialogs;

/// <summary>A compact, movable New Download window that shares the app's native title bar.</summary>
public sealed class NewDownloadWindow : Window
{
    private readonly TaskCompletionSource<NewDownloadDialog.Outcome> _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public NewDownloadDialog Dialog { get; }
    public Task<NewDownloadDialog.Outcome> ClosedTask => _closed.Task;

    public NewDownloadWindow(AppSettings settings, string downloadFolder, string title = "New download")
    {
        var titleBar = WindowAppearance.ConfigureUtilityWindow(this, title);

        Dialog = new NewDownloadDialog(WindowNative.GetWindowHandle(this), settings, downloadFolder);
        Dialog.CloseRequested += Close;
        var shell = new Grid { Background = (Brush)Application.Current.Resources["StrettoWindowBrush"] };
        shell.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        shell.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        shell.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var buttons = Dialog.DetachButtons();
        var form = new Border { Padding = new Thickness(16, 8, 16, 14), Child = Dialog };
        var footer = new Border
        {
            Padding = new Thickness(16, 10, 16, 12),
            BorderThickness = new Thickness(0, 1, 0, 0),
            BorderBrush = WindowAppearance.Brush("StrettoDividerBrush"),
            Background = WindowAppearance.Brush("StrettoFooterBrush"),
            Child = buttons,
        };
        Grid.SetRow(form, 1);
        Grid.SetRow(footer, 2);
        shell.Children.Add(titleBar);
        shell.Children.Add(form);
        shell.Children.Add(footer);
        Content = shell.Compact();

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
        }

        AppWindow.Resize(new SizeInt32(740, 620));
        AppWindow.Move(new PointInt32(-20000, -20000));
        Closed += (_, _) => _closed.TrySetResult(Dialog.Result);
        _ = WindowAppearance.FitToContentAsync(this, shell, 720, 900);
    }

    /// <summary>Usually opened by a browser handoff, while the browser owns the foreground.</summary>
    public void Show()
    {
        Activate();
        Interop.WindowFocus.BringToFront(this);
    }
}
