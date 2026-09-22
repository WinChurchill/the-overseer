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

        IReadOnlyList<VoiceCandidate> candidates;
        lock (_gate)
        {
            _history.Record(snapshot);
            candidates = _policy.Observe(snapshot);
        }

        _manager.Enqueue(candidates);
    }

    /// <summary>UI-thread step: flush completion aggregation and pending cooldowns.</summary>
    public void Pump()
    {
        if (_failed)
        {
            return;
        }

        IReadOnlyList<VoiceCandidate> candidates;
        lock (_gate)
        {
            candidates = _policy.Pump(_timeProvider.GetUtcNow());
        }

        _manager.Enqueue(candidates);
    }

    /// <summary>Terminal monitor failure: stop voice, break history continuity.</summary>
    public void OnMonitorFailure(string code)
    {
        _failed = true;
        lock (_gate)
        {
            _history.Clear();
            _policy.Reset();
        }

        _manager.SetMuted(true);
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
        }

        _manager.SetMuted(muted);
    }

    /// <summary>Applies other settings (provider/pack/endpoint) for persistence.</summary>
    public void UpdateSettings(VoiceSettings settings)
    {
        lock (_gate)
        {
            var normalized = settings.Normalized();
            _settings = normalized with { Muted = _manager.IsMuted };
        }
    }

    /// <summary>Revalidation used at playback: a queued cue must still be relevant.</summary>
    public bool IsStillRelevant(VoiceCandidate candidate)
    {
        if (candidate.IsAggregate)
        {
            return true;
        }

        // The policy already validated the episode; anything reaching this point
        // in the same generation is still the condition the user was told about.
        return candidate.ObservedAt != default;
    }

    public async ValueTask DisposeAsync()
    {
        await _manager.DisposeAsync().ConfigureAwait(false);
    }
}
