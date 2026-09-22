namespace TheOverseer.Core.Monitoring.State;

/// <summary>
/// Allocates small stable display numbers. Adapted from
/// codex-monitor-hud src-dotnet/CodexMonitorHud.Core/State/TaskNumberPool.cs
/// (revision df1d5b9e3d4b701de979b5c84a006db7cca5112f, MIT) with an injected
/// clock instead of ambient time and an explicit release cooldown.
/// </summary>
public sealed class TaskNumberPool
{
    private readonly Queue<ReleasedNumber> _released = new();
    private readonly HashSet<int> _releasedSet = new();
    private readonly TimeProvider _timeProvider;
    private int _nextNumber = 1;

    public TaskNumberPool(int maximumReleased = 512, TimeProvider? timeProvider = null)
    {
        MaximumReleased = Math.Max(32, maximumReleased);
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public int MaximumReleased { get; }

    public int ReleasedCount => _released.Count;

    public int NextNumber => _nextNumber;

    public int Acquire()
    {
        var now = _timeProvider.GetUtcNow();
        if (_released.TryPeek(out var candidate) && candidate.AvailableAt <= now)
        {
            _released.Dequeue();
            _releasedSet.Remove(candidate.Number);
            return candidate.Number;
        }

        return _nextNumber++;
    }

    public void Release(int number, TimeSpan cooldown)
    {
        if (number <= 0 || _releasedSet.Contains(number) || _released.Count >= MaximumReleased)
        {
            return;
        }

        if (cooldown < TimeSpan.Zero)
        {
            cooldown = TimeSpan.Zero;
        }

        _released.Enqueue(new ReleasedNumber(number, _timeProvider.GetUtcNow() + cooldown));
        _releasedSet.Add(number);
    }

    private sealed record ReleasedNumber(int Number, DateTimeOffset AvailableAt);
}
