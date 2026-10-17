using System.Text;
using TheOverseer.Core.Diagnostics;
using TheOverseer.Core.Models;
using TheOverseer.Core.Monitoring.Parsing;
using TheOverseer.Core.Monitoring.Sessions;
using TheOverseer.Core.Monitoring.State;

namespace TheOverseer.Core.Monitoring;

public sealed partial class CodexMonitor : IAsyncDisposable, IDisposable
{
    private void ApplyLine(TrackedSessionFile file, string line, ParseContext context,
        List<TaskTransition> transitions, ref bool sawLifecycle, ref bool malformedThisCycle)
    {
        if (!_tracked.ContainsKey(file.Path)) return;
        var result = CodexEventParser.ParseLine(line, context);
        if (result.Event?.Kind == RawCodexEventKind.SessionIdentity)
        {
            var identity = SessionIdentityReader.ParseLine(line);
            if (!identity.Found) return;
            if (!UpdateIdentity(file, identity, context.ObservedAt)) return;
            result = result with { Event = result.Event with { ConversationId = file.ConversationId } };
        }
        ApplyParseResult(file, result, transitions, ref sawLifecycle, ref malformedThisCycle);
    }

    private bool UpdateIdentity(TrackedSessionFile file, SessionIdentity identity, DateTimeOffset now)
    {
        var previousKey = file.Key;
        if (identity.IsExplicitInternal)
        {
            _tracked.Remove(file.Path);
            if (_internalPaths.Count < 512) _internalPaths.Add(file.Path);
            if (!_tracked.Values.Any(item => item.Key == previousKey)) _reducer.Forget(previousKey);
            return false;
        }
        file.ConversationId = identity.ConversationId;
        file.IdentityResolved = true;
        file.IdentityProvisional = false;
        file.Surface = identity.Surface;
        if (previousKey != file.Key && !_tracked.Values.Any(item => item.Key == previousKey)) _reducer.Forget(previousKey);
        var assignment = _projects?.Resolve(identity.WorkspaceRoot, isStandaloneChat: identity.IsStandaloneWorkspace);
        _reducer.RegisterConversation(file.ProfileId, file.ConversationId, file.Surface, identity.WorkspaceLeaf,
            true, now, identity.WorkspaceRoot, assignment?.Project);
        return true;
    }

    private void ApplyParseResult(
        TrackedSessionFile file,
        ParseResult result,
        List<TaskTransition> transitions,
        ref bool sawLifecycle,
        ref bool malformedThisCycle)
    {
        switch (result.Outcome)
        {
            case ParseOutcome.Malformed:
                file.EvidenceGap = true;
                _counters.Malformed++;
                malformedThisCycle = true;
                if (_malformedPaths.Count < 256)
                {
                    _malformedPaths.Add(file.Path);
                }

                return;
            case ParseOutcome.Oversize:
                file.EvidenceGap = true;
                _counters.Oversize++;
                return;
            case ParseOutcome.UnknownSchema:
                file.EvidenceGap = true;
                _counters.UnknownSchema++;
                return;
            case ParseOutcome.Ignored:
                return;
        }

        if (result.Event is not { } codexEvent)
        {
            return;
        }

        if (codexEvent.Kind is RawCodexEventKind.TaskStarted or RawCodexEventKind.TurnContext && codexEvent.TurnId.Length > 0)
            file.CurrentTurnId = codexEvent.TurnId;
        else if (codexEvent.TurnId.Length == 0 && codexEvent.Kind != RawCodexEventKind.SessionIdentity)
            codexEvent = codexEvent with { TurnId = file.CurrentTurnId };
        // Identity is verified by the header reader; a context id alone is not proof.
        if (codexEvent.Kind == RawCodexEventKind.SessionIdentity && !file.IdentityResolved) return;

        switch (codexEvent.Kind)
        {
            case RawCodexEventKind.SessionIdentity:
                _counters.SessionMeta++;
                break;
            case RawCodexEventKind.TaskStarted:
                _counters.TaskStarted++;
                sawLifecycle = true;
                break;
            case RawCodexEventKind.TaskCompleted:
                _counters.TaskCompleted++;
                sawLifecycle = true;
                break;
            case RawCodexEventKind.TurnAborted:
                _counters.TurnAborted++;
                sawLifecycle = true;
                break;
            case RawCodexEventKind.TaskFailed:
                _counters.TaskFailed++;
                sawLifecycle = true;
                break;
            case RawCodexEventKind.ItemActivity:
                _counters.ItemCompleted++;
                break;
            case RawCodexEventKind.ToolCallStarted:
            case RawCodexEventKind.ToolCallCompleted:
                _counters.ToolCalls++;
                break;
            case RawCodexEventKind.PermissionRequested:
                _counters.PermissionRequests++;
                break;
            case RawCodexEventKind.InputRequested:
                _counters.InputRequests++;
                break;
        }

        var outcome = _reducer.Apply(in codexEvent);
        if (outcome.Applied && codexEvent.TurnId.Length > 0 && codexEvent.EventTime is not null &&
            codexEvent.Kind is RawCodexEventKind.ItemActivity or RawCodexEventKind.ToolCallStarted &&
            codexEvent.Action is not null && codexEvent.Tool != ToolCallCategory.Message)
            file.HydrationLimited = false;
        if (outcome.Applied && codexEvent.Kind is RawCodexEventKind.TaskStarted or RawCodexEventKind.TaskCompleted or RawCodexEventKind.TurnAborted or RawCodexEventKind.TaskFailed)
        {
            file.HydrationLimited = false;
            file.EvidenceGap = false;
        }
        if (outcome.Transition is { } transition)
        {
            transitions.Add(transition);
        }
    }

    private void ResetForRehydrate(TrackedSessionFile file)
    {
        _reducer.ResetEvidence(file.Key);
        foreach (var sibling in _tracked.Values.Where(item => item.Key == file.Key)) ResetFile(sibling);
    }

    private static void ResetFile(TrackedSessionFile file)
    {
        file.Reader.Reset();
        file.Hydrated = false;
        file.HydrationEnd = -1;
        file.HydrationRecords = 0;
        file.IdentityCheckPending = true;
        file.CurrentTurnId = string.Empty;
        file.HydrationLimited = false;
        file.EvidenceGap = false;
        file.SourceOrder = 0;
        file.Signature = 0;
    }

    private void HandleFailure(
        TrackedSessionFile file,
        string reasonCode,
        SortedSet<string> reasons,
        ref bool coverageComplete,
        DateTimeOffset now)
    {
        // A file that is gone is not merely unreadable. Retaining it forever degraded
        // the whole source (and kept its task unobservable) even after the session had
        // moved on; after a bounded retention it is released instead. A file that comes
        // back is simply admitted and hydrated again.
        var absent = reasonCode == HealthReasons.FileMissing ||
            (reasonCode != HealthReasons.FileLocked && !File.Exists(file.Path));
        if (absent)
        {
            file.MissingSinceAt ??= now;
            if (now - file.MissingSinceAt.Value >= _options.MissingFileRetention)
            {
                _tracked.Remove(file.Path);
                if (!_tracked.Values.Any(item => item.Key == file.Key)) _reducer.Forget(file.Key);
                _log.Write(MonitorLogLevel.Info, "monitor-file-released", reasonCode);
                return;
            }

            reasonCode = HealthReasons.FileMissing;
        }
        else
        {
            file.MissingSinceAt = null;
        }

        file.ConsecutiveFailures++;
        file.FailureCode = reasonCode;
        var backoff = TimeSpan.FromTicks(Math.Min(
            _options.FailureBackoffMax.Ticks,
            _options.FailureBackoffInitial.Ticks * (1L << Math.Min(file.ConsecutiveFailures - 1, 16))));
        file.NextAttemptAt = now + backoff;
        file.ReadBlocked = reasonCode == HealthReasons.FileLocked;
        reasons.Add(reasonCode);
        coverageComplete = false;
    }

}
