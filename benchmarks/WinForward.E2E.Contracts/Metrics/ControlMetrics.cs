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
/// <see cref="Latency"/> and <see cref="Loss"/> are the phase arms' own typed records, so each phase
/// publishes exactly its kind's members one level down: the phase and the standalone arm are one value
/// with one writer, and a key cannot exist in one of them and not the other.
/// </remarks>
public sealed record ControlMetrics : IJsonWritable
{
    /// <summary>Wall time the two phases took together.</summary>
    public required double ElapsedSeconds { get; init; }

    /// <summary>The latency phase's metrics, one level down.</summary>
    public required LatencyMetrics Latency { get; init; }

    /// <summary>The loss phase's metrics, one level down.</summary>
    public required LossMetrics Loss { get; init; }

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
