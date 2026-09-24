using System.Windows;
using System.Windows.Threading;
using TheOverseer.App.Desktop;
using TheOverseer.App.Diagnostics;

namespace TheOverseer.App;

public partial class App : Application
{
    private SingleInstanceGuard? _instanceGuard;
    private HudApplicationController? _controller;
    private bool _exiting;

    protected override async void OnStartup(StartupEventArgs e)
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

            if (arguments.VoiceSmoke)
            {
                // Finite voice smoke: synthetic tones plus an owned loopback endpoint.
                Shutdown(await Task.Run(() => VoiceSmokeRunner.Run(arguments)));
                return;
            }

            if (arguments.VoicePackCheck)
            {
                // Silent packaged-asset check of the configured pack: no audio,
                // no network, no settings writes.
                Shutdown(await Task.Run(() => VoicePackCheckRunner.Run(arguments)));
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
            // A HUD is already running: ask it to restore its window, then exit
            // quietly without touching its monitor, tray or session.
            ShowRequestSignal.TrySignal();
            Shutdown(0);
            return;
        }

        try
        {
            var (controller, _, _) = await HudComposition.CreateAsync(arguments, Dispatcher);
            _controller = controller;
            controller.ExitRequested += (_, _) => _ = ExitAsync();
            await controller.StartAsync(CancellationToken.None);
        }
        catch (Exception exception) when (exception is System.IO.IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            MessageBox.Show("OVERSEER could not start. Check access to its data directory.", "OVERSEER", MessageBoxButton.OK, MessageBoxImage.Warning);
            if (_controller is not null) await _controller.DisposeAsync();
            Shutdown(1);
        }
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        // Let Windows end the session; pump only while bounded cleanup completes.
        _exiting = true;
        if (_controller is not null)
        {
            var cleanup = _controller.DisposeAsync().AsTask();
            var frame = new DispatcherFrame();
            _ = cleanup.ContinueWith(_ => Dispatcher.BeginInvoke(new Action(() => frame.Continue = false)), TaskScheduler.Default);
            Dispatcher.PushFrame(frame);
        }
        base.OnSessionEnding(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _instanceGuard?.Dispose();
        _instanceGuard = null;
        base.OnExit(e);
    }

    private async Task ExitAsync()
    {
        if (_exiting) return;
        _exiting = true;
        if (_controller is null)
        {
            Shutdown(0);
            return;
        }

        var controller = _controller;
        await controller.ShutdownAsync().ConfigureAwait(true);
        await controller.DisposeAsync().ConfigureAwait(true);
        _controller = null;
        Shutdown(0);
    }
}
