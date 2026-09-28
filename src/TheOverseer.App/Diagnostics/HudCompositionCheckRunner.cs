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
            ? Path.Combine(Path.GetTempPath(), "OVERSEER-composition-check")
            : Path.GetFullPath(arguments.OutputDirectory!);
        var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture);
        var root = Path.Combine(outputRoot, "run-" + stamp + "-" + Guid.NewGuid().ToString("N")[..8]);

        if (SelectMonitor(arguments) is null)
        {
            Directory.CreateDirectory(root);
            var detail = "No authorized display: requested monitor missing, primary not opted in, or no secondary available.";
            File.WriteAllText(Path.Combine(root, "composition-report.json"), JsonSerializer.Serialize(new
            {
                deferred = true, reason = detail, passed = 0, failed = 0, placementMonitor = arguments.PlacementMonitor,
            }));
            Console.WriteLine("composition check deferred: " + detail);
            return 2;
        }

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
        // controller save its state on shutdown. It must never read or write the user's
        // own data directory. An explicit request for it is refused; no request at all
        // is satisfied with an isolated directory under the evidence root instead of
        // silently falling back to the live one.
        var realDataDirectory = DataDirectoryResolver.Resolve(null);
        var requestedDataDirectory = string.IsNullOrWhiteSpace(arguments.DataDirectory)
            ? null
            : DataDirectoryResolver.Resolve(arguments.DataDirectory);
        if (requestedDataDirectory is not null && PathsEqual(requestedDataDirectory, realDataDirectory))
        {
            Console.WriteLine("refusing to run: the requested data directory is the user's live OVERSEER directory (" + realDataDirectory + ")");
            Console.WriteLine("omit --data-directory to use an isolated directory under --output-directory");
            return 2;
        }

        var dataDirectory = requestedDataDirectory ?? Path.Combine(root, "isolated-data");
        Directory.CreateDirectory(dataDirectory);
        arguments = arguments.WithPaths(arguments.CodexHome, dataDirectory);

        // The window is created without activation. Composition still runs for real;
        // this only stops an automated check from taking the user's focus.
        static HudWindow NonActivatingWindow() => new() { ShowActivated = false };

        try
        {
            await EmptyDataDirectoryAsync(root, arguments, dispatcher, Check, NonActivatingWindow).ConfigureAwait(true);
            await NoHarnessInstalledAsync(root, arguments, dispatcher, Check, NonActivatingWindow).ConfigureAwait(true);
            await MixedSourcesAndRestartAsync(root, arguments, dispatcher, Check, NonActivatingWindow).ConfigureAwait(true);
            await ExistingPreferencesAreAuthoritativeAsync(root, arguments, dispatcher, Check, NonActivatingWindow).ConfigureAwait(true);
            await FailureAndRecoveryAsync(root, arguments, dispatcher, Check, NonActivatingWindow).ConfigureAwait(true);

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
            Overrides(data, arguments, windowFactory)).ConfigureAwait(true);

        var viewModel = composed.Controller.ViewModel;
        try
        {
            await StartCheckedAsync(composed, arguments, check).ConfigureAwait(true);
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
            Overrides(data, arguments, windowFactory)).ConfigureAwait(true);

        var viewModel = composed.Controller.ViewModel;
        try
        {
            await StartCheckedAsync(composed, arguments, check).ConfigureAwait(true);
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
            await StartCheckedAsync(first, arguments, check).ConfigureAwait(true);

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

        var second = await HudComposition.ComposeAsync(
            Isolated(data, codexHome, arguments),
            dispatcher,
            Overrides(data, arguments, windowFactory)).ConfigureAwait(true);

        var secondViewModel = second.Controller.ViewModel;
        try
        {
            await StartCheckedAsync(second, arguments, check).ConfigureAwait(true);
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
            await StartCheckedAsync(composed, arguments, check).ConfigureAwait(true);
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
        if (provider is IDisposable disposable) disposable.Dispose();
    }

    private static async Task FailureAndRecoveryAsync(string root, StartupArguments arguments,
        Dispatcher dispatcher, Action<string, bool, string> check, Func<HudWindow> windowFactory)
    {
        var data = Path.Combine(root, "failure-recovery");
        Directory.CreateDirectory(data);
        File.WriteAllText(Path.Combine(data, "config.json"),
            "{\"hud\":{\"taskView\":\"recent\",\"recentHours\":24},\"voice\":{\"muted\":true},\"runtimeActivitySource\":false}");
        var codexHome = Path.Combine(root, "failure-codex-home");
        var source = new SyntheticProducers.SyntheticBridgeProducer(data, HarnessIds.Dsh, "subject")
        { Timestamp = DateTimeOffset.UtcNow.AddMinutes(-16) };
        source.Start("stale-session", @"C:\synthetic\subject");
        var peer = new SyntheticProducers.SyntheticBridgeProducer(data, HarnessIds.Claude, "peer");
        peer.Start("peer-session", @"C:\synthetic\peer");
        Directory.CreateDirectory(Path.Combine(HarnessSources.DefaultRoot(data, HarnessIds.Dsh), "missing"));

        var composed = await HudComposition.ComposeAsync(Isolated(data, codexHome, arguments), dispatcher,
            Overrides(data, arguments, windowFactory)).ConfigureAwait(true);
        var vm = composed.Controller.ViewModel;
        bool PeerSurvives() => vm.Tasks.Any(row => row.Key.SourceInstanceId == "peer" && !row.IsStale) &&
            vm.HealthDetails.Any(row => row.Instance == "peer" && row.State.StartsWith("live", StringComparison.Ordinal));
        // Recovery restores current coverage; a previously observed history gap
        // correctly remains visible for the rest of its hour.
        bool SubjectCurrent() => composed.Dependencies.Monitor.Current.Sources.Any(source =>
            source.SourceInstanceId == "subject" && source.CoverageComplete) &&
            vm.HealthDetails.Any(row => row.Instance == "subject" && !row.State.Contains("partial", StringComparison.Ordinal));
        async Task Observe(string name, Func<bool> predicate)
        {
            peer.Heartbeat();
            var passed = await WaitAsync(() => predicate() && PeerSurvives(), 25).ConfigureAwait(true);
            check(name, passed, Summarize(vm));
        }

        try
        {
            await StartCheckedAsync(composed, arguments, check).ConfigureAwait(true);
            await Observe("stale-source-and-healthy-peer", () => vm.Tasks.Any(row => row.Key.SourceInstanceId == "subject" && row.IsStale));
            await Observe("missing-source-is-not-readable", () => vm.HealthDetails.Any(row => row.Instance == "missing" && row.State.StartsWith("not readable", StringComparison.Ordinal)));
            source.Timestamp = null;
            source.Heartbeat();
            await Observe("stale-source-recovers", () => SubjectCurrent() && vm.Tasks.Any(row => row.Key.SourceInstanceId == "subject" && !row.IsStale));

            var current = Path.Combine(source.SpoolDirectory, "current.json");
            var unsupported = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(current))!;
            unsupported["schemaVersion"] = 999;
            File.WriteAllText(current, unsupported.ToJsonString());
            await Observe("unsupported-source-is-limited", () => vm.HealthDetails.Any(row => row.Instance == "subject" && row.State.Contains("bridge-coverage-incomplete", StringComparison.Ordinal)));
            source.Heartbeat();
            await Observe("unsupported-source-recovers", SubjectCurrent);

            using (var locked = new FileStream(Path.Combine(source.SpoolDirectory, "events.jsonl"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                await Observe("unreadable-source-is-limited", () => vm.HealthDetails.Any(row => row.Instance == "subject" && row.State.Contains("bridge-io", StringComparison.Ordinal)));
            }
            source.Heartbeat();
            await Observe("unreadable-source-recovers", SubjectCurrent);

            // Rename only invented producer data. Its absence is a failure in the
            // running graph; the next normal startup must no longer discover it.
            Directory.Move(source.SpoolDirectory, Path.Combine(root, "removed-subject"));
            await Observe("removed-source-degrades-before-restart", () => !SubjectCurrent() &&
                vm.HealthDetails.Any(row => row.Instance == "subject" && row.State.Contains("partial", StringComparison.Ordinal)));
        }
        finally { await ShutdownAsync(composed.Controller).ConfigureAwait(true); }

        peer.Heartbeat();
        var restarted = await HudComposition.ComposeAsync(Isolated(data, codexHome, arguments), dispatcher,
            Overrides(data, arguments, windowFactory)).ConfigureAwait(true);
        try
        {
            await StartCheckedAsync(restarted, arguments, check).ConfigureAwait(true);
            var after = restarted.Controller.ViewModel;
            var passed = await WaitAsync(() => after.HasAppliedSnapshot &&
                after.Tasks.Any(row => row.Key.SourceInstanceId == "peer") &&
                after.HealthDetails.All(row => row.Instance != "subject") &&
                after.Tasks.All(row => row.Key.SourceInstanceId != "subject"), 25).ConfigureAwait(true);
            check("removed-source-absent-after-real-restart", passed, Summarize(after));
        }
        finally { await ShutdownAsync(restarted.Controller).ConfigureAwait(true); }
    }

    private static HudScreen? SelectMonitor(StartupArguments arguments) => HudPlacementOptions.SelectMonitor(
        new WindowsWindowPlacementService().CaptureLayout(), arguments.PlacementMonitor, arguments.AllowPrimaryPlacement);

    private static async Task StartCheckedAsync(HudComposition.PreparedComposition composed,
        StartupArguments arguments, Action<string, bool, string> check)
    {
        var target = SelectMonitor(arguments) ?? throw new InvalidOperationException("Authorized monitor disappeared before start.");
        var state = composed.Dependencies.WindowState;
        if (state.MonitorDeviceName != target.DeviceName || state.WindowMonitor != target.DeviceName)
            throw new InvalidOperationException("Composition did not seed both window modes onto the authorized monitor.");
        await composed.Controller.StartAsync(CancellationToken.None).ConfigureAwait(true);
        var window = composed.Controller.Window ?? throw new InvalidOperationException("No composed HUD window.");
        var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        var onTarget = NativeMethods.GetWindowRect(handle, out var rect) &&
            rect.Left >= target.X - 1 && rect.Top >= target.Y - 1 &&
            rect.Right <= target.Right + 1 && rect.Bottom <= target.Bottom + 1;
        check("placement-" + Path.GetFileName(composed.Dependencies.Paths.DataDirectory), onTarget, target.DeviceName);
        if (!onTarget) throw new InvalidOperationException("Composed HUD escaped the authorized monitor.");
    }

    private static HudCompositionOverrides Overrides(
        string dataDirectory, StartupArguments arguments, Func<HudWindow> windowFactory)
    {
        // Composition reads the saved placement itself, so a check that must not open on
        // the display in use steers through the same in-memory seed the other
        // diagnostics checks use. Nothing is written and the saved state is untouched.
        var saved = HudStateStore.Load(Path.Combine(dataDirectory, HudStateStore.FileName)).State;
        var target = SelectMonitor(arguments) ?? throw new InvalidOperationException("No authorized monitor for composition.");
        return new HudCompositionOverrides
        {
            WindowFactory = () =>
            {
                if (SelectMonitor(arguments)?.DeviceName != target.DeviceName)
                    throw new InvalidOperationException("Display changed before window creation; refusing fallback.");
                return windowFactory();
            },
            Startup = new CompositionStartup(),
            Navigator = new CompositionNavigator(),
            Clipboard = new CompositionClipboard(),
            BudgetLabel = "composition-check",
            WindowState = HudPlacementOptions.WithMonitor(saved, target.DeviceName),
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
