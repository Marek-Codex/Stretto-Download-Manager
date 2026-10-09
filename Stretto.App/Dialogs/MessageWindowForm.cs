using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Stretto.App.Services;

namespace Stretto.App.Dialogs;

/// <summary>Small confirmations and notices inside the shared utility window.</summary>
public sealed class MessageWindowForm : UserControl, IWindowForm
{
    public string PrimaryText { get; }
    public string? SecondaryText { get; }
    public string? CancelText { get; }
    public bool AccentPrimary { get; }
    public bool CanPrimary => true;
    public bool CanSecondary => true;
    public MessageWindowForm(string message, string primaryText = "OK", string? cancelText = null,
        string? secondaryText = null, FrameworkElement? extraContent = null,
        bool accentPrimary = true, double width = 440)
    {
        PrimaryText = primaryText;
        CancelText = cancelText;
        SecondaryText = secondaryText;
        AccentPrimary = accentPrimary;

        var content = new StackPanel { Spacing = 12, MaxWidth = width };
        content.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
        if (extraContent is not null)
            content.Children.Add(extraContent);

        Content = content.Compact();
    }
}
