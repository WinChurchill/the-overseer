using System.Text;
using TheOverseer.Core.Diagnostics;
using TheOverseer.Core.Models;
using TheOverseer.Core.Monitoring.Parsing;
using TheOverseer.Core.Monitoring.Sessions;
using TheOverseer.Core.Monitoring.State;

namespace TheOverseer.Core.Monitoring;

public sealed partial class CodexMonitor : IAsyncDisposable, IDisposable
{
    private void ProcessFile(
        TrackedSessionFile file,
        ref int globalBudget,
        List<TaskTransition> transitions,
        SortedSet<string> reasons,
        ref bool coverageComplete,
        ref bool malformedThisCycle,
        ref bool hydrationDoneThisCycle,
        DateTimeOffset now)
    {
        file.LastProcessedAt = now;
        try
        {
            var profile = FindState(file.ProfileId)!.Profile;
            if (!IsUnderRoot(profile, file.Path))
            {
                HandleFailure(file, HealthReasons.ReadFailure, reasons, ref coverageComplete, now);
                return;
            }
            if (file.IdentityCheckPending)
            {
                var budget = Math.Min(Math.Min(_options.MaxIdentityHeaderBytes, _options.PerFileBytesPerPass), _options.GlobalBytesPerPass / 4);
                if (globalBudget < budget) { coverageComplete = false; reasons.Add(HealthReasons.HydrationBudget); return; }
                globalBudget -= budget;
                file.BytesReservedThisPass += budget;
                var identity = SessionIdentityReader.Read(file.Path, budget);
                file.IdentityCheckPending = false;
                if (!identity.Found)
                {
                    file.IdentityResolved = false;
                    file.IdentityProvisional = true;
                }
                else if (!UpdateIdentity(file, identity, now)) return;
            }
            if (!file.Hydrated)
            {
                HydrateFile(file, ref globalBudget, transitions, reasons, ref coverageComplete, ref malformedThisCycle, ref hydrationDoneThisCycle, now);
                return;
            }

            ReadAppended(file, ref globalBudget, transitions, reasons, ref coverageComplete, ref malformedThisCycle, now);
        }
        catch (FileNotFoundException)
        {
            HandleFailure(file, HealthReasons.FileMissing, reasons, ref coverageComplete, now);
        }
        catch (DirectoryNotFoundException)
        {
            HandleFailure(file, HealthReasons.FileMissing, reasons, ref coverageComplete, now);
        }
        catch (IOException)
        {
            HandleFailure(file, HealthReasons.FileLocked, reasons, ref coverageComplete, now);
        }
        catch (UnauthorizedAccessException)
        {
            HandleFailure(file, HealthReasons.ReadFailure, reasons, ref coverageComplete, now);
        }
    }

    private void HydrateFile(
        TrackedSessionFile file,
        ref int globalBudget,
        List<TaskTransition> transitions,
        SortedSet<string> reasons,
        ref bool coverageComplete,
        ref bool malformedThisCycle,
        ref bool hydrationDoneThisCycle,
        DateTimeOffset now)
    {
        var info = new FileInfo(file.Path);
        info.Refresh();
        if (!info.Exists)
        {
            HandleFailure(file, HealthReasons.FileMissing, reasons, ref coverageComplete, now);
            return;
        }

        if (file.HydrationEnd < 0)
        {
            file.HydrationEnd = info.Length;
            var window = Math.Min(_options.HydrationInitialWindowBytes, _options.HydrationMaxScanBytes);
            var start = Math.Max(0, info.Length - window);
            file.Reader.SeekTo(start, discardPartialRecord: start > 0);
            file.HydrationLimited = start > 0;
        }
        var allowance = Math.Min(_options.PerFileBytesPerPass - file.BytesReservedThisPass, Math.Max(0, globalBudget));
        if (allowance == 0)
        {
            reasons.Add(HealthReasons.HydrationBudget);
            coverageComplete = false;
            return;
        }
        var read = file.Reader.ReadAppended(file.Path, allowance, file.HydrationEnd, _options.MaxHydrationRecords);
        globalBudget -= read.BytesRead;
        _bytesRead += read.BytesRead;
        file.BytesRead += read.BytesRead;
        hydrationDoneThisCycle = true;
        if (read.Truncated)
        {
            ResetForRehydrate(file);
            reasons.Add(HealthReasons.FileTruncated);
            coverageComplete = false;
            return;
        }
        if (read.OversizeDiscarded)
        {
            MarkEvidenceGap(file, HealthReasons.RecordOversize);
            reasons.Add(HealthReasons.RecordOversize);
            _counters.Oversize++;
            file.HydrationLimited = true;
        }
        var sawLifecycle = false;
        foreach (var line in read.Lines)
        {
            if (file.HydrationRecords >= _options.MaxHydrationRecords)
            {
                MarkEvidenceGap(file, HealthReasons.HydrationBudget);
                file.HydrationLimited = true;
                continue;
            }
            file.HydrationRecords++;
            var order = ++file.SourceOrder;
            var context = new ParseContext(file.ProfileId, file.ConversationId, file.FileId,
                order, order, file.Reader.Generation, true, now, _options.MaxRecordChars);
            ApplyLine(file, line, context, transitions,
                ref sawLifecycle, ref malformedThisCycle);
        }
        file.Hydrated = file.Reader.Offset >= file.HydrationEnd;
        file.Length = info.Length;
        file.LastWriteUtc = info.LastWriteTimeUtc;
        file.ReadBlocked = false;
        file.ConsecutiveFailures = 0;
        file.FailureCode = null;
        file.MissingSinceAt = null;
        _lastSuccessfulReadAt = now;
        if (file.Hydrated) _counters.FilesHydrated++;
        if (!file.Hydrated || file.HydrationLimited)
        {
            reasons.Add(HealthReasons.HydrationBudget);
            coverageComplete = false;
        }
    }
    private void ReadAppended(
        TrackedSessionFile file,
        ref int globalBudget,
        List<TaskTransition> transitions,
        SortedSet<string> reasons,
        ref bool coverageComplete,
        ref bool malformedThisCycle,
        DateTimeOffset now)
    {
        var info = new FileInfo(file.Path);
        info.Refresh();
        if (!info.Exists)
        {
            HandleFailure(file, HealthReasons.FileMissing, reasons, ref coverageComplete, now);
            return;
        }

        if (info.Length < file.Reader.Offset)
        {
            reasons.Add(HealthReasons.FileTruncated);
            coverageComplete = false;
            ResetForRehydrate(file);
            return;
        }

        if (now - file.LastSignatureCheckAt >= _options.ReconcileInterval)
        {
            if (globalBudget < 4096) { coverageComplete = false; reasons.Add(HealthReasons.HydrationBudget); return; }
            globalBudget -= 4096;
            file.BytesReservedThisPass += 4096;
            file.LastSignatureCheckAt = now;
            var signature = ComputeSignature(file.Path, file.SignatureBytes);
            if (file.Signature != 0 && signature != 0 && signature != file.Signature)
            {
                reasons.Add(HealthReasons.FileReplaced);
                coverageComplete = false;
                ResetForRehydrate(file);
                return;
            }

            file.Signature = signature;
        }

        var allowance = Math.Min(_options.PerFileBytesPerPass - file.BytesReservedThisPass, Math.Max(0, globalBudget));
        if (allowance <= 0)
        {
            reasons.Add(HealthReasons.HydrationBudget);
            coverageComplete = false;
            return;
        }

        var read = file.Reader.ReadAppended(file.Path, allowance);
        globalBudget -= read.BytesRead;
        _bytesRead += read.BytesRead;
        file.BytesRead += read.BytesRead;

        if (read.Truncated)
        {
            reasons.Add(HealthReasons.FileTruncated);
            coverageComplete = false;
            ResetForRehydrate(file);
            return;
        }

        if (read.OversizeDiscarded)
        {
            MarkEvidenceGap(file, HealthReasons.RecordOversize);
            reasons.Add(HealthReasons.RecordOversize);
            _counters.Oversize++;
        }

        var sawLifecycle = false;
        var order = file.SourceOrder;
        foreach (var line in read.Lines)
        {
            order++;
            var context = new ParseContext(
                file.ProfileId,
                file.ConversationId,
                file.FileId,
                order,
                order,
                file.Reader.Generation,
                false,
                now,
                _options.MaxRecordChars);
            ApplyLine(file, line, context, transitions, ref sawLifecycle, ref malformedThisCycle);
        }

        file.SourceOrder = order;
        file.Length = info.Length;
        file.LastWriteUtc = info.LastWriteTimeUtc;
        file.ConsecutiveFailures = 0;
        file.FailureCode = null;
        file.MissingSinceAt = null;
        file.ReadBlocked = false;
        _lastSuccessfulReadAt = now;
        if (read.BudgetExhausted)
        {
            reasons.Add(HealthReasons.HydrationBudget);
            coverageComplete = false;
        }
    }

}
