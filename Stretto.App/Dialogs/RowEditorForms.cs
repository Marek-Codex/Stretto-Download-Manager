using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Stretto.App.Services;

namespace Stretto.App.Dialogs;

/// <summary>
/// Edits one download category in a window of its own.
///
/// This used to be a panel that unfolded under the categories table, pushing the rest of
/// the page down while you typed. The comment beside it said a dialog cannot open another
/// dialog in WinUI, which was true of ContentDialog but not of the windows Stretto uses now.
/// Works on a copy: Cancel really does leave the row alone.
/// </summary>
public sealed class CategoryEditorForm : UserControl, IButtonStateWindowForm
{
    private readonly TextBox _name = new() { Header = "Name" };
    private readonly TextBox _types = new() { Header = "File types", PlaceholderText = "mp4, mkv, webm" };
    private readonly TextBox _folder = new() { Header = "Folder", PlaceholderText = "A subfolder named after the category" };

    public string PrimaryText => "Save";
    public string? SecondaryText => null;
    public string? CancelText => "Cancel";
    public bool AccentPrimary => true;
    public bool CanPrimary => _name.Text.Trim().Length > 0;
    public bool CanSecondary => false;
    public event EventHandler? ButtonStateChanged;

    public CategoryEditorForm(CategoryRule rule, nint owner)
    {
        _name.Text = rule.Name;
        _types.Text = rule.ExtensionsText;
        _folder.Text = rule.Folder;
        _name.TextChanged += (_, _) => ButtonStateChanged?.Invoke(this, EventArgs.Empty);
        _name.Loaded += (_, _) => { _name.Focus(FocusState.Programmatic); _name.SelectAll(); };

        var browse = FormKit.PushButton("Browse", async (_, _) =>
        {
            var picker = new Windows.Storage.Pickers.FolderPicker();
            picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, owner);

            try
            {
                if (await picker.PickSingleFolderAsync() is { } picked)
                    _folder.Text = picked.Path;
            }
            catch (Exception exception)
            {
                App.Log($"Folder picker failed: {exception.Message}");
            }
        });
        browse.VerticalAlignment = VerticalAlignment.Bottom;

        var panel = new StackPanel { Spacing = 12, Width = 400 };
        panel.Children.Add(_name);
        panel.Children.Add(_types);
        panel.Children.Add(FormKit.Hint("Separated by commas or spaces. A leading dot is fine."));
        panel.Children.Add(FormKit.WithButton(_folder, browse));
        Content = panel;
    }

    public void ApplyTo(CategoryRule rule)
    {
        rule.Name = _name.Text.Trim();
        rule.ExtensionsText = _types.Text;
        rule.Folder = _folder.Text.Trim();
    }
}

/// <summary>Edits one saved site login, in its own window for the same reason as above.</summary>
public sealed class LoginEditorForm : UserControl, IButtonStateWindowForm
{
    private readonly TextBox _host = new() { Header = "Website or server", PlaceholderText = "files.example.com" };
    private readonly TextBox _user = new() { Header = "User name" };
    private readonly PasswordBox _password = new() { Header = "Password" };

    public string PrimaryText => "Save";
    public string? SecondaryText => null;
    public string? CancelText => "Cancel";
    public bool AccentPrimary => true;
    public bool CanPrimary => _host.Text.Trim().Length > 0;
    public bool CanSecondary => false;
    public event EventHandler? ButtonStateChanged;

    public LoginEditorForm(LoginRow row)
    {
        _host.Text = row.Host;
        _user.Text = row.Username;
        _password.Password = row.Password;
        _host.TextChanged += (_, _) => ButtonStateChanged?.Invoke(this, EventArgs.Empty);
        _host.Loaded += (_, _) => _host.Focus(FocusState.Programmatic);

        var credentials = new Grid { ColumnSpacing = 10 };
        credentials.ColumnDefinitions.Add(new ColumnDefinition());
        credentials.ColumnDefinitions.Add(new ColumnDefinition());
        credentials.Children.Add(_user);
        Grid.SetColumn(_password, 1);
        credentials.Children.Add(_password);

        var panel = new StackPanel { Spacing = 12, Width = 400 };
        panel.Children.Add(_host);
        panel.Children.Add(credentials);
        panel.Children.Add(FormKit.Hint("Encrypted for your Windows account, and only ever sent to this host."));
        Content = panel;
    }

    public void ApplyTo(LoginRow row)
    {
        row.Host = _host.Text.Trim();
        row.Username = _user.Text.Trim();
        row.Password = _password.Password;
    }
}
