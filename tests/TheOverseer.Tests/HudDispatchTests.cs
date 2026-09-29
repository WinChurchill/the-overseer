using Microsoft.Win32;
using TheOverseer.App;
using TheOverseer.App.Desktop;
using TheOverseer.App.Diagnostics;
using TheOverseer.App.Monitoring;
using TheOverseer.Core.Configuration;
using TheOverseer.Core.Diagnostics;
using TheOverseer.Core.History;
using TheOverseer.Core.Models;
using TheOverseer.Core.Voice;
using TheOverseer.Tests.Support;
using TheOverseer.UI;

namespace TheOverseer.Tests;

public sealed class HudDispatchTests
{
    [Fact]
    public void CompositeStartupDoesNotMarkHudOfflineAndLaterSnapshotsStillApply()
    {
        StaTestHost.Invoke(() =>
        {
            var dispatcher = new QueuedUiDispatcher();
            var source = new FakeMonitorSource();
            var composite = new CompositeMonitorSource(new[] { new CodexMonitorAdapter(source) });
            var controller = CreateController(dispatcher, composite, new FakePlacementService());
            try
            {
                controller.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
                dispatcher.DrainAll();
                Assert.False(composite.Completion.IsCompleted);
                source.Push(SyntheticHudScenarios.Mixed());
                dispatcher.DrainAll();
                Assert.False(controller.ViewModel.MonitoringUnavailable);
                Assert.NotEmpty(controller.ViewModel.Tasks);
                source.Push(SyntheticHudScenarios.HealthyEmpty());
                dispatcher.DrainAll();
                Assert.False(controller.ViewModel.MonitoringUnavailable);
                Assert.Empty(controller.ViewModel.Tasks);
                source.Lifetime.SetException(new InvalidOperationException("synthetic source fault"));
                dispatcher.DrainAll();
                Assert.True(controller.ViewModel.MonitoringUnavailable);
                Assert.Contains("source-faulted", controller.ViewModel.MonitoringDetail);
                Assert.False(composite.Completion.IsCompleted);
            }
            finally
            {
                StaTestHost.Complete(controller.DisposeAsync().AsTask());
                Assert.True(composite.Completion.IsCompleted);
                Cleanup();
            }
        });
    }

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
        pump.OnSnapshot(Snapshot(2));
        dispatcher.DrainAll();
        pump.OnSnapshot(Snapshot(3));
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
            StaTestHost.Complete(controller.ShutdownAsync());
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
            dispatcher.DrainAll();

            Assert.True(controller.ViewModel.MonitoringUnavailable, "a failed monitor start must surface as unavailable");
            Assert.Equal("?", controller.ViewModel.WorkingCountText);
            Assert.Contains("unknown", controller.ViewModel.SummaryText, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("monitor-faulted", controller.ViewModel.MonitoringDetail);
            StaTestHost.Complete(controller.ShutdownAsync());
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

            StaTestHost.Complete(controller.ShutdownAsync());

            Assert.True(monitor.Disposed);
            Assert.True(controller.IsShuttingDown);

            var state = HudStateStore.Load(StatePath);
            Assert.Equal(HudStateLoadStatus.Loaded, state.Status);
            Assert.True(state.State.IsExpanded);
            Cleanup();
        });
    }

    [Fact]
    public void MissingTrayKeepsTheWindowReachableAndTheMonitorRunning()
    {
        StaTestHost.Invoke(() =>
        {
            var dispatcher = new QueuedUiDispatcher();
            var monitor = new FakeMonitorSource();
            var controller = CreateController(dispatcher, monitor, new FakePlacementService());

            controller.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
            Assert.True(controller.Window!.IsVisible);

            controller.ToggleVisibility();
            Assert.True(controller.Window!.IsVisible);
            Assert.NotNull(controller.Window.ContextMenu);
            Assert.False(monitor.Disposed);

            monitor.Push(SyntheticHudScenarios.Mixed());
            dispatcher.DrainAll();
            Assert.True(controller.ViewModel.Tasks.Count > 0, "a hidden HUD keeps tracking tasks");

            controller.ToggleVisibility();
            Assert.True(controller.Window!.IsVisible);
            StaTestHost.Complete(controller.ShutdownAsync());
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
            controller.ViewModel.TaskView = "recent";
            controller.ViewModel.RecentHours = 8;
            StaTestHost.Complete(controller.ShutdownAsync());

            var config = MonitorConfigStore.Load(ConfigPath).Config;
            Assert.Equal("light", config.Hud.ThemeName);
            Assert.Equal(0.8, config.Hud.Opacity);
            Assert.False(config.Hud.AlwaysOnTop);
            Assert.Equal("recent", config.Hud.TaskView);
            Assert.Equal(8, config.Hud.RecentHours);
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
            Assert.Equal(392, controller.Window!.Width); // 360-DIP content plus the default 32-DIP right light.
            Assert.True(controller.Window.Height > collapsedDip, "expanded geometry must be taller");
            Assert.NotNull(placement.LastPlacement);
            StaTestHost.Complete(controller.ShutdownAsync());
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
            StaTestHost.Complete(controller.ShutdownAsync());
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
            StaTestHost.Complete(controller.ShutdownAsync());
            Cleanup();
        });
    }

    [Fact]
    public void HiddenStartupWithoutTrayShowsAControllableWindow()
    {
        StaTestHost.Invoke(() =>
        {
            var controller = CreateController(new QueuedUiDispatcher(), new FakeMonitorSource(), new FakePlacementService(), startHidden: true);
            controller.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
            Assert.True(controller.Window!.IsVisible);
            Assert.Contains(controller.Window.ContextMenu.Items.Cast<System.Windows.Controls.MenuItem>(), item => Equals(item.Header, "Exit"));
            StaTestHost.Complete(controller.DisposeAsync().AsTask());
            Cleanup();
        });
    }

    [Fact]
    public void CompletedMonitorCannotLeaveHealthyRowsOrAcceptLateSnapshots()
    {
        StaTestHost.Invoke(() =>
        {
            var dispatcher = new QueuedUiDispatcher();
            var monitor = new FakeMonitorSource();
            var controller = CreateController(dispatcher, monitor, new FakePlacementService());
            controller.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
            monitor.Push(SyntheticHudScenarios.Mixed());
            dispatcher.DrainAll();
            Assert.NotEmpty(controller.ViewModel.Tasks);
            monitor.Lifetime.SetResult();
            monitor.Push(SyntheticHudScenarios.HealthyEmpty());
            dispatcher.DrainAll();
            Assert.True(controller.ViewModel.MonitoringUnavailable);
            Assert.Contains("UNAVAILABLE", controller.ViewModel.HealthLine);
            Assert.Contains("monitor-stopped", controller.ViewModel.MonitoringDetail);
            Assert.Empty(controller.ViewModel.Tasks);
            Assert.Equal("?", controller.ViewModel.WorkingCountText);
            StaTestHost.Complete(controller.DisposeAsync().AsTask());
            Cleanup();
        });
    }

    [Fact]
    public void SlowPersistenceKeepsDispatcherResponsiveCoalescesAndFlushesLatestSettings()
    {
        StaTestHost.Invoke(() =>
        {
            using var writer = new BlockingPersistence();
            var controller = CreateController(new QueuedUiDispatcher(), new FakeMonitorSource(), new FakePlacementService(), persistence: writer);
            controller.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
            controller.SetOpacity(0.8);
            StaTestHost.Complete(writer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.False(writer.UsedUiThread);
            for (var i = 0; i < 100; i++) controller.SetOpacity(i % 2 == 0 ? 0.75 : 0.85);
            StaTestHost.Complete(Task.Delay(500));
            Assert.Equal(1, writer.Calls);
            writer.Release.Set();
            var shutdown = controller.ShutdownAsync();
            Assert.Same(shutdown, controller.ShutdownAsync());
            StaTestHost.Complete(shutdown);
            Assert.Equal(2, writer.Calls);
            Assert.Equal(0.85, writer.LastConfig!.Hud.Opacity);
            StaTestHost.Complete(controller.DisposeAsync().AsTask());
            Cleanup();
        });
    }

    [Fact]
    public void StalledSaveDoesNotPreventMonitorAndWindowShutdown()
    {
        StaTestHost.Invoke(() =>
        {
            using var writer = new BlockingPersistence();
            var monitor = new FakeMonitorSource();
            var controller = CreateController(new QueuedUiDispatcher(), monitor, new FakePlacementService(), persistence: writer);
            controller.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
            var shutdown = controller.ShutdownAsync();
            StaTestHost.Complete(shutdown.WaitAsync(TimeSpan.FromSeconds(4)));
            Assert.True(monitor.Disposed);
            Assert.Null(controller.Window);
            Assert.Equal(1, writer.Calls);
            writer.Release.Set();
            StaTestHost.Complete(writer.Finished.Task.WaitAsync(TimeSpan.FromSeconds(1)));
            StaTestHost.Complete(controller.DisposeAsync().AsTask());
            Cleanup();
        });
    }

    [Fact]
    public void SuspendDiscardsVoiceInlineAndResumeRepairsGeometryOnTheDispatcher()
    {
        StaTestHost.Invoke(() =>
        {
            var dispatcher = new QueuedUiDispatcher();
            var monitor = new FakeMonitorSource();
            var placement = new FakePlacementService();
            var key = VoiceTestData.Key();
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var provider = new FakeVoiceProvider { Gate = gate };
            var player = new FakeVoicePlayer();
            var policy = new VoiceEventPolicy(VoicePolicyOptions.Default with { Cooldown = TimeSpan.Zero });
            var manager = new VoiceManager(
                provider,
                player,
                VoiceManagerOptions.Default with { MinimumStartSpacing = TimeSpan.Zero },
                TimeProvider.System,
                revalidate: candidate => policy.IsRelevant(candidate, DateTimeOffset.UtcNow),
                muted: false);
            var coordinator = new NotificationCoordinator(
                new ActivityHistory(),
                policy,
                manager,
                TimeProvider.System,
                new VoiceSettings { Provider = "pack", Muted = false });
            var controller = CreateController(dispatcher, monitor, placement, notifications: coordinator);
            controller.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
            coordinator.OnSnapshot(VoiceTestData.Snapshot(
                DateTimeOffset.UtcNow,
                new[] { VoiceTestData.Task(key: key, state: AgentTaskState.NeedsApproval) },
                new[] { VoiceTestData.Event(AttentionEventKind.PermissionRequired, key: key, observedAt: DateTimeOffset.UtcNow) }));
            Assert.True(SpinUntil(() => provider.Calls == 1), "provider was not entered");

            dispatcher.DrainAll();
            var postsBeforeSuspend = dispatcher.PostCount;
            var appliesBeforeSuspend = placement.ApplyCount;

            // Suspend has to run inline: a posted callback would not run before
            // the machine sleeps, and stale audio would survive the wake.
            controller.NotifyPowerModeChanged(PowerModes.Suspend);
            Assert.Equal(postsBeforeSuspend, dispatcher.PostCount);
            Assert.Equal(appliesBeforeSuspend, placement.ApplyCount);
            Assert.False(coordinator.IsMuted);
            Assert.Equal(0, coordinator.Status.QueuedCount);
            gate.TrySetResult();
            Assert.True(SpinUntil(() => player.Played.Count == 0 && player.StopCount >= 1), "audio was not stopped");

            // Resume repairs geometry and reconciles through the UI thread.
            controller.NotifyPowerModeChanged(PowerModes.Resume);
            Assert.Equal(postsBeforeSuspend + 1, dispatcher.PostCount);
            Assert.False(monitor.ReconcileRequested);
            dispatcher.DrainAll();
            Assert.True(monitor.ReconcileRequested);
            Assert.True(placement.ApplyCount > appliesBeforeSuspend);

            // A display change takes the same repair path.
            var postsBeforeDisplay = dispatcher.PostCount;
            controller.NotifyDisplaySettingsChanged();
            Assert.Equal(postsBeforeDisplay + 1, dispatcher.PostCount);
            dispatcher.DrainAll();
            Assert.True(placement.ApplyCount > appliesBeforeSuspend + 1);

            StaTestHost.Complete(controller.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(4)));
            StaTestHost.Complete(coordinator.DisposeAsync().AsTask());
            StaTestHost.Complete(controller.DisposeAsync().AsTask());
            Cleanup();
        });
    }

    [Fact]
    public void CloseHidesToTrayWhileShutdownStillQuits()
    {
        StaTestHost.Invoke(() =>
        {
            var dispatcher = new QueuedUiDispatcher();
            var controller = CreateController(dispatcher, new FakeMonitorSource(), new FakePlacementService(), trayAvailable: true);
            controller.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
            dispatcher.DrainAll();

            var exitRequests = 0;
            controller.ExitRequested += (_, _) => exitRequests++;
            var window = controller.Window!;
            Assert.True(controller.CanHideToTray);

            // x, Alt+F4, the system menu and WM_CLOSE all hide while the tray works.
            window.Close();
            Assert.False(window.IsVisible);
            Assert.Equal(0, exitRequests);
            Assert.False(controller.IsShuttingDown);
            Assert.Same(window, controller.Window);

            // The explicit Exit path still runs the unconditional orderly shutdown.
            StaTestHost.Complete(controller.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(4)));
            Assert.True(controller.IsShuttingDown);
            Assert.Null(controller.Window);
            StaTestHost.Complete(controller.DisposeAsync().AsTask());
            Cleanup();
        });
    }

    [Fact]
    public void SessionEndingAndAMissingTrayNeverHideTheWindow()
    {
        StaTestHost.Invoke(() =>
        {
            var ending = CreateController(new QueuedUiDispatcher(), new FakeMonitorSource(), new FakePlacementService(), trayAvailable: true);
            ending.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
            var endingExits = 0;
            ending.ExitRequested += (_, _) => endingExits++;
            ending.NotifySessionEnding();
            Assert.False(ending.CanHideToTray);
            ending.Window!.Close();
            Assert.Equal(1, endingExits);
            StaTestHost.Complete(ending.DisposeAsync().AsTask());
            Cleanup();

            var noTray = CreateController(new QueuedUiDispatcher(), new FakeMonitorSource(), new FakePlacementService(), trayAvailable: false);
            noTray.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
            var noTrayExits = 0;
            noTray.ExitRequested += (_, _) => noTrayExits++;
            Assert.False(noTray.CanHideToTray);
            noTray.Window!.Close();
            Assert.Equal(1, noTrayExits);
            StaTestHost.Complete(noTray.DisposeAsync().AsTask());
            Cleanup();
        });
    }

    [Fact]
    public void ShowRequestRestoresTheSameHiddenWindowAndSession()
    {
        StaTestHost.Invoke(() =>
        {
            var dispatcher = new QueuedUiDispatcher();
            var controller = CreateController(dispatcher, new FakeMonitorSource(), new FakePlacementService(), trayAvailable: true);
            controller.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
            dispatcher.DrainAll();
            var window = controller.Window!;

            controller.HideToTray();
            Assert.False(window.IsVisible);

            // The second-launch signal restores the existing window, never a new one.
            controller.RequestShow();
            dispatcher.DrainAll();
            Assert.True(window.IsVisible);
            Assert.Same(window, controller.Window);

            StaTestHost.Complete(controller.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(4)));
            StaTestHost.Complete(controller.DisposeAsync().AsTask());
            Cleanup();
        });
    }

    [Fact]
    public void MenuOffersTheCleanTopLevelSetAndOnlyOffersHideToTrayWhenItWorks()
    {
        StaTestHost.Invoke(() =>
        {
            var withTray = CreateController(new QueuedUiDispatcher(), new FakeMonitorSource(), new FakePlacementService(), trayAvailable: true);
            withTray.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
            var menu = withTray.Window!.ContextMenu!;
            var items = menu.Items.Cast<System.Windows.Controls.MenuItem>().ToArray();

            Assert.Equal(
                new[] { "Always on top", "Expand all projects", "Collapse all projects", "Compact / expanded HUD",
                    "Light & voice…", "Projects & sources…", "Hide to tray", "Exit" },
                items.Select(item => item.Header!.ToString()).ToArray());
            Assert.All(items, item => Assert.Empty(item.Items));
            // Check state changes immediately, even with the menu closed and no actual tray.
            var pin = items.Single(item => Equals(item.Header, "Always on top"));
            Assert.True(pin.IsChecked);
            withTray.ToggleTopmost();
            Assert.False(pin.IsChecked);
            Assert.False(withTray.MenuState.Snapshot.AlwaysOnTop);
            withTray.ToggleWindowView();
            Assert.False(pin.IsChecked);
            withTray.ToggleTopmost();
            Assert.True(pin.IsChecked);
            Assert.True(withTray.MenuState.Snapshot.AlwaysOnTop);
            withTray.ToggleWindowView();
            Assert.False(pin.IsChecked);
            withTray.ToggleTopmost();

            Assert.True(pin.IsChecked);
            Assert.True(items.Single(item => Equals(item.Header, "Hide to tray")).IsEnabled);

            StaTestHost.Complete(withTray.DisposeAsync().AsTask());
            Cleanup();

            // A session with no tray never pretends hiding would be recoverable.
            var noTray = CreateController(new QueuedUiDispatcher(), new FakeMonitorSource(), new FakePlacementService(), trayAvailable: false);
            noTray.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
            Assert.False(noTray.Window!.ContextMenu!.Items.Cast<System.Windows.Controls.MenuItem>()
                .Single(item => Equals(item.Header, "Hide to tray")).IsEnabled);
            StaTestHost.Complete(noTray.DisposeAsync().AsTask());
            Cleanup();
        });
    }

    [Fact]
    public void TheCloseControlHidesToTrayWhileExitStillShutsDown()
    {
        StaTestHost.Invoke(() =>
        {
            var dispatcher = new QueuedUiDispatcher();
            var controller = CreateController(dispatcher, new FakeMonitorSource(), new FakePlacementService(), trayAvailable: true);
            controller.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
            dispatcher.DrainAll();
            var exits = 0;
            controller.ExitRequested += (_, _) => exits++;
            var window = controller.Window!;
            var close = (System.Windows.Controls.Button)window.FindName("CloseButton")!;
            Assert.Equal("Hide to tray", close.ToolTip);

            close.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));

            Assert.False(window.IsVisible);
            Assert.Equal(0, exits);
            Assert.False(controller.IsShuttingDown);

            // Exit stays explicit and unconditional.
            StaTestHost.Complete(controller.ShutdownAsync());
            Assert.True(controller.IsShuttingDown);
            StaTestHost.Complete(controller.DisposeAsync().AsTask());
            Cleanup();
        });
    }

    [Fact]
    public void AnUnsupportedHarnessOffersTheHonestCopyIdFallback()
    {
        StaTestHost.Invoke(() =>
        {
            var dispatcher = new QueuedUiDispatcher();
            var navigator = new FakeTaskNavigator
            {
                Result = new TaskNavigationResult(TaskNavigationOutcome.UnsupportedSurface,
                    "Open this task in its coding application — this harness has no supported route.", null),
            };
            var clipboard = new FakeClipboardWriter();
            var controller = CreateController(dispatcher, new FakeMonitorSource(), new FakePlacementService(), null, navigator, clipboard);
            controller.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
            var monitor = (FakeMonitorSource)controllerMonitor!;
            monitor.Push(SyntheticHudScenarios.Mixed());
            dispatcher.DrainAll();

            var dshRow = controller.ViewModel.Tasks.First(row => row.Key.HarnessId == HarnessIds.Dsh);
            Assert.False(dshRow.CanNavigate);
            controller.ViewModel.ActivateTaskCommand.Execute(dshRow);

            // No route is claimed and the honest action happened instead.
            Assert.Equal(dshRow.Key.ConversationId, clipboard.Text);
            Assert.Contains("copied", controller.ViewModel.FooterHint, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(TaskNavigationOutcome.UnsupportedSurface.ToString(), controller.NavigationCode);

            StaTestHost.Complete(controller.ShutdownAsync());
            Cleanup();
        });
    }

    [Fact]
    public void CollapsingAProjectKeepsMonitoringAndHistoryRunning()
    {
        StaTestHost.Invoke(() =>
        {
            var dispatcher = new QueuedUiDispatcher();
            var monitor = new FakeMonitorSource();
            var controller = CreateController(dispatcher, monitor, new FakePlacementService());
            controller.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
            monitor.Push(SyntheticHudScenarios.Mixed());
            dispatcher.DrainAll();
            var starts = monitor.StartCount;
            Assert.NotNull(controller.Window!.ContextMenu);

            controller.ViewModel.SetAllProjectsExpanded(false);
            Assert.All(controller.ViewModel.Projects, project => Assert.False(project.IsExpanded));
            Assert.NotEmpty(controller.ViewModel.Tasks);

            // A collapsed project is presentation only: the monitor keeps publishing,
            // rows keep updating and the next snapshot is still applied.
            monitor.Push(SyntheticHudScenarios.PartialUnknown());
            dispatcher.DrainAll();
            Assert.Equal(starts, monitor.StartCount);
            Assert.False(monitor.Disposed);
            Assert.All(controller.ViewModel.Projects, project => Assert.False(project.IsExpanded));
            Assert.NotEmpty(controller.ViewModel.Tasks);

            StaTestHost.Complete(controller.ShutdownAsync());
            Cleanup();
        });
    }

    [Fact]
    public void ProjectSourcesSettingsListDiscoveriesAndPersistAdditively()
    {
        StaTestHost.Invoke(() =>
        {
            var dispatcher = new QueuedUiDispatcher();
            var controller = CreateController(dispatcher, new FakeMonitorSource(), new FakePlacementService());
            controller.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
            var monitor = (FakeMonitorSource)controllerMonitor!;
            monitor.Push(SyntheticHudScenarios.Mixed());
            dispatcher.DrainAll();
            // The Recent view keeps the unassigned members visible, so the settings
            // surface can show that bucket alongside the named projects.
            controller.ViewModel.TaskView = "recent";

            controller.OpenProjectSources();
            var window = controller.ProjectSources;
            Assert.NotNull(window);
            var model = window!.ViewModel;

            // Discovered harness sources and the Unassigned bucket are both visible.
            Assert.Contains(model.Sources, source => source.HarnessId == HarnessIds.Codex);
            Assert.Contains(model.Sources, source => source.HarnessId == HarnessIds.Dsh);
            Assert.All(model.Sources, source => Assert.True(source.Enabled));
            Assert.Contains(model.Projects, project => project.Name == "Unassigned");
            Assert.Empty(model.DisabledHarnessSources);

            // Disabling one source is additive: every unrelated preference survives.
            var dsh = model.Sources.Single(source => source.HarnessId == HarnessIds.Dsh);
            dsh.Enabled = false;
            Assert.Equal(new[] { dsh.Key }, model.DisabledHarnessSources);

            StaTestHost.Complete(controller.ShutdownAsync());
            var saved = MonitorConfigStore.Load(ConfigPath).Config;
            Assert.Equal(new[] { dsh.Key }, saved.DisabledHarnessSources);
            Assert.Equal("C:\\synthetic", saved.CodexHome);
            Assert.Equal(32, saved.Limits.MaxTrackedFiles);
            Assert.Equal(HudSettings.Default.ThemeName, saved.Hud.ThemeName);
            Cleanup();
        });
    }

    private static bool SpinUntil(Func<bool> condition, int timeoutMs = 2000)
    {
        var started = Environment.TickCount64;
        while (Environment.TickCount64 - started < timeoutMs)
        {
            if (condition())
            {
                return true;
            }

            Thread.Sleep(5);
        }

        return condition();
    }

    private sealed class BlockingPersistence : IHudPersistence, IDisposable
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim Release { get; } = new(false);
        public bool UsedUiThread { get; private set; }
        public int Calls;
        public MonitorConfig? LastConfig { get; private set; }

        public HudSaveResult Save(HudControllerPaths paths, HudWindowState state, MonitorConfig config)
        {
            UsedUiThread |= StaTestHost.Dispatcher.CheckAccess();
            Interlocked.Increment(ref Calls);
            Entered.TrySetResult();
            if (!Release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("writer was not released");
            LastConfig = config;
            Finished.TrySetResult();
            return new(true, true);
        }

        public void Dispose() => Release.Dispose();
    }

    private static string _directory = string.Empty;
    private static IMonitorSource? controllerMonitor;

    private static string ConfigPath => Path.Combine(_directory, MonitorConfigStore.FileName);

    private static string StatePath => Path.Combine(_directory, HudStateStore.FileName);

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(1.75)]
    [InlineData(2.0)]
    [InlineData(2.5)]
    [InlineData(3.0)]
    public void WindowViewFitsLogicalWorkAreaAtEachScale(double scale)
    {
        StaTestHost.Invoke(() =>
        {
            var placement = new FakePlacementService { DpiOverride = scale,
                Layout = new HudScreenLayout(new[] { new HudScreen("scaled", -1280, 0, 1280, 720, scale) }) };
            var controller = CreateController(new QueuedUiDispatcher(), new FakeMonitorSource(), placement);
            controller.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
            controller.ToggleWindowView();
            Assert.True(controller.Window!.Width * scale <= 1282);
            Assert.True(controller.Window.Height * scale <= 722);
            Assert.Equal("scaled", placement.LastPlacement!.DeviceName);
            Assert.InRange(placement.LastPlacement.X, -1280, 0);
            StaTestHost.Complete(controller.ShutdownAsync());
            Cleanup();
        });
    }

    [Fact]
    public void WindowViewRetainsUserSizeAcrossLightUpdatesAndRoundTrips()
    {
        StaTestHost.Invoke(() =>
        {
            var placement = new FakePlacementService();
            var controller = CreateController(new QueuedUiDispatcher(), new FakeMonitorSource(), placement);
            controller.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
            var window = controller.Window!;
            var originalWidth = window.Width;
            controller.ToggleWindowView();
            Assert.True(controller.IsWindowView);
            Assert.Equal(System.Windows.ResizeMode.CanResize, window.ResizeMode);
            Assert.Equal(System.Windows.WindowStyle.SingleBorderWindow, window.WindowStyle);
            Assert.False(window.Topmost);
            window.Width = 610;
            window.Height = 810;
            controller.ViewModel.LightSize = 128;
            controller.ViewModel.IsExpanded = false;
            Assert.True(controller.ViewModel.IsExpanded);
            Assert.InRange(window.Width, 609, 611);
            Assert.InRange(window.Height, 809, 811);
            controller.ToggleTopmost();
            Assert.True(window.Topmost);
            controller.ToggleWindowView();
            Assert.False(controller.ViewModel.IsExpanded);
            Assert.Equal(originalWidth + 96, window.Width);
            controller.ToggleWindowView();
            Assert.InRange(window.Width, 609, 611);
            Assert.InRange(window.Height, 809, 811);
            Assert.True(window.Topmost);
            StaTestHost.Complete(controller.ShutdownAsync());
            var saved = HudStateStore.Load(StatePath).State;
            Assert.True(saved.WindowView);
            Assert.InRange(saved.WindowWidth, 609, 611);
            Assert.False(saved.IsExpanded);
            Cleanup();
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CompactResizePromotesOnlyCommittedBounds(bool commit)
    {
        StaTestHost.Invoke(() =>
        {
            var controller = CreateController(new QueuedUiDispatcher(), new FakeMonitorSource(), new FakePlacementService());
            controller.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
            var window = controller.Window!;
            var width = window.Width;
            var height = window.Height;
            void Gesture(string name) => typeof(HudApplicationController).GetMethod(name,
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(controller, null);
            Gesture("BeginNativeSizeMove");
            window.Width = 600;
            window.Height = 700;
            window.UpdateLayout();
            Assert.False(controller.IsWindowView);
            if (!commit) { window.Width = width; window.Height = height; window.UpdateLayout(); }
            Gesture("EndNativeSizeMove");
            Assert.Equal(commit, controller.IsWindowView);
            if (commit) { Assert.Equal(600, window.Width); Assert.True(controller.ViewModel.IsExpanded); }
            StaTestHost.Complete(controller.ShutdownAsync());
            Cleanup();
        });
    }

    [Fact]
    public void WindowViewRecoversRemovedMonitorWithoutResettingOnOrdinaryUpdates()
    {
        StaTestHost.Invoke(() =>
        {
            var dispatcher = new QueuedUiDispatcher();
            var placement = new FakePlacementService();
            var controller = CreateController(dispatcher, new FakeMonitorSource(), placement);
            controller.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
            controller.ToggleWindowView();
            controller.Window!.Width = 620;
            var before = placement.ApplyCount;
            controller.ViewModel.LightPosition = "top";
            Assert.Equal(before, placement.ApplyCount);
            placement.Layout = new HudScreenLayout(new[] { new HudScreen("replacement", -1000, 0, 1000, 700, 1) });
            controller.NotifyDisplaySettingsChanged();
            dispatcher.DrainAll();
            Assert.Equal("replacement", placement.LastPlacement!.DeviceName);
            Assert.True(controller.Window.Height <= 700);
            StaTestHost.Complete(controller.ShutdownAsync());
            Cleanup();
        });
    }

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
        IClipboardWriter? clipboard = null,
        bool startHidden = false,
        IHudPersistence? persistence = null,
        NotificationCoordinator? notifications = null,
        bool? trayAvailable = null)
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
            StartHidden: startHidden,
            TrayEnabled: false,
            Persistence: persistence,
            Notifications: notifications,
            TrayAvailableOverride: trayAvailable);
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
