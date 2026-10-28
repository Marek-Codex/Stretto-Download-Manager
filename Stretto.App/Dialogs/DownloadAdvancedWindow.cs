using Stretto.Core;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Stretto.App.Services;
using System.Diagnostics;

namespace Stretto.App.Dialogs;

/// <summary>Per-download authentication plus a per-download proxy override.</summary>
public sealed class DownloadAdvancedWindow : Window
{
    private readonly TextBox _user = new() { MinWidth = 300 };
    private readonly PasswordBox _password = new() { MinWidth = 300 };
    private readonly CheckBox _remember = new() { Content = "Remember this login for the website" };
    private readonly ComboBox _proxyMode = new();
    private readonly ComboBox _proxyProtocol = new();
    private readonly TextBox _proxyHost = new() { Header = "Proxy host" };
    private readonly NumberBox _proxyPort = new() { Header = "Port", Minimum = 1, Maximum = 65535, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Hidden };
    private readonly TextBox _proxyUser = new() { Header = "Proxy user name" };
    private readonly PasswordBox _proxyPassword = new() { Header = "Password" };
    private readonly StackPanel _manualProxy = new() { Spacing = 8 };
    private readonly StackPanel _authenticationPanel = new() { Spacing = 8 };
    private readonly StackPanel _proxyPanel = new() { Spacing = 8, Visibility = Visibility.Collapsed };
    private readonly SelectorBarItem _authenticationTab = new() { Text = "Login", IsSelected = true };
    private readonly SelectorBarItem _proxyTab = new() { Text = "Proxy" };
    private readonly TaskCompletionSource<bool> _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _accepted;

    public string Username { get => _user.Text; set => _user.Text = value; }
    public string Password { get => _password.Password; set => _password.Password = value; }
    public bool RememberLogin { get => _remember.IsChecked == true; set => _remember.IsChecked = value; }

    /// <summary>Null means "use the global settings". Otherwise the override for this download only.</summary>
    public ProxyMode? ProxyModeOverride => _proxyMode.SelectedIndex switch
    {
        1 => ProxyMode.None,
        2 => ProxyMode.Manual,
        _ => null,
    };
    public ProxyProtocol? ProxyProtocolOverride =>
        _proxyMode.SelectedIndex == 2 ? (ProxyProtocol)Math.Max(0, _proxyProtocol.SelectedIndex) : null;

    public string ProxyHost => _proxyHost.Text.Trim();
    public int ProxyPort => double.IsNaN(_proxyPort.Value) ? 0 : (int)_proxyPort.Value;
    public string ProxyUsername => _proxyUser.Text.Trim();
    public string ProxyPassword => _proxyPassword.Password;
    public Task<bool> ClosedTask => _closed.Task;

    public DownloadAdvancedWindow(
        AppSettings settings,
        string username,
        string password,
        bool rememberLogin,
        ProxyMode? proxyOverride = null,
        ProxyProtocol? proxyProtocolOverride = null,
        string proxyHost = "",
        int proxyPort = 0,
        string proxyUsername = "",
        string proxyPassword = "")
    {
        var titleBar = WindowAppearance.ConfigureUtilityWindow(this, "Download options");

        _user.Header = "User name";
        _password.Header = "Password";
        _user.Text = username;
        _password.Password = password;
        _remember.IsChecked = rememberLogin;
        _proxyMode.Items.Add($"Use global settings ({DescribeGlobal(settings)})");
        _proxyMode.Items.Add("No proxy for this download");
        _proxyMode.Items.Add("Manual proxy for this download");
        _proxyProtocol.Items.Add("HTTP proxy");
        _proxyProtocol.Items.Add("SOCKS5 proxy");
        _proxyProtocol.SelectedIndex = (int)(proxyProtocolOverride ?? settings.ProxyProtocol);
        _proxyMode.SelectedIndex = proxyOverride switch
        {
            ProxyMode.None => 1,
            ProxyMode.Manual => 2,
            _ => 0,
        };
        _proxyHost.Text = proxyHost;
        _proxyPort.Value = proxyPort;
        _proxyUser.Text = proxyUsername;
        _proxyPassword.Password = proxyPassword;
        _proxyMode.SelectionChanged += (_, _) => UpdateProxyFields();

        var proxyEndpoint = new Grid { ColumnSpacing = 10 };
        proxyEndpoint.ColumnDefinitions.Add(new ColumnDefinition());
        proxyEndpoint.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(116) });
        _proxyPort.HorizontalAlignment = HorizontalAlignment.Stretch;
        Grid.SetColumn(_proxyPort, 1);
        proxyEndpoint.Children.Add(_proxyHost);
        proxyEndpoint.Children.Add(_proxyPort);
        var proxyLogin = new Grid { ColumnSpacing = 10 };
        proxyLogin.ColumnDefinitions.Add(new ColumnDefinition());
        proxyLogin.ColumnDefinitions.Add(new ColumnDefinition());
        Grid.SetColumn(_proxyPassword, 1);
        proxyLogin.Children.Add(_proxyUser);
        proxyLogin.Children.Add(_proxyPassword);
        _manualProxy.Children.Add(proxyEndpoint);
        _manualProxy.Children.Add(proxyLogin);

        _authenticationPanel.Children.Add(_user);
        _authenticationPanel.Children.Add(_password);
        _authenticationPanel.Children.Add(_remember);
        _authenticationPanel.Children.Add(new TextBlock
        {
            Text = "Credentials are sent only when the site asks for them (Basic, Digest, or supported Windows authentication). Remembered passwords are encrypted for your Windows account.",
            FontSize = 11.5, Opacity = 0.68, TextWrapping = TextWrapping.Wrap
        });

        var proxyTop = new StackPanel { Spacing = 6 };
        _proxyMode.HorizontalAlignment = HorizontalAlignment.Stretch;
        proxyTop.Children.Add(_proxyMode);
        proxyTop.Children.Add(_proxyProtocol);
        proxyTop.Children.Add(new TextBlock
        {
            Text = "This download only. SOCKS5 can resolve the destination host through the proxy. The global proxy stays in Settings, Network.",
            FontSize = 11.5, Opacity = 0.68, TextWrapping = TextWrapping.Wrap,
        });
        var systemSettings = new HyperlinkButton { Content = "Windows settings", FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center };
        systemSettings.HorizontalAlignment = HorizontalAlignment.Left;
        systemSettings.Click += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo("ms-settings:network-proxy") { UseShellExecute = true }); }
            catch (Exception exception) { App.Log($"Could not open proxy settings: {exception.Message}"); }
        };
        _proxyPanel.Children.Add(proxyTop);
        _proxyPanel.Children.Add(_manualProxy);
        proxyTop.Children.Add(systemSettings);

        // The same segmented bar as Batch and Queues, rather than two hand-coloured buttons.
        var selector = new SelectorBar { Margin = new Thickness(-12, 0, 0, 0) };
        selector.Items.Add(_authenticationTab);
        selector.Items.Add(_proxyTab);
        selector.SelectionChanged += (_, _) => SelectPane(authentication: selector.SelectedItem == _authenticationTab);
        var selectorSurface = selector;

        var content = new StackPanel { Spacing = 10, Width = 420, Padding = new Thickness(16, 4, 16, 14) };
        content.Children.Add(selectorSurface);
        content.Children.Add(_authenticationPanel);
        content.Children.Add(_proxyPanel);

        var cancel = new Button { Content = "Cancel", MinWidth = 80 };
        cancel.Click += (_, _) => CloseWithResult(accepted: false);
        var ok = new Button { Content = "OK", MinWidth = 80, Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        ok.Click += (_, _) => CloseWithResult(accepted: true);
        var footer = WindowAppearance.Footer([ok, cancel]);

        var shell = new Grid { Background = (Brush)Application.Current.Resources["StrettoWindowBrush"] };
        shell.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        shell.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        shell.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        shell.Children.Add(titleBar);
        Grid.SetRow(content, 1);
        shell.Children.Add(content);
        Grid.SetRow(footer, 2);
        shell.Children.Add(footer);
        Content = shell.Compact();
        Closed += (_, _) => _closed.TrySetResult(_accepted);
        UpdateProxyFields();
        SelectPane(authentication: true);
        AppWindow.Resize(new Windows.Graphics.SizeInt32(500, 520));
        AppWindow.Move(new Windows.Graphics.PointInt32(-20000, -20000));
        _ = WindowAppearance.FitToContentAsync(this, shell, 480, double.PositiveInfinity);
    }

    private static string DescribeGlobal(AppSettings settings) => settings.ProxyMode switch
    {
        ProxyMode.None => "direct",
        ProxyMode.Manual when string.IsNullOrWhiteSpace(settings.ProxyHost) => "manual, no host",
        ProxyMode.Manual => $"{settings.ProxyProtocol} via {settings.ProxyHost}:{settings.ProxyPort}",
        _ => "system",
    };

    private void UpdateProxyFields()
    {
        var enabled = _proxyMode.SelectedIndex == 2;
        _proxyProtocol.IsEnabled = enabled;
        foreach (var field in _manualProxy.Children)
            if (field is Control control) control.IsEnabled = enabled;
        _manualProxy.Opacity = enabled ? 1 : 0.5;
    }

    private void SelectPane(bool authentication)
    {
        _authenticationPanel.Visibility = authentication ? Visibility.Visible : Visibility.Collapsed;
        _proxyPanel.Visibility = authentication ? Visibility.Collapsed : Visibility.Visible;
    }

    private void CloseWithResult(bool accepted)
    {
        _accepted = accepted;
        Close();
    }

}
