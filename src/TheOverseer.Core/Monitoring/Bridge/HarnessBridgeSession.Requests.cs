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
        private readonly HashSet<string> _observed = new(StringComparer.Ordinal);
        private bool _uncorrelated;
        private bool _uncorrelatedObserved;
        private bool _overflow;

        public int Count => _identities.Count + (_uncorrelated ? 1 : 0) + (_overflow ? 1 : 0);

        public bool Add(string? identity, bool observed = false)
        {
            if (identity is null)
            {
                _uncorrelated = true;
                _uncorrelatedObserved |= observed;
                return true;
            }
            if (!_identities.Contains(identity) && _identities.Count >= MaximumIdentities)
            {
                _overflow = true;
                return false;
            }
            _identities.Add(identity);
            if (observed) _observed.Add(identity);
            return true;
        }

        public void Remove(string identity)
        {
            _identities.Remove(identity);
            _observed.Remove(identity);
        }

        public bool HasObserved => _uncorrelatedObserved || _observed.Count > 0;

        public void WithdrawConfirmation()
        {
            _observed.Clear();
            _uncorrelatedObserved = false;
        }

        /// <summary>A request without an identity is pending.</summary>
        public bool Uncorrelated => _uncorrelated;

        /// <summary>The only pending request's identity, when exactly one is known.</summary>
        public string? SingleIdentity => _identities.Count == 1 && !_uncorrelated && !_overflow ? _identities.First() : null;

        /// <summary>
        /// Clears only the identity-less request, after the one thing it was
        /// observed to block has itself been observed to finish.
        /// </summary>
        public void ClearUncorrelated()
        {
            _uncorrelated = false;
            _uncorrelatedObserved = false;
        }

        public void Clear()
        {
            _identities.Clear();
            WithdrawConfirmation();
            _uncorrelated = false;
            _overflow = false;
        }
    }
}
