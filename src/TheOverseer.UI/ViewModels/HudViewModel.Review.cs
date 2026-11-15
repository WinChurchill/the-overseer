using System.Globalization;
using System.Windows.Input;
using TheOverseer.Core.Models;

namespace TheOverseer.UI.ViewModels;

/// <summary>
/// The "To review" checklist: finished tasks stay listed until the user ticks
/// them, opens them, or they start working again. The rules live in
/// <see cref="ReviewList"/>; this partial only projects and persists them.
/// </summary>
public sealed partial class HudViewModel
{
    private ReviewListState _review = ReviewListState.Empty;

    /// <summary>Persisted with the HUD settings.</summary>
    public ReviewListState Review => _review;

    public int ReviewCount => _review.Items.Count;

    public bool HasReviewItems => _review.Items.Count > 0;

    public string ReviewCountText => _review.Items.Count.ToString(CultureInfo.InvariantCulture);

    public bool IsFilterReview { get => _taskFilter == "review"; set { if (value) TaskFilter = "review"; } }

    /// <summary>"Tick all" appears only on the checklist when it has items.</summary>
    public bool ShowTickAll => IsFilterReview && HasReviewItems;

    public ICommand ShowReviewCommand { get; private set; } = null!;

    /// <summary>Ticks one row (command parameter: the row).</summary>
    public ICommand MarkReviewedCommand { get; private set; } = null!;

    public ICommand MarkAllReviewedCommand { get; private set; } = null!;

    private void InitializeReview(ReviewListState state)
    {
        _review = state;
        ShowReviewCommand = new RelayCommand(() =>
        {
            TaskFilter = "review";
            IsExpanded = true;
        });
        MarkReviewedCommand = new RelayCommand<TaskRowViewModel>(row =>
        {
            if (row is not null) MarkReviewed(row.Key);
        });
        MarkAllReviewedCommand = new RelayCommand(() => SetReview(ReviewList.MarkAllReviewed(_review, _viewNow)));
    }

    public bool IsReviewItem(TaskKey key)
    {
        foreach (var item in _review.Items)
        {
            if (item.Key.Equals(key)) return true;
        }
        return false;
    }

    /// <summary>The stored task for a listed item, for opening it after it left the snapshot.</summary>
    public AgentTask? ReviewTaskFor(TaskKey key)
    {
        foreach (var item in _review.Items)
        {
            if (item.Key.Equals(key)) return item.ToTask();
        }
        return null;
    }

    public void MarkReviewed(TaskKey key) => SetReview(ReviewList.MarkReviewed(_review, key, _viewNow));

    private void ApplyReview(IReadOnlyList<AgentTask> tasks, DateTimeOffset now)
    {
        var next = ReviewList.Apply(_review, tasks, now);
        if (!ReferenceEquals(next, _review)) SetReview(next, refresh: false);
    }

    private void SetReview(ReviewListState next, bool refresh = true)
    {
        if (ReferenceEquals(next, _review)) return;
        _review = next;
        Raise(nameof(Review));
        Raise(nameof(ReviewCount));
        Raise(nameof(HasReviewItems));
        Raise(nameof(ReviewCountText));
        Raise(nameof(ShowTickAll));
        SettingsChanged?.Invoke(this, EventArgs.Empty);
        if (refresh) RefreshTaskView();
    }

    /// <summary>Listed items as rows: the live task when it is still finished, else the stored copy.</summary>
    private IReadOnlyList<AgentTask> ReviewTasks(IReadOnlyList<AgentTask> live)
    {
        var rows = new List<AgentTask>(_review.Items.Count);
        foreach (var item in _review.Items)
        {
            AgentTask? match = null;
            foreach (var task in live)
            {
                if (task.Key.Equals(item.Key) && task.IsTerminal) { match = task; break; }
            }
            rows.Add(match ?? item.ToTask());
        }
        return rows;
    }
}
