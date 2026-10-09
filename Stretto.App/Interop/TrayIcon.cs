using System.Runtime.InteropServices;

namespace Stretto.App.Interop;

/// <summary>
/// A notification area icon.
///
/// Hand rolled because WinUI 3 has no tray support of its own and the packages that add it
/// are heavier than the hundred lines of shell interop they wrap.
///
/// The icon needs a window to send its callbacks to, but it must not be the app's window:
/// the whole point is to keep working while that one is hidden. So this owns a message-only
/// window, which never draws anything and exists purely as an address.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private const int WmApp = 0x8000;
    private const int CallbackMessage = WmApp + 1;

    private const int WmLeftButtonUp = 0x0202;
    private const int WmLeftDoubleClick = 0x0203;
    private const int WmRightButtonUp = 0x0205;
    private const int WmDestroy = 0x0002;

    private const int NimAdd = 0x0000;
    private const int NimModify = 0x0001;
    private const int NimDelete = 0x0002;

    private const int NifMessage = 0x0001;
    private const int NifIcon = 0x0002;
    private const int NifTip = 0x0004;

    private const int ImageIcon = 1;
    private const int LoadFromFile = 0x0010;
    private const int DefaultSize = 0x0040;

    private const int MfString = 0x0000;
    private const int MfSeparator = 0x0800;

    private const int TpmRightButton = 0x0002;
    private const int TpmReturnCmd = 0x0100;

    private const nint HwndMessage = -3;

    // Held as a field so the delegate is not collected while the OS still has the pointer.
    private readonly WndProc _wndProc;
    private readonly List<MenuEntry> _menu = [];

    /// <summary>One right-click entry. Run null is a separator; Children makes a submenu;
    /// IsChecked, when given, is asked each time the menu opens.</summary>
    private sealed record MenuEntry(string Text, Action? Run, Func<bool>? IsChecked = null, List<MenuEntry>? Children = null);

    private nint _window;
    private nint _icon;
    private bool _added;

    /// <summary>Raised when the icon is clicked, which should bring the app back.</summary>
    public event Action? Activated;

    public TrayIcon(string iconPath, string tooltip)
    {
        _wndProc = HandleMessage;

        var className = "StrettoTray_" + Guid.NewGuid().ToString("n");

        var windowClass = new WndClass
        {
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = GetModuleHandle(null),
            lpszClassName = className,
        };

        if (RegisterClass(ref windowClass) == 0)
            throw new InvalidOperationException("Could not register the tray window class.");

        _window = CreateWindowEx(0, className, string.Empty, 0, 0, 0, 0, 0,
            HwndMessage, nint.Zero, windowClass.hInstance, nint.Zero);

        if (_window == nint.Zero)
            throw new InvalidOperationException("Could not create the tray message window.");

        // LoadImage from the file on disk rather than an embedded resource, because an
        // unpackaged app already ships the .ico next to the executable.
        _icon = LoadImage(nint.Zero, iconPath, ImageIcon, 0, 0, LoadFromFile | DefaultSize);

        var data = NewData();
        data.uFlags = NifMessage | NifIcon | NifTip;
        data.uCallbackMessage = CallbackMessage;
        data.hIcon = _icon;
        data.szTip = tooltip;

        _added = Shell_NotifyIcon(NimAdd, ref data);

        // The shell can refuse without throwing, typically when Explorer is still starting.
        // Silently ending up with no icon is the confusing outcome, so say so.
        if (!_added)
            throw new InvalidOperationException("The shell refused the notification icon.");
    }

    /// <summary>Adds a right-click menu entry. Order of calls is the order shown.</summary>
    public void AddMenuItem(string text, Action run) => _menu.Add(new(text, run));

    /// <summary>A separator in the right-click menu.</summary>
    public void AddSeparator() => _menu.Add(new("", null));

    /// <summary>A submenu of choices, each ticked when its IsChecked says so.</summary>
    public void AddSubmenu(string text, IEnumerable<(string Text, Func<bool> IsChecked, Action Run)> choices) =>
        _menu.Add(new(text, null, null, [.. choices.Select(c => new MenuEntry(c.Text, c.Run, c.IsChecked))]));

    /// <summary>Changes the hover text, so it can carry the current transfer state.</summary>
    public void SetTooltip(string tooltip)
    {
        if (!_added) return;

        var data = NewData();
        data.uFlags = NifTip;
        data.szTip = tooltip;

        Shell_NotifyIcon(NimModify, ref data);
    }

    private nint HandleMessage(nint window, uint message, nint wParam, nint lParam)
    {
        switch (message)
        {
            case CallbackMessage:
                switch ((int)lParam)
                {
                    case WmLeftButtonUp:
                    case WmLeftDoubleClick:
                        Activated?.Invoke();
                        break;

                    case WmRightButtonUp:
                        ShowMenu();
                        break;
                }

                return nint.Zero;

            case WmDestroy:
                return nint.Zero;
        }

        return DefWindowProc(window, message, wParam, lParam);
    }

    private void ShowMenu()
    {
        if (_menu.Count == 0)
            return;

        var menu = CreatePopupMenu();
        if (menu == nint.Zero)
            return;

        // Ids are handed out as the menu is built, so submenu items can be found again
        // from the single number TrackPopupMenuEx returns.
        _pending.Clear();

        try
        {
            Fill(menu, _menu);

            GetCursorPos(out var point);

            // Without this the menu does not close when you click elsewhere: the shell
            // expects the owner to be foreground while a tray menu is up.
            SetForegroundWindow(_window);

            var chosen = TrackPopupMenuEx(menu, TpmReturnCmd | TpmRightButton,
                point.X, point.Y, _window, nint.Zero);

            if (chosen > 0 && chosen <= _pending.Count)
                _pending[chosen - 1]();
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    // Submenu handles are owned by their parent: DestroyMenu on the root frees them too.
    private readonly List<Action> _pending = [];

    private void Fill(nint menu, List<MenuEntry> entries)
    {
        foreach (var entry in entries)
        {
            if (entry.Children is { } children)
            {
                var sub = CreatePopupMenu();
                Fill(sub, children);
                AppendMenu(menu, MfString | MfPopup, sub, entry.Text);
            }
            else if (entry.Run is null)
            {
                AppendMenu(menu, MfSeparator, 0, null);
            }
            else
            {
                var flags = MfString | (entry.IsChecked?.Invoke() == true ? MfChecked : 0);
                _pending.Add(entry.Run);
                AppendMenu(menu, flags, _pending.Count, entry.Text);
            }
        }
    }

    private NotifyIconData NewData() => new()
    {
        cbSize = Marshal.SizeOf<NotifyIconData>(),
        hWnd = _window,
        uID = 1,
        szTip = "",
        szInfo = "",
        szInfoTitle = "",
    };

    public void Dispose()
    {
        if (_added)
        {
            var data = NewData();
            Shell_NotifyIcon(NimDelete, ref data);
            _added = false;
        }

        if (_icon != nint.Zero)
        {
            DestroyIcon(_icon);
            _icon = nint.Zero;
        }

        if (_window != nint.Zero)
        {
            DestroyWindow(_window);
            _window = nint.Zero;
        }
    }

    // ═══ Native ═══

    private delegate nint WndProc(nint window, uint message, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClass
    {
        public uint style;
        public nint lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public nint hInstance;
        public nint hIcon;
        public nint hCursor;
        public nint hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public int cbSize;
        public nint hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public nint hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public int dwState;
        public int dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public int uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public int dwInfoFlags;
        public Guid guidItem;
        public nint hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(int message, ref NotifyIconData data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClass(ref WndClass windowClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowEx(int exStyle, string className, string windowName,
        int style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);

    [DllImport("user32.dll")]
    private static extern nint DefWindowProc(nint window, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(nint window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint LoadImage(nint instance, string name, int type, int cx, int cy, int load);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(nint icon);

    [DllImport("user32.dll")]
    private static extern nint CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(nint menu, int flags, nint id, string? item);

    private const int MfChecked = 0x0008;
    private const int MfPopup = 0x0010;

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(nint menu);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenuEx(nint menu, int flags, int x, int y, nint window, nint parameters);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint window);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandle(string? name);
}
