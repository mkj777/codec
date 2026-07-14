using System;
using System.Threading;
using Codec.Services.Storage;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Velopack;
using WinRT;

namespace Codec;

public static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        VelopackApp.Build()
            .OnBeforeUninstallFastCallback(_ => AppResetService.DeleteAppData())
            .Run();

        ComWrappersSupport.InitializeComWrappers();
        Application.Start((p) =>
        {
            var context = new DispatcherQueueSynchronizationContext(
                DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });
    }
}
