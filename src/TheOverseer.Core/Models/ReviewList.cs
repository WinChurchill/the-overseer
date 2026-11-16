using System.Text.Json.Serialization;

namespace TheOverseer.Core.Models;

/// <summary>
/// One finished task waiting for the user to look at it. It keeps only what the
/// HUD needs to show and open the task after it has left the live snapshot:
/// identity, the cleaned label and project name, and when it finished. Never
/// prompts, paths or conversation text.
/// </summary>
public sealed record ReviewItem(
    [property: JsonPropertyName("harnessId")] string HarnessId,
    [property: JsonPropertyName("sourceInstanceId")] string SourceInstanceId,
    [property: JsonPropertyName("conversationId")] string ConversationId,
    [property: JsonPropertyName("number")] int Number,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("project")] string Project,
    [property: JsonPropertyName("surface")] ClientSurface Surface,
    [property: JsonPropertyName("completedAt")] DateTimeOffset CompletedAt,
    [property: JsonPropertyName("failed")] bool Failed = false,
    [property: JsonPropertyName("desktopSessionId")] string? DesktopSessionId = null,
    [property: JsonPropertyName("projectId")] string ProjectId = "")
{
    [JsonIgnore]
    public TaskKey Key => TaskKey.For(HarnessId, SourceInstanceId, ConversationId);

    /// <summary>A task row for an item whose live task is no longer in the snapshot.</summary>
    public AgentTask ToTask() => new(
        Key, Number, ConversationId, SourceInstanceId, Surface, Label, string.Empty,
        Failed ? AgentTaskState.Error : AgentTaskState.Done,
        new AgentAction(Failed ? AgentActionKind.Error : AgentActionKind.Completed, EvidenceKind.Structured, "review"),
        EvidenceKind.Structured, CompletedAt, CompletedAt, CompletedAt, null, CompletedAt,
        IsStale: false, IsProvisional: false, NeedsHuman: false, CancellationObserved: false,
        TerminalFailureObserved: Failed, AttentionCode: null, SourceFileCount: 0,
        Project: string.IsNullOrEmpty(Project) || string.IsNullOrEmpty(ProjectId) ? null
            : new ProjectDescriptor(TheOverseer.Core.Models.ProjectId.FromKey(ProjectId), Project, string.Empty, ProjectResolution.Explicit))
    {
        DesktopSessionId = DesktopSessionId,
    };
}

/// <summary>A finish the user already ticked; the same finish is never re-added.</summary>
public sealed record ReviewedMark(
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("completedAt")] DateTimeOffset CompletedAt);

/// <summary>The persisted checklist: items to review and finishes already ticked.</summary>
public sealed record ReviewListState(
    [property: JsonPropertyName("items")] IReadOnlyList<ReviewItem> Items,
    [property: JsonPropertyName("reviewed")] IReadOnlyList<ReviewedMark> Reviewed)
{
    public static ReviewListState Empty { get; } = new(Array.Empty<ReviewItem>(), Array.Empty<ReviewedMark>());
}

/// <summary>
/// Pure, bounded rules for the "To review" checklist. A finished task stays until
/// the user ticks or opens it, or the task starts working again. Nothing drops
/// off by age except to respect the size bounds.
/// </summary>
public static class ReviewList
{
    public const int MaximumItems = 30;
    public const int MaximumReviewedMarks = 200;
    public const int MaximumLabelChars = 120;
    public const int MaximumProjectChars = 80;
    public const int MaximumIdChars = 128;

    /// <summary>Finishes older than this when first seen (e.g. on first run) are not added.</summary>
    public static readonly TimeSpan AdmissionWindow = TimeSpan.FromHours(12);

    /// <summary>Ticked marks are kept this long so a finish still in the snapshot is not re-added.</summary>
    public static readonly TimeSpan ReviewedRetention = TimeSpan.FromDays(3);

    public static string MarkKey(TaskKey key) => key.HarnessId + "|" + key.ProfileId + "|" + key.ConversationId;

    /// <summary>Folds one snapshot into the checklist. Returns the same instance when nothing changed.</summary>
    public static ReviewListState Apply(ReviewListState state, IReadOnlyList<AgentTask> tasks, DateTimeOffset now)
    {
        var items = state.Items.ToList();
        var changed = false;
        var reviewed = state.Reviewed.ToDictionary(mark => mark.Key, mark => mark.CompletedAt, StringComparer.Ordinal);

        foreach (var task in tasks)
        {
            if (task.IsProvisional || task.Key.IsEmpty) continue;
            var index = items.FindIndex(item => item.Key.Equals(task.Key));

            if (task.IsTerminal && task.HasCurrentState && task.CompletedAt is { } completedAt)
            {
                if (completedAt < now - AdmissionWindow) continue;
                if (reviewed.TryGetValue(MarkKey(task.Key), out var ticked) && completedAt <= ticked) continue;
                var item = FromTask(task, completedAt);
                if (item is null) continue;
                if (index < 0) { items.Add(item); changed = true; }
                else if (items[index] != item) { items[index] = item; changed = true; }
                continue;
            }

            // Working again: the earlier finish is no longer what needs a look.
            if (index >= 0 && task.HasCurrentState && !task.IsTerminal && task.State != AgentTaskState.Idle &&
                task.StateSince > items[index].CompletedAt)
            {
                items.RemoveAt(index);
                changed = true;
            }
        }

        if (!changed) return state;
        return Normalize(new ReviewListState(items, state.Reviewed), now);
    }

    /// <summary>Removes one item and remembers its finish so it is not added back.</summary>
    public static ReviewListState MarkReviewed(ReviewListState state, TaskKey key, DateTimeOffset now)
    {
        var item = state.Items.FirstOrDefault(candidate => candidate.Key.Equals(key));
        if (item is null) return state;
        var items = state.Items.Where(candidate => !candidate.Key.Equals(key)).ToArray();
        var marks = state.Reviewed.Where(mark => mark.Key != MarkKey(key)).Append(new ReviewedMark(MarkKey(key), item.CompletedAt)).ToArray();
        return Normalize(new ReviewListState(items, marks), now);
    }

    /// <summary>Ticks every item at once.</summary>
    public static ReviewListState MarkAllReviewed(ReviewListState state, DateTimeOffset now) =>
        state.Items.Aggregate(state, (current, item) => MarkReviewed(current, item.Key, now));

    /// <summary>Validates and bounds a persisted or hand-edited checklist.</summary>
    public static ReviewListState Normalize(ReviewListState? state, DateTimeOffset now)
    {
        if (state is null) return ReviewListState.Empty;
        var items = (state.Items ?? Array.Empty<ReviewItem>())
            .Where(item => item is not null && ValidId(item.HarnessId) && ValidId(item.SourceInstanceId) && ValidId(item.ConversationId))
            .Select(item => item with
            {
                HarnessId = HarnessIds.Normalize(item.HarnessId),
                Label = Clip(DisplayText.Clean(item.Label), MaximumLabelChars),
                Project = Clip(DisplayText.Clean(item.Project), MaximumProjectChars),
                Number = Math.Clamp(item.Number, 0, 9999),
                DesktopSessionId = ClaudeDesktopSession.IsValidId(item.DesktopSessionId) ? item.DesktopSessionId : null,
                ProjectId = ValidId(item.ProjectId) ? item.ProjectId : string.Empty,
            })
            .GroupBy(item => MarkKey(item.Key), StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(item => item.CompletedAt).First())
            .OrderByDescending(item => item.CompletedAt)
            .Take(MaximumItems)
            .ToArray();
        var marks = (state.Reviewed ?? Array.Empty<ReviewedMark>())
            .Where(mark => mark is not null && !string.IsNullOrEmpty(mark.Key) && mark.Key.Length <= MaximumIdChars * 3 + 2 &&
                           mark.CompletedAt >= now - ReviewedRetention)
            .GroupBy(mark => mark.Key, StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(mark => mark.CompletedAt).First())
            .OrderByDescending(mark => mark.CompletedAt)
            .Take(MaximumReviewedMarks)
            .ToArray();
        return new ReviewListState(items, marks);
    }

    private static ReviewItem? FromTask(AgentTask task, DateTimeOffset completedAt)
    {
        if (!ValidId(task.Key.HarnessId) || !ValidId(task.Key.ProfileId) || !ValidId(task.Key.ConversationId)) return null;
        return new ReviewItem(
            task.Key.HarnessId, task.Key.ProfileId, task.Key.ConversationId, Math.Clamp(task.Number, 0, 9999),
            Clip(task.DisplayLabel, MaximumLabelChars),
            Clip(task.Project is { IsUnassigned: false } project ? project.DisplayName : string.Empty, MaximumProjectChars),
            task.Surface, completedAt, task.State == AgentTaskState.Error,
            ClaudeDesktopSession.IsValidId(task.DesktopSessionId) ? task.DesktopSessionId : null,
            task.Project is { IsUnassigned: false } owner && ValidId(owner.Id.Value) ? owner.Id.Value : string.Empty);
    }

    private static bool ValidId(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= MaximumIdChars && !value.Any(char.IsControl);

    private static string Clip(string? value, int maximum)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        if (value.Length <= maximum) return value;
        var length = char.IsHighSurrogate(value[maximum - 2]) ? maximum - 2 : maximum - 1;
        return value[..length] + "…";
    }
}
