using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace Stretto.App;

internal static class Program
{
    // Single instance matters here beyond tidiness: the browser extension will hand off
    // downloads to one listener, and a second process would fight it for the port.
    private const string InstanceKey = "MarekCodex.Stretto.Singleton.v1";

    [STAThread]
    private static int Main()
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();

        var primary = AppInstance.FindOrRegisterForKey(InstanceKey);
        if (!primary.IsCurrent)
            return 0;

        Application.Start(initialization =>
        {
            DispatcherQueueSynchronizationContext context = new(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
            GC.KeepAlive(initialization);
        });

        return Environment.ExitCode;
    }
}
