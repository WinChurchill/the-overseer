using TheOverseer.Core.Configuration;
using TheOverseer.Core.Models;

namespace TheOverseer.Core.Voice;

/// <summary>Bounded, deterministic lifecycle cues. Never interprets task inactivity as a request.</summary>
public sealed class VoiceLifecyclePolicy
{
    private readonly Dictionary<TaskKey, Waiting> _waiting = new();
    private MonitoringSnapshot? _latest;
    private bool _baseline, _startupConsumed, _available, _lossConsumed, _lossPlayed;
    private DateTimeOffset? _lostAt;
    private long _episode;
    private readonly Dictionary<VoiceEventKind, VoiceCandidate> _current = new();

    public IReadOnlyList<VoiceCandidate> Observe(MonitoringSnapshot snapshot, VoiceSettings settings, bool audible)
    {
        if (_latest is not null && snapshot.GeneratedAt < _latest.GeneratedAt) return Array.Empty<VoiceCandidate>();
        _latest = snapshot;
        var now = snapshot.GeneratedAt;
        var ready = new List<VoiceCandidate>();
        var available = snapshot.Health.Level != MonitoringHealthLevel.Unavailable;
        if (available && !_available)
        {
            _current.Remove(VoiceEventKind.MonitoringLost);
            if (_lossPlayed)
            {
                Add(VoiceEventKind.MonitoringRestored, now, settings, audible, ready);
                if (audible && settings.IsEnabled(VoiceEventKind.MonitoringRestored)) _startupConsumed = true;
            }
            _lostAt = null;
            _lossConsumed = _lossPlayed = false;
        }
        if (!available && _lostAt is null) BeginLoss(now);
        _available = available;
        if (available && !_startupConsumed)
        {
            _startupConsumed = true;
            Add(VoiceEventKind.SystemOnline, now, settings, audible, ready);
        }
        if (!available) _current.Remove(VoiceEventKind.SystemOnline);

        var present = new HashSet<TaskKey>();
        foreach (var task in snapshot.Tasks.Take(512))
        {
            if (!available || !IsWaiting(task)) continue;
            present.Add(task.Key);
            if (!_waiting.TryGetValue(task.Key, out var waiting) || waiting.Turn != task.TurnId)
                _waiting[task.Key] = new Waiting(task.TurnId, now, !_baseline || !audible || !settings.IsEnabled(VoiceEventKind.StillWaiting) ||
                    !snapshot.Transitions.Any(item => item.Key == task.Key && item.TurnId == task.TurnId &&
                        item.Kind is AttentionEventKind.PermissionRequired or AttentionEventKind.InputRequired &&
                        now - item.ObservedAt >= TimeSpan.Zero && now - item.ObservedAt <= TimeSpan.FromSeconds(30)));
        }
        foreach (var key in _waiting.Keys.Where(key => !present.Contains(key)).ToArray()) _waiting.Remove(key);
        _baseline = true;
        ready.AddRange(Pump(now, settings, audible));
        return ready;
    }

    public void Fail(DateTimeOffset now)
    {
        _available = false;
        _latest = null;
        _waiting.Clear();
        if (_lostAt is null) BeginLoss(now);
    }

    private void BeginLoss(DateTimeOffset now)
    {
        _lostAt = now;
        _episode++;
        _current.Clear();
        _lossConsumed = _lossPlayed = false;
    }

    public IReadOnlyList<VoiceCandidate> Pump(DateTimeOffset now, VoiceSettings settings, bool audible)
    {
        var ready = new List<VoiceCandidate>();
        if (!audible) Suppress();
        if (!_available && !_lossConsumed && _lostAt is { } lost && now - lost >= TimeSpan.FromSeconds(10))
        {
            _lossConsumed = true;
            Add(VoiceEventKind.MonitoringLost, now, settings, audible, ready);
        }
        if (_available && _latest is { } snapshot && now - snapshot.GeneratedAt <= TimeSpan.FromSeconds(5))
        {
            foreach (var task in snapshot.Tasks.Take(512))
            {
                if (!_waiting.TryGetValue(task.Key, out var waiting) || waiting.Consumed || !IsWaiting(task)) continue;
                if (now - waiting.Since < TimeSpan.FromMinutes(5)) continue;
                waiting.Consumed = true;
                if (audible && settings.IsEnabled(VoiceEventKind.StillWaiting))
                    ready.Add(new VoiceCandidate(VoiceEventKind.StillWaiting, task.Key, task.Number, "Task", task.TurnId, now, "reminder", false, 1, ""));
            }
        }
        return ready;
    }

    public void Suppress()
    {
        _startupConsumed = true;
        _lossPlayed = false;
        if (_lostAt is not null) _lossConsumed = true;
        _current.Clear();
        foreach (var waiting in _waiting.Values) waiting.Consumed = true;
    }

    public void Played(VoiceCandidate candidate)
    {
        if (candidate.Kind == VoiceEventKind.MonitoringLost && !_available &&
            _current.TryGetValue(candidate.Kind, out var loss) && loss.Identity == candidate.Identity) _lossPlayed = true;
    }

    public bool IsRelevant(VoiceCandidate candidate, DateTimeOffset now)
    {
        if (now - candidate.ObservedAt > TimeSpan.FromSeconds(30)) return false;
        if (candidate.Kind == VoiceEventKind.StillWaiting)
            return _available && _latest is { } snapshot && now - snapshot.GeneratedAt <= TimeSpan.FromSeconds(5) &&
                snapshot.Tasks.Any(task => task.Key == candidate.Key && task.TurnId == candidate.TurnId && IsWaiting(task));
        return _current.TryGetValue(candidate.Kind, out var current) && current.Identity == candidate.Identity;
    }

    private void Add(VoiceEventKind kind, DateTimeOffset now, VoiceSettings settings, bool audible, List<VoiceCandidate> ready)
    {
        if (!audible || !settings.IsEnabled(kind)) return;
        var candidate = new VoiceCandidate(kind, new TaskKey("overseer", "monitor"), 0, "OVERSEER", _episode.ToString(System.Globalization.CultureInfo.InvariantCulture), now, "lifecycle", false, 1, "");
        _current[kind] = candidate;
        ready.Add(candidate);
    }

    private static bool IsWaiting(AgentTask task) => !task.IsStale && !task.IsProvisional && !task.CancellationObserved &&
        (task.State is AgentTaskState.NeedsApproval or AgentTaskState.NeedsInput ||
            task.State == AgentTaskState.Working && task.NeedsHuman);

    private sealed class Waiting(string turn, DateTimeOffset since, bool consumed)
    {
        public string Turn { get; } = turn;
        public DateTimeOffset Since { get; } = since;
        public bool Consumed { get; set; } = consumed;
    }
}
