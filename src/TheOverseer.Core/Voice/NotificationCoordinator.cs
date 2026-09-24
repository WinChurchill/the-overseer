using TheOverseer.Core.Configuration;
using TheOverseer.Core.History;
using TheOverseer.Core.Models;

namespace TheOverseer.Core.Voice;

/// <summary>
/// The single owner of Plan 3 input processing: it observes EVERY normalized
/// snapshot before the lossy UI pump, keeps the rolling history, drives the
/// voice policy and enqueues immutable candidates. Everything here is
/// synchronous and bounded; no file, network, audio or UI work happens here.
/// </summary>
public sealed class NotificationCoordinator : IAsyncDisposable
{
    private readonly ActivityHistory _history;
    private readonly TaskActivityHistory _activity;
    private readonly VoiceEventPolicy _policy;
    private readonly VoiceManager _manager;
    private readonly TimeProvider _timeProvider;
    private readonly object _gate = new();
    private volatile VoiceSettings _settings;
    private bool _failed;
    private bool _suspended;
    private DateTimeOffset? _resumeAt;
    private readonly VoiceLifecyclePolicy _lifecycle = new();
    private bool _previewing;
    private bool _monitorUnavailable;

    public NotificationCoordinator(
        ActivityHistory history,
        VoiceEventPolicy policy,
        VoiceManager manager,
        TimeProvider timeProvider,
        VoiceSettings settings)
    {
        _history = history;
        _activity = new TaskActivityHistory();
        _policy = policy;
        _manager = manager;
        _timeProvider = timeProvider;
        _settings = (settings ?? VoiceSettings.Default).Normalized();
        _manager.Played += candidate => { lock (_gate) _lifecycle.Played(candidate); };
    }

    // Immutable snapshots allow provider status reads without inverting the
    // coordinator -> manager lock order during a concurrent monitor callback.
    public VoiceSettings Settings => _settings;

    public bool IsMuted => _manager.IsMuted;

    public VoiceStatus Status => _manager.Status;

    public void Start()
    {
        _manager.SetMuted(_settings.Muted);
        _manager.Start();
    }

    /// <summary>
    /// Producer-thread entry point. Records history, evaluates the policy and
    /// enqueues candidates. Never blocks and never performs I/O.
    /// </summary>
    public void OnSnapshot(MonitoringSnapshot snapshot)
    {
        if (snapshot is null || _failed)
        {
            return;
        }

        lock (_gate)
        {
            if (_failed) return;
            _history.Record(snapshot);
            // Observed activity is recorded from the same pre-UI-coalescing snapshot
            // as history and voice, using each task's own observation instant, so an
            // aggregate refresh can never renew a stale action.
            TaskActivityRecorder.Record(_activity, snapshot);
            var unavailable = snapshot.Health.Level == MonitoringHealthLevel.Unavailable;
            if (_monitorUnavailable && !unavailable)
                snapshot = snapshot with { Transitions = Array.Empty<AttentionEvent>() };
            _monitorUnavailable = unavailable;
            // A monitor cycle already in progress at wake can contain pre-wake
            // events. Keep its state/history but never revive those notifications.
            if (_resumeAt is { } resumed)
                snapshot = snapshot with { Transitions = snapshot.Transitions.Where(e => e.ObservedAt > resumed).ToArray() };
            if (!_settings.IsEnabled(VoiceEventKind.AllTasksCompleted))
                snapshot = snapshot with { Transitions = snapshot.Transitions.Where(item => item.Kind != AttentionEventKind.AllTasksCompleted).ToArray() };
            var candidates = _policy.Observe(snapshot);
            var audible = !_manager.IsMuted && !_suspended && !_previewing;
            var extra = _lifecycle.Observe(snapshot, _settings, audible);
            if (!audible) _policy.DiscardPending();
            if (audible) _manager.Enqueue(candidates.Concat(extra).Where(candidate => _settings.IsEnabled(candidate.Kind)).ToArray());
        }

    }

    /// <summary>UI-thread step: flush completion aggregation and pending cooldowns.</summary>
    public void Pump()
    {
        lock (_gate)
        {
            if (_suspended) { _policy.DiscardPending(); return; }
            var audible = !_manager.IsMuted && !_previewing;
            if (!audible) _policy.DiscardPending();
            var now = _timeProvider.GetUtcNow();
            var extra = _lifecycle.Pump(now, _settings, audible);
            var candidates = _failed ? Array.Empty<VoiceCandidate>() : _policy.Pump(now);
            if (audible) _manager.Enqueue(candidates.Concat(extra).Where(candidate => _settings.IsEnabled(candidate.Kind)).ToArray());
        }

    }

    /// <summary>
    /// System is going to sleep: drop queued/pending cues and stop audio, but
    /// keep the mute preference and the once-per-condition latches. Nothing that
    /// was captured before the suspend may play after the resume.
    /// </summary>
    public void OnSuspend()
    {
        lock (_gate)
        {
            _suspended = true;
            _lifecycle.Suppress();
            _policy.DiscardPending();
            _manager.DiscardPendingWork("suspended");
        }
    }

    /// <summary>Permit fresh post-wake events without replaying work from sleep.</summary>
    public void OnResume()
    {
        lock (_gate)
        {
            if (!_suspended) return;
            _resumeAt = _timeProvider.GetUtcNow();
            _policy.DiscardPending();
            _suspended = false;
        }
    }

    /// <summary>Terminal monitor failure: stop voice, break history continuity.</summary>
    public void OnMonitorFailure(string code)
    {
        lock (_gate)
        {
            _failed = true;
            _policy.Reset();
            _manager.DiscardPendingWork("monitor-failed");
            _lifecycle.Fail(_timeProvider.GetUtcNow());
        }

        _ = code;
    }

    public ActivitySeries History(DateTimeOffset now) => _history.Snapshot(now);

    /// <summary>Observed per-task activity lanes, clipped to the rolling window.</summary>
    public IReadOnlyList<TaskActivityLane> ActivityLanes(DateTimeOffset now) => _activity.Lanes(now);

    /// <summary>Union activity strip for one project.</summary>
    public ProjectActivityStrip ActivityStrip(ProjectId projectId, DateTimeOffset now) => _activity.Project(projectId, now);

    /// <summary>Union activity strips for every project with observed activity.</summary>
    public IReadOnlyList<ProjectActivityStrip> ActivityStrips(DateTimeOffset now) => _activity.Projects(now);

    /// <summary>Safe bounded diagnostic summary: counts and codes only.</summary>
    public string Describe()
    {
        var status = _manager.Status;
        return _policy.Describe() +
               " status=" + status.LastOutcomeCode +
               " queued=" + status.QueuedCount.ToString(System.Globalization.CultureInfo.InvariantCulture) +
               " spoken=" + status.SpokenCount.ToString(System.Globalization.CultureInfo.InvariantCulture) +
               " dropped=" + status.DroppedCount.ToString(System.Globalization.CultureInfo.InvariantCulture) +
               " failed=" + status.FailedCount.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Applies a mute change immediately and remembers it for persistence.</summary>
    public void SetMuted(bool muted)
    {
        lock (_gate)
        {
            _settings = _settings with { Muted = muted };
            _policy.DiscardPending();
            _lifecycle.Suppress();
            _manager.SetMuted(muted);
        }

    }

    /// <summary>Revalidation used at playback: a queued cue must still be relevant.</summary>
    public bool IsStillRelevant(VoiceCandidate candidate)
    {
        lock (_gate) return !_suspended && !_previewing && _settings.IsEnabled(candidate.Kind) &&
            (candidate.Kind >= VoiceEventKind.MonitoringLost
                ? _lifecycle.IsRelevant(candidate, _timeProvider.GetUtcNow())
                : !_failed && _policy.IsRelevant(candidate, _timeProvider.GetUtcNow()));
    }

    public void UpdateSettings(VoiceSettings settings)
    {
        lock (_gate)
        {
            _settings = settings.Normalized();
            _policy.DiscardPending();
            _lifecycle.Suppress();
            _manager.DiscardPendingWork("settings-changed");
            _manager.SetMuted(_settings.Muted);
        }
    }

    public void SetPreviewing(bool previewing)
    {
        lock (_gate)
        {
            _previewing = previewing;
            _policy.DiscardPending();
            _lifecycle.Suppress();
            _manager.DiscardPendingWork("preview");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _manager.DisposeAsync().ConfigureAwait(false);
    }
}
