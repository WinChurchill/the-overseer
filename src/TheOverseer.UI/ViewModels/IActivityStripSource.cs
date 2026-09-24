using TheOverseer.Core.History;
using TheOverseer.Core.Models;

namespace TheOverseer.UI.ViewModels;

/// <summary>
/// Host-supplied observed activity for the visible rows. The UI never computes
/// history: the notification coordinator owns the producer-side recording and this
/// only projects bounded bins for the strip controls.
/// </summary>
public interface IActivityStripSource
{
    /// <summary>Last-hour project bins (union of member work), at most the strip width.</summary>
    IReadOnlyList<ActivityCategory> ProjectBins(ProjectId projectId, DateTimeOffset now);

    /// <summary>Last-hour bins for one task.</summary>
    IReadOnlyList<ActivityCategory> TaskBins(TaskKey key, DateTimeOffset now);
}
