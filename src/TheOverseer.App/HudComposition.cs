using System.IO;
using System.Windows.Threading;
using TheOverseer.App.Desktop;
using TheOverseer.App.Diagnostics;
using TheOverseer.App.Monitoring;
using TheOverseer.Core.Configuration;
using TheOverseer.Core.Diagnostics;
using TheOverseer.Core.Monitoring;
using TheOverseer.UI;

namespace TheOverseer.App;

/// <summary>
/// Builds the normal-mode object graph. App owns platform services; the
/// controller owns lifetime; the UI owns presentation only.
/// </summary>
internal static class HudComposition
{
    public static (HudApplicationController Controller, ConfigLoadResult Config, HudStateLoadResult State) Create(
        StartupArguments arguments,
        Dispatcher dispatcher)
    {
        var dataDirectory = DataDirectoryResolver.Resolve(arguments.DataDirectory);
        Directory.CreateDirectory(dataDirectory);

        var configPath = Path.Combine(dataDirectory, MonitorConfigStore.FileName);
        var statePath = Path.Combine(dataDirectory, HudStateStore.FileName);
        var configResult = MonitorConfigStore.Load(configPath);
        var stateResult = HudStateStore.Load(statePath);

        var codexHome = arguments.CodexHome ?? configResult.Config.CodexHome ?? MonitorProfiles.ResolveCodexHome(null);
        var profiles = MonitorProfiles.Create(codexHome, configResult.Config.AdditionalRoots);
        var monitorOptions = MonitorConfigStore.ToOptions(configResult.Config);

        IMonitorLog log = NullMonitorLog.Instance;
        if (configResult.Config.Logging.Enabled)
        {
            var fileLog = LocalLog.TryCreate(
                Path.Combine(dataDirectory, "logs", "overseer.log"),
                configResult.Config.Logging.MaxBytes,
                configResult.Config.Logging.Level);
            if (fileLog is not null)
            {
                log = fileLog;
            }
        }

        IRuntimeActivitySource activitySource = configResult.Config.RuntimeActivitySourceEnabled
            ? new WindowsSessionActivitySource()
            : NullRuntimeActivitySource.Instance;

        var monitor = new CodexMonitor(monitorOptions, profiles, activitySource, log, TimeProvider.System);
        var executablePath = Environment.ProcessPath ?? string.Empty;
        var startup = new StartupRegistrationAdapter(new StartupRegistration(new WindowsStartupRegistry(), executablePath));
        var navigator = new TaskNavigatorAdapter(new CodexTaskNavigator(new ShellTaskLaunchBoundary()));

        var dependencies = new HudControllerDependencies(
            new CodexMonitorSource(monitor),
            new WpfUiDispatcher(dispatcher),
            new WindowsWindowPlacementService(),
            navigator,
            startup,
            new WpfClipboardWriter(),
            log,
            configResult.Config,
            stateResult.State,
            new HudControllerPaths(dataDirectory, configPath, statePath),
            () => new HudWindow(),
            TimeProvider.System,
            arguments.StartInTray,
            !arguments.NoTray);

        return (new HudApplicationController(dependencies), configResult, stateResult);
    }
}