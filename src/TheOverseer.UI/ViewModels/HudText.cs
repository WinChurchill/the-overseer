using TheOverseer.Core.Models;

namespace TheOverseer.UI.ViewModels;

/// <summary>
/// Pure display formatting shared by the HUD and its tests. No monitor I/O, no
/// state derivation: unknown stays unknown and partial stays qualified.
/// </summary>
public static class HudText
{
    public const string UnknownMark = "?";

    public static string State(AgentTaskState state) => state switch
    {
        AgentTaskState.NeedsInput => "NEEDS_INPUT",
        AgentTaskState.NeedsApproval => "NEEDS_APPROVAL",
        _ => state.ToString().ToUpperInvariant(),
    };

    /// <summary>Big number for one count value: "3", "3+", or "?".</summary>
    public static string CountValue(CountValue value) =>
        !value.IsKnown ? UnknownMark : value.IsComplete ? value.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) : value.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) + "+";

    public static string CountQualifier(CountValue working, CountValue needsHuman) =>
        !working.IsKnown || !needsHuman.IsKnown ? "unknown"
        : !working.IsComplete || !needsHuman.IsComplete ? "partial"
        : string.Empty;

    public static string HealthTitle(MonitoringHealthLevel level) => level switch
    {
        MonitoringHealthLevel.Healthy => "MONITORING HEALTHY",
        MonitoringHealthLevel.Degraded => "MONITORING DEGRADED",
        _ => "MONITORING UNAVAILABLE",
    };

    /// <summary>Safe reason codes only; never titles, prompts or paths.</summary>
    public static string HealthDetail(MonitoringSnapshot snapshot)
    {
        var health = snapshot.Health;
        var reasons = health.Reasons.Count == 0 ? "none" : string.Join(", ", health.Reasons.Take(4));
        return "reasons: " + reasons +
               " · coverage " + (health.CoverageComplete ? "complete" : "partial") +
               " · fresh " + Freshness(health.LastSuccessfulReadAt, snapshot.GeneratedAt);
    }

    public static string Freshness(DateTimeOffset? lastReadAt, DateTimeOffset now)
    {
        if (lastReadAt is not { } last)
        {
            return "unknown";
        }

        var age = now - last;
        if (age < TimeSpan.Zero)
        {
            return "now";
        }

        return Relative(age) + " ago";
    }

    public static string Relative(TimeSpan age)
    {
        if (age < TimeSpan.Zero)
        {
            return "now";
        }

        if (age.TotalSeconds < 60)
        {
            return ((int)age.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture) + "s";
        }

        if (age.TotalMinutes < 60)
        {
            return ((int)age.TotalMinutes).ToString(System.Globalization.CultureInfo.InvariantCulture) + "m";
        }

        if (age.TotalHours < 24)
        {
            return ((int)age.TotalHours).ToString(System.Globalization.CultureInfo.InvariantCulture) + "h";
        }

        return ((int)age.TotalDays).ToString(System.Globalization.CultureInfo.InvariantCulture) + "d";
    }

    /// <summary>Row marks. Non-blocking needs-human is disclosed, never relabelled as blocked.</summary>
    public static string Marks(AgentTask task)
    {
        var marks = new List<string>(4);
        if (task.NeedsHuman && task.State == AgentTaskState.Working)
        {
            marks.Add("needs you soon");
        }

        if (task.IsStale)
        {
            marks.Add("stale");
        }

        if (task.IsProvisional)
        {
            marks.Add("provisional");
        }

        if (task.CancellationObserved)
        {
            marks.Add("cancelled");
        }

        if (task.TerminalFailureObserved)
        {
            marks.Add("failed");
        }

        return string.Join(" · ", marks);
    }

    public static string Label(AgentTask task) =>
        string.IsNullOrWhiteSpace(task.DisplayLabel)
            ? "Task #" + task.Number.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : task.DisplayLabel;

    public static string CapabilityDisclosure(MonitorCapabilities capabilities)
    {
        var permission = Support(capabilities.PermissionRequests.Support);
        var input = Support(capabilities.InputRequests.Support);
        if (capabilities.PermissionRequests.Support == CapabilitySupport.LiveObserved &&
            capabilities.InputRequests.Support == CapabilitySupport.LiveObserved)
        {
            return "approval and input requests: observed";
        }

        return "approval requests: " + permission + " · input requests: " + input +
               " (this Codex version keeps them inside the running process)";
    }

    public static string Support(CapabilitySupport support) => support switch
    {
        CapabilitySupport.Supported => "supported",
        CapabilitySupport.Unsupported => "unsupported",
        CapabilitySupport.Unavailable => "unavailable",
        CapabilitySupport.NotExercised => "not exercised",
        _ => "observed",
    };

    /// <summary>Distinct copy for healthy-empty, degraded-empty and unavailable.</summary>
    public static string EmptyState(MonitoringSnapshot snapshot)
    {
        if (snapshot.Health.Level == MonitoringHealthLevel.Unavailable)
        {
            return "Monitor unavailable — task state is unknown, not zero.";
        }

        if (snapshot.Health.Level == MonitoringHealthLevel.Degraded)
        {
            return "No conversations in view while monitoring is degraded.";
        }

        return "No Codex conversations tracked yet.";
    }
}
