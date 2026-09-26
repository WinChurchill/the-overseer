using System.IO;
using System.Windows.Threading;
using TheOverseer.App.Desktop;
using TheOverseer.App.Diagnostics;
using TheOverseer.App.Monitoring;
using TheOverseer.Core.Configuration;
using TheOverseer.Core.Diagnostics;
using TheOverseer.Core.History;
using TheOverseer.Core.Models;
using TheOverseer.Core.Monitoring;
using TheOverseer.Core.Voice;
using TheOverseer.App.Voice;
using TheOverseer.UI;

namespace TheOverseer.App;

/// <summary>
/// Builds the normal-mode object graph. App owns platform services; the
/// controller owns lifetime; the UI owns presentation only.
/// </summary>
internal static class HudComposition
{
    public static async Task<(HudApplicationController Controller, ConfigLoadResult Config, HudStateLoadResult State)> CreateAsync(
        StartupArguments arguments,
        Dispatcher dispatcher)
    {
        var prepared = await Task.Run(() => Prepare(arguments, dispatcher));
        return (new HudApplicationController(prepared.Dependencies), prepared.Config, prepared.State);
    }

    private static (HudControllerDependencies Dependencies, ConfigLoadResult Config, HudStateLoadResult State) Prepare(
        StartupArguments arguments, Dispatcher dispatcher)
    {
        var dataDirectory = DataDirectoryResolver.Resolve(arguments.DataDirectory);
        Directory.CreateDirectory(dataDirectory);

        var configPath = Path.Combine(dataDirectory, MonitorConfigStore.FileName);
        var statePath = Path.Combine(dataDirectory, HudStateStore.FileName);
        var configResult = MonitorConfigStore.Load(configPath);
        var stateResult = HudStateStore.Load(statePath);
        // A brand-new data directory has no stored preference to preserve, so it
        // takes the agreed offline baseline default (still muted). An existing
        // config.json is used exactly as written: a missing "provider" key keeps the
        // current pack behaviour, and explicit provider/mute/endpoint choices and
        // assigned recordings are never reset.
        var config = configResult.Config with
        {
            Voice = VoiceSettings.Effective(configResult.Status, configResult.Config.Voice),
        };

        var codexHome = arguments.CodexHome ?? config.CodexHome ?? MonitorProfiles.ResolveCodexHome(null);
        var profiles = MonitorProfiles.Create(codexHome, config.AdditionalRoots);
        var monitorOptions = MonitorConfigStore.ToOptions(config);

        IMonitorLog log = NullMonitorLog.Instance;
        if (config.Logging.Enabled)
        {
            var fileLog = LocalLog.TryCreate(
                Path.Combine(dataDirectory, "logs", "overseer.log"),
                config.Logging.MaxBytes,
                config.Logging.Level);
            if (fileLog is not null)
            {
                log = fileLog;
            }
        }

        IRuntimeActivitySource activitySource = config.RuntimeActivitySourceEnabled
            ? new WindowsSessionActivitySource()
            : NullRuntimeActivitySource.Instance;

        // Project attribution is resolved by the monitor from each session's
        // retained workspace root; the resolver is bounded and cached.
        var projects = new ProjectResolver(config.Project);
        var monitor = new CodexMonitor(monitorOptions, profiles, activitySource, log, TimeProvider.System, projects);

        // One HUD-facing monitor over every enabled source: Codex plus each bridge
        // spool that is actually installed. A source failure stays local.
        var adapters = new List<IAgentMonitorAdapter>
        {
            new CodexMonitorAdapter(new CodexMonitorSource(monitor), new SourceInstance(HarnessIds.Codex, "codex", "Codex")),
        };
        var discovery = HarnessSources.DiscoverDetailed(
            dataDirectory,
            config.DisabledHarnessSources,
            new HarnessDiscoveryOptions(
                MaximumSources: config.MaximumHarnessSources,
                ConfiguredRoots: config.HarnessRoots));
        foreach (var source in discovery.Sources)
        {
            adapters.Add(new BridgeMonitorAdapter(source, dataDirectory, projects));
        }

        // One aggregate per-pass allowance is shared by every source rather than
        // multiplied per source, and the tracked-session allowance is derived from the
        // configured tracked-file limit so adding a harness cannot raise the bound.
        var monitorSource = new CompositeMonitorSource(adapters, log, TimeProvider.System, new SourceBudget(
            config.Limits.GlobalBytesPerPass,
            Math.Max(SourceBudget.MinimumSessions, config.Limits.MaxTrackedFiles * 16),
            "config-aggregate"), discoveryIncomplete: discovery.Capped);
        var executablePath = Environment.ProcessPath ?? string.Empty;
        var startup = new StartupRegistrationAdapter(new StartupRegistration(new WindowsStartupRegistry(), executablePath));
        var navigator = new TaskNavigatorAdapter(new CodexTaskNavigator(new ShellTaskLaunchBoundary()));

        var notifications = CreateNotifications(dataDirectory, config.Voice, log);

        var dependencies = new HudControllerDependencies(
            monitorSource,
            new WpfUiDispatcher(dispatcher),
            new WindowsWindowPlacementService(),
            navigator,
            startup,
            new WpfClipboardWriter(),
            log,
            config,
            stateResult.State,
            new HudControllerPaths(dataDirectory, configPath, statePath),
            () => new HudWindow(),
            TimeProvider.System,
            arguments.StartInTray,
            !arguments.NoTray,
            Persistence: null,
            Notifications: notifications);

        return (dependencies, configResult, stateResult);
    }

    /// <summary>
    /// Builds the history/voice graph. The pack provider is local-only; the TTS
    /// provider exists only when the user selected it, and it is never contacted
    /// without a configured endpoint.
    /// </summary>
    private static NotificationCoordinator CreateNotifications(string dataDirectory, VoiceSettings settings, IMonitorLog log)
    {
        var normalized = (settings ?? VoiceSettings.Default).Normalized();
        var policy = new VoiceEventPolicy();
        NotificationCoordinator? coordinator = null;
        VoiceSettings Settings() => coordinator?.Settings ?? normalized;
        bool Relevant(VoiceCandidate candidate) => coordinator?.IsStillRelevant(candidate) ?? false;
        // The assigned recording wins; otherwise exactly the provider the current
        // settings select, decided per request so a settings change takes effect at
        // once. No automatic cross-provider fallback: a failing selected provider is
        // reported unavailable instead of quietly speaking another voice.
        var provider = VoiceProviderFactory.Create(dataDirectory, Settings);

        var player = new WavPlaybackService();
        var manager = new VoiceManager(
            provider,
            player,
            VoiceManagerOptions.Default,
            TimeProvider.System,
            revalidate: Relevant,
            muted: normalized.Muted);
        _ = log;
        coordinator = new NotificationCoordinator(new ActivityHistory(), policy, manager, TimeProvider.System, normalized);
        return coordinator;
    }

}
