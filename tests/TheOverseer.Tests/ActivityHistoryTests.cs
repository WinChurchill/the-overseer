using TheOverseer.Core.History;
using TheOverseer.Core.Models;
using TheOverseer.Tests.Support;

namespace TheOverseer.Tests;

public sealed class ActivityHistoryTests
{
    private static readonly DateTimeOffset Start = VoiceTestData.Start;

    [Fact]
    public void RecordsSamplesInOneSecondBucketsReplacingTheSameBucket()
    {
        var history = new ActivityHistory();
        history.Record(VoiceTestData.Snapshot(Start, working: CountValue.Known(1)));
        history.Record(VoiceTestData.Snapshot(Start.AddMilliseconds(400), working: CountValue.Known(3)));

        var series = history.Snapshot(Start.AddSeconds(1));
        Assert.Single(series.Samples);
        Assert.Equal(3, series.Samples[0].Working.Value);
        Assert.Equal(3, series.PeakWorking);
    }

    [Fact]
    public void QualityChangesAreRecordedWithoutAttentionEvents()
    {
        var history = new ActivityHistory();
        history.Record(VoiceTestData.Snapshot(Start, working: CountValue.Known(2), transitions: Array.Empty<AttentionEvent>()));
        history.Record(VoiceTestData.Snapshot(Start.AddSeconds(1), working: CountValue.Partial(2)));
        history.Record(VoiceTestData.Snapshot(Start.AddSeconds(2), working: CountValue.Unknown));

        var series = history.Snapshot(Start.AddSeconds(3));
        Assert.Equal(3, series.SampleCount);
        Assert.True(series.HasPartial);
        Assert.True(series.HasUnknown);
        Assert.False(series.Samples[2].Working.IsKnown);
    }

    [Fact]
    public void UnknownIsNotRecordedAsKnownZero()
    {
        var history = new ActivityHistory();
        history.Record(VoiceTestData.Snapshot(Start, working: CountValue.Unknown));
        var sample = Assert.Single(history.Snapshot(Start).Samples);
        Assert.False(sample.Working.IsKnown);
    }

    [Fact]
    public void SixtyMinuteTrimHappensEvenWhenInputStops()
    {
        var history = new ActivityHistory();
        for (var index = 0; index < 120; index++)
        {
            history.Record(VoiceTestData.Snapshot(Start.AddMinutes(index), working: CountValue.Known(1)));
        }

        var moved = history.Snapshot(Start.AddMinutes(200));
        Assert.Empty(moved.Samples);
        Assert.True(moved.IsEmpty);
    }

    [Fact]
    public void WindowKeepsExactlyTheLatestHour()
    {
        var history = new ActivityHistory();
        for (var index = 0; index <= 90; index++)
        {
            history.Record(VoiceTestData.Snapshot(Start.AddMinutes(index), working: CountValue.Known(1)));
        }

        var now = Start.AddMinutes(90);
        var series = history.Snapshot(now);
        Assert.Equal(61, series.SampleCount);
        Assert.True(series.Samples[0].Timestamp >= now - ActivityHistory.Window);
    }

    [Fact]
    public void OlderSamplesAreIgnoredAndClockRewindIsSafe()
    {
        var history = new ActivityHistory();
        history.Record(VoiceTestData.Snapshot(Start.AddSeconds(10), working: CountValue.Known(5)));
        history.Record(VoiceTestData.Snapshot(Start, working: CountValue.Known(1)));

        var series = history.Snapshot(Start.AddSeconds(10));
        Assert.Single(series.Samples);
        Assert.Equal(5, series.Samples[0].Working.Value);

        // A rewind renders only what is at or before "now" and never throws.
        var rewound = history.Snapshot(Start.AddSeconds(5));
        Assert.Empty(rewound.Samples);
    }

    [Fact]
    public void BucketCapIsEnforcedForVeryLongSessions()
    {
        var history = new ActivityHistory();
        for (var index = 0; index < 4000; index++)
        {
            history.Record(VoiceTestData.Snapshot(Start.AddSeconds(index), working: CountValue.Known(index % 4)));
        }

        Assert.True(history.BucketCount <= ActivityHistory.MaximumBuckets, "buckets: " + history.BucketCount);
    }

    [Fact]
    public void SleepGapIsReportedInsteadOfJoined()
    {
        var history = new ActivityHistory();
        history.Record(VoiceTestData.Snapshot(Start, working: CountValue.Known(2)));
        history.Record(VoiceTestData.Snapshot(Start.AddMinutes(30), working: CountValue.Known(2)));

        var series = history.Snapshot(Start.AddMinutes(30));
        Assert.True(series.HasGap);

        Assert.Equal(2, series.SampleCount);
    }

    [Fact]
    public void OneHundredThousandSamplesStayBounded()
    {
        var history = new ActivityHistory();
        for (var index = 0; index < 100_000; index++)
        {
            history.Record(VoiceTestData.Snapshot(Start.AddMilliseconds(index * 10), working: CountValue.Known(index % 7)));
        }

        var series = history.Snapshot(Start.AddSeconds(1000));
        Assert.True(series.SampleCount <= ActivityHistory.MaximumBuckets);
    }

    [Fact]
    public void PartialAndCompleteSamplesRetainTheirQuality()
    {
        var history = new ActivityHistory();
        // Quality stays attached to each observation throughout the hour.
        history.Record(VoiceTestData.Snapshot(Start, working: CountValue.Partial(2)));
        history.Record(VoiceTestData.Snapshot(Start.AddMinutes(30), working: CountValue.Known(5)));
        history.Record(VoiceTestData.Snapshot(Start.AddMinutes(60), working: CountValue.Known(1)));

        var series = history.Snapshot(Start.AddMinutes(60));
        Assert.Contains(series.Samples, sample => sample.IsPartial);
        Assert.Contains(series.Samples, sample => sample.Working.IsKnown && !sample.IsPartial);

        // Latest observation wins inside one second bucket, so a superseded
        // partial sample is not retained as a stale lower bound.
        var sameBucket = new ActivityHistory();
        sameBucket.Record(VoiceTestData.Snapshot(Start, working: CountValue.Partial(2)));
        sameBucket.Record(VoiceTestData.Snapshot(Start.AddMilliseconds(500), working: CountValue.Known(3)));
        var sameBucketSeries = sameBucket.Snapshot(Start.AddSeconds(1));
        Assert.Single(sameBucketSeries.Samples);
        Assert.True(sameBucketSeries.Samples[0].Working.IsComplete);
    }

}
