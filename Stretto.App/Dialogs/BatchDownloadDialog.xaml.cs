using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Stretto.Core;

namespace Stretto.App.Dialogs;

public sealed partial class BatchDownloadDialog : UserControl, IButtonStateWindowForm
{
    private IReadOnlyList<string> _resolved = [];

    // Selector controls fire SelectionChanged during InitializeComponent, before the
    // rest of the named fields are assigned.
    private readonly bool _ready;

    public string PrimaryText => "Add";
    public string? SecondaryText => null;
    public string? CancelText => "Cancel";
    public bool AccentPrimary => true;
    public bool CanPrimary => _resolved.Count > 0;
    public bool CanSecondary => false;
    public event EventHandler? ButtonStateChanged;

    public BatchDownloadDialog()
    {
        InitializeComponent();
        _ready = true;
        UpdatePattern();
        // SelectorBar ignores a selection made before it is loaded, so the starting tab is
        // applied here; SelectLinksTab only records which one it should be.
        Loaded += (_, _) => Tabs.SelectedItem = _startOnLinks ? LinksTabItem : PatternTabItem;
    }

    /// <summary>The URLs the active tab resolves to. Empty until the input is valid.</summary>
    public IReadOnlyList<string> Urls => _resolved;

    private bool OnLinksTab => Tabs.SelectedItem is null ? _startOnLinks : Tabs.SelectedItem == LinksTabItem;

    private bool _startOnLinks;

    public void SelectLinksTab()
    {
        _startOnLinks = true;
        if (IsLoaded) Tabs.SelectedItem = LinksTabItem;
    }

    /// <summary>Opens on the Links tab with these filled in, for a batch from the browser.
    /// Still editable: deleting a line is how a link gets left out.</summary>
    public void PrefillLinks(IEnumerable<string> urls)
    {
        LinksBox.Text = string.Join(Environment.NewLine, urls);
        SelectLinksTab();
        PatternPanel.Visibility = Visibility.Collapsed;
        LinksPanel.Visibility = Visibility.Visible;
        UpdateLinks();
    }

    private void Tabs_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (!_ready) return;

        PatternPanel.Visibility = OnLinksTab ? Visibility.Collapsed : Visibility.Visible;
        LinksPanel.Visibility = OnLinksTab ? Visibility.Visible : Visibility.Collapsed;

        // Each tab is a separate way of producing the same list, so switching tabs has to
        // re-resolve from whichever one is now in front.
        if (OnLinksTab) UpdateLinks(); else UpdatePattern();
    }

    private void Pattern_Changed(object sender, object e) => UpdatePattern();

    private void PadWidth_Changed(NumberBox sender, NumberBoxValueChangedEventArgs args) => UpdatePattern();

    private void Wildcard_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;

        // Leading zeros only mean something for numbers.
        var numbers = WildcardBox.SelectedIndex == 0;
        LeadingZeroPanel.Opacity = numbers ? 1 : 0.4;
        LeadingZeroCheck.IsEnabled = numbers;
        PadWidthBox.IsEnabled = numbers && LeadingZeroCheck.IsChecked == true;

        if (!numbers)
        {
            FromBox.Text = "a";
            ToBox.Text = "z";
        }
        else if (!long.TryParse(FromBox.Text, out _))
        {
            FromBox.Text = "1";
            ToBox.Text = "10";
        }

        UpdatePattern();
    }

    private void Links_Changed(object sender, TextChangedEventArgs e) => UpdateLinks();

    private void UpdateLinks()
    {
        if (!_ready) return;

        _resolved = BatchExpander.ParseLinks(LinksBox.Text);
        LinksCount.Text = _resolved.Count switch
        {
            0 => "No valid links yet",
            1 => "1 link",
            _ => $"{_resolved.Count} links",
        };

        ButtonStateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>An empty TextBlock still takes a line of height, which left the preview card
    /// looking like a large empty box whenever the pattern was incomplete.</summary>
    private static void SetLine(TextBlock line, string text)
    {
        line.Text = text;
        line.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private static Microsoft.UI.Xaml.Media.Brush Brush(string key) =>
        (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources[key];

    private void UpdatePattern()
    {
        if (!_ready || OnLinksTab)
            return;

        PadWidthBox.IsEnabled = WildcardBox.SelectedIndex == 0 && LeadingZeroCheck.IsChecked == true;

        try
        {
            var kind = WildcardBox.SelectedIndex == 1 ? BatchWildcard.Letters : BatchWildcard.Numbers;
            var pad = LeadingZeroCheck.IsChecked == true ? (int)PadWidthBox.Value : 0;

            _resolved = BatchExpander.Expand(PatternBox.Text.Trim(), kind,
                FromBox.Text.Trim(), ToBox.Text.Trim(), pad);

            PreviewCount.Text = _resolved.Count == 1 ? "1 file" : $"{_resolved.Count} files";
            PreviewCount.Foreground = Brush("StrettoAccentBrush");
            SetLine(PreviewFirst, _resolved[0]);
            SetLine(PreviewSecond, _resolved.Count > 2 ? "…" : "");
            SetLine(PreviewLast, _resolved.Count > 1 ? _resolved[^1] : "");

            ButtonStateChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (ArgumentException exception)
        {
            // The message is written for the user, so show it rather than a generic error.
            _resolved = [];
            PreviewCount.Text = exception.Message;
            PreviewCount.Foreground = Brush("StrettoPausedBrush");
            SetLine(PreviewFirst, "");
            SetLine(PreviewSecond, "");
            SetLine(PreviewLast, "");
            ButtonStateChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
