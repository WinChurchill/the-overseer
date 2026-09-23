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
    private readonly VoiceEventPolicy _policy;
    private readonly VoiceManager _manager;
    private readonly TimeProvider _timeProvider;
    private readonly object _gate = new();
    private VoiceSettings _settings;
    private bool _failed;
    private bool _suspended;
    private DateTimeOffset? _resumeAt;

    public NotificationCoordinator(
        ActivityHistory history,
        VoiceEventPolicy policy,
        VoiceManager manager,
        TimeProvider timeProvider,
        VoiceSettings settings)
    {
        _history = history;
        _policy = policy;
        _manager = manager;
        _timeProvider = timeProvider;
        _settings = (settings ?? VoiceSettings.Default).Normalized();
    }

    public VoiceSettings Settings
    {
        get
        {
            lock (_gate)
            {
                return _settings;
            }
        }
    }

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
            // A monitor cycle already in progress at wake can contain pre-wake
            // events. Keep its state/history but never revive those notifications.
            if (_resumeAt is { } resumed)
                snapshot = snapshot with { Transitions = snapshot.Transitions.Where(e => e.ObservedAt > resumed).ToArray() };
            var candidates = _policy.Observe(snapshot);
            if (_manager.IsMuted || _suspended) _policy.DiscardPending();
            if (!_suspended) _manager.Enqueue(candidates);
        }

    }

    /// <summary>UI-thread step: flush completion aggregation and pending cooldowns.</summary>
    public void Pump()
    {
        if (_failed)
        {
            return;
        }

        lock (_gate)
        {
            if (_failed) return;
            if (_suspended) { _policy.DiscardPending(); return; }
            if (_manager.IsMuted) _policy.DiscardPending();
            _manager.Enqueue(_policy.Pump(_timeProvider.GetUtcNow()));
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
            if (_failed)
            {
                return;
            }

            _suspended = true;
            _policy.DiscardPending();
            _manager.DiscardPendingWork("suspended");
        }
    }

    /// <summary>Permit fresh post-wake events without replaying work from sleep.</summary>
    public void OnResume()
    {
        lock (_gate)
        {
            if (_failed || !_suspended) return;
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
            _manager.SetMuted(true);
        }

        _ = code;
    }

    public ActivitySeries History(DateTimeOffset now) => _history.Snapshot(now);

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
            _manager.SetMuted(_failed || muted);
        }

    }

    /// <summary>Revalidation used at playback: a queued cue must still be relevant.</summary>
    public bool IsStillRelevant(VoiceCandidate candidate)
    {
        lock (_gate) return !_failed && !_suspended && _policy.IsRelevant(candidate, _timeProvider.GetUtcNow());
    }

    public async ValueTask DisposeAsync()
    {
        await _manager.DisposeAsync().ConfigureAwait(false);
    }
}
