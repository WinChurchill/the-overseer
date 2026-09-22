using System.Windows;
using TheOverseer.App.Diagnostics;
using TheOverseer.UI;

namespace TheOverseer.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        if (MonitorDebugRunner.IsDebugInvocation(e.Args))
        {
            // The finite debug harness must not create the scaffold window or
            // any other WPF surface. StartupUri is intentionally not used so
            // this path can exit before any window exists.
            var exitCode = MonitorDebugRunner.Run(e.Args);
            Shutdown(exitCode);
            return;
        }

        base.OnStartup(e);

        // Equivalent to the previous StartupUri="/TheOverseer.UI;component/MainWindow.xaml"
        // normal startup; the window is only created outside debug mode.
        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }
}
