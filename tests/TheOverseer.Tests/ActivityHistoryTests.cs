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
    public void UnknownIsNotZeroOnTheChart()
    {
        var history = new ActivityHistory();
        history.Record(VoiceTestData.Snapshot(Start, working: CountValue.Unknown));
        var segments = ActivityChartGeometry.Build(history.Snapshot(Start), 100, 30);
        Assert.Empty(segments);
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

        var segments = ActivityChartGeometry.Build(series, 300, 30);
        Assert.Equal(2, segments.Count);
        foreach (var segment in segments)
        {
            Assert.Single(segment.Points);
        }
    }

    [Fact]
    public void EmptyAndConstantSeriesAreFinite()
    {
        var history = new ActivityHistory();
        var empty = ActivityChartGeometry.Build(history.Snapshot(Start), 200, 28);
        Assert.Empty(empty);

        history.Record(VoiceTestData.Snapshot(Start, working: CountValue.Known(0)));
        var constant = ActivityChartGeometry.Build(history.Snapshot(Start), 200, 28);
        Assert.NotEmpty(constant);
        foreach (var segment in constant)
        {
            foreach (var point in segment.Points)
            {
                Assert.True(double.IsFinite(point.X) && double.IsFinite(point.Y));
                Assert.InRange(point.X, 0, 200);
                Assert.InRange(point.Y, 0, 28);
            }
        }
    }

    [Fact]
    public void OneHundredThousandSamplesStayBoundedAndLegible()
    {
        var history = new ActivityHistory();
        for (var index = 0; index < 100_000; index++)
        {
            history.Record(VoiceTestData.Snapshot(Start.AddMilliseconds(index * 10), working: CountValue.Known(index % 7)));
        }

        var series = history.Snapshot(Start.AddSeconds(1000));
        var segments = ActivityChartGeometry.Build(series, 320, 30);
        var points = segments.Sum(segment => segment.Points.Count);
        Assert.True(points <= ActivityChartGeometry.DefaultMaximumColumns, "points: " + points);
        Assert.True(series.SampleCount <= ActivityHistory.MaximumBuckets);
    }

    [Fact]
    public void PartialColumnsUseTheDistinctSegmentStyle()
    {
        var history = new ActivityHistory();
        // The axis is a fixed 60 minutes, so complete and partial observations
        // must land in different columns to be styled differently.
        history.Record(VoiceTestData.Snapshot(Start, working: CountValue.Partial(2)));
        history.Record(VoiceTestData.Snapshot(Start.AddMinutes(30), working: CountValue.Known(5)));
        history.Record(VoiceTestData.Snapshot(Start.AddMinutes(60), working: CountValue.Known(1)));

        var segments = ActivityChartGeometry.Build(history.Snapshot(Start.AddMinutes(60)), 200, 30);

        Assert.Contains(segments, segment => segment.Kind == ActivitySegmentKind.Partial);
        Assert.Contains(segments, segment => segment.Kind == ActivitySegmentKind.Complete);

        // Latest observation wins inside one second bucket, so a superseded
        // partial sample is not retained as a stale lower bound.
        var sameBucket = new ActivityHistory();
        sameBucket.Record(VoiceTestData.Snapshot(Start, working: CountValue.Partial(2)));
        sameBucket.Record(VoiceTestData.Snapshot(Start.AddMilliseconds(500), working: CountValue.Known(3)));
        var sameBucketSeries = sameBucket.Snapshot(Start.AddSeconds(1));
        Assert.Single(sameBucketSeries.Samples);
        Assert.True(sameBucketSeries.Samples[0].Working.IsComplete);
    }

    [Fact]
    public void TooltipIsBoundedAndHonest()
    {
        var history = new ActivityHistory();
        var tooltip = ActivityChartGeometry.Tooltip(history.Snapshot(Start), Start);
        Assert.Contains("No activity", tooltip, StringComparison.Ordinal);

        history.Record(VoiceTestData.Snapshot(Start, working: CountValue.Partial(3)));
        history.Record(VoiceTestData.Snapshot(Start.AddMinutes(20), working: CountValue.Known(1)));
        var series = history.Snapshot(Start.AddMinutes(20));
        var text = ActivityChartGeometry.Tooltip(series, Start.AddMinutes(20));
        Assert.True(text.Length <= 240, "tooltip length " + text.Length);
        Assert.Contains("partial", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("gaps", text, StringComparison.OrdinalIgnoreCase);
    }
}
