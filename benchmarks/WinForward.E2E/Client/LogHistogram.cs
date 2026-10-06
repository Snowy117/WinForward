using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;
using WinForward.E2E.Contracts;
using WinForward.E2E.Contracts.Json;

namespace WinForward.E2E.Client;

[StructLayout(LayoutKind.Auto)]
internal readonly struct HistogramSnapshot
{
    internal HistogramSnapshot(long count, long min, long max, double mean, long p50, long p90, long p99, long p999)
    {
        Count = count;
        Min = min;
        Max = max;
        Mean = mean;
        P50 = p50;
        P90 = p90;
        P99 = p99;
        P999 = p999;
    }

    private long Count { get; }

    private long Min { get; }

    private long Max { get; }

    private double Mean { get; }

    private long P50 { get; }

    private long P90 { get; }

    private long P99 { get; }

    private long P999 { get; }

    internal void WriteTo(Utf8JsonWriter writer, string propertyName)
    {
        writer.WritePropertyName(propertyName);
        writer.WriteStartObject();
        writer.WriteNumber(ArmKeys.Common.LatencyRecord.Histogram.Count, Count);
        writer.WriteNumber(ArmKeys.Common.LatencyRecord.Histogram.MinUs, NumberFormat.Microseconds(Min));
        writer.WriteNumber(ArmKeys.Common.LatencyRecord.Histogram.MaxUs, NumberFormat.Microseconds(Max));
        writer.WriteNumber(ArmKeys.Common.LatencyRecord.Histogram.MeanUs, NumberFormat.Microseconds((long)Mean));
        writer.WriteNumber(ArmKeys.Common.LatencyRecord.Histogram.P50Us, NumberFormat.Microseconds(P50));
        writer.WriteNumber(ArmKeys.Common.LatencyRecord.Histogram.P90Us, NumberFormat.Microseconds(P90));
        writer.WriteNumber(ArmKeys.Common.LatencyRecord.Histogram.P99Us, NumberFormat.Microseconds(P99));
        writer.WriteNumber(ArmKeys.Common.LatencyRecord.Histogram.P999Us, NumberFormat.Microseconds(P999));
        writer.WriteEndObject();
    }
}

// Percentiles report the exclusive upper bound of the bucket they land in, never an underestimate.
internal sealed class LogHistogram
{
    private const int SubBucketBits = 11;
    private const int SubBucketCount = 1 << SubBucketBits;
    private const int BucketCount = 34;
    private const long MaxTrackedValue = (1L << 34) - 1;
    private const long MinTrackedValue = 1;

    private readonly Lock _gate = new();
    private readonly long[] _counts = new long[BucketCount * SubBucketCount];
    private long _count;
    private long _min = long.MaxValue;
    private long _max;
    private double _sum;

    internal long Count
    {
        get
        {
            lock (_gate)
            {
                return _count;
            }
        }
    }

    internal void Record(long nanoseconds)
    {
        var value = Math.Clamp(nanoseconds, MinTrackedValue, MaxTrackedValue);
        var bucket = 63 - BitOperations.LeadingZeroCount((ulong)value);
        lock (_gate)
        {
            _counts[(bucket * SubBucketCount) + SubBucketIndex(value, bucket)]++;

            _count++;
            _sum += value;
            if (value < _min)
            {
                _min = value;
            }

            if (value > _max)
            {
                _max = value;
            }
        }
    }

    internal HistogramSnapshot Snapshot()
    {
        lock (_gate)
        {
            return new HistogramSnapshot(
                _count,
                _count == 0 ? 0 : _min,
                _max,
                _count == 0 ? 0 : _sum / _count,
                Percentile(50),
                Percentile(90),
                Percentile(99),
                Percentile(99.9));
        }
    }

    private static int SubBucketIndex(long value, int bucket) => bucket < SubBucketBits
        ? (int)(value - (1L << bucket))
        : (int)((value - (1L << bucket)) >> (bucket - SubBucketBits));

    private static long HighestEquivalentValue(int index)
    {
        var bucket = index / SubBucketCount;
        var subBucket = index % SubBucketCount;
        if (bucket < SubBucketBits)
        {
            return (1L << bucket) + subBucket;
        }

        var width = 1L << (bucket - SubBucketBits);
        return (1L << bucket) + ((subBucket + 1) * width) - 1;
    }

    private long Percentile(double percentile)
    {
        if (_count == 0)
        {
            return 0;
        }

        var target = (long)Math.Ceiling(percentile / 100.0 * _count);
        long cumulative = 0;
        for (var index = 0; index < _counts.Length; index++)
        {
            cumulative += _counts[index];
            if (cumulative >= target)
            {
                return HighestEquivalentValue(index);
            }
        }

        return _max;
    }
}
