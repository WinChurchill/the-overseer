using TheOverseer.Core.Models;

namespace TheOverseer.Core.Monitoring.State;

public sealed record ActiveToolView(ToolCallCategory Category, AgentActionKind? Action, string? DetailCode, int Order);

/// <summary>
/// Everything the classifier may look at. No recorded text or command line is
/// part of this context.
/// </summary>
public sealed record ClassifierContext(
    AgentTaskState State,
    bool HasApprovalRequest,
    bool HasBlockingInputRequest,
    IReadOnlyList<ActiveToolView> ActiveTools,
    AgentActionKind? LastActivityAction,
    string? LastActivityDetailCode,
    ToolCallCategory LastActivityCategory,
    bool HasActivityInCurrentTurn,
    bool CancellationObserved);

/// <summary>
/// Derives exactly one canonical action label. Priority follows the frozen
/// specification: structured activity first, then the narrow command category
/// already carried by the event, then turn-state fallback.
/// </summary>
public sealed class CurrentActionClassifier
{
    public static CurrentActionClassifier Instance { get; } = new();

    public AgentAction Classify(ClassifierContext context)
    {
        if (context.State == AgentTaskState.Done)
        {
            return new AgentAction(AgentActionKind.Completed, EvidenceKind.Explicit, "task-complete");
        }

        if (context.State == AgentTaskState.Error)
        {
            return new AgentAction(AgentActionKind.Error, EvidenceKind.Explicit, "task-error");
        }

        // Approval has display precedence over input, but every waiting task is
        // counted once as needing a human.
        if (context.HasApprovalRequest)
        {
            return new AgentAction(AgentActionKind.WaitingForApproval, EvidenceKind.Explicit, "permission-requested");
        }

        if (context.HasBlockingInputRequest)
        {
            return new AgentAction(AgentActionKind.WaitingForInput, EvidenceKind.Explicit, "input-requested");
        }

        // A settled state (IDLE) must not keep displaying an old work label.
        if (context.State == AgentTaskState.Working)
        {
            var active = SelectActiveTool(context.ActiveTools);
            if (active?.Action is { } activeAction)
            {
                return new AgentAction(activeAction, EvidenceKind.Structured, active.DetailCode);
            }

            if (context.LastActivityAction is { } lastAction && context.HasActivityInCurrentTurn)
            {
                return new AgentAction(lastAction, EvidenceKind.Structured, context.LastActivityDetailCode);
            }

            return new AgentAction(AgentActionKind.Thinking, EvidenceKind.Inferred, "turn-active");
        }

        return context.State switch
        {
            AgentTaskState.NeedsApproval => new AgentAction(AgentActionKind.WaitingForApproval, EvidenceKind.Inferred, "waiting"),
            AgentTaskState.NeedsInput => new AgentAction(AgentActionKind.WaitingForInput, EvidenceKind.Inferred, "waiting"),
            _ => new AgentAction(AgentActionKind.Idle, EvidenceKind.None, context.CancellationObserved ? "cancelled" : null),
        };
    }

    /// <summary>
    /// Deterministic precedence for simultaneous tools: the most specific work
    /// wins, and ties are broken by the most recently observed call.
    /// </summary>
    private static ActiveToolView? SelectActiveTool(IReadOnlyList<ActiveToolView> tools)
    {
        ActiveToolView? best = null;
        foreach (var tool in tools)
        {
            if (tool.Action is null)
            {
                continue;
            }

            if (best is null)
            {
                best = tool;
                continue;
            }

            var toolRank = Rank(tool.Action.Value);
            var bestRank = Rank(best.Action!.Value);
            if (toolRank < bestRank || (toolRank == bestRank && tool.Order > best.Order))
            {
                best = tool;
            }
        }

        return best;
    }

    private static int Rank(AgentActionKind action) => action switch
    {
        AgentActionKind.RunningTests => 0,
        AgentActionKind.RunningCommand => 1,
        AgentActionKind.EditingFiles => 2,
        AgentActionKind.Searching => 3,
        AgentActionKind.ReadingFiles => 4,
        AgentActionKind.Thinking => 5,
        _ => 6,
    };
}
