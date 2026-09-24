using TheOverseer.Core.History;
using TheOverseer.Core.Models;

namespace TheOverseer.UI.ViewModels;

/// <summary>
/// Host-supplied observed activity for the visible rows. The UI never computes
/// history: the notification coordinator owns the producer-side recording and this
/// only projects bounded bins for the strip controls.
///
/// A bin carries the category plus the evidence, coverage, interval and structural
/// detail, so a stale, partial or inferred segment can never render as a confirmed
/// one. Implementations must serve every call for one refresh instant from one
/// projection: the HUD asks for each distinct project once and for each task once
/// per tick.
/// </summary>
public interface IActivityStripSource
{
    /// <summary>Last-hour project bins (union of member work), at most the strip width.</summary>
    IReadOnlyList<ActivityStripBin> ProjectBins(ProjectId projectId, DateTimeOffset now);

    /// <summary>Last-hour bins for one task.</summary>
    IReadOnlyList<ActivityStripBin> TaskBins(TaskKey key, DateTimeOffset now);
}
