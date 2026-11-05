namespace TheOverseer.Core.Monitoring.Bridge;

public sealed partial class HarnessBridgeSession
{
    // Source-local retirement survives session eviction. Never evict a tombstone:
    // after saturation, unknown epochs require a fresh collector attachment.
    private string? AdmitEpoch(string epoch)
    {
        if (_hasEpoch && string.Equals(_currentEpoch, epoch, StringComparison.Ordinal)) return null;
        if (_retiredEpochs.Contains(epoch)) return "retired-epoch";
        if (_hasEpoch)
        {
            if (_retiredEpochs.Count >= 64)
            {
                _epochHistoryFull = true;
                foreach (var lane in _sessions.Values) lane.CurrentStateComplete = false;
                return "epoch-history-limit";
            }
            _retiredEpochs.Add(_currentEpoch);
            foreach (var lane in _sessions.Values) lane.CurrentStateComplete = false;
        }
        _currentEpoch = epoch;
        _hasEpoch = true;
        _lastSnapshotAtUtc = DateTimeOffset.MinValue;
        return null;
    }
}
