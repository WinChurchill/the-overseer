using TheOverseer.Core.Models;

namespace TheOverseer.UI.ViewModels;

/// <summary>
/// Pure display formatting shared by the HUD and its tests. No monitor I/O, no
/// state derivation: unknown stays unknown and partial stays qualified.
///
/// Health wording is source-aware. Composite monitoring prefixes every reason with
/// the harness that produced it ("dsh:source-silent"), so matching only the raw
/// unprefixed code silently mislabels degraded state. Every helper here matches a
/// reason by exact code or by the code after the source prefix.
/// </summary>
public static class HudText
{
    public const string UnknownMark = "?";

    public static string State(AgentTaskState state) => state switch
    {
        AgentTaskState.NeedsInput => "Needs input",
        AgentTaskState.NeedsApproval => "Needs approval",
        AgentTaskState.Done => "Completed",
        AgentTaskState.Error => "Failed",
        _ => state.ToString(),
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

    /// <summary>
    /// The one concise health label the pinned header shows. Coverage is part of
    /// the claim: a complete-looking level with partial coverage is "limited".
    /// </summary>
    public static string HealthLabel(MonitoringHealthLevel level, bool coverageComplete) => level switch
    {
        MonitoringHealthLevel.Unavailable => "Monitoring offline",
        _ => coverageComplete ? "Live" : "Limited coverage",
    };

    /// <summary>Safe reason codes only; never titles, prompts or paths.</summary>
    public static string HealthDetail(MonitoringSnapshot snapshot)
    {
        var health = snapshot.Health;
        var reasons = health.Reasons.Count == 0 ? HealthReasons.None : string.Join(", ", health.Reasons.Take(4));
        return "reasons: " + reasons +
               " · coverage " + (health.CoverageComplete ? "complete" : "partial") +
               " · fresh " + Freshness(health.LastSuccessfulReadAt, snapshot.GeneratedAt);
    }

    /// <summary>
    /// True when the reason list contains the code, with or without a source
    /// prefix. Composite monitoring publishes "&lt;source&gt;:&lt;code&gt;", so a
    /// plain equality test against the unprefixed code silently fails.
    /// </summary>
    public static bool HasReason(IReadOnlyList<string>? reasons, string code)
    {
        if (reasons is null)
        {
            return false;
        }

        foreach (var reason in reasons)
        {
            if (MatchesReason(reason, code))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Exact match, or match on the code after the "source:" prefix.</summary>
    public static bool MatchesReason(string? reason, string code)
    {
        if (string.IsNullOrEmpty(reason))
        {
            return false;
        }

        if (string.Equals(reason, code, StringComparison.Ordinal))
        {
            return true;
        }

        var separator = reason!.IndexOf(':');
        return separator >= 0 && separator + 1 < reason.Length &&
               string.Equals(reason[(separator + 1)..], code, StringComparison.Ordinal);
    }

    /// <summary>
    /// The harness that published a reason, or an empty string for a global one.
    /// Never a path and never free text: only the normalized source prefix.
    /// </summary>
    public static string ReasonSource(string? reason)
    {
        if (string.IsNullOrEmpty(reason))
        {
            return string.Empty;
        }

        var separator = reason!.IndexOf(':');
        if (separator <= 0)
        {
            return string.Empty;
        }

        var prefix = reason[..separator];
        return HarnessIds.IsKnown(prefix) ? HarnessIds.Normalize(prefix) : string.Empty;
    }

    /// <summary>The bare code of a reason, without its source prefix.</summary>
    public static string ReasonCode(string? reason)
    {
        if (string.IsNullOrEmpty(reason))
        {
            return string.Empty;
        }

        var separator = reason!.IndexOf(':');
        return separator >= 0 && separator + 1 < reason.Length ? reason[(separator + 1)..] : reason;
    }

    /// <summary>
    /// One useful next action per known reason. Actions are guidance, never a
    /// promise that the condition is fixable from the HUD.
    /// </summary>
    public static string ReasonAction(string? reasonCode) => ReasonCode(reasonCode) switch
    {
        "async-input-status-unconfirmed" => "An earlier async question has no fresh status. Codex does not report prompt dismissal in this stream.",
        "projects-directory-has-no-worker-state" => "Claude's Projects list exposes names but no worker status. Open a project's Threads overview; background loaded views can be observed.",
        "projects-overview-not-visible" => "Open the Threads overview in Claude. OVERSEER does not navigate for you.",
        "projects-visible-overviews-only" or "projects-account-coverage-unverified" => "Only open Claude overviews are observed; account totals remain partial.",
        "projects-membership-incomplete" => "Expand the status groups in Claude to expose more threads.",
        "projects-reader-timeout" or "projects-reader-busy" or "projects-accessibility-unavailable" => "Reopen Claude's Threads overview. Previous observations stay qualified until readable.",
        HealthReasons.RootMissing => "Set the harness root in Projects & sources.",
        HealthReasons.RootInaccessible or HealthReasons.ReadFailure => "Check that OVERSEER can read the harness data.",
        HealthReasons.RootEmpty => "No sessions have been written yet.",
        HealthReasons.DiscoveryBacklog or HealthReasons.HydrationBudget => "History is still loading; counts can rise.",
        HealthReasons.DiscoveryCapped or HealthReasons.TrackedFileLimit => "More sessions exist than the read budget covers.",
        HealthReasons.StaleTasks => "Some rows have no recent update; current work is unconfirmed.",
        HealthReasons.ProvisionalTasks => "Some identities are unresolved; they may change.",
        HealthReasons.FileLocked or HealthReasons.FileReplaced or HealthReasons.FileTruncated => "The producer is still writing; this clears on its own.",
        HealthReasons.FileMalformed or HealthReasons.RecordOversize => "A record could not be read and was skipped.",
        HealthReasons.TitleSourceUnavailable => "Session titles are unavailable; labels stay generic.",
        HealthReasons.ActivitySourceUnavailable => "Machine activity is unavailable; agent activity is unaffected.",
        HealthReasons.WatcherFailed or HealthReasons.WatcherOverflow => "File watching degraded to polling.",
        HealthReasons.IncompleteCoverage => "Coverage is partial; counts are lower bounds.",
        "source-silent" => "The integration stopped publishing; restart it.",
        "source-stopped" or "source-faulted" => "The harness process ended; restart it to resume.",
        "source-start-failed" => "The source could not start; check its root and access.",
        "no-sources" => "No source is installed; add one in Projects & sources.",
        _ => "No action needed.",
    };

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
        if (task.HasUnconfirmedInput) marks.Add("Input status unconfirmed");
        if (task.NeedsHuman && task.State == AgentTaskState.Working)
        {
            marks.Add("question observed");
        }

        if (task.IsStale)
        {
            marks.Add("stale");
        }

        if (task.IsProvisional)
        {
            marks.Add("provisional");
        }

        if (task.IsStateUncertain)
        {
            marks.Add("state unconfirmed");
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

    /// <summary>
    /// Short harness badge plus the source instance when it differs, so a task row
    /// names the harness that produced it. Never a path, profile root or provider
    /// guess: the badge is the normalized harness id.
    /// </summary>
    public static string Harness(AgentTask task)
    {
        var harness = HarnessIds.Normalize(task.Key.HarnessId);
        var badge = ShortHarness(harness);
        var instance = task.ProfileId ?? string.Empty;
        return instance.Length > 0 && !string.Equals(instance, harness, StringComparison.OrdinalIgnoreCase)
            ? badge + "/" + instance
            : badge;
    }

    /// <summary>Short display name for one harness id.</summary>
    public static string ShortHarness(string harnessId) => HarnessIds.Normalize(harnessId) switch
    {
        HarnessIds.Codex => "Codex",
        HarnessIds.Dsh => "DSH",
        HarnessIds.Claude => "Claude",
        var other => other,
    };

    /// <summary>
    /// Concise capability disclosure. It states which request classes are actually
    /// observed and never claims a Codex-only limitation for other harnesses; the
    /// per-source notes stay available in the health details.
    /// </summary>
    public static string CapabilityDisclosure(MonitorCapabilities capabilities)
    {
        var permission = capabilities.PermissionRequests.Support;
        var input = capabilities.InputRequests.Support;
        if (permission == CapabilitySupport.LiveObserved && input == CapabilitySupport.LiveObserved)
        {
            return "approval and input requests: observed";
        }

        return "approval requests: " + Support(permission) + " · input requests: " + Support(input);
    }

    /// <summary>
    /// Typed capability distinction for one source's request detection. These are the
    /// states the health detail must not blur: observed (seen live), supported
    /// (implemented and contract-verified), not exercised, unsupported (the host cannot
    /// expose it) and unreadable (present in principle, not readable now).
    /// </summary>
    public static string CapabilityDetail(MonitorCapabilities capabilities) =>
        "requests: approval " + CapabilityWord(capabilities.PermissionRequests.Support) +
        ", input " + CapabilityWord(capabilities.InputRequests.Support);

    public static string CapabilityWord(CapabilitySupport support) => support switch
    {
        CapabilitySupport.LiveObserved => "observed",
        CapabilitySupport.Supported => "supported",
        CapabilitySupport.NotExercised => "not exercised",
        CapabilitySupport.Unavailable => "unreadable",
        _ => "unsupported",
    };

    public static string Support(CapabilitySupport support) => support switch
    {
        CapabilitySupport.Supported => "supported",
        CapabilitySupport.Unsupported => "unsupported",
        CapabilitySupport.Unavailable => "unavailable",
        CapabilitySupport.NotExercised => "not exercised",
        _ => "observed",
    };

    /// <summary>
    /// Distinct copy for healthy-empty, degraded-empty and unavailable. It names no
    /// single harness: the HUD monitors Codex and every optional bridge, so
    /// "no Codex conversations" was wrong copy on a dsh-only or Claude-only machine.
    /// </summary>
    public static string EmptyState(MonitoringSnapshot snapshot)
    {
        if (snapshot.Health.Level == MonitoringHealthLevel.Unavailable)
        {
            return "Task activity unavailable. Open monitoring details to see which source is not readable.";
        }

        if (snapshot.Health.Level == MonitoringHealthLevel.Degraded)
        {
            return "No tasks visible yet. Coverage is limited — see monitoring details, or add a source in Projects & sources.";
        }

        return "No agent sessions tracked yet. Start a task, or add a harness source in Projects & sources.";
    }
}
