using TheOverseer.Core.Models;

namespace TheOverseer.Core.History;

/// <summary>One observed count sample. Timestamps come from the snapshot itself.</summary>
public readonly record struct ActivitySample(DateTimeOffset Timestamp, CountValue Working, CountValue NeedsHuman)
{
    public bool IsUnknown => !Working.IsKnown;

    public bool IsPartial => Working.IsKnown && !Working.IsComplete;
}

/// <summary>
/// The bounded latest-hour view: samples inside the window plus the derived
/// count and observation-quality summaries. Gaps are simply absent samples.
/// </summary>
public sealed record ActivitySeries(
    IReadOnlyList<ActivitySample> Samples,
    DateTimeOffset From,
    DateTimeOffset To,
    int PeakWorking,
    int SampleCount,
    bool HasGap,
    bool HasUnknown,
    bool HasPartial)
{
    public static ActivitySeries Empty(DateTimeOffset now) => new(
        Array.Empty<ActivitySample>(), now - ActivityHistory.Window, now, 0, 0, false, false, false);

    public bool IsEmpty => Samples.Count == 0;
}

/// <summary>
/// In-memory rolling history of working/needs-human counts. One second per
/// bucket, 60 minutes retained, newest observation wins inside a bucket. It is
/// deliberately not event accounting and it is never persisted.
/// </summary>
public sealed class ActivityHistory
{
    public const int MaximumBuckets = 3601;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(60);
    public static readonly TimeSpan GapThreshold = TimeSpan.FromSeconds(10);
    public const int MaximumSamplesExamined = 100_000;

    private readonly object _gate = new();
    private readonly SortedDictionary<long, ActivitySample> _buckets = new();
    private long _newestBucket = long.MinValue;

    public int BucketCount
    {
        get
        {
            lock (_gate)
            {
                return _buckets.Count;
            }
        }
    }

    /// <summary>Clears the trace, for example after a terminal monitoring failure.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _buckets.Clear();
            _newestBucket = long.MinValue;
        }
    }

    /// <summary>
    /// Records one snapshot. Strictly older samples are ignored so a degraded
    /// republication cannot rewrite newer history; equal timestamps replace the
    /// bucket deterministically.
    /// </summary>
    public void Record(MonitoringSnapshot snapshot)
    {
        var timestamp = snapshot.GeneratedAt;
        var bucket = timestamp.ToUnixTimeSeconds();
        lock (_gate)
        {
            if (bucket < _newestBucket)
            {
                return;
            }

            if (bucket == _newestBucket && _buckets.TryGetValue(bucket, out var existing) && existing.Timestamp > timestamp)
            {
                return;
            }

            _buckets[bucket] = new ActivitySample(timestamp, snapshot.Counts.Working, snapshot.Counts.NeedsHuman);
            if (bucket > _newestBucket)
            {
                _newestBucket = bucket;
            }

            Trim(timestamp);
        }
    }

    /// <summary>
    /// Latest-hour view at <paramref name="now"/>. The clock is injected here so
    /// stopped input still trims and renders honestly.
    /// </summary>
    public ActivitySeries Snapshot(DateTimeOffset now)
    {
        lock (_gate)
        {
            Trim(now);
            var from = now - Window;
            var samples = new List<ActivitySample>(Math.Min(_buckets.Count, MaximumBuckets));
            var peak = 0;
            var hasGap = false;
            var hasUnknown = false;
            var hasPartial = false;
            DateTimeOffset? previous = null;
            var examined = 0;

            foreach (var pair in _buckets)
            {
                examined++;
                if (examined > MaximumSamplesExamined)
                {
                    break;
                }

                var sample = pair.Value;
                if (sample.Timestamp < from || sample.Timestamp > now)
                {
                    continue;
                }

                if (previous is { } last && sample.Timestamp - last > GapThreshold)
                {
                    hasGap = true;
                }

                previous = sample.Timestamp;
                if (!sample.Working.IsKnown)
                {
                    hasUnknown = true;
                }
                else
                {
                    if (sample.Working.Value > peak)
                    {
                        peak = sample.Working.Value;
                    }

                    if (!sample.Working.IsComplete)
                    {
                        hasPartial = true;
                    }
                }

                samples.Add(sample);
            }

            hasGap |= previous is { } latest && now - latest > GapThreshold;
            return new ActivitySeries(samples, from, now, peak, samples.Count, hasGap, hasUnknown, hasPartial);
        }
    }

    private void Trim(DateTimeOffset newest)
    {
        var oldestAllowed = newest - Window;
        while (_buckets.Count > 0)
        {
            var first = FirstKey();
            if (_buckets.Count <= MaximumBuckets && first >= oldestAllowed.ToUnixTimeSeconds())
            {
                break;
            }

            _buckets.Remove(first);
        }
    }

    private long FirstKey()
    {
        foreach (var key in _buckets.Keys)
        {
            return key;
        }

        return long.MinValue;
    }
}
