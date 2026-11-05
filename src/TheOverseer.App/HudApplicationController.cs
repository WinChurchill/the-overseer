using System.IO;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using System.Threading.Channels;
using Microsoft.Win32;
using TheOverseer.App.Desktop;
using TheOverseer.App.Diagnostics;
using TheOverseer.App.Monitoring;
using TheOverseer.Core.Configuration;
using TheOverseer.Core.Diagnostics;
using TheOverseer.Core.Models;
using TheOverseer.Core.Voice;
using TheOverseer.UI;
using TheOverseer.UI.ViewModels;

namespace TheOverseer.App;

public sealed record HudControllerPaths(string DataDirectory, string ConfigPath, string StatePath);

public sealed record HudControllerDependencies(
    IMonitorSource Monitor,
    IUiDispatcher Dispatcher,
    IWindowPlacementService Placement,
    ITaskNavigator Navigator,
    IStartupRegistration Startup,
    IClipboardWriter Clipboard,
    IMonitorLog Log,
    MonitorConfig Config,
    HudWindowState WindowState,
    HudControllerPaths Paths,
    Func<HudWindow> WindowFactory,
    TimeProvider TimeProvider,
    bool StartHidden,
    bool TrayEnabled,
    IHudPersistence? Persistence = null,
    NotificationCoordinator? Notifications = null,
    bool? TrayAvailableOverride = null);

/// <summary>
/// Composition and lifetime owner for normal HUD mode: one monitor, one window,
/// one tray icon, coalesced snapshot delivery and serialized persistence.
/// </summary>
public sealed partial class HudApplicationController : IAsyncDisposable
{
    private static readonly HudSize ExpandedSize = new(360, 480);

    private readonly HudControllerDependencies _dependencies;
    private readonly SnapshotPump _pump;
    private Task _saveTask = Task.CompletedTask;
    private Task? _shutdownTask;
    private bool _disposed;
    private bool _monitorFailed;
    private readonly DispatcherTimer _ageTimer;
    private readonly DispatcherTimer _saveTimer;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly List<AgentTask> _lastTasks = new();
    private readonly Channel<(MonitorLogLevel Level, string Code)> _logQueue = Channel.CreateBounded<(MonitorLogLevel, string)>(
        new BoundedChannelOptions(32) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private readonly Task _logTask;

    private HudWindow? _window;
    private TrayService? _tray;
    private ShowRequestSignal? _showRequest;
    private IntPtr _handle;
    private double _dpiScale = 1.0;
    private bool _applyingGeometry;
    private HudWindowState _state;
    private bool _shuttingDown;
    private bool _sessionEnding;
    private volatile bool _showPending;
    private bool _suppressSettings;
    private bool _stateDirty;
    private int _trayFailures;
    private string _startupCode = "not-read";
    private string _navigationCode = "none";
    private IReadOnlyList<string> _disabledHarnessSources = Array.Empty<string>();
    private bool _claudeProjectsEnabled;

    public HudApplicationController(HudControllerDependencies dependencies)
    {
        _dependencies = dependencies;
        _voiceSettings = dependencies.Config.Voice.Normalized();
        _disabledHarnessSources = dependencies.Config.DisabledHarnessSources;
        _claudeProjectsEnabled = dependencies.Config.ClaudeProjectsEnabled &&
            !dependencies.Config.DisabledHarnessSources.Contains("claude/projects-desktop", StringComparer.OrdinalIgnoreCase);
        _logTask = Task.Run(async () =>
        {
            await foreach (var entry in _logQueue.Reader.ReadAllAsync())
                dependencies.Log.Write(entry.Level, entry.Code);
        });
        _state = (dependencies.WindowState ?? HudWindowState.Default).Normalized();
        ViewModel = new HudViewModel(dependencies.Config.Hud, OnActivateTask, OnCopyTaskId,
            dependencies.TimeProvider.GetUtcNow());
        if (dependencies.Notifications is { } notifications)
        {
            // Strips render producer-side observed activity; the UI only projects bins.
            ViewModel.UseActivitySource(new CoordinatorActivityStripSource(notifications, dependencies.TimeProvider));
        }
        _pump = new SnapshotPump(dependencies.Dispatcher, ApplySnapshot);
        _ageTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher.CurrentDispatcher)
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _ageTimer.Tick += OnAgeTick;
        _saveTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher.CurrentDispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(400),
        };
        _saveTimer.Tick += OnSaveTick;
        ViewModel.IsExpanded = _state.IsExpanded;
        _suppressSettings = true;
        try
        {
            // The shared mute state starts from the persisted voice settings.
            ViewModel.IsMuted = dependencies.Notifications?.IsMuted ?? dependencies.Config.Voice.Muted;
        }
        finally
        {
            _suppressSettings = false;
        }

        // The show listener starts with the controller, before any window exists:
        // a second launch that arrives during startup is queued, not lost.
        _showRequest = ShowRequestSignal.TryListen(OnShowRequested, _dependencies.Paths.DataDirectory);
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        ViewModel.SettingsChanged += (_, _) =>
        {
            OnViewModelSettingsChanged();
            RefreshTrayState();
        };
    }

    public HudViewModel ViewModel { get; }

    public HudMenuState MenuState { get; } = new();

    public HudWindow? Window => _window;

    public bool TrayAvailable { get; private set; }

    public string StartupCode => _startupCode;

    public string NavigationCode => _navigationCode;

    public bool IsShuttingDown => _shuttingDown;

    public event EventHandler? ExitRequested;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        ApplyTheme();
        CreateWindow();
        CreateTray();
        if (_showPending)
        {
            // A restore request arrived while the window was still being created.
            _showPending = false;
            RequestShow();
        }

        SyncStartupState();
        SubscribeSystemEvents();
        _ageTimer.Start();
        _dependencies.Notifications?.Start();
        SubscribeMonitor();
        RefreshTrayState();
        RefreshActivity(_dependencies.TimeProvider.GetUtcNow());
        return Task.CompletedTask;
    }

    // ---- monitor ----------------------------------------------------------------

    private void SubscribeMonitor()
    {
        _dependencies.Monitor.SnapshotAvailable += OnSnapshot;
        Task monitorTask;
        try
        {
            monitorTask = ObserveMonitorAsync();
        }
        catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException)
        {
            OnMonitorFailed("monitor-start");
            return;
        }

        _ = monitorTask.ContinueWith(
            task =>
            {
                _ = task.Exception; // Observe faults without logging potentially private exception text.
                var code = task.IsCanceled ? "monitor-cancelled" : task.IsFaulted ? "monitor-faulted" : "monitor-stopped";
                _dependencies.Dispatcher.Post(() => OnMonitorFailed(code));
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task ObserveMonitorAsync()
    {
        await _dependencies.Monitor.StartAsync(_lifetime.Token).ConfigureAwait(false);
        await _dependencies.Monitor.Completion.ConfigureAwait(false);
    }

    /// <summary>
    /// Producer callback. Plan 3 observation happens here, BEFORE the lossy UI
    /// pump, so every attention episode and clearance is seen even when the UI
    /// coalesces many snapshots into one render. Everything in this path is
    /// in-memory and bounded: no I/O, no HTTP, no audio and no Dispatcher work.
    /// </summary>
    private void OnSnapshot(MonitoringSnapshot snapshot)
    {
        _dependencies.Notifications?.OnSnapshot(snapshot);
        _pump.OnSnapshot(snapshot);
    }

    private void ApplySnapshot(MonitoringSnapshot snapshot)
    {
        if (_shuttingDown || _monitorFailed)
        {
            return;
        }

        RefreshActivity(snapshot.GeneratedAt);

        ViewModel.ApplySnapshot(snapshot);
        _lastTasks.Clear();
        _lastTasks.AddRange(snapshot.Tasks);
        RefreshTrayState();
    }

    private void OnMonitorFailed(string code)
    {
        if (_shuttingDown)
        {
            return;
        }

        _monitorFailed = true;
        _pump.Stop();
        _lastTasks.Clear();
        ViewModel.NotifyMonitorFailure(code);
        _dependencies.Notifications?.OnMonitorFailure(code);
        Log(MonitorLogLevel.Error, "hud-monitor-" + code);
        RefreshTrayState();
    }


}
