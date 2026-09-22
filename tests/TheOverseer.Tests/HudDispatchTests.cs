using TheOverseer.App;
using TheOverseer.App.Desktop;
using TheOverseer.App.Diagnostics;
using TheOverseer.Core.Configuration;
using TheOverseer.Core.Diagnostics;
using TheOverseer.Core.Models;
using TheOverseer.Tests.Support;
using TheOverseer.UI;

namespace TheOverseer.Tests;

public sealed class HudDispatchTests
{
    [Fact]
    public void ProducerFloodCoalescesIntoOneDeliveryWithTheNewestSnapshot()
    {
        var dispatcher = new QueuedUiDispatcher();
        var applied = new List<MonitoringSnapshot>();
        var pump = new SnapshotPump(dispatcher, applied.Add);

        for (var index = 0; index < 50; index++)
        {
            pump.OnSnapshot(Snapshot(index));
        }

        Assert.Equal(50, pump.ReceivedCount);
        Assert.Equal(0, pump.AppliedCount);
        Assert.Equal(1, dispatcher.PendingCount);

        dispatcher.DrainAll();

        Assert.Equal(1, pump.AppliedCount);
        Assert.Single(applied);
        Assert.Equal(49, applied[0].Observation.FilesTracked);
    }

    [Fact]
    public void SnapshotsArrivingDuringApplyGetExactlyOneMoreDelivery()
    {
        var dispatcher = new QueuedUiDispatcher();
        var applied = 0;
        SnapshotPump? pump = null;
        pump = new SnapshotPump(dispatcher, _ =>
        {
            applied++;
            if (applied == 1)
            {
                pump!.OnSnapshot(Snapshot(100));
            }
        });

        pump.OnSnapshot(Snapshot(1));
        dispatcher.DrainAll();

        Assert.Equal(2, applied);
        Assert.Equal(2, pump.AppliedCount);
    }

    [Fact]
    public void StopDropsPendingDeliveryDuringShutdown()
    {
        var dispatcher = new QueuedUiDispatcher();
        var applied = 0;
        var pump = new SnapshotPump(dispatcher, _ => applied++);

        pump.OnSnapshot(Snapshot(1));
        pump.Stop();
        dispatcher.DrainAll();

        Assert.Equal(0, applied);
    }

    [Fact]
    public void ControllerAppliesSnapshotsThroughTheCoalescedDeliveryPath()
    {
        StaTestHost.Invoke(() =>
        {
            var dispatcher = new QueuedUiDispatcher();
            var monitor = new FakeMonitorSource();
            var placement = new FakePlacementService();
            var controller = CreateController(dispatcher, monitor, placement);

            controller.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
            monitor.Push(SyntheticHudScenarios.Mixed());
            Assert.Empty(controller.ViewModel.Tasks);
            Assert.Equal(1, dispatcher.PendingCount);

            dispatcher.DrainAll();

            Assert.True(controller.ViewModel.Tasks.Count > 0);
            Assert.True(controller.ViewModel.HasAppliedSnapshot);
            Assert.True(monitor.Started);
            controller.ShutdownAsync().GetAwaiter().GetResult();
            Cleanup();
        });
    }

    [Fact]
    public void MonitorStartFaultYieldsUnknownUiInsteadOfAFrozenSummary()
    {
        StaTestHost.Invoke(() =>
        {
            var dispatcher = new QueuedUiDispatcher();
            var monitor = new FaultingMonitorSource();
            var controller = CreateController(dispatcher, monitor, new FakePlacementService());

            controller.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
            for (var index = 0; index < 100 && !controller.ViewModel.MonitoringUnavailable; index++)
            {
                Thread.Sleep(20);
            }

            Assert.True(controller.ViewModel.MonitoringUnavailable, "a failed monitor start must surface as unavailable");
            Assert.Equal("?", controller.ViewModel.WorkingCountText);
            Assert.Contains("unknown", controller.ViewModel.SummaryText, StringComparison.OrdinalIgnoreCase);
            controller.ShutdownAsync().GetAwaiter().GetResult();
            Cleanup();
        });
    }

    [Fact]
    public void ShutdownUnsubscribesSavesStateAndDisposesTheMonitor()
    {
        StaTestHost.Invoke(() =>
        {
            var dispatcher = new QueuedUiDispatcher();
            var monitor = new FakeMonitorSource();
            var placement = new FakePlacementService();
            var controller = CreateController(dispatcher, monitor, placement);

            controller.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
            monitor.Push(SyntheticHudScenarios.Mixed());
            dispatcher.DrainAll();
            controller.ViewModel.IsExpanded = true;

            controller.ShutdownAsync().GetAwaiter().GetResult();

            Assert.True(monitor.Disposed);
            Assert.True(controller.IsShuttingDown);

            var state = HudStateStore.Load(StatePath);
            Assert.Equal(HudStateLoadStatus.Loaded, state.Status);
            Assert.True(state.State.IsExpanded);
            Cleanup();
        });
    }

    [Fact]
    public void HidingKeepsTheMonitorRunningAndDoesNotDisposeIt()
    {
        StaTestHost.Invoke(() =>
        {
            var dispatcher = new QueuedUiDispatcher();
            var monitor = new FakeMonitorSource();
            var controller = CreateController(dispatcher, monitor, new FakePlacementService());

            controller.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
            Assert.True(controller.Window!.IsVisible);

            controller.ToggleVisibility();
            Assert.False(controller.Window!.IsVisible);
            Assert.False(monitor.Disposed);

            monitor.Push(SyntheticHudScenarios.Mixed());
            dispatcher.DrainAll();
            Assert.True(controller.ViewModel.Tasks.Count > 0, "a hidden HUD keeps tracking tasks");

            controller.ToggleVisibility();
            Assert.True(controller.Window!.IsVisible);
            controller.ShutdownAsync().GetAwaiter().GetResult();
            Cleanup();
        });
    }

    [Fact]
    public void SettingsChangesPersistWithoutLosingMonitorFields()
    {
        StaTestHost.Invoke(() =>
        {
            var dispatcher = new QueuedUiDispatcher();
            var controller = CreateController(dispatcher, new FakeMonitorSource(), new FakePlacementService());

            controller.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
            controller.ViewModel.Theme = HudTheme.Light;
            controller.ViewModel.Opacity = 0.8;
            controller.ViewModel.AlwaysOnTop = false;
            controller.ShutdownAsync().GetAwaiter().GetResult();

            var config = MonitorConfigStore.Load(ConfigPath).Config;
            Assert.Equal("light", config.Hud.ThemeName);
            Assert.Equal(0.8, config.Hud.Opacity);
            Assert.False(config.Hud.AlwaysOnTop);
            Assert.Equal(32, config.Limits.MaxTrackedFiles);
            Assert.Equal("C:\\synthetic", config.CodexHome);
            Cleanup();
        });
    }

    [Fact]
    public void ExpandingReappliesBoundedGeometryOnThePlacementBoundary()
    {
        StaTestHost.Invoke(() =>
        {
            var dispatcher = new QueuedUiDispatcher();
            var placement = new FakePlacementService();
            var controller = CreateController(dispatcher, new FakeMonitorSource(), placement);

            controller.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
            var before = placement.ApplyCount;
            var collapsedDip = controller.Window!.Height;

            controller.ViewModel.IsExpanded = true;

            Assert.True(placement.ApplyCount > before, "expansion must re-apply bounded geometry");
            // WPF owns the size in DIPs; the placement boundary owns position.
            Assert.Equal(360, controller.Window!.Width);
            Assert.True(controller.Window.Height > collapsedDip, "expanded geometry must be taller");
            Assert.NotNull(placement.LastPlacement);
            controller.ShutdownAsync().GetAwaiter().GetResult();
            Cleanup();
        });
    }

    [Fact]
    public void StartWithWindowsTogglesThroughTheInjectedRegistration()
    {
        StaTestHost.Invoke(() =>
        {
            var dispatcher = new QueuedUiDispatcher();
            var startup = new FakeStartupRegistration();
            var controller = CreateController(dispatcher, new FakeMonitorSource(), new FakePlacementService(), startup);

            controller.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
            Assert.False(controller.ViewModel.StartWithWindows);

            controller.ToggleStartWithWindows();
            Assert.True(startup.LastRequest);
            Assert.True(controller.ViewModel.StartWithWindows);

            controller.ToggleStartWithWindows();
            Assert.False(startup.LastRequest);
            Assert.False(controller.ViewModel.StartWithWindows);
            controller.ShutdownAsync().GetAwaiter().GetResult();
            Cleanup();
        });
    }

    [Fact]
    public void TaskActivationRoutesThroughTheNavigatorAndCopiesOnlyOnRequest()
    {
        StaTestHost.Invoke(() =>
        {
            var dispatcher = new QueuedUiDispatcher();
            var navigator = new FakeTaskNavigator();
            var clipboard = new FakeClipboardWriter();
            var controller = CreateController(dispatcher, new FakeMonitorSource(), new FakePlacementService(), null, navigator, clipboard);

            controller.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
            var monitor = (FakeMonitorSource)controllerMonitor!;
            monitor.Push(SyntheticHudScenarios.Mixed());
            dispatcher.DrainAll();

            var row = controller.ViewModel.Tasks[0];
            controller.ViewModel.ActivateTaskCommand.Execute(row);
            Assert.NotNull(navigator.LastTask);
            Assert.Null(clipboard.Text);

            controller.ViewModel.CopyTaskIdCommand.Execute(row);
            Assert.Equal(row.Key.ConversationId, clipboard.Text);
            controller.ShutdownAsync().GetAwaiter().GetResult();
            Cleanup();
        });
    }

    private static string _directory = string.Empty;
    private static IMonitorSource? controllerMonitor;

    private static string ConfigPath => Path.Combine(_directory, MonitorConfigStore.FileName);

    private static string StatePath => Path.Combine(_directory, HudStateStore.FileName);

    private static void Cleanup()
    {
        try
        {
            if (_directory.Length > 0 && Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    private static HudApplicationController CreateController(
        IUiDispatcher dispatcher,
        IMonitorSource monitor,
        IWindowPlacementService placement,
        FakeStartupRegistration? startup = null,
        ITaskNavigator? navigator = null,
        IClipboardWriter? clipboard = null)
    {
        _directory = Path.Combine(Path.GetTempPath(), "overseer-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        controllerMonitor = monitor;
        var config = new MonitorConfig
        {
            CodexHome = "C:\\synthetic",
            Limits = new MonitorLimits { MaxTrackedFiles = 32 },
        };
        MonitorConfigStore.Save(ConfigPath, config);

        var dependencies = new HudControllerDependencies(
            monitor,
            dispatcher,
            placement,
            navigator ?? new FakeTaskNavigator(),
            startup ?? new FakeStartupRegistration(),
            clipboard ?? new FakeClipboardWriter(),
            NullMonitorLog.Instance,
            config,
            HudWindowState.Default,
            new HudControllerPaths(_directory, ConfigPath, StatePath),
            () => new HudWindow(),
            TimeProvider.System,
            StartHidden: false,
            TrayEnabled: false);
        return new HudApplicationController(dependencies);
    }

    private static MonitoringSnapshot Snapshot(int index) => CompleteSnapshot(index) with
    {
        Observation = LiveObservationStats.Empty with { FilesTracked = index },
    };

    private static MonitoringSnapshot CompleteSnapshot(int working) => new(
        new DateTimeOffset(2026, 1, 2, 12, 0, 0, TimeSpan.Zero),
        new MonitoringHealth(MonitoringHealthLevel.Healthy, Array.Empty<string>(), true, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch),
        new MonitoringCounts(
            CountValue.Known(working),
            CountValue.Known(0),
            CountValue.Known(0),
            CountValue.Known(0),
            CountValue.Known(0),
            CountValue.Known(0),
            CountValue.Known(0),
            CountValue.Known(working)),
        Array.Empty<AgentTask>(),
        Array.Empty<AttentionEvent>(),
        MonitorCapabilities.Initial,
        LiveObservationStats.Empty with { FilesTracked = working });
}
