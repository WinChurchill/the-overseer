namespace TheOverseer.Core.Monitoring.Bridge;

public sealed partial class HarnessBridgeSession
{
    /// <summary>
    /// Bounded source-local request identities. Missing IDs and capacity loss are
    /// separate flags, never invented IDs that a producer could accidentally clear.
    /// Only a turn/session boundary or authoritative snapshot can clear those flags.
    /// </summary>
    private sealed class PendingRequests
    {
        private const int MaximumIdentities = 64;
        private readonly HashSet<string> _identities = new(StringComparer.Ordinal);
        private bool _uncorrelated;
        private bool _overflow;

        public int Count => _identities.Count + (_uncorrelated ? 1 : 0) + (_overflow ? 1 : 0);

        public bool Add(string? identity)
        {
            if (identity is null) { _uncorrelated = true; return true; }
            if (_identities.Contains(identity)) return true;
            if (_identities.Count >= MaximumIdentities) { _overflow = true; return false; }
            _identities.Add(identity);
            return true;
        }

        public void Remove(string identity) => _identities.Remove(identity);

        public void Clear()
        {
            _identities.Clear();
            _uncorrelated = false;
            _overflow = false;
        }
    }
}
