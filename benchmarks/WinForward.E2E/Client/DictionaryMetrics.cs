using System.Text.Json;
using WinForward.E2E.Contracts.Json;

namespace WinForward.E2E.Client;

/// <summary>
/// The metrics carrier of an arm that still builds its <c>metrics</c> object as name/value pairs.
/// It writes itself through <see cref="JsonValue"/>, so an unmigrated arm reaches the record through
/// the same <see cref="IJsonWritable"/> seam a typed record does.
/// </summary>
/// <remarks>
/// This is a migration adapter, not a second record model: each arm that moves to a typed record
/// stops constructing one, and the type goes away with the last of them (D14.15/D14.21). It writes
/// in insertion order and hands every value to <see cref="JsonValue.WriteProperties"/>, which is the
/// dispatcher that publishes an unknown or non-finite number as JSON <see langword="null"/>.
/// </remarks>
internal sealed class DictionaryMetrics : IJsonWritable
{
    private readonly Dictionary<string, object?> _values = new(StringComparer.Ordinal);

    /// <summary>Publishes <paramref name="value"/> under <paramref name="key"/>.</summary>
    internal object? this[string key]
    {
        set => _values[key] = value;
    }

    /// <inheritdoc/>
    public void WriteTo(Utf8JsonWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.WriteStartObject();
        JsonValue.WriteProperties(writer, _values);
        writer.WriteEndObject();
    }
}
