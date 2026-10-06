using System.Text.Json;
using WinForward.E2E.Contracts.Json;

namespace WinForward.E2E.Contracts.Metrics;

/// <summary>
/// The <c>metrics</c> object of a <c>"base"</c> result record: how long the two phases took together,
/// then the metrics of each phase it ran, back to back inside one record. Every property is
/// <see langword="required"/> so a new key cannot be added without the factory in the shape test
/// failing to compile, and the write order below is the declaration order of <see cref="ArmKeys.Control"/>.
/// </summary>
/// <remarks>
/// <see cref="Latency"/> is the latency arm's own typed record, so the phase publishes exactly the
/// latency kind's members one level down. <see cref="Loss"/> is still carried as an
/// <see cref="IJsonWritable"/> because the loss arm's metrics are not typed yet: the phase arrives as
/// one JSON object with its own braces either way, and B2c narrows the property to the loss record
/// without changing a byte of what it publishes.
/// </remarks>
public sealed record ControlMetrics : IJsonWritable
{
    /// <summary>Wall time the two phases took together.</summary>
    public required double ElapsedSeconds { get; init; }

    /// <summary>The latency phase's metrics, one level down.</summary>
    public required LatencyMetrics Latency { get; init; }

    /// <summary>The loss phase's metrics, one level down.</summary>
    public required IJsonWritable Loss { get; init; }

    /// <inheritdoc/>
    public void WriteTo(Utf8JsonWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.WriteStartObject();
        writer.WriteNumber(ArmKeys.Control.ElapsedSeconds, ElapsedSeconds);
        writer.WritePropertyName(ArmKeys.Control.Latency);
        Latency.WriteTo(writer);
        writer.WritePropertyName(ArmKeys.Control.Loss);
        Loss.WriteTo(writer);
        writer.WriteEndObject();
    }
}
