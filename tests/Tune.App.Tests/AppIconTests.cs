using System.Windows.Threading;
using H.NotifyIcon;
using Tune.App.Platform;

namespace Tune.App.Tests;

public class AppIconTests
{
    private static void RunSta(Action action)
    {
        Exception? error = null;
        var t = new Thread(() =>
        {
            try { action(); }
            catch (Exception e) { error = e; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error is not null) throw new Xunit.Sdk.XunitException("STA action failed: " + error);
    }

    [Fact]
    public void App_icon_is_accepted_by_the_tray_icon_library()
    {
        RunSta(() =>
        {
            using var trayImage = AppIcon.CreateTrayIcon(32);
            Assert.Equal(32, trayImage.Width);
            using var icon = new TaskbarIcon();
            Exception? deferred = null;
            Dispatcher.CurrentDispatcher.UnhandledException += (_, e) => { deferred = e.Exception; e.Handled = true; };
            icon.Icon = trayImage;
            icon.ForceCreate(enablesEfficiencyMode: false);
            // let any queued dispatcher work (where the library converts the image) run
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            if (deferred is not null) throw deferred;
        });
    }
}
