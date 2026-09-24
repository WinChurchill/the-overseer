using TheOverseer.Core.History;
using TheOverseer.Core.Models;
using TheOverseer.Core.Voice;
using TheOverseer.UI.ViewModels;

namespace TheOverseer.App.Monitoring;

/// <summary>
/// Projects the coordinator's producer-side observed activity into the bounded bins
/// the strip controls draw: one hour of history, at most one bin per minute. The UI
/// never reads logs or reconstructs history; it renders what was observed.
/// </summary>
public sealed class CoordinatorActivityStripSource : IActivityStripSource
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(60);
    private const int MaximumBins = 56;

    private readonly NotificationCoordinator _coordinator;
    private readonly TimeProvider _timeProvider;

    public CoordinatorActivityStripSource(NotificationCoordinator coordinator, TimeProvider? timeProvider = null)
    {
        _coordinator = coordinator;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public IReadOnlyList<ActivityCategory> ProjectBins(ProjectId projectId, DateTimeOffset now)
    {
        var strip = _coordinator.ActivityStrip(projectId, now);
        return Categories(ActivityTimelineGeometry.Bin(strip, now, Window, MaximumBins));
    }

    public IReadOnlyList<ActivityCategory> TaskBins(TaskKey key, DateTimeOffset now)
    {
        foreach (var lane in _coordinator.ActivityLanes(now))
        {
            if (lane.Key.Equals(key))
            {
                return Categories(ActivityTimelineGeometry.Bin(lane.Intervals, now, Window, MaximumBins));
            }
        }

        return Array.Empty<ActivityCategory>();
    }

    private static IReadOnlyList<ActivityCategory> Categories(IReadOnlyList<ActivityInterval> bins)
    {
        var categories = new ActivityCategory[bins.Count];
        for (var index = 0; index < bins.Count; index++)
        {
            categories[index] = bins[index].Category;
        }

        return categories;
    }
}
