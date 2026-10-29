using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Stretto.App.Services;

namespace Stretto.App.Dialogs;

/// <summary>Short, local help and product details. Useful even when the network has opinions.</summary>
public sealed class HelpAboutForm : UserControl, IWindowForm
{
    public string PrimaryText => "Close";
    public string? SecondaryText => null;
    public string? CancelText => null;
    public bool AccentPrimary => false;
    public bool CanPrimary => true;
    public bool CanSecondary => false;

    public HelpAboutForm(bool about)
    {
        var body = new StackPanel { Spacing = 16, MaxWidth = 520 };
        void Section(string heading, string text)
        {
            var section = new StackPanel { Spacing = 5 };
            section.Children.Add(new TextBlock { Text = heading, FontSize = 16,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
            section.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true });
            body.Children.Add(section);
        }

        if (about)
        {
            Section("Stretto Download Manager", $"Version {Updates.CurrentText} · Free alpha preview\nMade by Marek Codex. Part of Plexus Apps.");
            Section("Less babysitting. More downloading.", "Resumable downloads, adaptive connections, queues and browser integration. Built to keep the transfers moving while you get on with your day.");
            Section("Built with", "WinUI 3 and .NET 10. Optional ffmpeg and yt-dlp components are separate programs with their own upstream licenses.");
            Section("Release status", "This is a testing build. Final browser, interface and signed-installation acceptance checks are still in progress. Self-signed testing packages are not independently verified publisher releases.");
            Section("License", "Copyright © 2026 Marek Codex. All rights reserved. Third-party components retain their own licenses.");
        }
        else
        {
            Section("A quick hand with Stretto", "Start here. The progress bar can look after itself for a minute.");
            Section("Add and find downloads", "Choose Add download (Ctrl+N), paste a link and check its destination. Ctrl+F searches the list. Transfers shows work in progress; Library keeps finished downloads easy to find.");
            Section("Connect your browser", "Open Browser → Set up the extension and follow the three steps. Check the extension popup says Connected. Regular Firefox forgets a temporary unsigned add-on when it restarts.");
            Section("Pause, resume and queues", "Pause a transfer before closing Stretto if you want to leave it parked. Resume depends on the server and whether its file or link has changed. Queues let you choose an order and timetable.");
            Section("Media and optional tools", "Ordinary downloads need nothing extra. Settings → Components & about offers ffmpeg and yt-dlp for supported streams, conversion and video sites. Only download content you are authorized to save.");
            Section("If a download stays in the browser", "Check capture is enabled and the site is not excluded. Form submissions, unknown request methods and some one-use or browser-protected links must stay with the browser.");
            Section("When something goes wrong", "Settings → Components & about → Troubleshooting can open the log or copy diagnostics. Include the error, app/browser versions and steps to reproduce in a report. Review URLs, paths and credentials before sharing anything publicly.");
        }

        Content = new ScrollViewer { Content = body, MaxHeight = 460,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    }
}
