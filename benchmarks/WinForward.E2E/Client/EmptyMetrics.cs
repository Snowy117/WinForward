using System.Text.Json;
using WinForward.E2E.Contracts.Json;

namespace WinForward.E2E.Client;

/// <summary>
/// The metrics of an arm that failed before it built any: the record still publishes a
/// <c>metrics</c> object, the same empty one a run that reached the writer with nothing measured has
/// always had.
/// </summary>
internal sealed class EmptyMetrics : IJsonWritable
{
    /// <inheritdoc/>
    public void WriteTo(Utf8JsonWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.WriteStartObject();
        writer.WriteEndObject();
    }
}
