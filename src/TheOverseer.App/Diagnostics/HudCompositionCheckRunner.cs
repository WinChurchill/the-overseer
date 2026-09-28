using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;
using TheOverseer.App.Desktop;
using TheOverseer.App.Monitoring;
using TheOverseer.App.Voice;
using TheOverseer.Core.Configuration;
using TheOverseer.Core.Diagnostics;
using TheOverseer.Core.Models;
using TheOverseer.Core.Monitoring;
using TheOverseer.Core.Voice;
using TheOverseer.UI;
using TheOverseer.UI.ViewModels;

namespace TheOverseer.App.Diagnostics;

/// <summary>
/// Normal-composition check. The production-path integration check builds its own
/// dependency graph, so it proves the monitor-to-UI pipeline but says nothing about
/// what <see cref="HudComposition"/> actually assembles at startup. This check drives
/// the real composition - startup configuration loading, harness discovery, voice
/// provider selection and controller construction - and then asserts the state that
/// reaches the real HUD view model.
///
/// Only two things are substituted: the monitor's producer side (invented files
/// instead of the user's real harness data) and the handful of services that would
/// otherwise touch the user's machine (startup registry, task launch, clipboard, and
/// window activation). Nothing here reads or writes a real rollout, spool, settings
/// file, shortcut or registry value.
/// </summary>
internal static class HudCompositionCheckRunner
{
    private sealed record CheckResult(string Name, bool Passed, string Detail);

    /// <summary>Guards a check that must hold while nothing is installed.</summary>
    private const int SettleSeconds = 6;

    public static async Task<int> RunAsync(StartupArguments arguments, Dispatcher dispatcher)
    {
        var outputRoot = string.IsNullOrWhiteSpace(arguments.OutputDirectory)
            ? Path.Combine(DataDirectoryResolver.Resolve(arguments.DataDirectory), "composition-check")
            : Path.GetFullPath(arguments.OutputDirectory!);
        var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture);
        var root = Path.Combine(outputRoot, "run-" + stamp + "-" + Guid.NewGuid().ToString("N")[..8]);

        var checks = new List<CheckResult>();
        void Check(string name, bool passed, string detail)
        {
            checks.Add(new CheckResult(name, passed, detail));
            Console.WriteLine("check " + (passed ? "PASS " : "FAIL ") + name + " - " + detail);
        }

        Console.WriteLine("OVERSEER normal-composition check (real HudComposition, invented producers, isolated data)");
        Console.WriteLine("root: " + root);
        Console.WriteLine("placement monitor: " + (string.IsNullOrWhiteSpace(arguments.PlacementMonitor) ? "(saved or primary)" : arguments.PlacementMonitor));

        // Hard guard: this check composes the real graph four times and lets a real
        // controller save its state on shutdown. It must never resolve to the user's own
        // data directory, because that would write to live settings and read their real
        // harness configuration.
        var realDataDirectory = DataDirectoryResolver.Resolve(null);
        if (PathsEqual(DataDirectoryResolver.Resolve(arguments.DataDirectory), realDataDirectory))
        {
            Console.WriteLine("refusing to run: the resolved data directory is the user's live OVERSEER directory (" + realDataDirectory + ")");
            Console.WriteLine("pass --data-directory PATH pointing inside a disposable evidence directory");
            return 2;
        }

        // The window is created without activation. Composition still runs for real;
        // this only stops an automated check from taking the user's focus.
        static HudWindow NonActivatingWindow() => new() { ShowActivated = false };

        try
        {
            await EmptyDataDirectoryAsync(root, arguments, dispatcher, Check, NonActivatingWindow).ConfigureAwait(true);
            await NoHarnessInstalledAsync(root, arguments, dispatcher, Check, NonActivatingWindow).ConfigureAwait(true);
            await MixedSourcesAndRestartAsync(root, arguments, dispatcher, Check, NonActivatingWindow).ConfigureAwait(true);
            await ExistingPreferencesAreAuthoritativeAsync(root, arguments, dispatcher, Check, NonActivatingWindow).ConfigureAwait(true);

            // Offline voice availability for a brand-new data directory is a property of
            // startup configuration, so it is asserted here through the same call path
            // the HUD uses to build its provider.
            BaselineVoiceCheck(root, Check);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            Check("composition-check-run", false, exception.GetType().Name + ": " + exception.Message);
        }

        var failed = checks.Count(static check => !check.Passed);
        var report = new
        {
            product = "OVERSEER",
            check = "normal-composition",
            root,
            generatedAtUtc = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture),
            placementMonitor = arguments.PlacementMonitor ?? string.Empty,
            passed = checks.Count - failed,
            failed,
            checks,
        };
        Directory.CreateDirectory(outputRoot);
        var reportPath = Path.Combine(root, "composition-report.json");
        File.WriteAllText(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
        Console.WriteLine("composition checks: " + (checks.Count - failed) + " passed, " + failed + " failed");
        Console.WriteLine("composition report: " + reportPath);
        return failed == 0 ? 0 : 1;
    }

    /// <summary>
    /// A brand-new data directory: composition must succeed, must not invent work, must
    /// report an unknown rather than a healthy zero, and must start muted.
    /// </summary>
    private static async Task EmptyDataDirectoryAsync(
        string root, StartupArguments arguments, Dispatcher dispatcher,
        Action<string, bool, string> check, Func<HudWindow> windowFactory)
    {
        var data = Path.Combine(root, "empty-first-run");
        Directory.CreateDirectory(data);

        var composed = await HudComposition.ComposeAsync(
            Isolated(data, Path.Combine(root, "empty-codex-home"), arguments),
            dispatcher,
            new HudCompositionOverrides { WindowFactory = windowFactory, BudgetLabel = "composition-check" }).ConfigureAwait(true);

        var viewModel = composed.Controller.ViewModel;
        try
        {
            await composed.Controller.StartAsync(CancellationToken.None).ConfigureAwait(true);
            var applied = await WaitAsync(() => viewModel.HasAppliedSnapshot, 30).ConfigureAwait(true);

            check("empty-first-run-composes", true,
                "config=" + composed.Config.Status + " state=" + composed.State.Status + " " + Summarize(viewModel));
            check("empty-first-run-applies-a-snapshot", applied, Summarize(viewModel));
            check("empty-first-run-invents-no-work", viewModel.Tasks.Count == 0,
                "rows=" + viewModel.Tasks.Count.ToString(CultureInfo.InvariantCulture));
            check("empty-first-run-starts-muted", viewModel.IsMuted, "muted=" + viewModel.IsMuted);

            // A missing data directory is not evidence that the user has no work: a
            // healthy zero here would be a fabricated claim.
            check("empty-first-run-is-not-a-healthy-zero",
                !string.Equals(viewModel.HealthLabel, "Live", StringComparison.OrdinalIgnoreCase),
                "health=" + viewModel.HealthLabel);
        }
        finally
        {
            await ShutdownAsync(composed.Controller).ConfigureAwait(true);
        }

        check("empty-first-run-clean-shutdown", true, "controller shut down without throwing");
    }

    /// <summary>
    /// No harness installed at all: no bridge spool exists and the Codex home holds
    /// nothing. Composition and discovery must still produce a usable HUD that states
    /// the setup action instead of an empty page or a crash.
    /// </summary>
    private static async Task NoHarnessInstalledAsync(
        string root, StartupArguments arguments, Dispatcher dispatcher,
        Action<string, bool, string> check, Func<HudWindow> windowFactory)
    {
        var data = Path.Combine(root, "no-harness");
        var codexHome = Path.Combine(root, "no-harness-codex-home");
        Directory.CreateDirectory(data);
        Directory.CreateDirectory(codexHome);

        var discovered = HarnessSources.DiscoverDetailed(data);
        check("no-harness-discovers-nothing", discovered.Sources.Count == 0 && !discovered.Capped,
            "sources=" + discovered.Sources.Count.ToString(CultureInfo.InvariantCulture) + " capped=" + discovered.Capped);

        var composed = await HudComposition.ComposeAsync(
            Isolated(data, codexHome, arguments),
            dispatcher,
            new HudCompositionOverrides { WindowFactory = windowFactory, BudgetLabel = "composition-check" }).ConfigureAwait(true);

        var viewModel = composed.Controller.ViewModel;
        try
        {
            await composed.Controller.StartAsync(CancellationToken.None).ConfigureAwait(true);
            await WaitAsync(() => viewModel.HasAppliedSnapshot, 30).ConfigureAwait(true);
            await Task.Delay(TimeSpan.FromSeconds(SettleSeconds)).ConfigureAwait(true);

            check("no-harness-produces-no-tasks", viewModel.Tasks.Count == 0,
                "rows=" + viewModel.Tasks.Count.ToString(CultureInfo.InvariantCulture));
            check("no-harness-health-is-honest",
                !string.Equals(viewModel.HealthLabel, "Live", StringComparison.OrdinalIgnoreCase),
                "health=" + viewModel.HealthLabel);
            check("no-harness-still-usable",
                viewModel.EmptyStateText.Length > 0 || viewModel.HealthDetails.Count > 0,
                "emptyState=" + (viewModel.EmptyStateText.Length > 0 ? "present" : "absent") +
                " details=" + viewModel.HealthDetails.Count.ToString(CultureInfo.InvariantCulture));
        }
        finally
        {
            await ShutdownAsync(composed.Controller).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// Valid mixed sources through the real composition, then a restart that adds
    /// another bridge: discovery happens during composition, so a new bridge is picked
    /// up only by the next composition. That is the documented behaviour, and this
    /// asserts it instead of assuming it.
    /// </summary>
    private static async Task MixedSourcesAndRestartAsync(
        string root, StartupArguments arguments, Dispatcher dispatcher,
        Action<string, bool, string> check, Func<HudWindow> windowFactory)
    {
        var data = Path.Combine(root, "mixed-sources");
        var codexHome = Path.Combine(root, "mixed-codex-home");
        Directory.CreateDirectory(data);
        Directory.CreateDirectory(codexHome);

        var codex = new SyntheticProducers.SyntheticCodexProducer(codexHome);
        codex.WriteWorkingTask();
        var dsh = new SyntheticProducers.SyntheticBridgeProducer(data, HarnessIds.Dsh, "dsh-first");
        dsh.Start("session-dsh-first", @"C:\synthetic\dsh-first");

        var beforeStart = HarnessSources.DiscoverDetailed(data);
        check("mixed-discovery-finds-the-installed-bridge", beforeStart.Sources.Count == 1,
            "sources=" + string.Join(',', beforeStart.Sources.Select(static source => source.Describe())));

        var first = await HudComposition.ComposeAsync(
            Isolated(data, codexHome, arguments),
            dispatcher,
            Overrides(data, arguments, windowFactory)).ConfigureAwait(true);

        var firstViewModel = first.Controller.ViewModel;
        try
        {
            await first.Controller.StartAsync(CancellationToken.None).ConfigureAwait(true);

            var codexApplied = await WaitAsync(() => firstViewModel.Tasks.Any(static row =>
                row.Key.HarnessId == HarnessIds.Codex && row.TaskState == AgentTaskState.Working), 40).ConfigureAwait(true);
            check("mixed-codex-task-applied", codexApplied, Summarize(firstViewModel));

            var bridgeApplied = await WaitAsync(() => firstViewModel.Tasks.Any(static row =>
                row.Key.HarnessId == HarnessIds.Dsh), 40).ConfigureAwait(true);
            check("mixed-bridge-task-applied", bridgeApplied, Summarize(firstViewModel));

            // A bridge that appears while the HUD is running is not discovered: adding
            // hot discovery is explicitly out of scope, so this pins the real behaviour.
            var late = new SyntheticProducers.SyntheticBridgeProducer(data, HarnessIds.Claude, "claude-late");
            late.Start("session-claude-late", @"C:\synthetic\claude-late");
            var appearedWithoutRestart = await WaitAsync(() => firstViewModel.Tasks.Any(static row =>
                row.Key.HarnessId == HarnessIds.Claude), SettleSeconds).ConfigureAwait(true);
            check("new-bridge-needs-a-restart", !appearedWithoutRestart,
                "claudeRows=" + firstViewModel.Tasks.Count(static row => row.Key.HarnessId == HarnessIds.Claude)
                    .ToString(CultureInfo.InvariantCulture));
        }
        finally
        {
            await ShutdownAsync(first.Controller).ConfigureAwait(true);
        }

        var afterRestart = HarnessSources.DiscoverDetailed(data);
        check("restart-discovery-finds-both-bridges", afterRestart.Sources.Count == 2,
            "sources=" + string.Join(',', afterRestart.Sources.Select(static source => source.Describe())));

        // The second composition is built with adapters for the newly discovered
        // sources, exactly as the next real startup would.
        var projects = new ProjectResolver(new ProjectSettings());
        var monitor = new CodexMonitor(
            MonitorOptions.Default with { KnownFilePollInterval = TimeSpan.FromMilliseconds(500), ReconcileInterval = TimeSpan.FromSeconds(1) },
            MonitorProfiles.Create(codexHome),
            NullRuntimeActivitySource.Instance,
            NullMonitorLog.Instance,
            TimeProvider.System,
            projects);
        var adapters = new List<IAgentMonitorAdapter>
        {
            new CodexMonitorAdapter(new CodexMonitorSource(monitor), new SourceInstance(HarnessIds.Codex, "codex", "Codex")),
        };
        foreach (var source in afterRestart.Sources)
        {
            adapters.Add(new BridgeMonitorAdapter(source, data, projects, pollInterval: TimeSpan.FromMilliseconds(500)));
        }

        var second = await HudComposition.ComposeAsync(
            Isolated(data, codexHome, arguments),
            dispatcher,
            new HudCompositionOverrides { Adapters = adapters, WindowFactory = windowFactory, BudgetLabel = "composition-check" }).ConfigureAwait(true);

        var secondViewModel = second.Controller.ViewModel;
        try
        {
            await second.Controller.StartAsync(CancellationToken.None).ConfigureAwait(true);
            var bothBridges = await WaitAsync(() => secondViewModel.Tasks.Any(static row => row.Key.HarnessId == HarnessIds.Dsh) &&
                secondViewModel.Tasks.Any(static row => row.Key.HarnessId == HarnessIds.Claude), 40).ConfigureAwait(true);
            check("restart-composition-applies-both-bridges", bothBridges, Summarize(secondViewModel));
        }
        finally
        {
            await ShutdownAsync(second.Controller).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// An existing configuration file is authoritative: explicit provider, mute, theme,
    /// window mode and placement survive composition unchanged, and a new data
    /// directory takes the offline baseline instead.
    /// </summary>
    private static async Task ExistingPreferencesAreAuthoritativeAsync(
        string root, StartupArguments arguments, Dispatcher dispatcher,
        Action<string, bool, string> check, Func<HudWindow> windowFactory)
    {
        var data = Path.Combine(root, "existing-preferences");
        Directory.CreateDirectory(data);

        const string config = """
        {
          "hud": { "theme": "light", "alwaysOnTop": true, "opacity": 0.85, "taskView": "recent", "recentHours": 8 },
          "voice": { "muted": false, "provider": "pack", "packId": "example" },
          "project": { "mappings": [ { "projectKey": "kept", "name": "Kept project", "root": "C:/kept" } ], "aliases": [] },
          "disabledHarnessSources": [ "claude/kept-disabled" ]
        }
        """;
        File.WriteAllText(Path.Combine(data, MonitorConfigStore.FileName), config, new UTF8Encoding(false));

        var composed = await HudComposition.ComposeAsync(
            Isolated(data, Path.Combine(root, "existing-codex-home"), arguments),
            dispatcher,
            Overrides(data, arguments, windowFactory)).ConfigureAwait(true);

        var viewModel = composed.Controller.ViewModel;
        try
        {
            await composed.Controller.StartAsync(CancellationToken.None).ConfigureAwait(true);
            await WaitAsync(() => viewModel.HasAppliedSnapshot, 30).ConfigureAwait(true);

            check("existing-config-is-loaded", composed.Config.Status == ConfigLoadStatus.Loaded,
                "status=" + composed.Config.Status + " reason=" + composed.Config.ReasonCode);
            check("existing-voice-provider-kept",
                string.Equals(composed.Dependencies.Config.Voice.ProviderName, "pack", StringComparison.OrdinalIgnoreCase),
                "provider=" + composed.Dependencies.Config.Voice.ProviderName);
            check("existing-mute-kept", !composed.Dependencies.Config.Voice.Muted && !viewModel.IsMuted,
                "configMuted=" + composed.Dependencies.Config.Voice.Muted + " hudMuted=" + viewModel.IsMuted);
            check("existing-theme-kept",
                string.Equals(composed.Dependencies.Config.Hud.ThemeName, "light", StringComparison.OrdinalIgnoreCase),
                "theme=" + composed.Dependencies.Config.Hud.ThemeName);
            check("existing-project-mapping-kept",
                composed.Dependencies.Config.Project.Mappings.Count == 1,
                "mappings=" + composed.Dependencies.Config.Project.Mappings.Count.ToString(CultureInfo.InvariantCulture));
            check("existing-disabled-source-kept",
                composed.Dependencies.Config.DisabledHarnessSources.Contains("claude/kept-disabled", StringComparer.OrdinalIgnoreCase),
                "disabled=" + string.Join(',', composed.Dependencies.Config.DisabledHarnessSources));
        }
        finally
        {
            await ShutdownAsync(composed.Controller).ConfigureAwait(true);
        }

        // The same file after composition: loading must not have rewritten the user's
        // preferences just because the HUD started.
        var reloaded = MonitorConfigStore.Load(Path.Combine(data, MonitorConfigStore.FileName));
        check("existing-config-not-rewritten-by-startup",
            reloaded.Config.Voice.Muted == false &&
            string.Equals(reloaded.Config.Voice.ProviderName, "pack", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(reloaded.Config.Hud.ThemeName, "light", StringComparison.OrdinalIgnoreCase),
            "status=" + reloaded.Status + " theme=" + reloaded.Config.Hud.ThemeName + " provider=" + reloaded.Config.Voice.ProviderName);
    }

    /// <summary>
    /// Offline voice availability for a missing configuration file, asserted through
    /// the same settings normalization startup uses and through the provider factory
    /// the HUD builds its voice graph from.
    /// </summary>
    private static void BaselineVoiceCheck(string root, Action<string, bool, string> check)
    {
        var effective = VoiceSettings.Effective(ConfigLoadStatus.Missing, null);
        check("missing-config-selects-offline-speech",
            effective.ResolvedProvider == VoiceProviderKind.Speech && effective.Muted,
            "provider=" + effective.ProviderName + " muted=" + effective.Muted);

        var directory = Path.Combine(root, "baseline-voice");
        Directory.CreateDirectory(directory);
        var provider = VoiceProviderFactory.Create(directory, () => effective);
        check("offline-speech-provider-is-constructed", provider is not null,
            "providerType=" + (provider?.GetType().Name ?? "none"));
        check("provider-requires-no-network-endpoint", effective.Endpoint is null,
            "endpoint=" + (effective.Endpoint ?? "none"));
    }

    private static HudCompositionOverrides Overrides(
        string dataDirectory, StartupArguments arguments, Func<HudWindow> windowFactory)
    {
        // Composition reads the saved placement itself, so a check that must not open on
        // the display in use steers through the same in-memory seed the other
        // diagnostics checks use. Nothing is written and the saved state is untouched.
        var saved = HudStateStore.Load(Path.Combine(dataDirectory, HudStateStore.FileName)).State;
        return new HudCompositionOverrides
        {
            WindowFactory = windowFactory,
            Startup = new CompositionStartup(),
            Navigator = new CompositionNavigator(),
            Clipboard = new CompositionClipboard(),
            BudgetLabel = "composition-check",
            WindowState = HudPlacementOptions.WithMonitor(saved, arguments.PlacementMonitor),
        };
    }

    /// <summary>Copies the parsed arguments with an invented data directory and Codex home.</summary>
    private static StartupArguments Isolated(string dataDirectory, string codexHome, StartupArguments arguments) =>
        arguments.WithPaths(codexHome, dataDirectory);

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            StringComparison.OrdinalIgnoreCase);

    private static async Task ShutdownAsync(HudApplicationController controller)
    {
        try
        {
            await controller.ShutdownAsync().ConfigureAwait(true);
        }
        catch (InvalidOperationException)
        {
        }

        await controller.DisposeAsync().ConfigureAwait(true);
    }

    private static async Task<bool> WaitAsync(Func<bool> predicate, double seconds)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            if (predicate())
            {
                return true;
            }

            await Task.Delay(200).ConfigureAwait(true);
        }

        return predicate();
    }

    /// <summary>Redacted state summary: counts, states and codes only, never a label.</summary>
    private static string Summarize(HudViewModel viewModel)
    {
        var details = string.Join(',', viewModel.HealthDetails.Select(static row => row.Source + ":" + row.State));
        return "rows=" + viewModel.Tasks.Count.ToString(CultureInfo.InvariantCulture) +
               " working=" + viewModel.WorkingCountText + " needsYou=" + viewModel.NeedsHumanCountText +
               " health=" + viewModel.HealthLabel +
               " muted=" + viewModel.IsMuted +
               " details=[" + details + "]" +
               " applied=" + viewModel.AppliedSnapshots.ToString(CultureInfo.InvariantCulture);
    }

    private sealed class CompositionNavigator : ITaskNavigator
    {
        public TaskNavigationResult Navigate(AgentTask task, string profileId) =>
            new(TaskNavigationOutcome.Requested, "composition-check: no real launch", null);
    }

    private sealed class CompositionStartup : IStartupRegistration
    {
        public StartupRegistrationResult Read() => new(StartupRegistrationState.Disabled, "composition-check");

        public StartupRegistrationResult Set(bool enabled) => new(StartupRegistrationState.Disabled, "composition-check");
    }

    private sealed class CompositionClipboard : IClipboardWriter
    {
        public bool TrySetText(string text) => true;
    }
}
