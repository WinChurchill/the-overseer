using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using TheOverseer.App.Desktop;
using TheOverseer.App.Monitoring;
using TheOverseer.App.Voice;
using TheOverseer.Core.History;
using TheOverseer.Core.Configuration;
using TheOverseer.Core.Voice;
using TheOverseer.UI;
using TheOverseer.UI.ViewModels;

namespace TheOverseer.App.Diagnostics;

/// <summary>
/// Explicit, isolated HUD diagnostics. Fixture mode renders synthetic previews;
/// smoke mode runs the whole production composition against a synthetic monitor
/// and exits cleanly. Neither registers startup, reads rollout files, contacts
/// tasks, nor replaces production input.
/// </summary>
internal static class HudFixtureRunner
{
    private const double CollapsedWidth = 280;
    private const double CollapsedHeight = 72;
    private const double ExpandedWidth = 360;
    private const double ExpandedHeight = 480;

    /// <summary>Schedules the preview render on the UI thread; returns immediately.</summary>
    public static void StartFixture(StartupArguments arguments, Dispatcher dispatcher)
    {
        var output = arguments.OutputDirectory;
        if (string.IsNullOrWhiteSpace(output))
        {
            output = Path.Combine(DataDirectoryResolver.Resolve(arguments.DataDirectory), "ui-fixture");
        }

        dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
        {
            var exit = 0;
            try
            {
                exit = RenderFixture(Path.GetFullPath(output));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                Console.WriteLine("hud fixture failed: " + exception.GetType().Name);
                exit = 1;
            }
            finally
            {
                Application.Current?.Shutdown(exit);
            }
        }));
    }

    /// <summary>
    /// Muted coordinator plus strip source for fixture renders. Muted keeps audio
    /// out of the fixture entirely; only the observed-activity recording is used.
    /// </summary>
    private static (NotificationCoordinator Coordinator, CoordinatorActivityStripSource Source) CreateFixtureActivity()
    {
        var settings = new VoiceSettings { Muted = true, Provider = "pack" };
        var dataDirectory = Path.Combine(Path.GetTempPath(), "overseer-fixture-voice");
        var provider = new VoicePackProvider(dataDirectory, () => settings);
        var manager = new VoiceManager(
            provider,
            new WavPlaybackService(),
            VoiceManagerOptions.Default with { MinimumStartSpacing = TimeSpan.Zero },
            TimeProvider.System,
            muted: true);
        var coordinator = new NotificationCoordinator(
            new ActivityHistory(),
            new VoiceEventPolicy(),
            manager,
            TimeProvider.System,
            settings);
        coordinator.Start();
        return (coordinator, new CoordinatorActivityStripSource(coordinator, TimeProvider.System));
    }

    private static int RenderFixture(string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        var scenarios = SyntheticHudScenarios.Create();
        var activity = SyntheticHudScenarios.CreateActivitySeries();
        var voiceStatus = new VoiceStatus(false, "pack", "ok", 0, 2, 0, 0, 0, false, "Spoken 2.");
        var rendered = new List<string>();

        // Fixtures use the same production composition path for observed activity: a
        // muted coordinator records the synthetic snapshot producer-side and the strip
        // source projects it, so a render proves the real strip wiring. The fixture is a
        // finite process, so the muted coordinator lives for the whole render pass.
        var fixtureActivity = CreateFixtureActivity();
        fixtureActivity.Coordinator.OnSnapshot(scenarios[0]);

        foreach (var theme in new[] { HudTheme.Light, HudTheme.Dark })
        {
            foreach (var expanded in new[] { false, true })
            {
                var viewModel = new HudViewModel(HudSettings.Default with { Opacity = 1.0 });
                viewModel.UseActivitySource(fixtureActivity.Source);
                viewModel.ApplySnapshot(scenarios[0]);
                viewModel.ApplyActivity(activity, theme == HudTheme.Light && expanded ? VoiceStatus.Initial : voiceStatus);
                viewModel.IsExpanded = expanded;
                var path = Path.Combine(outputDirectory, theme.ToString().ToLowerInvariant() + "-" +
                    (expanded ? "expanded" : "collapsed") + ".png");
                Render(viewModel, theme, path);
                rendered.Add(path);
            }
        }

        // Project-surface cases: one project, several projects, and every project
        // collapsed, so the expandable row and its affordance are inspectable.
        foreach (var theme in new[] { HudTheme.Light, HudTheme.Dark })
        {
            foreach (var (name, snapshot, collapsed) in new[]
            {
                ("one-project", SyntheticHudScenarios.SingleProject(), false),
                ("collapsed-projects", scenarios[0], true),
            })
            {
                var viewModel = new HudViewModel(HudSettings.Default with { Opacity = 1.0 });
                viewModel.UseActivitySource(fixtureActivity.Source);
                viewModel.ApplySnapshot(snapshot);
                viewModel.ApplyActivity(activity, VoiceStatus.Initial);
                viewModel.IsExpanded = true;
                if (collapsed)
                {
                    viewModel.SetAllProjectsExpanded(false);
                }

                var path = Path.Combine(outputDirectory, theme.ToString().ToLowerInvariant() + "-" + name + ".png");
                Render(viewModel, theme, path);
                rendered.Add(path);
            }
        }

        // A staggered hour with a deliberate unobserved stretch: several categories,
        // an inferred tail, mixed projects and unknown coverage in one render.
        var hourActivity = CreateFixtureActivity();
        var hour = SyntheticHudScenarios.ActivityHour(SyntheticHudScenarios.Now);
        foreach (var step in hour)
        {
            hourActivity.Coordinator.OnSnapshot(step);
        }

        foreach (var theme in new[] { HudTheme.Light, HudTheme.Dark })
        {
            var viewModel = new HudViewModel(HudSettings.Default with { Opacity = 1.0, TaskView = "recent" });
            viewModel.UseActivitySource(hourActivity.Source);
            viewModel.ApplySnapshot(hour[^1]);
            viewModel.ApplyActivity(activity, VoiceStatus.Initial);
            viewModel.IsExpanded = true;
            var path = Path.Combine(outputDirectory, theme.ToString().ToLowerInvariant() + "-activity-hour.png");
            Render(viewModel, theme, path);
            rendered.Add(path);
        }

        foreach (var (name, snapshot) in new[]
        {
            ("dark-empty.png", SyntheticHudScenarios.HealthyEmpty()),
            ("dark-unavailable.png", SyntheticHudScenarios.Unavailable()),
        })
        {
            var viewModel = new HudViewModel(HudSettings.Default with { Opacity = 1.0 });
            viewModel.ApplySnapshot(snapshot);
            viewModel.ApplyActivity(TheOverseer.Core.History.ActivitySeries.Empty(snapshot.GeneratedAt), VoiceStatus.Initial);
            viewModel.IsExpanded = true;
            var path = Path.Combine(outputDirectory, name);
            Render(viewModel, HudTheme.Dark, path);
            rendered.Add(path);
        }

        foreach (var theme in new[] { HudTheme.Light, HudTheme.Dark })
        foreach (var taskView in new[] { "recent", "session" })
        {
            var viewModel = new HudViewModel(HudSettings.Default with { Opacity = 1, TaskView = taskView },
                sessionStartedAt: scenarios[0].GeneratedAt.AddMinutes(-10));
            viewModel.ApplySnapshot(scenarios[0]);
            viewModel.ApplyActivity(activity, VoiceStatus.Initial);
            viewModel.IsExpanded = true;
            var path = Path.Combine(outputDirectory, theme.ToString().ToLowerInvariant() + "-" + taskView + ".png");
            Render(viewModel, theme, path);
            rendered.Add(path);
        }

        foreach (var theme in new[] { HudTheme.Light, HudTheme.Dark })
        {
            foreach (var position in new[] { "top", "right" })
            {
                var vm = new HudViewModel(HudSettings.Default with { Opacity = 1, LightPosition = position, LightSize = 18 });
                vm.ApplySnapshot(scenarios[0]);
                vm.IsExpanded = position == "right";
                var path = Path.Combine(outputDirectory, theme.ToString().ToLowerInvariant() + "-light-" + position + ".png");
                Render(vm, theme, path);
                rendered.Add(path);
            }
            HudThemeManager.Apply(Application.Current, theme);
            var rows = VoiceEventCatalog.All.Select(item => new VoiceSetupRow(item, item.DefaultEnabled) { Status = "No usable recording — assign a WAV." }).ToArray();
            var settings = new NotificationSettingsWindow(new HudViewModel(HudSettings.Default), rows)
            {
                ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -4000, Top = -4000,
            };
            var settingsPath = Path.Combine(outputDirectory, theme.ToString().ToLowerInvariant() + "-settings.png");
            RenderWindow(settings, settingsPath);
            rendered.Add(settingsPath);
        }

        foreach (var theme in new[] { HudTheme.Light, HudTheme.Dark })
        foreach (var size in new[] { (320, 220), (280, 720), (320, 720), (440, 620), (480, 720), (900, 700), (640, 260), (1400, 900) })
        {
            HudThemeManager.Apply(Application.Current, theme);
            var vm = new HudViewModel(HudSettings.Default with { Opacity = 1, LightSize = 128 });
            vm.ApplySnapshot(scenarios[0]);
            vm.ApplyActivity(activity, voiceStatus);
            var window = new HudWindow { DataContext = vm, ShowActivated = false, ShowInTaskbar = false,
                Left = -4000, Top = -4000, Width = size.Item1, Height = size.Item2 };
            window.SetWindowView(true);
            var path = Path.Combine(outputDirectory, $"{theme.ToString().ToLowerInvariant()}-window-{size.Item1}x{size.Item2}.png");
            RenderWindow(window, path);
            rendered.Add(path);
        }

        Console.WriteLine("hud fixture rendered " + rendered.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) + " views:");
        foreach (var path in rendered) Console.WriteLine("  " + path);
        return 0;
    }

    private static void Render(HudViewModel viewModel, HudTheme theme, string path)
    {
        // A fresh visual tree produces an independent complete bitmap. Reusing
        // one window across synchronous captures can omit cached child visuals.
        HudThemeManager.Apply(Application.Current, theme);
        var window = new HudWindow
        {
            DataContext = viewModel, ShowActivated = false, ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -4000, Top = -4000,
            Width = (viewModel.IsExpanded ? ExpandedWidth : CollapsedWidth) + viewModel.LightExtraWidth,
            Height = (viewModel.IsExpanded ? ExpandedHeight : CollapsedHeight) + viewModel.LightExtraHeight,
        };
        RenderWindow(window, path);
    }

    private static void RenderWindow(Window window, string path)
    {
        try
        {
            window.Show();
            window.UpdateLayout();
            window.Dispatcher.Invoke(static () => { }, DispatcherPriority.ContextIdle);
            const double scale = 2.0;
            var bitmap = new RenderTargetBitmap(
                (int)Math.Ceiling(window.ActualWidth * scale),
                (int)Math.Ceiling(window.ActualHeight * scale),
                96 * scale, 96 * scale, PixelFormats.Pbgra32);
            bitmap.Render(window);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(path);
            encoder.Save(stream);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// Live, reversible check of the owned current-user startup entry. It reads
    /// the original value, enables, verifies, disables, verifies, then restores
    /// the original state exactly. Only the OVERSEER value is ever touched.
    /// </summary>
    public static int RunStartupCheck()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(WindowsStartupRegistry.RunKeyPath, writable: true);
        if (key is null) return 1;
        var name = StartupRegistration.ValueName;
        var existed = key.GetValueNames().Contains(name, StringComparer.OrdinalIgnoreCase);
        var original = key.GetValue(name, null, Microsoft.Win32.RegistryValueOptions.DoNotExpandEnvironmentNames);
        var kind = existed ? key.GetValueKind(name) : Microsoft.Win32.RegistryValueKind.String;
        var registration = new StartupRegistration(new WindowsStartupRegistry(), Environment.ProcessPath ?? string.Empty);
        var success = false;
        try
        {
            var enabled = registration.Set(true);
            var verified = registration.Read();
            var disabled = registration.Set(false);
            success = enabled.State == StartupRegistrationState.Enabled &&
                      verified.State == StartupRegistrationState.Enabled &&
                      disabled.State == StartupRegistrationState.Disabled;
            Console.WriteLine($"startup check: enable={enabled.State} verify={verified.State} disable={disabled.State}");
        }
        finally
        {
            // Preserve the original type and unexpanded value, even if checking fails.
            if (existed) key.SetValue(name, original!, kind);
            else key.DeleteValue(name, throwOnMissingValue: false);
        }

        var final = key.GetValue(name, null, Microsoft.Win32.RegistryValueOptions.DoNotExpandEnvironmentNames);
        var matches = existed
            ? key.GetValueKind(name) == kind && (original is Array left && final is Array right
                ? left.Cast<object>().SequenceEqual(right.Cast<object>()) : Equals(original, final))
            : !key.GetValueNames().Contains(name, StringComparer.OrdinalIgnoreCase);
        Console.WriteLine("startup original restored: " + matches);
        return success && matches ? 0 : 1;
    }

    /// <summary>Starts the finite integration smoke; completion shuts the app down.</summary>
    public static void StartSmoke(StartupArguments arguments, Dispatcher dispatcher)
    {
        var source = new SyntheticMonitorSource(SyntheticHudScenarios.Create());
        var dataDirectory = DataDirectoryResolver.Resolve(arguments.DataDirectory);
        Directory.CreateDirectory(dataDirectory);
        var configPath = Path.Combine(dataDirectory, MonitorConfigStore.FileName);
        var statePath = Path.Combine(dataDirectory, HudStateStore.FileName);

        var dependencies = new HudControllerDependencies(
            source,
            new WpfUiDispatcher(dispatcher),
            new WindowsWindowPlacementService(),
            new FakeNavigator(),
            new FakeStartupRegistration(),
            new FakeClipboardWriter(),
            TheOverseer.Core.Diagnostics.NullMonitorLog.Instance,
            MonitorConfigStore.Load(configPath).Config,
            HudStateStore.Load(statePath).State,
            new HudControllerPaths(dataDirectory, configPath, statePath),
            () => new HudWindow(),
            TimeProvider.System,
            StartHidden: false,
            TrayEnabled: !arguments.NoTray);

        var controller = new HudApplicationController(dependencies);
        Console.WriteLine("OVERSEER HUD smoke (synthetic input, isolated data, finite run)");
        Console.WriteLine("data-directory: " + dataDirectory);
        Console.WriteLine("duration: " + arguments.Duration.TotalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture) + "s");

        var advance = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromSeconds(3) };
        var scenario = 0;
        advance.Tick += (_, _) =>
        {
            scenario++;
            source.Push(scenario);
            controller.ViewModel.IsExpanded = scenario % 2 == 1;
            if (scenario == 2)
            {
                // Exercise a persisted display setting through the same path a
                // tray click uses.
                controller.ToggleTheme();
                controller.ViewModel.Opacity = 0.85;
            }
        };
        advance.Start();

        var finish = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = arguments.Duration };
        finish.Tick += async (_, _) =>
        {
            finish.Stop();
            advance.Stop();
            Console.WriteLine(controller.BuildStatusReport());
            await controller.ShutdownAsync().ConfigureAwait(true);
            Console.WriteLine("hud shutdown: clean");

            // Persistence is reported after shutdown has flushed it.
            var state = HudStateStore.Load(statePath);
            Console.WriteLine("hud persisted state: " + state.Status + " (" + state.ReasonCode + ") expanded=" + state.State.IsExpanded +
                              " monitor=" + (state.State.MonitorDeviceName.Length > 0 ? "saved" : "none"));
            var config = MonitorConfigStore.Load(configPath);
            Console.WriteLine("hud persisted config: " + config.Status + " (" + config.ReasonCode + ") theme=" + config.Config.Hud.ThemeName +
                              " opacity=" + config.Config.Hud.Opacity.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                              " topmost=" + config.Config.Hud.AlwaysOnTop);
            Application.Current?.Shutdown(0);
        };
        finish.Start();

        controller.ExitRequested += async (_, _) =>
        {
            finish.Stop();
            advance.Stop();
            await controller.ShutdownAsync().ConfigureAwait(true);
            Application.Current?.Shutdown(0);
        };

        _ = controller.StartAsync(CancellationToken.None);
    }

    private sealed class FakeNavigator : ITaskNavigator
    {
        public TaskNavigationResult Navigate(TheOverseer.Core.Models.AgentTask task, string profileId) =>
            new(TaskNavigationOutcome.Requested, "fixture: no real launch", null);
    }

    private sealed class FakeStartupRegistration : IStartupRegistration
    {
        public StartupRegistrationResult Read() => new(StartupRegistrationState.Disabled, "fixture");

        public StartupRegistrationResult Set(bool enabled) => new(StartupRegistrationState.Disabled, "fixture");
    }

    private sealed class FakeClipboardWriter : IClipboardWriter
    {
        public bool TrySetText(string text) => true;
    }
}
