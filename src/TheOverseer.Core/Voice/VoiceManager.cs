using System.Net.Http;

namespace TheOverseer.Core.Voice;

/// <summary>
/// Owns the notification queue and the single serial playback loop. One
/// consumer, bounded queue, instant mute with generation fencing, and no
/// overlapping audio. It never blocks the caller and never touches the UI.
/// </summary>
public sealed class VoiceManager : IAsyncDisposable
{
    private readonly IVoiceProvider _provider;
    private readonly IVoicePlayer _player;
    private readonly VoiceManagerOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly Func<VoiceCandidate, bool>? _revalidate;
    private readonly object _gate = new();
    private readonly List<QueueItem> _queue = new();
    private readonly SemaphoreSlim _signal = new(0, 1);
    private readonly CancellationTokenSource _lifetime = new();

    private CancellationTokenSource? _active;
    private Task? _worker;
    private Task? _disposeTask;
    private bool _muted;
    private bool _speaking;
    private bool _disposed;
    private bool _lastAttemptFailed;
    private int _generation;
    private int _spoken;
    private int _dropped;
    private int _failed;
    private string _lastCode = "idle";
    private DateTimeOffset _lastStart = DateTimeOffset.MinValue;

    public VoiceManager(
        IVoiceProvider provider,
        IVoicePlayer player,
        VoiceManagerOptions? options = null,
        TimeProvider? timeProvider = null,
        Func<VoiceCandidate, bool>? revalidate = null,
        bool muted = true)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _player = player ?? throw new ArgumentNullException(nameof(player));
        _options = options ?? VoiceManagerOptions.Default;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _revalidate = revalidate;
        _muted = muted;
    }

    public bool IsMuted
    {
        get
        {
            lock (_gate)
            {
                return _muted;
            }
        }
    }

    public int QueuedCount
    {
        get
        {
            lock (_gate)
            {
                return _queue.Count;
            }
        }
    }

    public VoiceStatus Status
    {
        get
        {
            lock (_gate)
            {
                return new VoiceStatus(
                    _muted,
                    _provider.Id,
                    _lastCode,
                    _queue.Count,
                    _spoken,
                    _dropped,
                    _failed,
                    (_provider as FallbackVoiceProvider)?.FallbackCount ?? 0,
                    _speaking,
                    Describe());
            }
        }
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_worker is not null || _disposed) return;
            var token = _lifetime.Token;
            _worker = Task.Run(() => RunAsync(token));
        }
    }

    /// <summary>
    /// Instant mute/unmute. Muting clears queued work, cancels in-flight network
    /// and audio, and fences every late result so nothing can revive sound.
    /// </summary>
    public void SetMuted(bool muted)
    {
        CancellationTokenSource? active;
        lock (_gate)
        {
            if (_disposed || _muted == muted)
            {
                return;
            }

            _muted = muted;
            Interlocked.Increment(ref _generation);
            _lastCode = muted ? "muted" : "unmuted";
            _queue.Clear();
            active = _active;
        }

        if (muted)
        {
            try
            {
                active?.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            _player.Stop();
        }

        Signal();
    }

    /// <summary>
    /// Discards queued and in-flight work without touching the mute preference.
    /// Used when the machine suspends: cues captured before sleep are stale on
    /// resume, but the user's own mute setting must survive untouched. Latches
    /// live in the policy, so a condition that is still active after resume is
    /// not announced twice.
    /// </summary>
    public void DiscardPendingWork(string code)
    {
        CancellationTokenSource? active;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            Interlocked.Increment(ref _generation);
            _dropped += _queue.Count;
            _queue.Clear();
            _lastCode = code;
            active = _active;
        }

        try
        {
            active?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        _player.Stop();
    }

    /// <summary>Non-blocking enqueue. Called from the monitor callback path.</summary>
    public void Enqueue(IReadOnlyList<VoiceCandidate> candidates)
    {
        if (candidates.Count == 0)
        {
            return;
        }

        var added = false;
        lock (_gate)
        {
            if (_disposed || _muted)
            {
                // Muted conditions are latched silently so unmuting does not
                // turn the current state into a backlog.
                _dropped += candidates.Count;
                return;
            }

            var generation = Volatile.Read(ref _generation);
            foreach (var candidate in candidates)
            {
                var item = new QueueItem(candidate, Priority(candidate.Kind), _timeProvider.GetUtcNow(), generation);
                if (_queue.Count < Math.Max(1, _options.QueueCapacity))
                {
                    _queue.Add(item);
                    added = true;
                    continue;
                }

                var victim = FindVictim();
                if (victim is null || victim.Priority < item.Priority)
                {
                    _dropped++;
                    continue;
                }

                _queue.Remove(victim);
                _dropped++;
                _queue.Add(item);
                added = true;
            }
        }

        if (added)
        {
            Signal();
        }
    }

    private void Signal()
    {
        try
        {
            _signal.Release();
        }
        catch (ObjectDisposedException)
        {
        }
        catch (SemaphoreFullException)
        {
        }
    }

    private QueueItem? FindVictim()
    {
        QueueItem? worst = null;
        foreach (var item in _queue)
        {
            if (worst is null)
            {
                worst = item;
                continue;
            }

            // Drop the lowest priority, then the oldest of that priority.
            if (item.Priority > worst.Priority ||
                (item.Priority == worst.Priority && item.EnqueuedAt < worst.EnqueuedAt))
            {
                worst = item;
            }
        }

        return worst;
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await _signal.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            while (TryDequeue(out var item))
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                await ProcessAsync(item, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private bool TryDequeue(out QueueItem item)
    {
        lock (_gate)
        {
            if (_queue.Count == 0)
            {
                item = default!;
                return false;
            }

            var bestIndex = 0;
            for (var index = 1; index < _queue.Count; index++)
            {
                var candidate = _queue[index];
                var best = _queue[bestIndex];
                if (candidate.Priority < best.Priority ||
                    (candidate.Priority == best.Priority && candidate.EnqueuedAt < best.EnqueuedAt))
                {
                    bestIndex = index;
                }
            }

            item = _queue[bestIndex];
            _queue.RemoveAt(bestIndex);
            return true;
        }
    }

    private async Task ProcessAsync(QueueItem item, CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        lock (_gate)
        {
            if (_disposed || _muted || cancellationToken.IsCancellationRequested || item.Generation != Volatile.Read(ref _generation))
            {
                _dropped++;
                return;
            }
        }

        if (now - item.Candidate.ObservedAt > _options.CandidateTtl)
        {
            IncrementDropped("expired");
            return;
        }

        if (_revalidate is not null && !_revalidate(item.Candidate))
        {
            IncrementDropped("invalidated");
            return;
        }

        var spacing = _options.MinimumStartSpacing - (now - _lastStart);
        if (spacing > TimeSpan.Zero)
        {
            try
            {
                await Task.Delay(spacing, _timeProvider, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            lock (_gate)
            {
                if (_disposed || _muted || cancellationToken.IsCancellationRequested || item.Generation != Volatile.Read(ref _generation))
                {
                    _dropped++;
                    return;
                }
            }
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (_gate)
        {
            if (_disposed || _muted || cancellationToken.IsCancellationRequested || item.Generation != Volatile.Read(ref _generation))
            {
                _dropped++;
                return;
            }

            _active = linked;
            _speaking = true;
        }

        try
        {
            if (!StillRelevant(item.Candidate)) { IncrementDropped("invalidated"); return; }
            var clip = await _provider.TryCreateAsync(item.Candidate, linked.Token).ConfigureAwait(false);
            if (clip is null)
            {
                IncrementFailed(_provider.Status.LastCode, item.Generation);
                return;
            }

            lock (_gate)
            {
                if (_disposed || _muted || cancellationToken.IsCancellationRequested || item.Generation != Volatile.Read(ref _generation))
                {
                    _dropped++;
                    return;
                }

                _lastStart = _timeProvider.GetUtcNow();
            }

            linked.Token.ThrowIfCancellationRequested();
            if (!StillRelevant(item.Candidate)) { IncrementDropped("invalidated"); return; }
            var played = await _player.PlayAsync(clip, linked.Token).ConfigureAwait(false);
            lock (_gate)
            {
                if (_disposed || _muted || linked.IsCancellationRequested || item.Generation != _generation) return;
                if (played)
                {
                    _lastAttemptFailed = false;
                    _spoken++;
                    _lastCode = clip.DetailCode.Length > 0 ? clip.DetailCode : "spoken";
                }
                else
                {
                    _lastAttemptFailed = true;
                    _failed++;
                    _lastCode = "playback-failed";
                }
            }
        }
        catch (OperationCanceledException)
        {
            lock (_gate)
            {
                // Only a cancellation of the current generation describes the
                // present state; a late cancellation from work that mute,
                // suspend, or shutdown already fenced must not relabel it.
                if (item.Generation == Volatile.Read(ref _generation))
                {
                    _lastCode = "cancelled";
                }
            }
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or InvalidOperationException or TimeoutException or HttpRequestException or NotSupportedException)
        {
            IncrementFailed("provider-error", item.Generation);
        }
        finally
        {
            lock (_gate)
            {
                _speaking = false;
                if (ReferenceEquals(_active, linked))
                {
                    _active = null;
                }
            }
        }
    }

    private bool StillRelevant(VoiceCandidate candidate) =>
        _timeProvider.GetUtcNow() - candidate.ObservedAt <= _options.CandidateTtl &&
        (_revalidate?.Invoke(candidate) ?? true);

    private void IncrementDropped(string code)
    {
        lock (_gate)
        {
            _dropped++;
            _lastCode = code;
        }
    }

    private void IncrementFailed(string code, int generation)
    {
        lock (_gate)
        {
            if (_disposed || generation != _generation) return;
            _lastAttemptFailed = true;
            _failed++;
            _lastCode = string.IsNullOrEmpty(code) ? "provider-unavailable" : code;
        }
    }

    private string Describe()
    {
        if (_muted)
        {
            return "Voice is muted.";
        }

        if (_lastAttemptFailed)
        {
            return "Voice unavailable: " + _lastCode + ".";
        }

        if (!_provider.Status.IsAvailable)
        {
            return "Voice provider unavailable: " + _provider.Status.LastCode + ".";
        }

        return _spoken == 0 ? "Voice ready." : "Spoken " + _spoken + ".";
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposeTask is null)
            {
                _disposed = true;
                _generation++;
                _queue.Clear();
                _disposeTask = DisposeCoreAsync();
            }
            return new ValueTask(_disposeTask);
        }
    }

    private async Task DisposeCoreAsync()
    {
        try
        {
            _lifetime.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        try
        {
            _active?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        _player.Stop();
        Signal();

        if (_worker is not null)
        {
            try
            {
                await _worker.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                _ = _worker.ContinueWith(_ => DisposeResources(), TaskScheduler.Default);
                return;
            }
            catch (OperationCanceledException)
            {
            }
        }

        DisposeResources();
    }

    private void DisposeResources()
    {
        (_provider as IDisposable)?.Dispose();
        (_player as IDisposable)?.Dispose();
        _lifetime.Dispose();
        _signal.Dispose();
    }

    private static int Priority(VoiceEventKind kind) => kind switch
    {
        VoiceEventKind.PermissionRequired => 0,
        VoiceEventKind.InputRequired => 1,
        VoiceEventKind.TaskError => 2,
        VoiceEventKind.AllTasksCompleted => 3,
        _ => 4,
    };

    private sealed record QueueItem(VoiceCandidate Candidate, int Priority, DateTimeOffset EnqueuedAt, int Generation);
}
