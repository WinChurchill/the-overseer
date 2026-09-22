using System.Windows;
using System.Windows.Threading;
using TheOverseer.App.Desktop;
using TheOverseer.App.Diagnostics;

namespace TheOverseer.App;

public partial class App : Application
{
    private SingleInstanceGuard? _instanceGuard;
    private HudApplicationController? _controller;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (MonitorDebugRunner.IsDebugInvocation(e.Args))
        {
            // The finite debug harness owns the process: no window, no tray.
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var exitCode = MonitorDebugRunner.Run(e.Args);
            Shutdown(exitCode);
            return;
        }

        if (!StartupArguments.TryParse(e.Args, out var arguments, out var error))
        {
            MessageBox.Show(StartupArguments.Usage, "OVERSEER", MessageBoxButton.OK, MessageBoxImage.Warning);
            _ = error;
            Shutdown(1);
            return;
        }

        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        if (arguments.IsFixtureInvocation)
        {
            // Diagnostics fixtures are explicit, isolated and finite, and never
            // take the single-instance slot or register startup.
            ConsoleHost.Ensure();
            if (arguments.HudStartupCheck)
            {
                Shutdown(HudFixtureRunner.RunStartupCheck());
                return;
            }

            if (arguments.HudFixture)
            {
                HudFixtureRunner.StartFixture(arguments, Dispatcher);
            }
            else
            {
                HudFixtureRunner.StartSmoke(arguments, Dispatcher);
            }

            return;
        }

        _instanceGuard = SingleInstanceGuard.TryAcquire();
        if (!_instanceGuard.IsOwner)
        {
            // A HUD is already running: leave its monitor and tray alone.
            Shutdown(0);
            return;
        }

        var (controller, _, _) = HudComposition.Create(arguments, Dispatcher);
        _controller = controller;
        controller.ExitRequested += (_, _) => _ = ExitAsync();
        _ = controller.StartAsync(CancellationToken.None);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _instanceGuard?.Dispose();
        _instanceGuard = null;
        base.OnExit(e);
    }

    private async Task ExitAsync()
    {
        if (_controller is null)
        {
            Shutdown(0);
            return;
        }

        var controller = _controller;
        _controller = null;
        await controller.ShutdownAsync().ConfigureAwait(true);
        await controller.DisposeAsync().ConfigureAwait(true);
        Shutdown(0);
    }
}
