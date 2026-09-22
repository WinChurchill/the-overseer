using System.Globalization;
using System.Text;

namespace TheOverseer.Core.History;

/// <summary>
/// Turns an <see cref="ActivitySeries"/> into bounded polyline geometry for the
/// shared HUD sparkline. Pure math: no WPF, no theme and no monitor access.
/// </summary>
public static class ActivityChartGeometry
{
    public const int DefaultMaximumColumns = 240;

    /// <summary>
    /// Builds at most <paramref name="maximumColumns"/> columns over a fixed
    /// 60-minute axis with a zero baseline. Columns without samples are gaps and
    /// break the line; a column containing any unknown or partial sample keeps
    /// the qualified style so a lower bound is never drawn as a full observation.
    /// </summary>
    public static IReadOnlyList<ActivityChartSegment> Build(
        ActivitySeries series,
        double width,
        double height,
        int maximumColumns = DefaultMaximumColumns)
    {
        if (series is null || series.Samples.Count == 0 || width <= 0 || height <= 0)
        {
            return Array.Empty<ActivityChartSegment>();
        }

        var spanSeconds = Math.Max(1.0, (series.To - series.From).TotalSeconds);

        // One column per second until the cap, so sparse samples cannot stretch a
        // column across unobserved time and hide a gap.
        var columns = Math.Clamp((int)Math.Min(maximumColumns, Math.Ceiling(spanSeconds)), 1, Math.Max(1, maximumColumns));
        var columnSeconds = spanSeconds / columns;
        var peak = Math.Max(1, series.PeakWorking);

        var values = new int[columns];
        var known = new bool[columns];
        var partial = new bool[columns];
        var needsHuman = new int[columns];
        var timestamps = new DateTimeOffset[columns];

        foreach (var sample in series.Samples)
        {
            var offset = (sample.Timestamp - series.From).TotalSeconds;
            var index = (int)Math.Floor(offset / columnSeconds);
            index = Math.Clamp(index, 0, columns - 1);
            if (!sample.Working.IsKnown)
            {
                continue;
            }

            if (!known[index] || sample.Working.Value >= values[index])
            {
                values[index] = sample.Working.Value;
                timestamps[index] = sample.Timestamp;
                needsHuman[index] = sample.NeedsHuman.IsKnown ? sample.NeedsHuman.Value : 0;
            }

            known[index] = true;
            partial[index] = partial[index] || !sample.Working.IsComplete;
        }

        var segments = new List<ActivityChartSegment>();
        var current = new List<ActivityChartPoint>();
        var currentKind = ActivitySegmentKind.Complete;

        void Flush()
        {
            if (current.Count > 0)
            {
                segments.Add(new ActivityChartSegment(currentKind, current.ToArray()));
                current = new List<ActivityChartPoint>();
            }
        }

        for (var index = 0; index < columns; index++)
        {
            if (!known[index])
            {
                Flush();
                continue;
            }

            var kind = partial[index] ? ActivitySegmentKind.Partial : ActivitySegmentKind.Complete;
            if (kind != currentKind && current.Count > 0)
            {
                Flush();
            }

            currentKind = kind;
            var x = Math.Round((index + 0.5) / columns * width, 2);

            // Keep one pixel of separation from the baseline rule so a constant
            // zero series is still visible as a line rather than invisible.
            var usable = Math.Max(1.0, height - 2);
            var y = Math.Round(1 + (usable - (values[index] / (double)peak) * usable), 2);
            current.Add(new ActivityChartPoint(x, y, values[index], needsHuman[index], partial[index], timestamps[index]));
        }

        Flush();
        return segments;
    }

    /// <summary>Safe, bounded summary of the visible window for a tooltip.</summary>
    public static string Tooltip(ActivitySeries series, DateTimeOffset now)
    {
        if (series is null || series.IsEmpty)
        {
            return "No activity recorded in the last 60 minutes.";
        }

        var builder = new StringBuilder(160);
        builder.Append(series.SampleCount.ToString(CultureInfo.InvariantCulture)).Append(series.SampleCount == 1 ? " sample" : " samples");
        builder.Append(" in the last 60 minutes · peak ").Append(Math.Max(0, series.PeakWorking).ToString(CultureInfo.InvariantCulture)).Append(" working");
        var latest = series.Samples[^1];
        builder.Append(" · latest ").Append(Qualify(latest));
        builder.Append(" · updated ").Append(FormattableString.Invariant($"{Math.Max(0, (now - latest.Timestamp).TotalSeconds):0}s")).Append(" ago");
        if (series.HasUnknown)
        {
            builder.Append(" · contains unknown coverage");
        }

        if (series.HasPartial)
        {
            builder.Append(" · contains partial counts");
        }

        if (series.HasGap)
        {
            builder.Append(" · gaps are unobserved time, not zero");
        }

        return builder.ToString();
    }

    private static string Qualify(ActivitySample sample)
    {
        var working = sample.Working.IsKnown ? sample.Working.Value.ToString(CultureInfo.InvariantCulture) : "unknown";
        var suffix = sample.Working.IsKnown && !sample.Working.IsComplete ? " (partial)" : string.Empty;
        return working + " working" + suffix;
    }
}
