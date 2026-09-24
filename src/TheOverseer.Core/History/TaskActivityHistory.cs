using System.Globalization;
using TheOverseer.Core.Models;

namespace TheOverseer.Core.History;

/// <summary>
/// Displayed activity categories. Thinking, planning and tool work are mutually
/// exclusive within Working; idle is confirmed inactivity and unknown is absent
/// or stale observation. Unknown is coverage, never a lowest-priority action.
/// </summary>
public enum ActivityCategory
{
    Unknown,
    Idle,
    Thinking,
    Planning,
    Working,
    Waiting,
    Error,
    Complete,
}

public enum ActivityEvidence
{
    Unknown,
    Inferred,
    Explicit,
}

/// <summary>Half-open observed interval. Markers are zero-length attention points.</summary>
public sealed record ActivityInterval(
    DateTimeOffset Start,
    DateTimeOffset End,
    ActivityCategory Category,
    ActivityEvidence Evidence,
    string DetailCode,
    bool IsMarker,
    ProjectId Project = default)
{
    public TimeSpan Duration => End > Start ? End - Start : TimeSpan.Zero;
}

/// <summary>One task lane: current known action plus observed intervals.</summary>
public sealed record TaskActivityLane(
    TaskKey Key,
    ProjectId ProjectId,
    string HarnessId,
    ActivityCategory Current,
    ActivityEvidence CurrentEvidence,
    string CurrentDetailCode,
    IReadOnlyList<ActivityInterval> Intervals,
    bool CoverageUnknown,
    DateTimeOffset LastObservedAt)
{
    public bool IsIdle => Current == ActivityCategory.Idle;

    public bool IsUnknown => CoverageUnknown || Current == ActivityCategory.Unknown;
}

/// <summary>Project strip: union of member intervals plus honest coverage.</summary>
public sealed record ProjectActivityStrip(
    ProjectId ProjectId,
    IReadOnlyList<ActivityInterval> Intervals,
    IReadOnlyList<ActivityCategory> MixedCategories,
    IReadOnlyList<ActivityInterval> Markers,
    TimeSpan ActiveObserved,
    bool CoverageUnknown,
    int ObservedTaskCount,
    int MemberTaskCount);

public sealed record TaskActivityOptions(
    TimeSpan Window,
    TimeSpan ObservationFreshness,
    int MaximumHistories,
    int MaximumIntervalsPerTask,
    int MaximumIntervalsTotal)
{
    public static TaskActivityOptions Default { get; } = new(
        TimeSpan.FromMinutes(60),
        TimeSpan.FromSeconds(10),
        512,
        2048,
        100_000);
}

/// <summary>
/// Producer-side observed activity. It records only what a source actually
/// reported, with the source observation time; nothing is reconstructed from
/// current state, task start times or old count samples. History is in memory
/// only: a restart begins with unknown pre-launch coverage.
/// </summary>
public sealed class TaskActivityHistory
{
    private readonly TaskActivityOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly object _gate = new();
    private readonly Dictionary<TaskKey, TaskLane> _lanes = new();
    private readonly List<TaskKey> _order = new();
    private int _intervalCount;

    public TaskActivityHistory(TaskActivityOptions? options = null, TimeProvider? timeProvider = null)
    {
        _options = options ?? TaskActivityOptions.Default;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public int HistoryCount
    {
        get
        {
            lock (_gate)
            {
                return _lanes.Count;
            }
        }
    }

    public int IntervalCount
    {
        get
        {
            lock (_gate)
            {
                return _intervalCount;
            }
        }
    }

    /// <summary>
    /// Records one observation for one task. Repeating the same category refreshes
    /// liveness without splitting the interval, so a healthy poll extends the
    /// action only while the action evidence is still the same; a different
    /// category closes the previous interval at the observed instant.
    /// </summary>
    public void Record(
        TaskKey key,
        ProjectId projectId,
        string harnessId,
        ActivityCategory category,
        ActivityEvidence evidence,
        string detailCode,
        DateTimeOffset observedAtUtc,
        bool isMarker = false)
    {
        if (key.IsEmpty)
        {
            return;
        }

        var now = observedAtUtc == default ? _timeProvider.GetUtcNow() : observedAtUtc;
        lock (_gate)
        {
            if (!_lanes.TryGetValue(key, out var lane))
            {
                Trim();
                lane = new TaskLane(key, projectId, HarnessIds.Normalize(harnessId));
                _lanes[key] = lane;
                _order.Add(key);
            }

            lane.ProjectId = projectId.IsUnassigned ? lane.ProjectId : projectId;
            lane.HarnessId = HarnessIds.Normalize(harnessId);
            lane.LastObservedAt = now;

            if (isMarker)
            {
                // A terminal marker closes the action timeline it belongs to: the
                // confirmed work stays visible and no new open interval starts.
                if (lane.Open is { } closing)
                {
                    AddInterval(lane, closing);
                    lane.Open = null;
                }

                AddInterval(lane, new ActivityInterval(now, now, category, evidence, detailCode, true, projectId));
                lane.MarkerCount++;
                return;
            }

            if (lane.Open is { } open && open.Category == category)
            {
                // Same action observed again: extend the open interval, keep evidence.
                lane.Open = open with { End = now, DetailCode = detailCode.Length > 0 ? detailCode : open.DetailCode };
                lane.LastObservedAt = now;
                lane.MarkerCount = 0;
                return;
            }

            if (lane.Open is { } previous)
            {
                AddInterval(lane, previous);
            }

            lane.Open = new ActivityInterval(now, now, category, evidence, detailCode, false, projectId);
            lane.MarkerCount = 0;
            lane.LastObservedAt = now;
        }
    }

    /// <summary>Explicit project reassignment: affects future intervals only.</summary>
    public void Reassign(TaskKey key, ProjectId projectId, DateTimeOffset nowUtc)
    {
        lock (_gate)
        {
            if (!_lanes.TryGetValue(key, out var lane))
            {
                return;
            }

            if (lane.Open is { } open)
            {
                AddInterval(lane, open);
                lane.Open = null;
            }

            lane.ProjectId = projectId;
            lane.LastObservedAt = nowUtc;
        }
    }

    public void Remove(TaskKey key)
    {
        lock (_gate)
        {
            if (_lanes.Remove(key, out var lane))
            {
                _intervalCount -= lane.Intervals.Count + (lane.Open is null ? 0 : 1);
                _order.Remove(key);
            }
        }
    }

    /// <summary>Task lanes clipped to the rolling window, with coverage honesty.</summary>
    public IReadOnlyList<TaskActivityLane> Lanes(DateTimeOffset now)
    {
        lock (_gate)
        {
            var lanes = new List<TaskActivityLane>(_lanes.Count);
            foreach (var key in _order)
            {
                if (_lanes.TryGetValue(key, out var lane))
                {
                    lanes.Add(Project(lane, now));
                }
            }

            return lanes;
        }
    }

    public TaskActivityLane? Lane(TaskKey key, DateTimeOffset now)
    {
        lock (_gate)
        {
            return _lanes.TryGetValue(key, out var lane) ? Project(lane, now) : null;
        }
    }

    /// <summary>
    /// Project strip computed from the members attributed to that project at each
    /// interval, independent of current filters. The union of intervals is never
    /// a sum, so it cannot exceed the window.
    /// </summary>
    public ProjectActivityStrip Project(ProjectId projectId, DateTimeOffset now)
    {
        lock (_gate)
        {
            return ProjectCore(projectId, now);
        }
    }

    public IReadOnlyList<ProjectActivityStrip> Projects(DateTimeOffset now)
    {
        lock (_gate)
        {
            var ids = new List<ProjectId>();
            foreach (var key in _order)
            {
                if (!_lanes.TryGetValue(key, out var lane))
                {
                    continue;
                }

                if (!lane.ProjectId.IsUnassigned && !ids.Contains(lane.ProjectId))
                {
                    ids.Add(lane.ProjectId);
                }

                foreach (var interval in lane.Intervals)
                {
                    if (!interval.Project.IsUnassigned && !ids.Contains(interval.Project))
                    {
                        ids.Add(interval.Project);
                    }
                }
            }

            var strips = new List<ProjectActivityStrip>(ids.Count);
            foreach (var id in ids)
            {
                strips.Add(ProjectCore(id, now));
            }

            return strips;
        }
    }

    /// <summary>Safe summary: counts only.</summary>
    public string Describe()
    {
        lock (_gate)
        {
            return "tasks=" + _lanes.Count.ToString(CultureInfo.InvariantCulture) +
                   " intervals=" + _intervalCount.ToString(CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// Project strip built from interval attribution: a reassignment moves future
    /// intervals only, so the project's strip keeps the history it actually
    /// observed and gains the new intervals from the reassignment instant.
    /// </summary>
    private ProjectActivityStrip ProjectCore(ProjectId projectId, DateTimeOffset now)
    {
        var windowStart = now - _options.Window;
        var collected = new List<ActivityInterval>();
        var markers = new List<ActivityInterval>();
        var coverageUnknown = false;
        var observedMembers = 0;
        var members = 0;

        foreach (var key in _order)
        {
            if (!_lanes.TryGetValue(key, out var lane))
            {
                continue;
            }

            var projected = Project(lane, now);
            var contributes = false;
            foreach (var interval in projected.Intervals)
            {
                if (interval.Project != projectId)
                {
                    continue;
                }

                contributes = true;
                if (interval.IsMarker)
                {
                    if (interval.End >= windowStart)
                    {
                        markers.Add(interval);
                    }

                    continue;
                }

                var start = interval.Start < windowStart ? windowStart : interval.Start;
                if (interval.End >= start)
                {
                    collected.Add(interval with { Start = start });
                }
            }

            if (!contributes)
            {
                continue;
            }

            members++;
            if (projected.CoverageUnknown)
            {
                coverageUnknown = true;
            }
            else
            {
                observedMembers++;
            }
        }

        collected.Sort(static (left, right) => left.Start.CompareTo(right.Start));
        markers.Sort(static (left, right) => left.Start.CompareTo(right.Start));

        var union = Union(collected);
        var mixed = union.Select(static interval => interval.Category).Distinct().Take(6).ToArray();
        var active = TimeSpan.Zero;
        foreach (var interval in union)
        {
            if (interval.Category is not (ActivityCategory.Unknown or ActivityCategory.Idle))
            {
                active += interval.Duration;
            }
        }

        return new ProjectActivityStrip(projectId, union, mixed, markers, active, coverageUnknown, observedMembers, members);
    }

    /// <summary>
    /// Merges member intervals into one lane. Overlapping categories resolve by
    /// priority (attention, failure, tool work, planning, thinking, confirmed
    /// idle); unknown never wins over a known action, it only lowers coverage.
    /// </summary>
    private static IReadOnlyList<ActivityInterval> Union(List<ActivityInterval> intervals)
    {
        if (intervals.Count == 0)
        {
            return Array.Empty<ActivityInterval>();
        }

        var windowStart = intervals[0].Start;
        var windowEnd = intervals[0].End;
        foreach (var interval in intervals)
        {
            if (interval.Start < windowStart) windowStart = interval.Start;
            if (interval.End > windowEnd) windowEnd = interval.End;
        }

        if (windowEnd <= windowStart)
        {
            return Array.Empty<ActivityInterval>();
        }

        // One-second segmentation at strip granularity: a brief attention point
        // occupies exactly the second it was observed in and is never widened
        // into invented duration. Very long spans fall back to exact boundaries.
        var seconds = (int)Math.Min(Math.Ceiling((windowEnd - windowStart).TotalSeconds), 6 * 60 * 60);
        var segments = new List<(DateTimeOffset Start, DateTimeOffset End, ActivityInterval Winner)>();
        for (var index = 0; index < seconds; index++)
        {
            var start = windowStart + TimeSpan.FromSeconds(index);
            var end = start + TimeSpan.FromSeconds(1);
            ActivityInterval? winner = null;
            foreach (var interval in intervals)
            {
                if (interval.IsMarker)
                {
                    continue;
                }

                var covers = interval.Start == interval.End
                    ? start <= interval.Start && interval.Start < end
                    : interval.Start <= start && interval.End >= end;
                if (covers && (winner is null || Priority(interval.Category) < Priority(winner.Category)))
                {
                    winner = interval;
                }
            }

            if (winner is not null)
            {
                segments.Add((start, end, winner));
            }
        }

        var result = new List<ActivityInterval>();
        foreach (var (start, end, winner) in segments)
        {
            if (result.Count > 0 && result[^1].Category == winner.Category && result[^1].End == start)
            {
                result[^1] = result[^1] with { End = end };
                continue;
            }

            result.Add(winner with { Start = start, End = end });
        }

        return result;
    }

    private static int Priority(ActivityCategory category) => category switch
    {
        ActivityCategory.Waiting => 0,
        ActivityCategory.Error => 1,
        ActivityCategory.Working => 2,
        ActivityCategory.Planning => 3,
        ActivityCategory.Thinking => 4,
        ActivityCategory.Idle => 5,
        _ => 6,
    };

    /// <summary>
    /// Projects one lane: clips to the window, splits stale open coverage into an
    /// explicit unknown tail, and keeps old intervals only inside the window.
    /// </summary>
    private TaskActivityLane Project(TaskLane lane, DateTimeOffset now)
    {
        var windowStart = now - _options.Window;
        var intervals = new List<ActivityInterval>(lane.Intervals.Count + 2);
        foreach (var interval in lane.Intervals)
        {
            if (interval.End < windowStart)
            {
                continue;
            }

            var start = interval.Start < windowStart ? windowStart : interval.Start;
            intervals.Add(interval with { Start = start });
        }

        var coverageUnknown = false;
        var current = ActivityCategory.Unknown;
        var evidence = ActivityEvidence.Unknown;
        var detail = string.Empty;

        if (lane.Open is { } open)
        {
            // The last observed action stays the reported current action even
            // after the freshness bound; what expires is coverage, not history.
            current = open.Category;
            evidence = open.Evidence;
            detail = open.DetailCode;

            // Confirmed evidence ends at the last observation. The bounded
            // belief window after it is rendered as inferred, and anything past
            // that is unknown coverage. The last action is never extended
            // indefinitely and idle is never synthesized.
            var start = open.Start < windowStart ? windowStart : open.Start;
            var confirmedEnd = open.End;
            if (confirmedEnd >= start && confirmedEnd >= windowStart)
            {
                // A point observation is kept as a zero-length confirmed interval;
                // the strip union widens it to the second it happened in.
                intervals.Add(open with { Start = start, End = confirmedEnd, Project = lane.ProjectId });
            }

            if (open.End < windowStart)
            {
                // The whole open action aged out of the rolling window.
                coverageUnknown = true;
                detail = "no-observation";
                intervals.Sort(static (left, right) => left.Start.CompareTo(right.Start));
                return new TaskActivityLane(lane.Key, lane.ProjectId, lane.HarnessId, current, evidence, detail, intervals, coverageUnknown, lane.LastObservedAt);
            }

            var beliefEnd = open.End + _options.ObservationFreshness;
            if (beliefEnd > now)
            {
                beliefEnd = now;
            }

            if (beliefEnd > confirmedEnd)
            {
                intervals.Add(new ActivityInterval(confirmedEnd, beliefEnd, open.Category, ActivityEvidence.Inferred, open.DetailCode, false, lane.ProjectId));
            }

            if (beliefEnd < now)
            {
                intervals.Add(new ActivityInterval(beliefEnd, now, ActivityCategory.Unknown, ActivityEvidence.Unknown, "no-observation", false, lane.ProjectId));
                coverageUnknown = true;
            }
        }
        else
        {
            // Nothing observed yet, or only closed intervals with no open action.
            coverageUnknown = lane.LastObservedAt + _options.ObservationFreshness < now || lane.Intervals.Count == 0;
            detail = "no-observation";
        }

        intervals.Sort(static (left, right) => left.Start.CompareTo(right.Start));
        return new TaskActivityLane(
            lane.Key,
            lane.ProjectId,
            lane.HarnessId,
            current,
            evidence,
            detail,
            intervals,
            coverageUnknown,
            lane.LastObservedAt);
    }

    private void AddInterval(TaskLane lane, ActivityInterval interval)
    {
        if (lane.Intervals.Count >= _options.MaximumIntervalsPerTask)
        {
            lane.Intervals.RemoveAt(0);
            _intervalCount--;
        }

        lane.Intervals.Add(interval);
        _intervalCount++;
    }

    /// <summary>Expire first, then the oldest inactive lane; capacity truncation shows unknown.</summary>
    private void Trim()
    {
        if (_lanes.Count < _options.MaximumHistories && _intervalCount < _options.MaximumIntervalsTotal)
        {
            return;
        }

        var now = _timeProvider.GetUtcNow();
        foreach (var key in _order.ToArray())
        {
            if (!_lanes.TryGetValue(key, out var lane))
            {
                continue;
            }

            if (lane.Open is null && lane.LastObservedAt + _options.Window < now)
            {
                _intervalCount -= lane.Intervals.Count;
                _lanes.Remove(key);
                _order.Remove(key);
            }
        }

        while ((_lanes.Count >= _options.MaximumHistories || _intervalCount >= _options.MaximumIntervalsTotal) && _order.Count > 0)
        {
            var oldest = _order[0];
            _order.RemoveAt(0);
            if (_lanes.Remove(oldest, out var lane))
            {
                _intervalCount -= lane.Intervals.Count + (lane.Open is null ? 0 : 1);
            }
        }
    }

    private sealed class TaskLane
    {
        public TaskLane(TaskKey key, ProjectId projectId, string harnessId)
        {
            Key = key;
            ProjectId = projectId;
            HarnessId = harnessId;
        }

        public TaskKey Key { get; }

        public ProjectId ProjectId { get; set; }

        public string HarnessId { get; set; }

        public List<ActivityInterval> Intervals { get; } = new();

        public ActivityInterval? Open { get; set; }

        public DateTimeOffset LastObservedAt { get; set; }

        public int MarkerCount { get; set; }
    }
}

/// <summary>
/// Render geometry for one strip: a bounded number of bins over the window with
/// category proportions preserved and brief attention markers retained.
/// </summary>
public static class ActivityTimelineGeometry
{
    public const int MaximumBins = 720;

    public static IReadOnlyList<ActivityInterval> Bin(
        ProjectActivityStrip strip,
        DateTimeOffset now,
        TimeSpan window,
        int maximumBins = MaximumBins)
    {
        var bins = new List<ActivityInterval>();
        if (window <= TimeSpan.Zero)
        {
            return bins;
        }

        var requested = maximumBins < 1 ? 1 : maximumBins;
        var seconds = Math.Max(1.0, window.TotalSeconds / requested);
        var width = TimeSpan.FromSeconds(seconds);
        var binCount = (int)Math.Ceiling(window.TotalSeconds / seconds);
        var windowStart = now - window;

        for (var index = 0; index < binCount; index++)
        {
            var start = windowStart + TimeSpan.FromSeconds(index * seconds);
            var end = start + width;
            bins.Add(new ActivityInterval(start, end > now ? now : end, ActivityCategory.Unknown, ActivityEvidence.Unknown, "empty", false));
        }

        foreach (var interval in strip.Intervals)
        {
            if (interval.End <= windowStart || interval.Start >= now)
            {
                continue;
            }

            var firstIndex = (int)Math.Floor((interval.Start - windowStart).TotalSeconds / seconds);
            var lastIndex = (int)Math.Floor(((interval.End > now ? now : interval.End) - windowStart).TotalSeconds / seconds);
            firstIndex = Math.Clamp(firstIndex, 0, bins.Count - 1);
            lastIndex = Math.Clamp(lastIndex, 0, bins.Count - 1);
            for (var index = firstIndex; index <= lastIndex; index++)
            {
                var bin = bins[index];
                if (interval.Category == ActivityCategory.Unknown && bin.Category != ActivityCategory.Unknown)
                {
                    continue;
                }

                if (Priority(interval.Category) < Priority(bin.Category))
                {
                    bins[index] = bin with { Category = interval.Category, Evidence = interval.Evidence, DetailCode = interval.DetailCode };
                }
            }
        }

        return bins;
    }

    private static int Priority(ActivityCategory category) => category switch
    {
        ActivityCategory.Waiting => 0,
        ActivityCategory.Error => 1,
        ActivityCategory.Complete => 2,
        ActivityCategory.Working => 3,
        ActivityCategory.Planning => 4,
        ActivityCategory.Thinking => 5,
        ActivityCategory.Idle => 6,
        _ => 7,
    };
}
