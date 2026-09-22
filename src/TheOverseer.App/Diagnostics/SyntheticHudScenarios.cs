using System.IO;
using TheOverseer.Core.History;
using TheOverseer.Core.Models;

namespace TheOverseer.App.Diagnostics;

/// <summary>
/// Synthetic normalized snapshots for HUD preview and smoke. Labels are clearly
/// synthetic, no recorded text is used, and every scenario is finite.
/// </summary>
public static class SyntheticHudScenarios
{
    private static readonly DateTimeOffset Now = new(2026, 1, 2, 12, 0, 0, TimeSpan.Zero);

    public static IReadOnlyList<MonitoringSnapshot> Create() => new[]
    {
        Mixed(),
        HealthyEmpty(),
        Unavailable(),
        PartialUnknown(),
    };

    /// <summary>
    /// Synthetic 60-minute activity series for the fixture: a busy hour with a
    /// quiet stretch (gap), partial lower bounds and a final peak, sampled at a
    /// live-monitor cadence so the line renders continuously where observed.
    /// </summary>
    public static ActivitySeries CreateActivitySeries()
    {
        var history = new ActivityHistory();
        var end = DateTimeOffset.UtcNow;

        for (var second = 3595; second >= 0; second -= 5)
        {
            // A deliberate unobserved stretch between minutes 21 and 35.
            if (second is <= 2100 and >= 1260)
            {
                continue;
            }

            var at = end.AddSeconds(-second);
            var minute = second / 60;
            var working = minute switch
            {
                < 10 => 1,
                < 25 => 3 + (minute % 3),
                < 45 => 2,
                _ => 4,
            };
            var needsHuman = minute % 7 == 0 ? 1 : 0;
            var value = minute is >= 40 and < 44 ? CountValue.Partial(working) : CountValue.Known(working);
            history.Record(new MonitoringSnapshot(
                at,
                new MonitoringHealth(MonitoringHealthLevel.Degraded, new[] { "synthetic" }, false, at, at),
                new MonitoringCounts(
                    value,
                    CountValue.Known(needsHuman),
                    CountValue.Known(0),
                    CountValue.Known(0),
                    CountValue.Known(0),
                    CountValue.Known(0),
                    CountValue.Known(0),
                    CountValue.Known(working)),
                Array.Empty<AgentTask>(),
                Array.Empty<AttentionEvent>(),
                MonitorCapabilities.Initial,
                LiveObservationStats.Empty));
        }

        return history.Snapshot(end);
    }

    /// <summary>Mixed live-looking state: attention, work, stale, provisional, long labels.</summary>
    public static MonitoringSnapshot Mixed()
    {
        var tasks = new List<AgentTask>
        {
            Task("01a0d000-0000-0000-0000-000000000001", 1, "Aurora release checklist", AgentTaskState.NeedsApproval,
                new AgentAction(AgentActionKind.WaitingForApproval, EvidenceKind.Explicit, "permission-requested"),
                needsHuman: true, stale: false, provisional: false, minutesAgo: 1, attention: "permission-requested"),
            Task("01a0d000-0000-0000-0000-000000000002", 2, "Webots simulation harness with an intentionally long synthetic label",
                AgentTaskState.NeedsInput,
                new AgentAction(AgentActionKind.WaitingForInput, EvidenceKind.Explicit, "input-requested"),
                needsHuman: true, stale: false, provisional: false, minutesAgo: 3, attention: "input-requested"),
            Task("01a0d000-0000-0000-0000-000000000003", 3, "TCS data pipeline", AgentTaskState.Working,
                new AgentAction(AgentActionKind.RunningTests, EvidenceKind.Structured, "test-runner"),
                needsHuman: false, stale: false, provisional: false, minutesAgo: 0),
            Task("01a0d000-0000-0000-0000-000000000004", 4, "Docs refresh", AgentTaskState.Working,
                new AgentAction(AgentActionKind.EditingFiles, EvidenceKind.Structured, "file-change"),
                needsHuman: false, stale: false, provisional: false, minutesAgo: 2),
            Task("01a0d000-0000-0000-0000-000000000005", 5, "Search sweep", AgentTaskState.Working,
                new AgentAction(AgentActionKind.Searching, EvidenceKind.Structured, "search-command"),
                needsHuman: true, stale: false, provisional: false, minutesAgo: 1, attention: "input-requested-nonblocking"),
            Task("01a0d000-0000-0000-0000-000000000006", 6, "Sparse long-running job", AgentTaskState.Working,
                new AgentAction(AgentActionKind.RunningCommand, EvidenceKind.Structured, "opaque-command"),
                needsHuman: false, stale: true, provisional: false, minutesAgo: 90),
            Task("01a0d000-0000-0000-0000-000000000007", 7, "Unresolved identity", AgentTaskState.Idle,
                new AgentAction(AgentActionKind.Idle, EvidenceKind.None),
                needsHuman: false, stale: false, provisional: true, minutesAgo: 12),
            Task("01a0d000-0000-0000-0000-000000000008", 8, "Cancelled turn", AgentTaskState.Idle,
                new AgentAction(AgentActionKind.Idle, EvidenceKind.Explicit, "cancelled"),
                needsHuman: false, stale: false, provisional: false, minutesAgo: 20, cancelled: true),
            Task("01a0d000-0000-0000-0000-000000000009", 9, "Failed verification", AgentTaskState.Error,
                new AgentAction(AgentActionKind.Error, EvidenceKind.Explicit, "task-error"),
                needsHuman: false, stale: false, provisional: false, minutesAgo: 35, failed: true),
            Task("01a0d000-0000-0000-0000-00000000000a", 10, "Finished report", AgentTaskState.Done,
                new AgentAction(AgentActionKind.Completed, EvidenceKind.Explicit, "task-complete"),
                needsHuman: false, stale: false, provisional: false, minutesAgo: 45),
        };

        return Snapshot(tasks, MonitoringHealthLevel.Degraded, new[] { "stale-tasks", "discovery-backlog" }, coverageComplete: false, known: true);
    }

    public static MonitoringSnapshot HealthyEmpty() =>
        Snapshot(Array.Empty<AgentTask>(), MonitoringHealthLevel.Healthy, Array.Empty<string>(), coverageComplete: true, known: true);

    public static MonitoringSnapshot Unavailable() =>
        Snapshot(Array.Empty<AgentTask>(), MonitoringHealthLevel.Unavailable, new[] { "root-missing" }, coverageComplete: false, known: false);

    public static MonitoringSnapshot PartialUnknown()
    {
        var tasks = new List<AgentTask>
        {
            Task("01a0d000-0000-0000-0000-00000000000b", 1, "Partial coverage task", AgentTaskState.Working,
                new AgentAction(AgentActionKind.ReadingFiles, EvidenceKind.Structured, "read-command"),
                needsHuman: false, stale: false, provisional: false, minutesAgo: 5),
            Task("01a0d000-0000-0000-0000-00000000000c", 2, "Hydration unfinished", AgentTaskState.Idle,
                new AgentAction(AgentActionKind.Idle, EvidenceKind.None),
                needsHuman: false, stale: false, provisional: true, minutesAgo: 8),
        };

        return Snapshot(tasks, MonitoringHealthLevel.Degraded, new[] { "hydration-budget-exhausted" }, coverageComplete: false, known: false);
    }

    private static AgentTask Task(
        string conversationId,
        int number,
        string label,
        AgentTaskState state,
        AgentAction action,
        bool needsHuman,
        bool stale,
        bool provisional,
        int minutesAgo,
        string? attention = null,
        bool cancelled = false,
        bool failed = false)
    {
        var lastActivity = Now.AddMinutes(-minutesAgo);
        return new AgentTask(
            new TaskKey("codex", conversationId),
            number,
            conversationId,
            "codex",
            ClientSurface.Desktop,
            label,
            "turn-" + number.ToString(System.Globalization.CultureInfo.InvariantCulture),
            state,
            action,
            action.Evidence,
            lastActivity.AddMinutes(-5),
            lastActivity,
            lastActivity,
            state == AgentTaskState.Idle ? null : lastActivity.AddMinutes(-6),
            state == AgentTaskState.Done ? lastActivity : null,
            stale,
            provisional,
            needsHuman,
            cancelled,
            failed,
            attention,
            1);
    }

    private static MonitoringSnapshot Snapshot(
        IReadOnlyList<AgentTask> tasks,
        MonitoringHealthLevel level,
        IReadOnlyList<string> reasons,
        bool coverageComplete,
        bool known)
    {
        var working = tasks.Count(static task => task.State == AgentTaskState.Working);
        var needsHuman = tasks.Count(static task => task.NeedsHuman);
        CountValue Count(int value) => known
            ? coverageComplete ? CountValue.Known(value) : CountValue.Partial(value)
            : CountValue.Unknown;

        var counts = new MonitoringCounts(
            Count(working),
            Count(needsHuman),
            Count(tasks.Count(static task => task.State == AgentTaskState.NeedsApproval)),
            Count(tasks.Count(static task => task.State == AgentTaskState.NeedsInput)),
            Count(tasks.Count(static task => task.State == AgentTaskState.Error)),
            Count(tasks.Count(static task => task.State == AgentTaskState.Done)),
            Count(tasks.Count(static task => task.State == AgentTaskState.Idle)),
            Count(tasks.Count));

        var health = new MonitoringHealth(level, reasons, coverageComplete, Now.AddSeconds(-2), Now);
        return new MonitoringSnapshot(
            Now,
            health,
            counts,
            tasks,
            Array.Empty<AttentionEvent>(),
            MonitorCapabilities.Initial,
            LiveObservationStats.Empty);
    }
}
