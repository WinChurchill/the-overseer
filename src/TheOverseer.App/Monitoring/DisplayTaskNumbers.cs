using TheOverseer.Core.Models;
using TheOverseer.Core.Monitoring.State;

namespace TheOverseer.App.Monitoring;

/// <summary>
/// One display sequence for HUD, history and voice. Canonical source-qualified
/// identity stays separate from presentation. Live membership is already bounded
/// by adapter budgets; at most 512 absent identities survive a short gap.
/// Called only under the composite publication lock.
/// </summary>
internal sealed class DisplayTaskNumbers
{
    private const int MaximumAbsent = 512;
    private static readonly TimeSpan GapRetention = TimeSpan.FromMinutes(2);
    private readonly Dictionary<TaskKey, Assignment> _assignments = new();
    private readonly TaskNumberPool _pool;

    public DisplayTaskNumbers(TimeProvider clock) => _pool = new(MaximumAbsent, clock);

    public void Assign(List<AgentTask> tasks, List<AttentionEvent> transitions, DateTimeOffset now)
    {
        var present = tasks.Select(task => task.Key).ToHashSet();
        foreach (var edge in transitions)
            if (!edge.Key.IsEmpty) present.Add(edge.Key);

        // Bound absent identities before allocating any new ones.
        var absent = _assignments.Where(pair => !present.Contains(pair.Key))
            .OrderByDescending(pair => pair.Value.LastSeen)
            .ThenByDescending(pair => pair.Value.Number).ToArray();
        for (var index = 0; index < absent.Length; index++)
        {
            var pair = absent[index];
            if (index < MaximumAbsent && now - pair.Value.LastSeen <= GapRetention) continue;
            _assignments.Remove(pair.Key);
            _pool.Release(pair.Value.Number, GapRetention);
        }

        int Number(TaskKey key)
        {
            if (!_assignments.TryGetValue(key, out var assignment))
                assignment = new(_pool.Acquire(), now);
            _assignments[key] = assignment with { LastSeen = now };
            return assignment.Number;
        }

        for (var index = 0; index < tasks.Count; index++)
            tasks[index] = tasks[index] with { Number = Number(tasks[index].Key) };
        for (var index = 0; index < transitions.Count; index++)
            if (!transitions[index].Key.IsEmpty)
                transitions[index] = transitions[index] with { Number = Number(transitions[index].Key) };
    }

    private sealed record Assignment(int Number, DateTimeOffset LastSeen);
}
