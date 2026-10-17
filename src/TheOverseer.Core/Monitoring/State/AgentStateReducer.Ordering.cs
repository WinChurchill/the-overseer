using TheOverseer.Core.Models;

namespace TheOverseer.Core.Monitoring.State;

public sealed partial class AgentStateReducer
{
    private bool Accepts(MutableTask task, in RawCodexEvent codexEvent, out string rejectCode)
    {
        rejectCode = "accepted";
        if (codexEvent.SourceOrder > 0)
        {
            var marker = codexEvent.SourceFileId.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" +
                         codexEvent.SourceGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" +
                         codexEvent.SourceOrder.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (task.SeenMarkers.Contains(marker))
            {
                rejectCode = "duplicate-record";
                return false;
            }

            RememberMarker(task, marker);
        }

        // A reply resolves an existing question; it cannot establish a new turn.
        // In particular, a stale envelope must not adopt a turn and clear requests.
        if (codexEvent.Kind == RawCodexEventKind.InputResolved && codexEvent.InputAnswers.Count > 0)
        {
            if (codexEvent.TurnId.Length > 0 && codexEvent.TurnId != task.TurnId)
            {
                rejectCode = "input-answer-turn-mismatch";
                return false;
            }
            return true;
        }

        if (codexEvent.TurnId.Length == 0)
        {
            if (task.Turns.TryGetValue(task.TurnId, out var terminal) && terminal.Terminal &&
                codexEvent.Kind is not (RawCodexEventKind.SessionIdentity or RawCodexEventKind.TokenUsage or RawCodexEventKind.HistoryCompacted))
            {
                rejectCode = "uncorrelated-terminal-evidence";
                return false;
            }
            return true;
        }

        if (task.TurnId.Length == 0)
        {
            AdoptTurn(task, codexEvent.TurnId, codexEvent.EventTime ?? codexEvent.ObservedAt);
            return true;
        }

        if (string.Equals(codexEvent.TurnId, task.TurnId, StringComparison.Ordinal))
        {
            if (task.Turns.TryGetValue(task.TurnId, out var current) && current.Terminal &&
                codexEvent.Kind is not (RawCodexEventKind.PermissionResolved or RawCodexEventKind.InputResolved))
            {
                rejectCode = "terminal-turn-evidence";
                return false;
            }

            return true;
        }

        var eventAt = codexEvent.EventTime ?? codexEvent.ObservedAt;
        if (task.Turns.TryGetValue(codexEvent.TurnId, out var known))
        {
            if (known.Terminal || known.LastActivityAt <= task.TurnStartedAt)
            {
                rejectCode = "old-turn";
                return false;
            }

            AdoptTurn(task, codexEvent.TurnId, eventAt);
            return true;
        }

        if (eventAt < task.TurnLastActivityAt)
        {
            rejectCode = "old-turn";
            return false;
        }

        AdoptTurn(task, codexEvent.TurnId, eventAt);
        return true;
    }

    private static void RememberMarker(MutableTask task, string marker)
    {
        if (task.SeenMarkers.Add(marker))
        {
            task.SeenOrder.Enqueue(marker);
            while (task.SeenOrder.Count > task.SeenMarkerLimit)
            {
                var oldest = task.SeenOrder.Dequeue();
                task.SeenMarkers.Remove(oldest);
            }
        }
    }

    private static void AdoptTurn(MutableTask task, string turnId, DateTimeOffset at)
    {
        var isNewCurrentTurn = !string.Equals(task.TurnId, turnId, StringComparison.Ordinal);
        if (!task.Turns.TryGetValue(turnId, out var record))
        {
            record = new TurnRecord { TurnId = turnId, Order = task.TurnOrder++ };
            task.Turns[turnId] = record;
            task.TurnSequence.Enqueue(turnId);
            var attempts = task.TurnSequence.Count;
            while (task.Turns.Count > task.TurnLimit && task.TurnSequence.Count > 0 && attempts-- > 0)
            {
                var oldest = task.TurnSequence.Dequeue();
                if (string.Equals(oldest, task.TurnId, StringComparison.Ordinal) ||
                    string.Equals(oldest, turnId, StringComparison.Ordinal))
                {
                    task.TurnSequence.Enqueue(oldest);
                    continue;
                }

                task.Turns.Remove(oldest);
            }
        }

        if (at > record.LastActivityAt)
        {
            record.LastActivityAt = at;
        }

        if (isNewCurrentTurn)
        {
            ClearRequests(task, string.Empty);
            task.ActiveTools.Clear();
            task.ToolOrder.Clear();
            task.LastActivity = null;
            task.AttentionCode = null;
            task.CancellationObserved = false;
            task.FailureCode = null;
            task.TurnId = turnId;
            task.StartedAt = null;
            task.CompletedAt = null;
            task.TurnStartedAt = record.LastActivityAt;
            task.TurnLastActivityAt = record.LastActivityAt;
            task.HasActivityInCurrentTurn = false;
        }
        else if (record.LastActivityAt > task.TurnLastActivityAt)
        {
            task.TurnLastActivityAt = record.LastActivityAt;
        }
    }

}
