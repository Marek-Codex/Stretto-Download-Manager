using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Stretto.App.Services;
using Stretto.Core;
using System.Collections.ObjectModel;

namespace Stretto.App.Dialogs;

public sealed partial class VideoDownloadDialog : UserControl, IButtonStateWindowForm
{
    private readonly HttpClient _http;
    private readonly ObservableCollection<MediaRow> _results = [];
    private bool _canDownload;

    public string PrimaryText => "Download";
    public string? SecondaryText => "Add paused";
    public string? CancelText => "Cancel";
    public bool AccentPrimary => true;
    public bool CanPrimary => _canDownload;
    public bool CanSecondary => _canDownload;
    public event EventHandler? ButtonStateChanged;


    public VideoDownloadDialog(HttpClient http, string downloadFolder)
    {
        InitializeComponent();
        _http = http;

        ResultsList.ItemsSource = _results;

        UpdateButtons();

        SaveInText.Text = $"Saving to {Path.Combine(downloadFolder, "Video")}";
    }

    /// <summary>What the user ticked, in the order it was found.</summary>
    public IReadOnlyList<MediaCandidate> Selected =>
        [.. _results.Where(r => ResultsList.SelectedItems.Contains(r)).Select(r => r.Effective)];

    /// <summary>Fills the address and searches as soon as the dialog opens.</summary>
    public void Prefill(string url)
    {
        AddressBox.Text = url;
        Loaded += async (_, _) => await FindAsync();
    }

    private void Address_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
            _ = FindAsync();
    }

    private void Find_Click(object sender, RoutedEventArgs e) => _ = FindAsync();

    private async Task FindAsync()
    {
        var address = AddressBox.Text.Trim();

        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            StatusText.Text = "That is not a valid http or https address.";
            return;
        }

        Busy.IsActive = true;
        Busy.Visibility = Visibility.Visible;
        FindButton.IsEnabled = false;
        StatusText.Text = "Looking for media…";
        _results.Clear();
        ShowResults(false);
        if (!_installing)
            InstallBar.Visibility = Visibility.Collapsed;

        try
        {
            var found = await MediaExtractor.ExtractAsync(_http, uri);

            foreach (var candidate in found)
                _results.Add(new MediaRow(candidate));


            // Nothing in the page itself. If yt-dlp is installed it knows the sites whose
            // players build their URLs at runtime, and it expands a playlist into entries,
            // which is the whole point of pasting one here.
            if (_results.Count == 0 && ToolBox.IsAvailable(Tool.YtDlp))
            {
                StatusText.Text = "Asking yt-dlp about this address…";

                foreach (var entry in await new YtDlp(ToolBox.Resolve(Tool.YtDlp)).ListAsync(address))
                {
                    _results.Add(new MediaRow(new MediaCandidate
                    {
                        Url = new Uri(entry.Url),
                        // "c" is the invariant TimeSpan format, which needs no escapes
                        // in the literal and so cannot be mangled by one.
                        Label = entry.Duration is { } length
                            ? $"{entry.Title}  ({length.ToString("c")})"
                            : entry.Title,
                        Kind = MediaKind.Site,
                        Source = uri.Host,
                    }));
                }
            }

            if (_results.Count == 0)
            {
                // Worth saying why rather than just "nothing": the usual causes are fixable.
                StatusText.Text = ToolBox.IsAvailable(Tool.YtDlp)
                    ? "No media found here, and yt-dlp did not recognise the address either. "
                    + "The browser extension can still catch it while the video plays."
                    : "No media found. The page may build its video URLs in script, or need a login.";

                if (!ToolBox.IsAvailable(Tool.YtDlp))
                    OfferInstall(Tool.YtDlp, "yt-dlp adds support for video sites and playlists. Install it and search again?", searchAgain: true);
                return;
            }

            ShowResults(true);
            StatusText.Text = _results.Count == 1 ? "Found 1 item." : $"Found {_results.Count} items.";

            ResultsList.SelectAll();
            SelectAllCheck.IsChecked = true;
        }
        catch (Exception exception)
        {
            StatusText.Text = $"Could not read that page: {exception.Message}";
        }
        finally
        {
            Busy.IsActive = false;
            Busy.Visibility = Visibility.Collapsed;
            FindButton.IsEnabled = true;
            UpdateButtons();
        }
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        if (SelectAllCheck.IsChecked == true)
            ResultsList.SelectAll();
        else
            ResultsList.SelectedItems.Clear();   // ListView has no DeselectAll
    }

    private async void Results_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Keep the header checkbox honest, including the indeterminate middle state.
        SelectAllCheck.IsChecked = ResultsList.SelectedItems.Count switch
        {
            0 => false,
            var n when n == _results.Count => true,
            _ => null,
        };

        UpdateButtons();

        // Qualities are read for rows as they are ticked. Select all on a long playlist is
        // the exception: two hundred entries would be two hundred lookups, so past a
        // handful they wait until a row is ticked on its own.
        if (e.AddedItems.Count > 8) return;

        foreach (var row in e.AddedItems.OfType<MediaRow>())
            await row.LoadFormatsAsync(_http);
    }

    private void ShowResults(bool any)
    {
        SelectAllCheck.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        EmptyResults.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
    }

    private void UpdateButtons()
    {
        var list = ResultsList;
        var any = list.SelectedItems.Count > 0;
        var selected = _results.Where(r => list.SelectedItems.Contains(r)).Select(r => r.Effective).ToList();
        Tool? missingTool = selected.Any(c => c.NeedsMuxing) && !ToolBox.IsAvailable(Tool.Ffmpeg)
            ? Tool.Ffmpeg
            : selected.Any(c => c.Kind == MediaKind.Site) && !ToolBox.IsAvailable(Tool.YtDlp)
                ? Tool.YtDlp
                : null;
        _canDownload = any && missingTool is null;
        if (missingTool is { } tool)
            OfferInstall(tool, $"This selection needs {ToolBox.DisplayName(tool)}, which Stretto can fetch for you.", searchAgain: false);
        else if (_results.Count > 0 && !_installing)
            InstallBar.Visibility = Visibility.Collapsed;
        ButtonStateChanged?.Invoke(this, EventArgs.Empty);
    }

    // ═══ Installing a missing helper in place ═══

    private Tool? _offered;
    private bool _searchAgain;
    private bool _installing;

    private void OfferInstall(Tool tool, string why, bool searchAgain)
    {
        if (_installing) return;

        _offered = tool;
        _searchAgain = searchAgain;
        InstallText.Text = why;
        InstallButton.Content = $"Install {ToolBox.DisplayName(tool)}";
        InstallButton.IsEnabled = true;
        InstallProgress.Visibility = Visibility.Collapsed;
        InstallBar.Visibility = Visibility.Visible;
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (_offered is not { } tool) return;

        _installing = true;
        InstallButton.IsEnabled = false;
        InstallProgress.Value = 0;
        InstallProgress.Visibility = Visibility.Visible;
        InstallText.Text = $"Downloading {ToolBox.DisplayName(tool)}…";

        try
        {
            // Created here, on the UI thread, so reports land back on it.
            await ToolBox.InstallAsync(tool, _http, new Progress<double>(v => InstallProgress.Value = v));

            if (!ToolBox.IsAvailable(tool))
                throw new InvalidOperationException("it downloaded, but will not run.");

            _installing = false;
            InstallBar.Visibility = Visibility.Collapsed;

            if (_searchAgain)
                await FindAsync();
            else
                UpdateButtons();
        }
        catch (Exception exception)
        {
            App.Log($"Installing {ToolBox.DisplayName(tool)} from the video dialog failed: {exception}");
            _installing = false;
            InstallProgress.Visibility = Visibility.Collapsed;
            InstallText.Text = $"Could not install {ToolBox.DisplayName(tool)}: {exception.Message} "
                + "Settings > Advanced > Optional components can also install it with winget.";
            InstallButton.Content = "Try again";
            InstallButton.IsEnabled = true;
        }
    }
}
