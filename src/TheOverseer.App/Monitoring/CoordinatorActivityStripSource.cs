using TheOverseer.Core.History;
using TheOverseer.Core.Models;
using TheOverseer.Core.Voice;
using TheOverseer.UI.ViewModels;

namespace TheOverseer.App.Monitoring;

/// <summary>
/// Projects the coordinator's producer-side observed activity into the bounded bins
/// the strip controls draw: one hour of history, at most one bin per minute. The UI
/// never reads logs or reconstructs history; it renders what was observed.
///
/// A bin carries the category plus its evidence, coverage, interval and structural
/// detail code, so the transport no longer flattens an inferred, partial, mixed or
/// stale segment into a claim of confirmed work or idle.
///
/// Every call for one refresh instant is served from a single projection: the lanes
/// and the project strips are read once, each distinct project is binned once, and
/// each task lane is resolved once and cached for the remaining rows of that tick.
/// </summary>
public sealed class CoordinatorActivityStripSource : IActivityStripSource
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(60);
    private const int MaximumBins = 56;

    private readonly NotificationCoordinator _coordinator;
    private readonly TimeProvider _timeProvider;
    private readonly object _gate = new();

    private RefreshCache? _cache;

    public CoordinatorActivityStripSource(NotificationCoordinator coordinator, TimeProvider? timeProvider = null)
    {
        _coordinator = coordinator;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public IReadOnlyList<ActivityStripBin> ProjectBins(ProjectId projectId, DateTimeOffset now) =>
        Cache(now).Project(projectId);

    public IReadOnlyList<ActivityStripBin> TaskBins(TaskKey key, DateTimeOffset now) =>
        Cache(now).Task(key);

    /// <summary>
    /// One projection per refresh instant. The HUD asks with the same instant for
    /// every row of a tick, so the first call builds and the rest read the cache; a
    /// later instant (the next tick or a newer snapshot) rebuilds exactly once.
    /// </summary>
    private RefreshCache Cache(DateTimeOffset now)
    {
        lock (_gate)
        {
            if (_cache is { } existing && existing.Now == now)
            {
                return existing;
            }

            var cache = new RefreshCache(_coordinator, now, Window, MaximumBins);
            _cache = cache;
            return cache;
        }
    }

    private sealed class RefreshCache
    {
        private readonly Dictionary<ProjectId, IReadOnlyList<ActivityInterval>> _projectIntervals = new();
        private readonly Dictionary<ProjectId, bool> _projectCoverage = new();
        private readonly Dictionary<ProjectId, IReadOnlyList<ActivityStripBin>> _projects = new();
        private readonly Dictionary<TaskKey, TaskActivityLane> _lanes = new();
        private readonly Dictionary<TaskKey, IReadOnlyList<ActivityStripBin>> _tasks = new();
        private readonly Dictionary<ProjectId, ActivityConcurrency> _concurrency = new();
        private readonly TimeSpan _window;
        private readonly int _maximumBins;

        public RefreshCache(NotificationCoordinator coordinator, DateTimeOffset now, TimeSpan window, int maximumBins)
        {
            Now = now;
            _window = window;
            _maximumBins = maximumBins;

            // Exactly two source reads per refresh, never one per row.
            var strips = coordinator.ActivityStrips(now);
            var lanes = coordinator.ActivityLanes(now);
            foreach (var strip in strips)
            {
                _projectIntervals[strip.ProjectId] = Combined(strip);
                _projectCoverage[strip.ProjectId] = strip.CoverageUnknown;
            }

            foreach (var lane in lanes)
            {
                _lanes[lane.Key] = lane;
            }

            BuildConcurrency(lanes);
        }

        public DateTimeOffset Now { get; }

        public IReadOnlyList<ActivityStripBin> Project(ProjectId projectId)
        {
            if (_projects.TryGetValue(projectId, out var cached))
            {
                return cached;
            }

            IReadOnlyList<ActivityStripBin> bins;
            if (_projectIntervals.TryGetValue(projectId, out var intervals))
            {
                bins = Bins(intervals, Now, _window, _maximumBins, _projectCoverage[projectId]);
            }
            else
            {
                // Nothing was ever observed for this project: a full hour of unknown
                // coverage. Never a confirmed idle hour.
                bins = Unknown(Now, _window, _maximumBins, "no-observation");
            }

            if (_concurrency.TryGetValue(projectId, out var concurrency))
            {
                bins = concurrency.Apply(bins);
            }

            _projects[projectId] = bins;
            return bins;
        }

        public IReadOnlyList<ActivityStripBin> Task(TaskKey key)
        {
            if (_tasks.TryGetValue(key, out var cached))
            {
                return cached;
            }

            var bins = _lanes.TryGetValue(key, out var lane)
                ? Bins(lane.Intervals, Now, _window, _maximumBins, lane.CoverageUnknown)
                : Array.Empty<ActivityStripBin>();
            _tasks[key] = bins;
            return bins;
        }

        /// <summary>
        /// Project strip intervals: the observed union plus its point markers, so an
        /// explicit completion, failure or attention marker keeps its own bin instead
        /// of being dropped by the transport.
        /// </summary>
        private static IReadOnlyList<ActivityInterval> Combined(ProjectActivityStrip strip)
        {
            if (strip.Markers.Count == 0)
            {
                return strip.Intervals;
            }

            var combined = new ActivityInterval[strip.Intervals.Count + strip.Markers.Count];
            for (var index = 0; index < strip.Intervals.Count; index++)
            {
                combined[index] = strip.Intervals[index];
            }

            for (var index = 0; index < strip.Markers.Count; index++)
            {
                combined[strip.Intervals.Count + index] = strip.Markers[index];
            }

            return combined;
        }

        /// <summary>
        /// Concurrent observed tasks and mixed categories per bin, computed in one
        /// pass over the lanes instead of a lane scan per project row.
        /// </summary>
        private void BuildConcurrency(IReadOnlyList<TaskActivityLane> lanes)
        {
            var seconds = Math.Max(1.0, _window.TotalSeconds / _maximumBins);
            var windowStart = Now - _window;

            for (var laneIndex = 0; laneIndex < lanes.Count; laneIndex++)
            {
                var lane = lanes[laneIndex];
                foreach (var interval in lane.Intervals)
                {
                    if (interval.Category is ActivityCategory.Unknown or ActivityCategory.Idle)
                    {
                        // Idle and unknown are coverage, not concurrent work.
                        continue;
                    }

                    if (interval.Start > Now || interval.End < windowStart)
                    {
                        continue;
                    }

                    if (!_concurrency.TryGetValue(interval.Project, out var concurrency))
                    {
                        concurrency = new ActivityConcurrency(_maximumBins);
                        _concurrency[interval.Project] = concurrency;
                    }

                    var end = interval.End > Now ? Now : interval.End;
                    var first = (int)Math.Floor((interval.Start - windowStart).TotalSeconds / seconds);
                    var last = (int)Math.Floor((end - windowStart).TotalSeconds / seconds);
                    first = Math.Clamp(first, 0, _maximumBins - 1);
                    last = Math.Clamp(last, 0, _maximumBins - 1);
                    for (var index = first; index <= last; index++)
                    {
                        concurrency.Touch(index, laneIndex, interval.Category);
                    }
                }
            }
        }
    }

    /// <summary>Concurrent task count and category mix for one project's bins.</summary>
    private sealed class ActivityConcurrency
    {
        private readonly int[] _counts;
        private readonly int[] _masks;
        private readonly int[] _lastLane;

        public ActivityConcurrency(int bins)
        {
            _counts = new int[bins];
            _masks = new int[bins];
            _lastLane = new int[bins];
            for (var index = 0; index < bins; index++)
            {
                _lastLane[index] = -1;
            }
        }

        /// <summary>One lane touches a bin at most once, even with overlapping intervals.</summary>
        public void Touch(int index, int laneIndex, ActivityCategory category)
        {
            if (_lastLane[index] != laneIndex)
            {
                _lastLane[index] = laneIndex;
                _counts[index]++;
            }

            _masks[index] |= 1 << (int)category;
        }

        public IReadOnlyList<ActivityStripBin> Apply(IReadOnlyList<ActivityStripBin> bins)
        {
            if (bins.Count == 0 || bins.Count > _counts.Length)
            {
                return bins;
            }

            var result = new ActivityStripBin[bins.Count];
            for (var index = 0; index < bins.Count; index++)
            {
                var bin = bins[index];
                var tasks = _counts[index] > 0 ? _counts[index] : bin.IsKnown ? 1 : 0;
                result[index] = bin with { ConcurrentTasks = tasks, IsMixed = Mixed(_masks[index]) };
            }

            return result;
        }

        private static bool Mixed(int mask)
        {
            var seen = 0;
            for (var bit = 0; bit < ActivityStripText.CategoryCount; bit++)
            {
                if ((mask & (1 << bit)) != 0 && ++seen > 1)
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>
    /// Bins one interval sequence and preserves the evidence, coverage and detail the
    /// category-only projection used to drop. A known category never becomes complete
    /// coverage while the project or lane still has unobserved time.
    /// </summary>
    private static IReadOnlyList<ActivityStripBin> Bins(
        IReadOnlyList<ActivityInterval> intervals,
        DateTimeOffset now,
        TimeSpan window,
        int maximumBins,
        bool coverageUnknown)
    {
        var binned = ActivityTimelineGeometry.Bin(intervals, now, window, maximumBins);
        if (binned.Count == 0)
        {
            return Array.Empty<ActivityStripBin>();
        }

        var result = new ActivityStripBin[binned.Count];
        for (var index = 0; index < binned.Count; index++)
        {
            var bin = binned[index];
            var known = bin.Category != ActivityCategory.Unknown;
            var coverage = !known ? ActivityCoverage.Unknown
                : bin.Evidence == ActivityEvidence.Inferred ? ActivityCoverage.Partial
                : coverageUnknown ? ActivityCoverage.Partial
                : ActivityCoverage.Complete;
            result[index] = new ActivityStripBin(
                bin.Start,
                bin.End,
                bin.Category,
                bin.Evidence,
                coverage,
                bin.DetailCode,
                known ? 1 : 0,
                false);
        }

        return result;
    }

    private static IReadOnlyList<ActivityStripBin> Unknown(DateTimeOffset now, TimeSpan window, int maximumBins, string detailCode)
    {
        var seconds = Math.Max(1.0, window.TotalSeconds / maximumBins);
        var count = (int)Math.Ceiling(window.TotalSeconds / seconds);
        var windowStart = now - window;
        var result = new ActivityStripBin[count];
        for (var index = 0; index < count; index++)
        {
            var start = windowStart + TimeSpan.FromSeconds(index * seconds);
            var end = start + TimeSpan.FromSeconds(seconds);
            result[index] = new ActivityStripBin(start, end > now ? now : end, ActivityCategory.Unknown,
                ActivityEvidence.Unknown, ActivityCoverage.Unknown, detailCode, 0, false);
        }

        return result;
    }
}
