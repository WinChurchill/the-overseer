using TheOverseer.App;
using TheOverseer.App.Desktop;
using TheOverseer.App.Diagnostics;
using TheOverseer.Core.Configuration;
using TheOverseer.Core.Models;

namespace TheOverseer.Tests.Support;

public sealed class FakeMonitorSource : IMonitorSource
{
    private readonly List<MonitoringSnapshot> _snapshots = new();

    public event Action<MonitoringSnapshot>? SnapshotAvailable;

    public MonitoringSnapshot Current { get; private set; } = MonitoringSnapshot.Empty(DateTimeOffset.UnixEpoch);

    public bool Started { get; private set; }

    public bool ReconcileRequested { get; private set; }

    public bool Disposed { get; private set; }

    public bool FailStart { get; init; }

    public int StartCount { get; private set; }
    public TaskCompletionSource Lifetime { get; } = new();
    public Task Completion => Lifetime.Task;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        StartCount++;
        Started = true;
        if (FailStart)
        {
            throw new InvalidOperationException("synthetic start failure");
        }

        return Task.CompletedTask;
    }

    public void RequestReconcile() => ReconcileRequested = true;

    public void Push(MonitoringSnapshot snapshot)
    {
        Current = snapshot;
        _snapshots.Add(snapshot);
        SnapshotAvailable?.Invoke(snapshot);
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        Lifetime.TrySetResult();
        return ValueTask.CompletedTask;
    }
}

/// <summary>Failing monitor used to prove the HUD shows unknown instead of a frozen summary.</summary>
public sealed class FaultingMonitorSource : IMonitorSource
{
    public Task Completion => Task.CompletedTask;
    public event Action<MonitoringSnapshot>? SnapshotAvailable;

    public MonitoringSnapshot Current { get; } = MonitoringSnapshot.Empty(DateTimeOffset.UnixEpoch);

    public Task StartAsync(CancellationToken cancellationToken) =>
        Task.FromException(new InvalidOperationException("synthetic monitor fault"));

    public void RequestReconcile()
    {
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public void Raise() => SnapshotAvailable?.Invoke(Current);
}

public sealed class FakePlacementService : IWindowPlacementService
{
    public HudScreenLayout Layout { get; set; } = new(new[]
    {
        new HudScreen(@"\\.\DISPLAY1", 0, 0, 1920, 1040, 1.0),
    });

    public double DpiOverride { get; set; } = 1.0;

    public HudPhysicalPlacement? LastPlacement { get; private set; }

    public bool LastShowWithoutActivation { get; private set; }

    public bool LastTopmost { get; private set; }

    public int ApplyCount { get; private set; }

    public HudWindowState? Captured { get; private set; }

    public HudScreenLayout CaptureLayout() => Layout;

    public double GetDpiScale(IntPtr windowHandle, HudScreen? screen) => DpiOverride;

    public void Apply(IntPtr windowHandle, HudPhysicalPlacement placement, bool topmost, bool showWithoutActivation)
    {
        LastPlacement = placement;
        LastTopmost = topmost;
        LastShowWithoutActivation = showWithoutActivation;
        ApplyCount++;
    }

    public HudWindowState CaptureState(IntPtr windowHandle, bool isExpanded, double dpiScale, DateTimeOffset now)
    {
        var placement = LastPlacement ?? HudPlacementPolicy.Resolve(HudWindowState.Default, Layout, HudSize.Collapsed, dpiScale);
        var screen = Layout.Find(placement.DeviceName) ?? (Layout.IsEmpty ? null : Layout.Primary);
        Captured = new HudWindowState
        {
            IsExpanded = isExpanded,
            MonitorDeviceName = screen?.DeviceName ?? string.Empty,
            OffsetXDips = screen is null ? 0 : Math.Round((placement.X - screen.X) / Math.Max(0.5, dpiScale), 2),
            OffsetYDips = screen is null ? 0 : Math.Round((placement.Y - screen.Y) / Math.Max(0.5, dpiScale), 2),
            DpiScale = dpiScale,
            SavedAtUtc = now,
        };
        return Captured;
    }
}

public sealed class FakeStartupRegistration : IStartupRegistration
{
    public StartupRegistrationState State { get; set; } = StartupRegistrationState.Disabled;

    public string ReasonCode { get; set; } = "fake";

    public bool? LastRequest { get; private set; }

    public int SetCount { get; private set; }

    public StartupRegistrationResult Read() => new(State, ReasonCode);

    public StartupRegistrationResult Set(bool enabled)
    {
        LastRequest = enabled;
        SetCount++;
        State = enabled ? StartupRegistrationState.Enabled : StartupRegistrationState.Disabled;
        ReasonCode = enabled ? "registered" : "removed";
        return new StartupRegistrationResult(State, ReasonCode);
    }
}

public sealed class FakeStartupRegistry : IStartupRegistry
{
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

    public int WriteCount { get; private set; }

    public int DeleteCount { get; private set; }

    public string? Read(string valueName) => _values.TryGetValue(valueName, out var value) ? value : null;

    public void Write(string valueName, string value)
    {
        WriteCount++;
        _values[valueName] = value;
    }

    public void Delete(string valueName)
    {
        DeleteCount++;
        _values.Remove(valueName);
    }

    public IReadOnlyDictionary<string, string> Values => _values;

    public void Seed(string valueName, string value) => _values[valueName] = value;
}

public sealed class FakeTaskLaunchBoundary : ITaskLaunchBoundary
{
    public bool Succeed { get; set; } = true;

    public string? ErrorCode { get; set; }

    public Uri? LastUri { get; private set; }

    public int LaunchCount { get; private set; }

    public bool TryLaunch(Uri uri, out string? errorCode)
    {
        LastUri = uri;
        LaunchCount++;
        errorCode = Succeed ? null : ErrorCode ?? "no-handler";
        return Succeed;
    }
}

public sealed class FakeTaskNavigator : ITaskNavigator
{
    public TaskNavigationResult? Result { get; set; }

    public AgentTask? LastTask { get; private set; }

    public TaskNavigationResult Navigate(AgentTask task, string profileId)
    {
        LastTask = task;
        return Result ?? new TaskNavigationResult(TaskNavigationOutcome.Requested, "fake", null);
    }
}

public sealed class FakeClipboardWriter : IClipboardWriter
{
    public string? Text { get; private set; }

    public bool Succeed { get; set; } = true;

    public bool TrySetText(string text)
    {
        Text = text;
        return Succeed;
    }
}

/// <summary>Queued dispatcher so coalescing can be tested without a message loop.</summary>
public sealed class QueuedUiDispatcher : IUiDispatcher
{
    private readonly Queue<Action> _queue = new();

    public int PostCount { get; private set; }

    public bool CheckAccess() => true;

    public void Post(Action action)
    {
        PostCount++;
        _queue.Enqueue(action);
    }

    public int PendingCount => _queue.Count;

    public void DrainAll()
    {
        while (_queue.Count > 0)
        {
            _queue.Dequeue()();
        }
    }

    public void DrainOne()
    {
        if (_queue.Count > 0)
        {
            _queue.Dequeue()();
        }
    }
}
