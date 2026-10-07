using System.Text.Json;
using WinForward.E2E.Contracts.Json;

namespace WinForward.E2E.Contracts.Metrics;

/// <summary>
/// The <c>metrics</c> object of a <c>reliability</c> result record: the outcome distribution of one
/// connection schedule, the distribution its modes called for, and the joint mode x observed
/// breakdown beneath them. Every property is <see langword="required"/> so a new key cannot be added
/// without the factory in the shape test failing to compile, and the write order below is the
/// declaration order of <see cref="ArmKeys.Reliability"/>.
/// </summary>
/// <remarks>
/// <see cref="ByMode"/> is the record's data-driven member: its keys are the mode names the plan's
/// <c>modeMix</c> scheduled, so the member set follows the run while every block's schema does not.
/// Nothing else is conditional, and a rate with no population or duration behind it publishes JSON
/// <see langword="null"/> while keeping its key.
/// </remarks>
public sealed record ReliabilityMetrics : IJsonWritable
{
    /// <summary>Attempts that ran to a verdict.</summary>
    public required long ConnectAttempts { get; init; }

    /// <summary>Attempts the pacer offered.</summary>
    public required long ScheduledAttempts { get; init; }

    /// <summary>The observed outcome distribution.</summary>
    public required ReliabilityOutcomes Outcomes { get; init; }

    /// <summary>The distribution the scheduled modes called for.</summary>
    public required ReliabilityOutcomes Expected { get; init; }

    /// <summary>Unexpected end-of-stream observations, the partialFin mode excluded.</summary>
    public required long UnexpectedEof { get; init; }

    /// <summary>Unexpected end-of-stream observations the partialFin mode calls for.</summary>
    public required long ExpectedEarlyEof { get; init; }

    /// <summary>Attempts whose echo was cut short.</summary>
    public required long Truncated { get; init; }

    /// <summary>Attempts whose observation diverged from what their mode called for.</summary>
    public required long FidelityMismatch { get; init; }

    /// <summary>Mismatches over attempts; <see langword="null"/> when no attempt ran.</summary>
    public required double? FidelityRate { get; init; }

    /// <summary>Attempts that never connected.</summary>
    public required long ConnectFail { get; init; }

    /// <summary>The echo size the arm requested.</summary>
    public required long ExpectedBytes { get; init; }

    /// <summary>The scheduled modes, in schedule order, comma separated.</summary>
    public required string ModeSchedule { get; init; }

    /// <summary>Echo bytes read back across every attempt.</summary>
    public required long EchoedBytes { get; init; }

    /// <summary>Bytes read after the expected echo, across every attempt.</summary>
    public required long TrailerBytes { get; init; }

    /// <summary>
    /// The joint mode x observed distribution: one block per scheduled mode, in schedule order. The
    /// member names are data, so only the container's own key is a declared constant.
    /// </summary>
    public required IReadOnlyDictionary<string, ReliabilityModeMetrics> ByMode { get; init; }

    /// <summary>Per-attempt evidence records the arm wrote.</summary>
    public required long AttemptRecords { get; init; }

    /// <summary>Qualifying attempts that did not fit the record cap.</summary>
    public required long AttemptRecordsOmitted { get; init; }

    /// <summary>Mean connect duration in milliseconds; <see langword="null"/> when none succeeded.</summary>
    public required double? MeanConnectMs { get; init; }

    /// <summary>Mean request-send duration in milliseconds; <see langword="null"/> when none was measured.</summary>
    public required double? MeanTransferMs { get; init; }

    /// <summary>Attempts per elapsed second; <see langword="null"/> when no time passed.</summary>
    public required double? AchievedRate { get; init; }

    /// <summary>The mode mix the arm actually ran, after its own default was applied.</summary>
    public required string EffectiveModeMix { get; init; }

    /// <inheritdoc/>
    public void WriteTo(Utf8JsonWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.WriteStartObject();
        writer.WriteNumber(ArmKeys.Reliability.ConnectAttempts, ConnectAttempts);
        writer.WriteNumber(ArmKeys.Reliability.ScheduledAttempts, ScheduledAttempts);
        writer.WritePropertyName(ArmKeys.Reliability.Outcomes);
        Outcomes.WriteTo(writer);
        writer.WritePropertyName(ArmKeys.Reliability.Expected);
        Expected.WriteTo(writer);
        writer.WriteNumber(ArmKeys.Reliability.UnexpectedEof, UnexpectedEof);
        writer.WriteNumber(ArmKeys.Reliability.ExpectedEarlyEof, ExpectedEarlyEof);
        writer.WriteNumber(ArmKeys.Reliability.Truncated, Truncated);
        writer.WriteNumber(ArmKeys.Reliability.FidelityMismatch, FidelityMismatch);
        Reading.Write(writer, ArmKeys.Reliability.FidelityRate, FidelityRate);
        writer.WriteNumber(ArmKeys.Reliability.ConnectFail, ConnectFail);
        writer.WriteNumber(ArmKeys.Reliability.ExpectedBytes, ExpectedBytes);
        writer.WriteString(ArmKeys.Reliability.ModeSchedule, ModeSchedule);
        writer.WriteNumber(ArmKeys.Reliability.EchoedBytes, EchoedBytes);
        writer.WriteNumber(ArmKeys.Reliability.TrailerBytes, TrailerBytes);
        writer.WriteStartObject(ArmKeys.Reliability.ByMode);
        foreach (var pair in ByMode)
        {
            writer.WritePropertyName(pair.Key);
            pair.Value.WriteTo(writer);
        }

        writer.WriteEndObject();
        writer.WriteNumber(ArmKeys.Reliability.AttemptRecords, AttemptRecords);
        writer.WriteNumber(ArmKeys.Reliability.AttemptRecordsOmitted, AttemptRecordsOmitted);
        Reading.Write(writer, ArmKeys.Reliability.MeanConnectMs, MeanConnectMs);
        Reading.Write(writer, ArmKeys.Reliability.MeanTransferMs, MeanTransferMs);
        Reading.Write(writer, ArmKeys.Reliability.AchievedRate, AchievedRate);
        writer.WriteString(ArmKeys.Reliability.EffectiveModeMix, EffectiveModeMix);
        writer.WriteEndObject();
    }
}

/// <summary>
/// One outcome distribution over the seven names of <see cref="ArmKeys.Reliability.OutcomeNames"/>:
/// the arm-wide observation, the arm-wide expectation, and each mode's own observed distribution all
/// carry this record, and the container key above it is written by whichever record holds it.
/// </summary>
public sealed record ReliabilityOutcomes : IJsonWritable
{
    /// <summary>Exchanges that completed as their mode called for.</summary>
    public required long Clean { get; init; }

    /// <summary>Connections the peer reset.</summary>
    public required long Reset { get; init; }

    /// <summary>Streams that ended before the complete echo arrived.</summary>
    public required long UnexpectedEof { get; init; }

    /// <summary>Attempts that ran out of time.</summary>
    public required long Timeout { get; init; }

    /// <summary>Attempts that never connected.</summary>
    public required long ConnectFail { get; init; }

    /// <summary>Half-closing attempts whose peer reset instead of echoing the trailer.</summary>
    public required long HalfCloseViolation { get; init; }

    /// <summary>Socket, protocol and framing errors outside the named outcomes.</summary>
    public required long OtherError { get; init; }

    /// <inheritdoc/>
    public void WriteTo(Utf8JsonWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.WriteStartObject();
        writer.WriteNumber(ArmKeys.Reliability.OutcomeNames.Clean, Clean);
        writer.WriteNumber(ArmKeys.Reliability.OutcomeNames.Reset, Reset);
        writer.WriteNumber(ArmKeys.Reliability.OutcomeNames.UnexpectedEof, UnexpectedEof);
        writer.WriteNumber(ArmKeys.Reliability.OutcomeNames.Timeout, Timeout);
        writer.WriteNumber(ArmKeys.Reliability.OutcomeNames.ConnectFail, ConnectFail);
        writer.WriteNumber(ArmKeys.Reliability.OutcomeNames.HalfCloseViolation, HalfCloseViolation);
        writer.WriteNumber(ArmKeys.Reliability.OutcomeNames.OtherError, OtherError);
        writer.WriteEndObject();
    }
}

/// <summary>
/// One mode's slice of the joint distribution: its tally, its observed outcome distribution, and the
/// echo and trailer totals and extremes that say whether the mode's outcomes were uniform. The four
/// extremes are <see langword="null"/> when the mode produced no attempt at all.
/// </summary>
public sealed record ReliabilityModeMetrics : IJsonWritable
{
    /// <summary>Attempts this mode produced.</summary>
    public required long Attempts { get; init; }

    /// <summary>This mode's observed outcome distribution.</summary>
    public required ReliabilityOutcomes Observed { get; init; }

    /// <summary>This mode's attempts whose echo was cut short.</summary>
    public required long Truncated { get; init; }

    /// <summary>Echo bytes this mode read back.</summary>
    public required long EchoedBytes { get; init; }

    /// <summary>Bytes this mode read after the expected echo.</summary>
    public required long TrailerBytes { get; init; }

    /// <summary>Smallest echo this mode read; <see langword="null"/> when the mode produced no attempt.</summary>
    public required long? MinEchoedBytes { get; init; }

    /// <summary>Largest echo this mode read; <see langword="null"/> when the mode produced no attempt.</summary>
    public required long? MaxEchoedBytes { get; init; }

    /// <summary>Smallest trailer this mode read; <see langword="null"/> when the mode produced no attempt.</summary>
    public required long? MinTrailerBytes { get; init; }

    /// <summary>Largest trailer this mode read; <see langword="null"/> when the mode produced no attempt.</summary>
    public required long? MaxTrailerBytes { get; init; }

    /// <inheritdoc/>
    public void WriteTo(Utf8JsonWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.WriteStartObject();
        writer.WriteNumber(ArmKeys.Reliability.Mode.Attempts, Attempts);
        writer.WritePropertyName(ArmKeys.Reliability.Mode.Observed);
        Observed.WriteTo(writer);
        writer.WriteNumber(ArmKeys.Reliability.Mode.Truncated, Truncated);
        writer.WriteNumber(ArmKeys.Reliability.Mode.EchoedBytes, EchoedBytes);
        writer.WriteNumber(ArmKeys.Reliability.Mode.TrailerBytes, TrailerBytes);
        WriteOptional(writer, ArmKeys.Reliability.Mode.MinEchoedBytes, MinEchoedBytes);
        WriteOptional(writer, ArmKeys.Reliability.Mode.MaxEchoedBytes, MaxEchoedBytes);
        WriteOptional(writer, ArmKeys.Reliability.Mode.MinTrailerBytes, MinTrailerBytes);
        WriteOptional(writer, ArmKeys.Reliability.Mode.MaxTrailerBytes, MaxTrailerBytes);
        writer.WriteEndObject();
    }

    private static void WriteOptional(Utf8JsonWriter writer, string name, long? value)
    {
        if (value is { } number)
        {
            writer.WriteNumber(name, number);
        }
        else
        {
            writer.WriteNull(name);
        }
    }
}
