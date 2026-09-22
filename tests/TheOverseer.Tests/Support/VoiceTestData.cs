using TheOverseer.Core.Models;
using TheOverseer.Core.Voice;

namespace TheOverseer.Tests.Support;

/// <summary>Synthetic normalized data for history/voice tests. No private content.</summary>
public static class VoiceTestData
{
    public static readonly DateTimeOffset Start = new(2026, 3, 4, 10, 0, 0, TimeSpan.Zero);

    public static TaskKey Key(string id = "01a0e000-0000-0000-0000-000000000001") => new("codex", id);

    public static AgentTask Task(
        TaskKey? key = null,
        int number = 1,
        AgentTaskState state = AgentTaskState.Working,
        bool needsHuman = false,
        string turnId = "turn-1",
        bool stale = false,
        bool provisional = false,
        bool cancelled = false,
        bool failed = false,
        ClientSurface surface = ClientSurface.Desktop)
    {
        var actualKey = key ?? Key();
        return new AgentTask(
            actualKey,
            number,
            actualKey.ConversationId,
            actualKey.ProfileId,
            surface,
            "Synthetic",
            turnId,
            state,
            new AgentAction(state switch
            {
                AgentTaskState.Done => AgentActionKind.Completed,
                AgentTaskState.Error => AgentActionKind.Error,
                AgentTaskState.NeedsApproval => AgentActionKind.WaitingForApproval,
                AgentTaskState.NeedsInput => AgentActionKind.WaitingForInput,
                _ => AgentActionKind.RunningTests,
            }, EvidenceKind.Explicit),
            EvidenceKind.Explicit,
            Start,
            Start,
            Start,
            null,
            state == AgentTaskState.Done ? Start : null,
            stale,
            provisional,
            needsHuman,
            cancelled,
            failed,
            null,
            1);
    }

    public static AttentionEvent Event(
        AttentionEventKind kind,
        TaskKey? key = null,
        int number = 1,
        string turnId = "turn-1",
        DateTimeOffset? observedAt = null,
        string code = "cause",
        AgentTaskState state = AgentTaskState.NeedsApproval) => new(
        kind,
        key ?? Key(),
        number,
        "Synthetic",
        state,
        AgentActionKind.RunningTests,
        turnId,
        observedAt ?? Start,
        code);

    public static MonitoringSnapshot Snapshot(
        DateTimeOffset at,
        IReadOnlyList<AgentTask>? tasks = null,
        IReadOnlyList<AttentionEvent>? transitions = null,
        MonitoringHealthLevel level = MonitoringHealthLevel.Healthy,
        bool coverageComplete = true,
        CountValue? working = null,
        CountValue? needsHuman = null)
    {
        var taskList = tasks ?? Array.Empty<AgentTask>();
        var working1 = working ?? CountValue.Known(taskList.Count(task => task.State == AgentTaskState.Working));
        var needsHuman1 = needsHuman ?? CountValue.Known(taskList.Count(task => task.NeedsHuman));
        return new MonitoringSnapshot(
            at,
            new MonitoringHealth(level, Array.Empty<string>(), coverageComplete, at, at),
            new MonitoringCounts(
                working1,
                needsHuman1,
                CountValue.Known(taskList.Count(task => task.State == AgentTaskState.NeedsApproval)),
                CountValue.Known(taskList.Count(task => task.State == AgentTaskState.NeedsInput)),
                CountValue.Known(taskList.Count(task => task.State == AgentTaskState.Error)),
                CountValue.Known(taskList.Count(task => task.State == AgentTaskState.Done)),
                CountValue.Known(taskList.Count(task => task.State == AgentTaskState.Idle)),
                CountValue.Known(taskList.Count)),
            taskList,
            transitions ?? Array.Empty<AttentionEvent>(),
            MonitorCapabilities.Initial,
            LiveObservationStats.Empty);
    }
}

/// <summary>Scripted provider: returns a synthetic clip or a configured failure.</summary>
public sealed class FakeVoiceProvider : IVoiceProvider
{
    private int _calls;

    public FakeVoiceProvider(string id = "fake", bool available = true)
    {
        Id = id;
        Available = available;
    }

    public string Id { get; }

    public bool Available { get; set; }

    public int Calls => _calls;

    public int DelayMilliseconds { get; set; }

    public TaskCompletionSource? Gate { get; set; }

    public string LastCode { get; private set; } = "idle";

    public VoiceProviderStatus Status => new(Id, Available, LastCode);

    public async ValueTask<VoiceClip?> TryCreateAsync(VoiceCandidate candidate, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _calls);
        if (DelayMilliseconds > 0)
        {
            await Task.Delay(DelayMilliseconds, cancellationToken);
        }

        if (Gate is { } gate)
        {
            await gate.Task.WaitAsync(cancellationToken);
        }

        if (!Available)
        {
            LastCode = "unavailable";
            return null;
        }

        LastCode = "ok";
        return new VoiceClip(VoicePackValidator.CreateTone(440, 0.05), Id, Id);
    }
}

/// <summary>Records playback attempts; can block until released.</summary>
public sealed class FakeVoicePlayer : IVoicePlayer
{
    private readonly object _gate = new();
    private readonly List<VoiceClip> _played = new();

    public TaskCompletionSource? Gate { get; set; }

    public int StopCount { get; private set; }

    public bool ConcurrentPlayDetected { get; private set; }

    public IReadOnlyList<VoiceClip> Played
    {
        get
        {
            lock (_gate)
            {
                return _played.ToArray();
            }
        }
    }

    private int _active;

    public async ValueTask<bool> PlayAsync(VoiceClip clip, CancellationToken cancellationToken)
    {
        if (Interlocked.Increment(ref _active) > 1)
        {
            ConcurrentPlayDetected = true;
        }

        try
        {
            if (Gate is { } gate)
            {
                await gate.Task.WaitAsync(cancellationToken);
            }

            lock (_gate)
            {
                _played.Add(clip);
            }

            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        finally
        {
            Interlocked.Decrement(ref _active);
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            StopCount++;
        }
    }
}
