using System.Text;
using TheOverseer.Core.Diagnostics;
using TheOverseer.Core.Models;
using TheOverseer.Core.Monitoring.Parsing;
using TheOverseer.Core.Monitoring.Sessions;
using TheOverseer.Core.Monitoring.State;

namespace TheOverseer.Core.Monitoring;

public sealed partial class CodexMonitor : IAsyncDisposable, IDisposable
{
    private MonitoringCounts BuildCounts(IReadOnlyList<AgentTask> tasks, MonitoringHealthLevel level, bool coverageComplete)
    {
        if (level == MonitoringHealthLevel.Unavailable)
        {
            return MonitoringCounts.Unknown;
        }

        var authoritative = tasks.Where(static task => task.HasCurrentState).ToArray();
        var partial = !coverageComplete ||
                      tasks.Any(static task => !task.HasCurrentState);

        CountValue Count(Func<AgentTask, bool> predicate) =>
            partial ? CountValue.Partial(authoritative.Count(predicate)) : CountValue.Known(authoritative.Count(predicate));

        return new MonitoringCounts(
            Count(static task => task.State == AgentTaskState.Working),
            Count(static task => task.HasAttention),
            Count(static task => task.State == AgentTaskState.NeedsApproval),
            Count(static task => task.State == AgentTaskState.NeedsInput),
            Count(static task => task.State == AgentTaskState.Error),
            Count(static task => task.State == AgentTaskState.Done),
            Count(static task => task.State == AgentTaskState.Idle),
            Count(static _ => true));
    }

    private MonitorCapabilities BuildCapabilities(LiveObservationStats observation, bool anyRootAccessible)
    {
        var lifecycle = observation.AnyLifecycleObserved
            ? new CapabilityStatus(CapabilitySupport.LiveObserved, "rollout-lifecycle", "task_started/task_complete/turn_aborted observed live")
            : new CapabilityStatus(CapabilitySupport.Supported, "rollout-lifecycle", "implemented and contract-tested; no lifecycle record read in this run");

        var activity = observation.ItemCompletedRecords + observation.ToolCallRecords > 0
            ? new CapabilityStatus(CapabilitySupport.LiveObserved, "structured-activity", "item_completed/tool-call records observed live")
            : new CapabilityStatus(CapabilitySupport.Supported, "structured-activity", "implemented and contract-tested; not observed live");

        var permission = observation.PermissionRequestRecords > 0
            ? new CapabilityStatus(CapabilitySupport.LiveObserved, "permission-requests", "structured approval request observed live")
            : new CapabilityStatus(CapabilitySupport.Unsupported, "permission-requests", "No structured approval request observed in this rollout source; no live approval-event connection configured");

        var input = observation.InputRequestRecords > 0
            ? new CapabilityStatus(CapabilitySupport.LiveObserved, "input-requests", "structured input request observed live")
            : new CapabilityStatus(CapabilitySupport.Supported, "input-requests", "input tool-call metadata supported; transient event-only requests may be unavailable");

        var failure = _counters.TaskFailed > 0
            ? new CapabilityStatus(CapabilitySupport.LiveObserved, "task-failure", "explicit terminal failure records observed")
            : new CapabilityStatus(CapabilitySupport.Unsupported, "task-failure", "no explicit task-level failure record observed in this Codex version");

        var runtime = _runtimeAvailability switch
        {
            RuntimeActivityAvailability.Available => new CapabilityStatus(CapabilitySupport.LiveObserved, "runtime-activity-source", "optional read-only state store read succeeded"),
            RuntimeActivityAvailability.Failed => new CapabilityStatus(CapabilitySupport.Unavailable, "runtime-activity-source", "optional source present but unreadable"),
            RuntimeActivityAvailability.NotConfigured => new CapabilityStatus(CapabilitySupport.Unsupported, "runtime-activity-source", "not configured on this platform"),
            _ => new CapabilityStatus(CapabilitySupport.Unavailable, "runtime-activity-source", "optional source not read (headless or unavailable)"),
        };

        var titlesAvailable = _profileStates.Any(static state => state.Titles.Status.Availability == TitleIndexAvailability.Available);
        var titles = titlesAvailable
            ? new CapabilityStatus(CapabilitySupport.LiveObserved, "session-titles", "official session index read")
            : new CapabilityStatus(CapabilitySupport.Unavailable, "session-titles", "official session index missing or unreadable");

        var attention = _anyLiveAttention
            ? new CapabilityStatus(CapabilitySupport.LiveObserved, "attention-live", "a live attention transition was observed")
            : new CapabilityStatus(CapabilitySupport.NotExercised, "attention-live", "no live attention signal available to observe; contracts covered by synthetic tests");

        if (!anyRootAccessible)
        {
            lifecycle = new CapabilityStatus(CapabilitySupport.Unavailable, "rollout-lifecycle", "no readable session root in this run");
        }

        return new MonitorCapabilities(lifecycle, activity, permission, input, failure, runtime, titles, attention);
    }

    private ProfileState? FindState(string profileId)
    {
        foreach (var state in _profileStates)
        {
            if (string.Equals(state.Profile.Id, profileId, StringComparison.Ordinal))
            {
                return state;
            }
        }

        return null;
    }

    private static bool IsUnderRoot(SessionProfile profile, string path)
    {
        try
        {
            var root = Path.GetFullPath(profile.SessionsRoot);
            var full = Path.GetFullPath(path);
            if (!root.EndsWith(Path.DirectorySeparatorChar))
            {
                root += Path.DirectorySeparatorChar;
            }

            if (!full.StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) return false;
            for (var entry = full; entry.Length >= root.TrimEnd(Path.DirectorySeparatorChar).Length; entry = Path.GetDirectoryName(entry) ?? string.Empty)
            {
                if (BoundedSessionEnumerator.IsReparsePoint(entry)) return false;
            }
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
        catch (PathTooLongException)
        {
            return false;
        }
    }

    private static ulong ComputeSignature(string path, int maximumBytes)
    {
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                4096,
                FileOptions.SequentialScan);
            Span<byte> buffer = stackalloc byte[4096];
            var read = stream.Read(buffer[..Math.Clamp(maximumBytes, 0, buffer.Length)]);
            const ulong offset = 14695981039346656037;
            const ulong prime = 1099511628211;
            var hash = offset;
            foreach (var value in buffer[..read])
            {
                hash ^= value;
                hash *= prime;
            }

            return hash == 0 ? 1 : hash;
        }
        catch (IOException)
        {
            return 0;
        }
        catch (UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private sealed class ProfileState
    {
        public ProfileState(SessionProfile profile, MonitorOptions options)
        {
            Profile = profile;
            Enumerator = new BoundedSessionEnumerator(profile, options);
            Tracker = new SessionChangeTracker(profile.SessionsRoot, options.MaxWatcherHintPaths);
            Titles = new SessionTitleIndex(options.MaxTitleCacheEntries, options.TitleReadBytesPerPass);
        }

        public SessionProfile Profile { get; }

        public BoundedSessionEnumerator Enumerator { get; }

        public SessionChangeTracker Tracker { get; }

        public SessionTitleIndex Titles { get; }

        public RuntimeActivityAvailability RuntimeAvailability { get; set; } = RuntimeActivityAvailability.Unavailable;
    }

    private sealed class ObservationCounters
    {
        public int SessionMeta;
        public int TaskStarted;
        public int TaskCompleted;
        public int TurnAborted;
        public int TaskFailed;
        public int ItemCompleted;
        public int ToolCalls;
        public int PermissionRequests;
        public int InputRequests;
        public int Malformed;
        public int Oversize;
        public int UnknownSchema;
        public int FilesHydrated;
    }}
