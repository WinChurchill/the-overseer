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
    /// <summary>Fixed synthetic clock every scenario is anchored to.</summary>
    public static DateTimeOffset Now { get; } = new(2026, 1, 2, 12, 0, 0, TimeSpan.Zero);

    public static IReadOnlyList<MonitoringSnapshot> Create() => new[]
    {
        Mixed(),
        HealthyEmpty(),
        Unavailable(),
        PartialUnknown(),
    };

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

        // Typed per-source health, so the preview shows the real shape of the health
        // detail: a degraded Codex, a silent dsh and a stale Claude bridge.
        return Snapshot(tasks, MonitoringHealthLevel.Degraded, new[] { "stale-tasks", "discovery-backlog" }, coverageComplete: false, known: true) with
        {
            Sources = new[]
            {
                new SourceHealth(
                    HarnessIds.Codex, "codex", "Codex", MonitoringHealthLevel.Degraded,
                    new[] { "stale-tasks", "discovery-backlog" }, false, Now.AddSeconds(-2),
                    MonitorCapabilities.Initial with
                    {
                        InputRequests = new CapabilityStatus(CapabilitySupport.LiveObserved, "input-requests", "observed"),
                    }),
                new SourceHealth(
                    HarnessIds.Dsh, "dsh-default", "DeepSeek Harness (dsh-default)", MonitoringHealthLevel.Unavailable,
                    new[] { "source-silent" }, false, Now.AddMinutes(-3), MonitorCapabilities.ForBridge(HarnessIds.Dsh, true)),
                new SourceHealth(
                    HarnessIds.Claude, "claude-default", "Claude Code (claude-default)", MonitoringHealthLevel.Degraded,
                    new[] { "bridge-stale-or-incomplete" }, false, Now.AddSeconds(-20), MonitorCapabilities.ForBridge(HarnessIds.Claude, false)),
            },
        };
    }

    /// <summary>README overview uses supported Codex input waits, not approval placeholders.</summary>
    public static MonitoringSnapshot ReadmeOverview()
    {
        var mixed = Mixed();
        var tasks = mixed.Tasks.Select(task => task.Number switch
        {
            1 => task with
            {
                State = AgentTaskState.NeedsInput,
                Action = new AgentAction(AgentActionKind.WaitingForInput, EvidenceKind.Explicit, "input-requested"),
                AttentionCode = "input-requested",
            },
            2 => task with { Label = "Review simulation configuration" },
            _ => task,
        }).ToArray();
        return Snapshot(tasks, MonitoringHealthLevel.Degraded, mixed.Health.Reasons,
            coverageComplete: false, known: true) with { Sources = mixed.Sources };
    }

    /// <summary>One project with three tasks: the single-project render case.</summary>
    public static MonitoringSnapshot SingleProject()
    {
        var tasks = new List<AgentTask>
        {
            Task("01a0f000-0000-0000-0000-000000000001", 1, "Aurora release checklist", AgentTaskState.NeedsApproval,
                new AgentAction(AgentActionKind.WaitingForApproval, EvidenceKind.Explicit, "permission-requested"),
                needsHuman: true, stale: false, provisional: false, minutesAgo: 1, attention: "permission-requested"),
            Task("01a0f000-0000-0000-0000-000000000002", 2, "TCS data pipeline", AgentTaskState.Working,
                new AgentAction(AgentActionKind.RunningTests, EvidenceKind.Structured, "test-runner"),
                needsHuman: false, stale: false, provisional: false, minutesAgo: 0),
            Task("01a0f000-0000-0000-0000-000000000009", 9, "Failed verification", AgentTaskState.Error,
                new AgentAction(AgentActionKind.Error, EvidenceKind.Explicit, "task-error"),
                needsHuman: false, stale: false, provisional: false, minutesAgo: 4, failed: true),
        };

        return Snapshot(tasks, MonitoringHealthLevel.Healthy, Array.Empty<string>(), coverageComplete: true, known: true);
    }

    /// <summary>
    /// A staggered synthetic hour for render inspection: the same projects observed at
    /// different states with a deliberate unobserved stretch, so one PNG shows
    /// thinking, working, waiting, error, idle and unknown cells together. The last
    /// snapshot is the render source.
    /// </summary>
    public static IReadOnlyList<MonitoringSnapshot> ActivityHour(DateTimeOffset end)
    {
        var steps = new List<MonitoringSnapshot>();

        // Every observation carries its own instant, so the rendered rows agree with
        // the recorded intervals instead of claiming activity after the snapshot.
        AgentTask At(int number, string label, AgentTaskState state, AgentAction action, DateTimeOffset at, bool needsHuman = false, bool failed = false) =>
            Task(Conversation(number), number, label, state, action, needsHuman,
                stale: false, provisional: false, minutesAgo: 0, attention: needsHuman ? action.DetailCode : null,
                failed: failed, anchor: at);

        AgentTask IdleTask(DateTimeOffset at) =>
            At(3, "TCS data pipeline", AgentTaskState.Idle, new AgentAction(AgentActionKind.Idle, EvidenceKind.Explicit, "settled"), at);

        void Observe(int minutesAgo, Func<DateTimeOffset, AgentTask[]> build)
        {
            var at = end.AddMinutes(-minutesAgo);
            steps.Add(Snapshot(build(at), MonitoringHealthLevel.Degraded, new[] { "synthetic" }, coverageComplete: false, known: true, at: at));
        }

        // Aurora: an explicit plan marker, then inferred reasoning, then tool work.
        Observe(54, at => new AgentTask[]
        {
            At(1, "Aurora release checklist", AgentTaskState.Working, new AgentAction(AgentActionKind.EditingFiles, EvidenceKind.Explicit, "plan-mode"), at),
            IdleTask(at),
        });
        for (var minute = 48; minute >= 30; minute -= 3)
        {
            var step = minute;
            Observe(step, at => new AgentTask[]
            {
                At(1, "Aurora release checklist", AgentTaskState.Working, new AgentAction(AgentActionKind.Thinking, EvidenceKind.Inferred, "inferred-turn"), at),
                IdleTask(at),
            });
        }

        for (var minute = 27; minute >= 18; minute -= 3)
        {
            var step = minute;
            Observe(step, at => new AgentTask[]
            {
                At(1, "Aurora release checklist", AgentTaskState.Working, new AgentAction(AgentActionKind.RunningTests, EvidenceKind.Structured, "test-runner"), at),
                IdleTask(at),
            });
        }

        // Minutes 17..7 are deliberately unobserved: unknown coverage, never idle.
        Observe(6, at => new AgentTask[]
        {
            At(1, "Aurora release checklist", AgentTaskState.NeedsApproval, new AgentAction(AgentActionKind.WaitingForApproval, EvidenceKind.Explicit, "permission-requested"), at, needsHuman: true),
            IdleTask(at),
            At(5, "Webots simulation harness", AgentTaskState.Working, new AgentAction(AgentActionKind.Searching, EvidenceKind.Structured, "search-command"), at),
        });
        Observe(1, at => new AgentTask[]
        {
            At(1, "Aurora release checklist", AgentTaskState.Error, new AgentAction(AgentActionKind.Error, EvidenceKind.Explicit, "task-error"), at, failed: true),
            IdleTask(at),
            At(5, "Webots simulation harness", AgentTaskState.NeedsInput, new AgentAction(AgentActionKind.WaitingForInput, EvidenceKind.Explicit, "input-requested"), at, needsHuman: true),
            At(10, "Finished report", AgentTaskState.Done, new AgentAction(AgentActionKind.Completed, EvidenceKind.Explicit, "task-complete"), at),
        });
        return steps;
    }

    private static string Conversation(int number) =>
        "01a0f000-0000-0000-0000-0000000000" + number.ToString("x2", System.Globalization.CultureInfo.InvariantCulture);

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
        bool failed = false,
        DateTimeOffset? anchor = null)
    {
        var lastActivity = (anchor ?? Now).AddMinutes(-minutesAgo);
        // Synthetic project and harness variety so a fixture render shows real
        // project headers and multi-harness badges. Roots are synthetic placeholders.
        var harnessId = number is 5 or 6 ? HarnessIds.Dsh : HarnessIds.Codex;
        var sourceInstance = harnessId == HarnessIds.Dsh ? "dsh-default" : "codex";
        return new AgentTask(
            TaskKey.For(harnessId, sourceInstance, conversationId),
            number,
            conversationId,
            sourceInstance,
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
            1,
            @"C:\synthetic\" + ProjectName(number),
            ProjectFor(number));
    }

    private static string ProjectName(int number) => number switch
    {
        1 or 2 or 9 => "Aurora",
        3 or 4 or 10 => "TCS pipeline",
        5 or 6 => "Webots harness",
        _ => "Unassigned",
    };

    private static ProjectDescriptor ProjectFor(int number)
    {
        var name = ProjectName(number);
        return name == "Unassigned"
            ? ProjectDescriptor.UnassignedProject
            : new ProjectDescriptor(ProjectId.FromRoot("c:\\synthetic\\" + name), name, @"C:\synthetic\" + name, ProjectResolution.ConfiguredRoot);
    }

    private static MonitoringSnapshot Snapshot(
        IReadOnlyList<AgentTask> tasks,
        MonitoringHealthLevel level,
        IReadOnlyList<string> reasons,
        bool coverageComplete,
        bool known,
        DateTimeOffset? at = null)
    {
        var now = at ?? Now;
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

        var health = new MonitoringHealth(level, reasons, coverageComplete, now.AddSeconds(-2), now);
        return new MonitoringSnapshot(
            now,
            health,
            counts,
            tasks,
            Array.Empty<AttentionEvent>(),
            MonitorCapabilities.Initial,
            LiveObservationStats.Empty);
    }
}
