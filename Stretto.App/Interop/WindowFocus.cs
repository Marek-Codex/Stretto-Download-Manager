using Microsoft.UI.Xaml;
using System.Runtime.InteropServices;
using WinRT.Interop;

namespace Stretto.App.Interop;

/// <summary>
/// Brings a window to the front when the user is busy in another app.
///
/// Windows refuses SetForegroundWindow from a process that is not already in front, which
/// is exactly Stretto's situation when the browser hands over a download: the user just
/// clicked in the browser, so the browser owns the foreground. Plain Activate() then only
/// flashes the taskbar button and the New Download window opens behind the browser.
///
/// Briefly attaching to the foreground window's input thread is the documented way a
/// cooperating app gets past that lock. It only happens in answer to something the user
/// just did (a download they started), which is the case the lock is meant to allow.
/// </summary>
public static class WindowFocus
{
    public static void BringToFront(Window window) => BringToFront(WindowNative.GetWindowHandle(window));

    public static void BringToFront(nint hwnd)
    {
        if (hwnd == 0)
            return;

        try
        {
            if (IsIconic(hwnd))
                ShowWindow(hwnd, SwRestore);

            var foreground = GetForegroundWindow();
            if (foreground == hwnd)
                return;

            var foregroundThread = GetWindowThreadProcessId(foreground, out _);
            var ownThread = GetCurrentThreadId();
            var attached = foregroundThread != 0 && foregroundThread != ownThread
                && AttachThreadInput(ownThread, foregroundThread, true);

            try
            {
                // Topmost and back again raises it in the z-order even if focus is still
                // refused, so the window is at least visible above the browser.
                SetWindowPos(hwnd, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpShowWindow);
                SetWindowPos(hwnd, HwndNoTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpShowWindow);
                BringWindowToTop(hwnd);
                SetForegroundWindow(hwnd);
            }
            finally
            {
                if (attached)
                    AttachThreadInput(ownThread, foregroundThread, false);
            }

            // Still refused: at least make the taskbar button ask for attention.
            if (GetForegroundWindow() != hwnd)
            {
                var flash = new FlashInfo
                {
                    Size = (uint)Marshal.SizeOf<FlashInfo>(),
                    Window = hwnd,
                    Flags = FlashwAll | FlashwTimerNoFg,
                };
                FlashWindowEx(ref flash);
            }
        }
        catch (Exception exception)
        {
            App.Log($"Could not bring a window to the front: {exception.Message}");
        }
    }

    private const int SwRestore = 9;
    private static readonly nint HwndTopmost = -1;
    private static readonly nint HwndNoTopmost = -2;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpShowWindow = 0x0040;
    private const uint FlashwAll = 0x3;
    private const uint FlashwTimerNoFg = 0xC;

    [StructLayout(LayoutKind.Sequential)]
    private struct FlashInfo
    {
        public uint Size;
        public nint Window;
        public uint Flags;
        public uint Count;
        public uint Timeout;
    }

    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint attach, uint attachTo, bool doAttach);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(nint hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint hwnd, int command);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool FlashWindowEx(ref FlashInfo info);
}
