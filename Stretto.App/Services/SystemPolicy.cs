using Stretto.Core;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Stretto.App.Services;

/// <summary>
/// The Advanced settings that reach outside the app: keeping the machine awake while work is
/// in flight, and what to do once the queue drains.
/// </summary>
public sealed class SystemPolicy(AppSettings settings)
{
    [Flags]
    private enum ExecutionState : uint
    {
        SystemRequired = 0x00000001,
        Continuous = 0x80000000,
    }

    // DllImport rather than LibraryImport: the source generator emits unsafe code, and
    // enabling AllowUnsafeBlocks project-wide for one P/Invoke is a poor trade.
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint SetThreadExecutionState(ExecutionState flags);

    private bool _keepingAwake;
    private bool _wasBusy;
    private bool _completionFired;

    /// <summary>
    /// Called on the UI tick. Takes the count of transfers actually running, so the whole
    /// policy is a function of one number rather than scattered event handlers.
    /// </summary>
    public void Update(int active)
    {
        KeepAwake(settings.PreventSleep && active > 0);

        var busy = active > 0;

        // Fire on the falling edge only. Without the latch, a queue that drains and then
        // gets one more download would shut the machine down twice over.
        if (_wasBusy && !busy && !_completionFired)
        {
            _completionFired = true;
            OnEverythingFinished();
        }

        if (busy)
            _completionFired = false;

        _wasBusy = busy;
    }

    private void KeepAwake(bool keep)
    {
        if (keep == _keepingAwake)
            return;

        try
        {
            // CONTINUOUS makes the request stick until it is cleared; without it the request
            // applies to this call only and the machine sleeps anyway.
            _ = SetThreadExecutionState(keep
                ? ExecutionState.Continuous | ExecutionState.SystemRequired
                : ExecutionState.Continuous);

            _keepingAwake = keep;
        }
        catch (Exception exception)
        {
            App.Log($"Could not change sleep policy: {exception.Message}");
        }
    }

    private void OnEverythingFinished()
    {
        if (!string.IsNullOrWhiteSpace(settings.RunWhenDone))
        {
            try
            {
                Process.Start(new ProcessStartInfo(settings.RunWhenDone) { UseShellExecute = true });
            }
            catch (Exception exception)
            {
                App.Log($"Could not run '{settings.RunWhenDone}': {exception.Message}");
            }
        }

        if (!settings.ShutdownWhenDone)
            return;

        try
        {
            // Windows' own shutdown notice gives a visible countdown that the user can abort
            // with "shutdown /a", which is far safer than calling ExitWindowsEx immediately.
            Process.Start(new ProcessStartInfo("shutdown", "/s /t 60 /c \"Stretto finished downloading.\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            });
        }
        catch (Exception exception)
        {
            App.Log($"Could not schedule shutdown: {exception.Message}");
        }
    }

    /// <summary>Drops the keep-awake request so the machine is not left pinned after exit.</summary>
    public void Release() => KeepAwake(false);
}
