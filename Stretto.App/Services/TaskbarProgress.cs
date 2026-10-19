using Stretto.Core;
using System.Runtime.InteropServices;

namespace Stretto.App.Services;

/// <summary>
/// Aggregate transfer progress in the taskbar button, so a download can be watched from
/// another window. Best effort by design: any COM failure disables it silently rather
/// than ever surfacing chrome problems as app errors.
/// </summary>
public sealed class TaskbarProgress
{
    private enum State
    {
        NoProgress = 0,
        Indeterminate = 1,
        Normal = 2,
        Error = 4,
        Paused = 8,
    }

    // ITaskbarList3. Only HrInit and the two progress methods are ever called; the rest
    // exist purely to hold their vtable slots in order.
    [ComImport, Guid("ea1afb91-9e28-4b86-90e9-9e9f8a5eefaf")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITaskbarList3
    {
        void HrInit();
        void AddTab(IntPtr hwnd);
        void DeleteTab(IntPtr hwnd);
        void ActivateTab(IntPtr hwnd);
        void SetActiveAlt(IntPtr hwnd);
        void MarkFullscreenWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool fullscreen);
        void SetProgressValue(IntPtr hwnd, ulong completed, ulong total);
        void SetProgressState(IntPtr hwnd, State state);
    }

    [DllImport("ole32.dll", PreserveSig = false)]
    private static extern void CoCreateInstance(
        [In] ref Guid rclsid,
        [MarshalAs(UnmanagedType.IUnknown)] object? pUnkOuter,
        uint dwClsContext,
        [In] ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out ITaskbarList3 ppv);

    private static readonly Guid ClsidTaskbarList = new("56FDF344-FD6D-11D0-958A-006097C9A090");
    private static readonly Guid IidTaskbarList3 = new("ea1afb91-9e28-4b86-90e9-9e9f8a5eefaf");
    private const uint ClsctxInprocServer = 1;

    private ITaskbarList3? _taskbar;
    private bool _supported = true;
    private bool _initialized;
    private State _lastState = (State)(-1);
    private ulong _lastCompleted;
    private ulong _lastTotal;

    /// <summary>Called on the UI tick; pushes to the taskbar only when something changed.</summary>
    public void Update(nint hwnd, IEnumerable<DownloadItem> items)
    {
        if (!_supported || hwnd == 0)
            return;

        try
        {
            if (_taskbar is null)
            {
                var clsid = ClsidTaskbarList;
                var iid = IidTaskbarList3;
                CoCreateInstance(ref clsid, null, ClsctxInprocServer, ref iid, out var taskbar);
                _taskbar = taskbar;
            }

            if (!_initialized)
            {
                _taskbar.HrInit();
                _initialized = true;
            }

            var running = items.Where(i => i.IsRunning).ToList();

            State state;
            ulong completed = 0;
            ulong total = 0;

            if (running.Count == 0)
            {
                state = State.NoProgress;
            }
            else if (running.All(i => i.TotalBytes is > 0))
            {
                // Streams have no total, so any of those makes the whole thing indeterminate.
                state = State.Normal;
                completed = (ulong)running.Sum(i => i.BytesCompleted);
                total = (ulong)running.Sum(i => i.TotalBytes!.Value);
            }
            else
            {
                state = State.Indeterminate;
            }

            // Progress ticks several times a second; the bar cannot show that finely anyway.
            if (state == _lastState && (state != State.Normal
                || (completed == _lastCompleted && total == _lastTotal)
                || (completed > _lastCompleted && completed - _lastCompleted < 262144 && total == _lastTotal)))
                return;

            if (state != _lastState)
                _taskbar.SetProgressState(hwnd, state);

            if (state == State.Normal && total > 0)
                _taskbar.SetProgressValue(hwnd, Math.Min(completed, total), total);

            _lastState = state;
            _lastCompleted = completed;
            _lastTotal = total;
        }
        catch (Exception exception) when (exception is COMException or InvalidCastException or UnauthorizedAccessException)
        {
            // No taskbar, old Windows, or COM declined: run without it from here on.
            _supported = false;
            App.Log($"Taskbar progress unavailable: {exception.Message}");
        }
    }
}
