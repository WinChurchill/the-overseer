using System.Globalization;
using System.Text;

namespace TheOverseer.Core.History;

/// <summary>
/// How much observation backs one strip bin. Unknown coverage is missing
/// observation (a gap, a restart, a disabled source or sleep) and is never a
/// statement that an agent was idle.
/// </summary>
public enum ActivityCoverage
{
    Unknown,
    Partial,
    Complete,
}

/// <summary>
/// One bounded strip bin. The strip transport used to keep only the category and
/// dropped the evidence strength, the coverage and the interval detail, so a
/// stale or inferred segment could render like a confirmed one. A bin now carries
/// everything the tooltip, keyboard and screen-reader text need.
/// </summary>
public sealed record ActivityStripBin(
    DateTimeOffset Start,
    DateTimeOffset End,
    ActivityCategory Category,
    ActivityEvidence Evidence,
    ActivityCoverage Coverage,
    string DetailCode,
    int ConcurrentTasks = 0,
    bool IsMixed = false)
{
    /// <summary>Bin span. Bins are one minute of the rolling hour.</summary>
    public TimeSpan Duration => End > Start ? End - Start : TimeSpan.Zero;

    /// <summary>True when the bin holds an observed category rather than a gap.</summary>
    public bool IsKnown => Category != ActivityCategory.Unknown;

    /// <summary>True when the category is an inference, not a structured observation.</summary>
    public bool IsInferred => Evidence == ActivityEvidence.Inferred;

    /// <summary>
    /// Category, interval, duration, evidence and coverage on one line. Used as the
    /// per-bin hover tooltip and as the automation name, so the strip never relies
    /// on colour alone.
    /// </summary>
    public string Description
    {
        get
        {
            var text = new StringBuilder(96);
            text.Append(Category).Append(" · ")
                .Append(Start.ToString("HH:mm", CultureInfo.InvariantCulture)).Append('–')
                .Append(End.ToString("HH:mm", CultureInfo.InvariantCulture));
            var minutes = Duration.TotalMinutes;
            text.Append(" (").Append(minutes < 1 ? "under a minute" : Minutes(minutes)).Append(')');
            text.Append(" · ").Append(Evidence switch
            {
                ActivityEvidence.Explicit => "explicit observation",
                ActivityEvidence.Inferred => "inferred from the last observation (estimated)",
                _ => "no evidence",
            });
            text.Append(" · ").Append(Coverage switch
            {
                ActivityCoverage.Complete => "complete coverage",
                ActivityCoverage.Partial => "partial coverage",
                _ => "unknown coverage — missing observation, not idle",
            });
            if (ConcurrentTasks > 1)
            {
                text.Append(" · ").Append(ConcurrentTasks.ToString(CultureInfo.InvariantCulture)).Append(" tasks");
            }

            if (IsMixed)
            {
                text.Append(" · mixed categories");
            }

            if (DetailCode.Length > 0)
            {
                text.Append(" · detail ").Append(DetailCode);
            }

            return text.ToString();
        }
    }

    private static string Minutes(double minutes)
    {
        var rounded = (int)Math.Round(minutes, MidpointRounding.AwayFromZero);
        return rounded <= 1
            ? "1 min"
            : rounded.ToString(CultureInfo.InvariantCulture) + " min";
    }
}

/// <summary>
/// Plain-text strip summary shared by tooltips, the header summary and automation
/// names. It is bounded by the bin count and never claims more than was observed.
/// </summary>
public static class ActivityStripText
{
    public const int CategoryCount = 8;

    public static string Describe(IReadOnlyList<ActivityStripBin> bins)
    {
        if (bins.Count == 0)
        {
            return "No observation recorded for this row in the last hour.";
        }

        var minutes = new int[CategoryCount];
        var partial = 0;
        var peak = 0;
        var mixed = false;
        var idle = 0;
        foreach (var bin in bins)
        {
            var index = (int)bin.Category;
            if (index >= 0 && index < minutes.Length)
            {
                minutes[index] += Math.Max(1, (int)Math.Round(bin.Duration.TotalMinutes, MidpointRounding.AwayFromZero));
            }

            if (bin.Coverage == ActivityCoverage.Partial)
            {
                partial++;
            }

            if (bin.ConcurrentTasks > peak)
            {
                peak = bin.ConcurrentTasks;
            }

            if (bin.Category == ActivityCategory.Idle)
            {
                idle++;
            }

            mixed |= bin.IsMixed;
        }

        var text = new StringBuilder(160);
        text.Append("Last hour: ");
        var first = true;
        for (var index = 0; index < minutes.Length; index++)
        {
            if (minutes[index] == 0)
            {
                continue;
            }

            if (!first)
            {
                text.Append(" · ");
            }

            first = false;
            text.Append(minutes[index].ToString(CultureInfo.InvariantCulture)).Append("m ");
            text.Append(index == (int)ActivityCategory.Unknown
                ? "Unknown (no observation)"
                : ((ActivityCategory)index).ToString());
        }

        if (first)
        {
            text.Append("nothing observed");
        }

        if (partial > 0)
        {
            text.Append(" · partial coverage in ").Append(partial.ToString(CultureInfo.InvariantCulture))
                .Append(partial == 1 ? " minute" : " minutes");
        }

        if (idle == 0 && minutes[(int)ActivityCategory.Unknown] == 0)
        {
            text.Append(" · no confirmed idle");
        }

        if (peak > 1)
        {
            text.Append(" · up to ").Append(peak.ToString(CultureInfo.InvariantCulture)).Append(" tasks at once");
        }

        if (mixed)
        {
            text.Append(" · mixed categories");
        }

        return text.ToString();
    }
}
