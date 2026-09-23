using System.IO;
using System.Net.Http;
using System.Windows.Threading;
using TheOverseer.App.Desktop;
using TheOverseer.App.Diagnostics;
using TheOverseer.App.Monitoring;
using TheOverseer.Core.Configuration;
using TheOverseer.Core.Diagnostics;
using TheOverseer.Core.History;
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

        var notifications = CreateNotifications(dataDirectory, configResult.Config.Voice, log);

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
        var packProvider = new VoicePackProvider(dataDirectory, Settings);
        IVoiceProvider fallback = normalized.ResolvedProvider == VoiceProviderKind.Tts
            ? new FallbackVoiceProvider(new TtsEndpointProvider(CreateTtsClient(), Settings), packProvider, allowFallback: true, Relevant)
            : packProvider;
        IVoiceProvider provider = new AssignedVoiceProvider(dataDirectory, Settings, fallback);

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

    /// <summary>One reusable client: no redirects, no cookies, bounded by provider timeouts.</summary>
    private static HttpClient CreateTtsClient()
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
        };
        var client = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        return client;
    }
}
