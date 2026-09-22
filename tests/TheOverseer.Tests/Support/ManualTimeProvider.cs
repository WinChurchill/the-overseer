namespace TheOverseer.Tests.Support;

/// <summary>
/// Deterministic clock for tests: no wall-clock sleeps and no dependence on
/// ambient time. Timers created through <see cref="CreateTimer"/> fire when the
/// test advances the clock, which is what the monitor loop waits on.
/// </summary>
public sealed class ManualTimeProvider : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<ManualTimer> _timers = new();
    private DateTimeOffset _now;

    public ManualTimeProvider(DateTimeOffset start)
    {
        _now = start;
    }

    public static ManualTimeProvider StartingAtEpoch { get; } =
        new(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero));

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            return _now;
        }
    }

    public override long GetTimestamp()
    {
        lock (_gate)
        {
            return _now.UtcTicks;
        }
    }

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state, dueTime, period);
        lock (_gate)
        {
            _timers.Add(timer);
        }

        return timer;
    }

    public void Advance(TimeSpan delta)
    {
        lock (_gate)
        {
            _now += delta;
        }

        Fire();
    }

    public void AdvanceSeconds(double seconds) => Advance(TimeSpan.FromSeconds(seconds));

    private void Fire()
    {
        List<ManualTimer> due;
        lock (_gate)
        {
            due = _timers.Where(timer => timer.IsDue(_now)).ToList();
        }

        foreach (var timer in due)
        {
            timer.Fire(_now);
        }
    }

    private sealed class ManualTimer : ITimer
    {
        private readonly ManualTimeProvider _owner;
        private readonly TimerCallback _callback;
        private readonly object? _state;
        private DateTimeOffset? _dueAt;
        private TimeSpan _period;
        private bool _disposed;

        public ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            _owner = owner;
            _callback = callback;
            _state = state;
            _period = period;
            _dueAt = dueTime == Timeout.InfiniteTimeSpan ? null : owner.GetUtcNow() + dueTime;
        }

        public bool IsDue(DateTimeOffset now)
        {
            lock (this)
            {
                return !_disposed && _dueAt is { } due && due <= now;
            }
        }

        public void Fire(DateTimeOffset now)
        {
            lock (this)
            {
                if (_disposed || _dueAt is null || _dueAt > now)
                {
                    return;
                }

                _dueAt = _period > TimeSpan.Zero ? now + _period : null;
            }

            _callback(_state);
        }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (this)
            {
                if (_disposed)
                {
                    return false;
                }

                _period = period;
                _dueAt = dueTime == Timeout.InfiniteTimeSpan ? null : _owner.GetUtcNow() + dueTime;
                return true;
            }
        }

        public void Dispose()
        {
            lock (this)
            {
                _disposed = true;
                _dueAt = null;
            }

            lock (_owner._gate)
            {
                _owner._timers.Remove(this);
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
