using Microsoft.UI.Dispatching;
using Microsoft.Windows.AppLifecycle;
using Microsoft.UI.Xaml;

namespace PcMonitor.Widget;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();

        // Single instance: a second launch just brings up the first one's dashboard.
        var instance = AppInstance.FindOrRegisterForKey("PcMonitor.Widget.Main");
        if (!instance.IsCurrent)
        {
            var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
            instance.RedirectActivationToAsync(activation).AsTask().GetAwaiter().GetResult();
            return 0;
        }

        Application.Start(callbackParams =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });
        return 0;
    }
}
