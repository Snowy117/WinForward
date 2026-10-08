using System.Text.Json;
using WinForward.E2E.Client;
using Xunit;

namespace WinForward.E2E.Tests;

public sealed class LogHistogramTests
{
    private const long Ceiling = (1L << 34) - 1;

    [Fact]
    public void AnEmptyHistogramReportsZeros()
    {
        var snapshot = Snapshot(new LogHistogram());

        Assert.Equal(0, snapshot.GetProperty("count").GetInt64());
        foreach (var name in new[] { "minUs", "maxUs", "meanUs", "p50Us", "p90Us", "p99Us", "p999Us" })
        {
            Assert.Equal(0, snapshot.GetProperty(name).GetDouble());
        }
    }

    [Fact]
    public void ASingleValueLandsInItsOwnBucket()
    {
        var histogram = new LogHistogram();
        histogram.Record(1000);

        var snapshot = Snapshot(histogram);

        Assert.Equal(1, snapshot.GetProperty("count").GetInt64());
        // 1000 ns is below the sub-bucket density change (2^11), where a bucket holds exactly one
        // nanosecond of width -- the percentile therefore reports the recorded value itself.
        Assert.Equal(1.0, snapshot.GetProperty("p50Us").GetDouble());
        Assert.Equal(1.0, snapshot.GetProperty("minUs").GetDouble());
        Assert.Equal(1.0, snapshot.GetProperty("maxUs").GetDouble());
    }

    // The bucket layout is a published contract: the analysis reads p50Us and friends verbatim and
    // never recomputes them, so the exclusive upper bound of a coarse bucket is frozen here.
    [Fact]
    public void ACoarseBucketReportsItsExclusiveUpperBoundInMicroseconds()
    {
        var histogram = new LogHistogram();
        histogram.Record(100_000);

        var snapshot = Snapshot(histogram);

        Assert.Equal(100.031, snapshot.GetProperty("p50Us").GetDouble());
        Assert.Equal(100.031, snapshot.GetProperty("p99Us").GetDouble());
        Assert.Equal(100_000 / 1000.0, snapshot.GetProperty("minUs").GetDouble());
    }

    [Fact]
    public void PercentilesAreMonotonicAndInsideTheObservedRange()
    {
        var histogram = new LogHistogram();
        for (var value = 1; value <= 1000; value++)
        {
            histogram.Record(value);
        }

        var snapshot = Snapshot(histogram);
        var p50 = snapshot.GetProperty("p50Us").GetDouble();
        var p90 = snapshot.GetProperty("p90Us").GetDouble();
        var p99 = snapshot.GetProperty("p99Us").GetDouble();
        var p999 = snapshot.GetProperty("p999Us").GetDouble();

        Assert.Equal(1000, snapshot.GetProperty("count").GetInt64());
        Assert.True(p50 <= p90, $"{p50} > {p90}");
        Assert.True(p90 <= p99, $"{p90} > {p99}");
        Assert.True(p99 <= p999, $"{p99} > {p999}");
        Assert.True(snapshot.GetProperty("minUs").GetDouble() <= p50);
        Assert.True(snapshot.GetProperty("maxUs").GetDouble() >= p999);
        Assert.InRange(snapshot.GetProperty("meanUs").GetDouble(), 0.5, 1.0);
    }

    [Fact]
    public void TheCeilingSaturatesInsteadOfOverflowing()
    {
        var histogram = new LogHistogram();

        histogram.Record(long.MaxValue);

        var snapshot = Snapshot(histogram);
        Assert.Equal(1, snapshot.GetProperty("count").GetInt64());
        Assert.Equal(Ceiling / 1000.0, snapshot.GetProperty("maxUs").GetDouble());
        Assert.Equal(Ceiling / 1000.0, snapshot.GetProperty("p999Us").GetDouble());
    }

    [Fact]
    public void ValuesBelowOneNanosecondClampToTheFloor()
    {
        var histogram = new LogHistogram();

        histogram.Record(0);
        histogram.Record(-12345);

        var snapshot = Snapshot(histogram);
        Assert.Equal(2, snapshot.GetProperty("count").GetInt64());
        Assert.Equal(0.001, snapshot.GetProperty("minUs").GetDouble());
        Assert.Equal(0.001, snapshot.GetProperty("maxUs").GetDouble());
    }

    private static JsonElement Snapshot(LogHistogram histogram)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            histogram.Snapshot().WriteTo(writer, "histogram");
            writer.WriteEndObject();
        }

        return JsonDocument.Parse(buffer.ToArray()).RootElement.GetProperty("histogram").Clone();
    }
}
