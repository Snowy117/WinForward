using System.Text.Json;
using WinForward.E2E.Contracts.Json;

namespace WinForward.E2E.Contracts.Metrics;

/// <summary>
/// The <c>metrics</c> object of an <c>idle</c> result record. Every property is
/// <see langword="required"/> so a new key cannot be added without the factory in the shape test
/// failing to compile, and so the writer below and the keys it declares cannot drift apart in
/// silence.
/// </summary>
public sealed record IdleMetrics : IJsonWritable
{
    /// <summary>Measured wall time of the arm, in seconds.</summary>
    public required double ElapsedSeconds { get; init; }

    /// <inheritdoc/>
    public void WriteTo(Utf8JsonWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.WriteStartObject();
        writer.WriteNumber(ArmKeys.Idle.ElapsedSeconds, ElapsedSeconds);
        writer.WriteEndObject();
    }
}
