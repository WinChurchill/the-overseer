using TheOverseer.Core.Models;
using TheOverseer.Core.Monitoring;

namespace TheOverseer.App.Monitoring;

/// <summary>Optional displayed-status source with at most one UIA call in flight.</summary>
public sealed class ClaudeProjectsMonitorAdapter : IAgentMonitorAdapter, IBudgetedMonitorAdapter
{
    private readonly Func<SourceBudget, DateTimeOffset, ClaudeProjectRead> _read;
    private readonly TimeProvider _clock;
    private readonly ClaudeProjectTracker _tracker;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _pollGate = new(1, 1);
    private SourceBudget _budget = SourceBudget.Default;
    private Task<ClaudeProjectRead>? _pendingRead;
    private Task? _loop;
    private int _started;
    private int _disposed;
    private readonly TimeSpan _readTimeout;
    private readonly ClaudeProjectsDesktopReader? _desktopReader;

    public ClaudeProjectsMonitorAdapter(Func<SourceBudget, DateTimeOffset, ClaudeProjectRead>? read = null,
        TimeProvider? clock = null, TimeSpan? readTimeout = null)
    {
        Source = new(HarnessIds.Claude, "projects-desktop", "Claude Projects (open overviews)");
        _clock = clock ?? TimeProvider.System;
        _desktopReader = read is null ? new ClaudeProjectsDesktopReader() : null;
        _read = read ?? _desktopReader!.Read;
        _readTimeout = readTimeout ?? TimeSpan.FromSeconds(1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_readTimeout, TimeSpan.Zero);
        _tracker = new(Source);
        Current = SourceSnapshot.Empty(Source, _clock.GetUtcNow(), "not-started", "claude-projects-overview");
    }

    public SourceInstance Source { get; }
    public SourceSnapshot Current { get; private set; }
    public event Action<SourceSnapshot>? SnapshotAvailable;

    public void ApplyBudget(SourceBudget budget)
    {
        if (_started != 0) throw new InvalidOperationException("Apply budget before starting the observer.");
        ArgumentOutOfRangeException.ThrowIfLessThan(budget.BytesPerPass, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(budget.MaximumSessions, 1);
        _budget = budget;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        if (Interlocked.Exchange(ref _started, 1) == 0) _loop = Task.Run(async () =>
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, cancellationToken);
            try
            {
                while (!linked.IsCancellationRequested)
                {
                    await PollAsync(linked.Token).ConfigureAwait(false);
                    await Task.Delay(TimeSpan.FromSeconds(2), _clock, linked.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
        }, CancellationToken.None);
        return Task.CompletedTask;
    }

    public void RequestReconcile() { /* The fixed polling loop is already bounded. */ }

    public async Task<SourceSnapshot> PollAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed != 0 || !await _pollGate.WaitAsync(0, cancellationToken).ConfigureAwait(false)) return Current;
        try
        {
            if (_disposed != 0) return Current;
            ClaudeProjectRead read;
            if (_pendingRead is { IsCompleted: false })
                read = new(Array.Empty<ClaudeProjectObservation>(), "projects-reader-busy");
            else
            {
                if (_pendingRead is not null)
                {
                    // Observe failures of a timed-out read, but never publish a
                    // late result as a new observation or create a second worker.
                    _ = _pendingRead.Exception;
                    _pendingRead = null;
                }
                var observedAt = _clock.GetUtcNow();
                _pendingRead = Task.Run(() => _read(_budget, observedAt), CancellationToken.None);
                _ = _pendingRead.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                try
                {
                    read = await _pendingRead.WaitAsync(_readTimeout, cancellationToken).ConfigureAwait(false);
                    _pendingRead = null;
                }
                catch (TimeoutException) { read = new(Array.Empty<ClaudeProjectObservation>(), "projects-reader-timeout"); }
                catch (Exception error) when (error is not OperationCanceledException)
                { _pendingRead = null; read = new(Array.Empty<ClaudeProjectObservation>(), "projects-reader-failed"); }
            }
            if (_disposed != 0) return Current;
            Current = _tracker.Apply(read, _clock.GetUtcNow(), _budget.MaximumSessions);
            SnapshotAvailable?.Invoke(Current);
            return Current;
        }
        finally { _pollGate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await _lifetime.CancelAsync().ConfigureAwait(false);
        if (_loop is not null)
            try { await _loop.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
            catch (Exception error) when (error is TimeoutException or OperationCanceledException) { }
        if (_desktopReader is { } reader)
        {
            _ = Task.Run(async () =>
            {
                await _pollGate.WaitAsync().ConfigureAwait(false);
                try
                {
                    try { if (_pendingRead is not null) await _pendingRead.ConfigureAwait(false); }
                    catch (Exception) { /* Failure is already qualified in snapshots. */ }
                    try { reader.Dispose(); }
                    catch (Exception error) when (error is System.Runtime.InteropServices.COMException or InvalidOperationException) { }
                }
                finally { _pollGate.Release(); }
            });
        }
        // A blocked COM provider can retain its one background worker. It cannot
        // hold the UI, queue further reads, or keep the application alive.
    }
}
