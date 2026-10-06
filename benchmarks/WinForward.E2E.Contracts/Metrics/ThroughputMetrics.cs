using System.Text.Json;
using WinForward.E2E.Contracts.Json;

namespace WinForward.E2E.Contracts.Metrics;

/// <summary>
/// The <c>metrics</c> object of a <c>throughput</c> result record. Every property is
/// <see langword="required"/> so a new key cannot be added without the factory in the shape test
/// failing to compile, and the write order below is the declaration order of
/// <see cref="ArmKeys.Throughput"/>: the record publishes keys in a stable order because the
/// analyzer reads the files as text, not as a deserialized map.
/// </summary>
public sealed record ThroughputMetrics : IJsonWritable
{
    /// <summary>Echoed frame bytes the client read back: the measured transfer.</summary>
    public required long Bytes { get; init; }

    /// <summary>Frame bytes the client wrote.</summary>
    public required long BytesSent { get; init; }

    /// <summary>Echoed frames, derived from <see cref="Bytes"/> and the frame length.</summary>
    public required long Frames { get; init; }

    /// <summary>Frames the client wrote.</summary>
    public required long FramesSent { get; init; }

    /// <summary>Frames the client read back.</summary>
    public required long FramesEchoed { get; init; }

    /// <summary>Streams that connected; <c>streams - connectFailures</c>.</summary>
    public required long StreamConnects { get; init; }

    /// <summary>Streams whose connect attempt failed.</summary>
    public required long ConnectFailures { get; init; }

    /// <summary>Sends the kernel refused with an error.</summary>
    public required long SendFailures { get; init; }

    /// <summary>Declared byte budget for the arm: <c>targetBytesPerSecond * seconds</c>.</summary>
    public required long BudgetBytes { get; init; }

    /// <summary>True when the run ended because the budget was spent rather than because time ran out.</summary>
    public required bool BudgetReached { get; init; }

    /// <summary>Budget bytes no frame claimed: a frame is never part-budgeted.</summary>
    public required long BudgetRemainingBytes { get; init; }

    /// <summary>Measured wall time of the arm, in seconds.</summary>
    public required double ElapsedSeconds { get; init; }

    /// <summary>Echoed bytes per second, or <see langword="null"/> when no time passed.</summary>
    public required double? GoodputBps { get; init; }

    /// <summary>Echoed bits per second in megabits, or <see langword="null"/> when no time passed.</summary>
    public required double? GoodputMbps { get; init; }

    /// <summary>Smallest echoed-byte count of any stream, or 0 when there were no streams.</summary>
    public required long PerStreamMinBytes { get; init; }

    /// <summary>Largest echoed-byte count of any stream.</summary>
    public required long PerStreamMaxBytes { get; init; }

    /// <summary>Frames that failed their checksum.</summary>
    public required long Corrupt { get; init; }

    /// <summary>Frames that decoded to something other than a valid frame.</summary>
    public required long ProtocolErrors { get; init; }

    /// <summary>The effective aggregate send rate the streams were paced to.</summary>
    public required long TargetBytesPerSecond { get; init; }

    /// <inheritdoc/>
    public void WriteTo(Utf8JsonWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.WriteStartObject();
        writer.WriteNumber(ArmKeys.Throughput.Bytes, Bytes);
        writer.WriteNumber(ArmKeys.Throughput.BytesSent, BytesSent);
        writer.WriteNumber(ArmKeys.Throughput.Frames, Frames);
        writer.WriteNumber(ArmKeys.Throughput.FramesSent, FramesSent);
        writer.WriteNumber(ArmKeys.Throughput.FramesEchoed, FramesEchoed);
        writer.WriteNumber(ArmKeys.Throughput.StreamConnects, StreamConnects);
        writer.WriteNumber(ArmKeys.Throughput.ConnectFailures, ConnectFailures);
        writer.WriteNumber(ArmKeys.Throughput.SendFailures, SendFailures);
        writer.WriteNumber(ArmKeys.Throughput.BudgetBytes, BudgetBytes);
        writer.WriteBoolean(ArmKeys.Throughput.BudgetReached, BudgetReached);
        writer.WriteNumber(ArmKeys.Throughput.BudgetRemainingBytes, BudgetRemainingBytes);
        writer.WriteNumber(ArmKeys.Throughput.ElapsedSeconds, ElapsedSeconds);
        WriteReading(writer, ArmKeys.Throughput.GoodputBps, GoodputBps);
        WriteReading(writer, ArmKeys.Throughput.GoodputMbps, GoodputMbps);
        writer.WriteNumber(ArmKeys.Throughput.PerStreamMinBytes, PerStreamMinBytes);
        writer.WriteNumber(ArmKeys.Throughput.PerStreamMaxBytes, PerStreamMaxBytes);
        writer.WriteNumber(ArmKeys.Throughput.Corrupt, Corrupt);
        writer.WriteNumber(ArmKeys.Throughput.ProtocolErrors, ProtocolErrors);
        writer.WriteNumber(ArmKeys.Throughput.TargetBytesPerSecond, TargetBytesPerSecond);
        writer.WriteEndObject();
    }

    /// <summary>
    /// Writes a rate that is either unknown or overflowed to a non-finite value. Both publish as JSON
    /// <see langword="null"/>: a rate with no duration behind it is not a measured zero, and a
    /// non-finite number is not JSON at all.
    /// </summary>
    private static void WriteReading(Utf8JsonWriter writer, string propertyName, double? value)
    {
        if (value is { } number && double.IsFinite(number))
        {
            writer.WriteNumber(propertyName, number);
        }
        else
        {
            writer.WriteNull(propertyName);
        }
    }
}
