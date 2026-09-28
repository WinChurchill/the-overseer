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
/// Narrow substitution points for the normal-mode object graph.
/// </summary>
/// <remarks>
/// These exist so a check can exercise the REAL composition - startup configuration
/// loading, harness discovery, provider selection and controller construction - while
/// substituting only the monitor's producer side and the few services that would
/// otherwise touch the user's machine (the startup registry entry, a window).
/// Nothing here changes production behaviour: <see cref="HudComposition.CreateAsync"/>
/// passes no overrides at all.
/// </remarks>
internal sealed record HudCompositionOverrides
{
    /// <summary>
    /// Monitor adapters to use instead of the discovered set. Source discovery still
    /// runs; a caller that wants the real set leaves this null.
    /// </summary>
    public IReadOnlyList<IAgentMonitorAdapter>? Adapters { get; init; }

    /// <summary>Replaces the real Run-key registration (never touch the user's entry in a check).</summary>
    public IStartupRegistration? Startup { get; init; }

    /// <summary>Replaces the real Codex task launch.</summary>
    public ITaskNavigator? Navigator { get; init; }

    /// <summary>Replaces the real clipboard.</summary>
    public IClipboardWriter? Clipboard { get; init; }

    /// <summary>Replaces the HUD window factory; a check creates no visible window.</summary>
    public Func<HudWindow>? WindowFactory { get; init; }

    /// <summary>
    /// Replaces the window state loaded from disk. Only used to steer a windowed check
    /// off the display in use; it is never persisted.
    /// </summary>
    public HudWindowState? WindowState { get; init; }

    /// <summary>
    /// Label used for the shared per-pass monitor budget. It appears only in
    /// diagnostics, so a check can name itself without changing any bound.
    /// </summary>
    public string? BudgetLabel { get; init; }
}

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
        var prepared = await Task.Run(() => Prepare(arguments, dispatcher, overrides: null));
        return (new HudApplicationController(prepared.Dependencies), prepared.Config, prepared.State);
    }

    /// <summary>
    /// Composes the normal object graph with named substitutions and returns the
    /// dependency record instead of a controller. Diagnostics checks and tests use
    /// this to prove the real composition wiring; production uses CreateAsync.
    /// </summary>
    internal static async Task<PreparedComposition> ComposeAsync(
        StartupArguments arguments,
        Dispatcher dispatcher,
        HudCompositionOverrides? overrides)
    {
        var prepared = await Task.Run(() => Prepare(arguments, dispatcher, overrides));
        return new PreparedComposition(new HudApplicationController(prepared.Dependencies), prepared.Config, prepared.State, prepared.Dependencies);
    }

    /// <summary>The composed graph, its loaded configuration and its loaded window state.</summary>
    internal sealed record PreparedComposition(
        HudApplicationController Controller,
        ConfigLoadResult Config,
        HudStateLoadResult State,
        HudControllerDependencies Dependencies);

    private static (HudControllerDependencies Dependencies, ConfigLoadResult Config, HudStateLoadResult State) Prepare(
        StartupArguments arguments, Dispatcher dispatcher, HudCompositionOverrides? overrides)
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
        //
        // Discovery always runs here, so a caller that substitutes the adapters still
        // exercises the real startup discovery path and simply publishes different
        // producers. Production passes no substitution and gets exactly this set.
        var discovery = HarnessSources.DiscoverDetailed(
            dataDirectory,
            config.DisabledHarnessSources,
            new HarnessDiscoveryOptions(
                MaximumSources: config.MaximumHarnessSources,
                ConfiguredRoots: config.HarnessRoots));

        IReadOnlyList<IAgentMonitorAdapter> adapters;
        if (overrides?.Adapters is { } substituted)
        {
            adapters = substituted;
        }
        else
        {
            var built = new List<IAgentMonitorAdapter>
            {
                new CodexMonitorAdapter(new CodexMonitorSource(monitor), new SourceInstance(HarnessIds.Codex, "codex", "Codex")),
            };
            foreach (var source in discovery.Sources)
            {
                built.Add(new BridgeMonitorAdapter(source, dataDirectory, projects));
            }

            adapters = built;
        }

        // One aggregate per-pass allowance is shared by every source rather than
        // multiplied per source, and the tracked-session allowance is derived from the
        // configured tracked-file limit so adding a harness cannot raise the bound.
        var monitorSource = new CompositeMonitorSource(adapters, log, TimeProvider.System, new SourceBudget(
            config.Limits.GlobalBytesPerPass,
            Math.Max(SourceBudget.MinimumSessions, config.Limits.MaxTrackedFiles * 16),
            overrides?.BudgetLabel ?? "config-aggregate"), discoveryIncomplete: discovery.Capped);
        var executablePath = Environment.ProcessPath ?? string.Empty;
        var startup = overrides?.Startup
            ?? new StartupRegistrationAdapter(new StartupRegistration(new WindowsStartupRegistry(), executablePath));
        var navigator = overrides?.Navigator ?? new TaskNavigatorAdapter(new CodexTaskNavigator(new ShellTaskLaunchBoundary()));

        var notifications = CreateNotifications(dataDirectory, config.Voice, log);

        var dependencies = new HudControllerDependencies(
            monitorSource,
            new WpfUiDispatcher(dispatcher),
            new WindowsWindowPlacementService(),
            navigator,
            startup,
            overrides?.Clipboard ?? new WpfClipboardWriter(),
            log,
            config,
            overrides?.WindowState ?? stateResult.State,
            new HudControllerPaths(dataDirectory, configPath, statePath),
            overrides?.WindowFactory ?? (() => new HudWindow()),
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
