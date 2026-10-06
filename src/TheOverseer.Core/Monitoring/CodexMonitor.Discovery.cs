using System.Text;
using TheOverseer.Core.Diagnostics;
using TheOverseer.Core.Models;
using TheOverseer.Core.Monitoring.Parsing;
using TheOverseer.Core.Monitoring.Sessions;
using TheOverseer.Core.Monitoring.State;

namespace TheOverseer.Core.Monitoring;

public sealed partial class CodexMonitor : IAsyncDisposable, IDisposable
{
    private void CollectCandidates(
        ProfileState state,
        Dictionary<string, SessionFileCandidate> candidates,
        SortedSet<string> reasons,
        ref bool coverageComplete,
        ref bool anyRootAccessible,
        ref int globalBudget,
        DateTimeOffset now)
    {
        var profile = state.Profile;
        if (!Directory.Exists(profile.SessionsRoot))
        {
            if (Directory.Exists(profile.RootPath))
            {
                reasons.Add(HealthReasons.RootMissing);
            }
            else
            {
                reasons.Add(HealthReasons.RootMissing);
            }

            coverageComplete = false;
            return;
        }

        if (!state.Tracker.Available)
        {
            reasons.Add(HealthReasons.WatcherFailed);
        }

        if (state.Tracker.ConsumeOverflow())
        {
            reasons.Add(HealthReasons.WatcherOverflow);
        }

        if (state.Tracker.ConsumeStructuralChange())
        {
            if (state.Enumerator.SweepCompleted) state.Enumerator.Restart();
        }

        var hints = state.Tracker.DrainHints();
        var hintBudget = 0;
        foreach (var hint in hints)
        {
            if (hintBudget++ >= _options.MaxWatcherHintPaths)
            {
                reasons.Add(HealthReasons.WatcherOverflow);
                break;
            }

            if (!hint.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase) ||
                !IsUnderRoot(profile, hint))
            {
                continue;
            }

            var candidate = SessionFileProbe.TryStat(hint, profile.Id, "watcher-hint");
            if (candidate is not null)
            {
                candidates[candidate.FullName] = candidate;
            }
        }

        var slice = state.Enumerator.NextSlice();
        anyRootAccessible |= !slice.RootInaccessible || slice.Candidates.Count > 0;
        if (slice.RootInaccessible)
        {
            reasons.Add(HealthReasons.RootInaccessible);
            coverageComplete = false;
        }

        foreach (var candidate in slice.Candidates)
        {
            candidates.TryAdd(candidate.FullName, candidate);
        }

        if (!slice.SweepCompleted)
        {
            reasons.Add(HealthReasons.DiscoveryBacklog);
            coverageComplete = false;
        }

        var priorityIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in _tracked.Values)
        {
            if (string.Equals(file.ProfileId, profile.Id, StringComparison.Ordinal))
            {
                priorityIds.Add(file.ConversationId);
            }
        }

        var titleAllowance = Math.Min(_options.TitleReadBytesPerPass, Math.Max(0, globalBudget / 16 - 4096));
        if (titleAllowance > 0) globalBudget -= titleAllowance + 4096;
        if (titleAllowance > 0 && state.Titles.Refresh(profile.SessionIndexPath, priorityIds, titleAllowance))
        {
            foreach (var file in _tracked.Values)
            {
                if (!string.Equals(file.ProfileId, profile.Id, StringComparison.Ordinal))
                {
                    continue;
                }

                var title = state.Titles.GetTitle(file.ConversationId);
                if (title.Length > 0)
                {
                    _reducer.SetLabel(file.Key, title, file.Surface);
                }
            }
        }

        if (state.Titles.Status.Availability == TitleIndexAvailability.Failed)
        {
            reasons.Add(HealthReasons.TitleSourceUnavailable);
        }

        var runtime = _activitySource.Read(profile, now - _options.StaleLease, _options.MaxTrackedFiles);
        state.RuntimeAvailability = runtime.Availability;
        if (_profileStates.IndexOf(state) == 0)
        {
            _runtimeAvailability = runtime.Availability;
        }

        if (runtime.Availability == RuntimeActivityAvailability.Available)
        {
            foreach (var hint in runtime.Hints)
            {
                if (string.IsNullOrWhiteSpace(hint.RolloutPath) || !hint.RolloutPath.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase) || !IsUnderRoot(profile, hint.RolloutPath))
                {
                    continue;
                }

                var candidate = SessionFileProbe.TryStat(hint.RolloutPath, profile.Id, "runtime-hint");
                if (candidate is not null)
                {
                    candidates.TryAdd(candidate.FullName, candidate);
                }
            }
        }
    }

    private void AdmitCandidates(
        IEnumerable<SessionFileCandidate> candidates,
        SortedSet<string> reasons,
        ref bool coverageComplete,
        ref int globalBudget,
        DateTimeOffset now)
    {
        foreach (var candidate in candidates.OrderByDescending(static item => item.LastWriteUtc))
        {
            if (_tracked.ContainsKey(candidate.FullName) || _internalPaths.Contains(candidate.FullName))
            {
                continue;
            }

            var state = FindState(candidate.ProfileId);
            if (state is null || !IsUnderRoot(state.Profile, candidate.FullName))
            {
                continue;
            }

            if (_tracked.Count >= _options.MaxTrackedFiles)
            {
                var evictable = _tracked.Values
                    .Where(file => now - file.LastWriteUtc > _options.StaleLease)
                    .OrderBy(static file => file.LastWriteUtc)
                    .FirstOrDefault();
                if (evictable is null || evictable.LastWriteUtc >= candidate.LastWriteUtc)
                {
                    reasons.Add(HealthReasons.TrackedFileLimit);
                    coverageComplete = false;
                    continue;
                }

                // Admission below verifies identity before removing an existing row.
                reasons.Add(HealthReasons.TrackedFileLimit);
                coverageComplete = false;
            }

            if (SessionFileProbe.IsReadBlocked(candidate.FullName))
            {
                reasons.Add(HealthReasons.FileLocked);
                coverageComplete = false;
                continue;
            }

            var identityAllowance = Math.Min(Math.Min(_options.MaxIdentityHeaderBytes, _options.PerFileBytesPerPass),
                _options.GlobalBytesPerPass / 4);
            if (globalBudget < identityAllowance + _options.GlobalBytesPerPass / 2)
            {
                reasons.Add(HealthReasons.DiscoveryBacklog);
                coverageComplete = false;
                continue;
            }
            globalBudget -= identityAllowance;
            var identity = SessionIdentityReader.Read(candidate.FullName, identityAllowance);
            if (identity.Found && identity.IsExplicitInternal)
            {
                if (_internalPaths.Count < 512)
                {
                    _internalPaths.Add(candidate.FullName);
                }

                continue;
            }

            var conversationId = identity.ConversationId;
            var provisional = false;
            if (conversationId.Length == 0)
            {
                conversationId = SessionIdentityReader.TryDeriveConversationIdFromFileName(candidate.FullName);
                provisional = conversationId.Length > 0;
            }

            if (conversationId.Length == 0)
            {
                reasons.Add(HealthReasons.ReadFailure);
                coverageComplete = false;
                continue;
            }

            var file = new TrackedSessionFile
            {
                FileId = ++_nextFileId,
                ProfileId = state.Profile.Id,
                Path = candidate.FullName,
                ConversationId = conversationId,
                Surface = identity.Surface,
                WorkspaceLeaf = identity.WorkspaceLeaf,
                WorkspaceRoot = identity.WorkspaceRoot,
                IdentityResolved = identity.Found,
                IdentityProvisional = provisional,
                Length = candidate.Length,
                LastWriteUtc = candidate.LastWriteUtc,
                AdmittedAt = now,
                LastProcessedAt = DateTimeOffset.MinValue,
                BytesReservedThisPass = identityAllowance,
            };
            file.SignatureBytes = (int)Math.Min(Math.Min(Math.Min(4096, candidate.Length), globalBudget), _options.PerFileBytesPerPass - identityAllowance);
            globalBudget -= file.SignatureBytes;
            file.BytesReservedThisPass += file.SignatureBytes;
            file.Signature = ComputeSignature(candidate.FullName, file.SignatureBytes);
            file.LastSignatureCheckAt = now;
            if (_tracked.Count >= _options.MaxTrackedFiles)
            {
                var oldest = _tracked.Values.MinBy(static item => item.LastWriteUtc)!;
                _tracked.Remove(oldest.Path);
            }
            _tracked[candidate.FullName] = file;

            // Project attribution uses the retained workspace root. Resolution is
            // bounded and cached; an explicit configuration mapping outranks the
            // filesystem evidence, and an unresolved task stays Unassigned.
            ProjectAssignment? assignment = null;
            if (_projects is not null && identity.WorkspaceRoot.Length > 0)
            {
                assignment = _projects.Resolve(identity.WorkspaceRoot, isStandaloneChat: identity.IsStandaloneWorkspace);
            }

            var task = _reducer.RegisterConversation(
                state.Profile.Id,
                conversationId,
                identity.Surface,
                identity.WorkspaceLeaf,
                identity.Found,
                now,
                identity.WorkspaceRoot,
                assignment?.Project);

            var title = state.Titles.GetTitle(conversationId);
            if (title.Length > 0)
            {
                _reducer.SetLabel(task.Key, title, identity.Surface);
            }
        }
    }

}
