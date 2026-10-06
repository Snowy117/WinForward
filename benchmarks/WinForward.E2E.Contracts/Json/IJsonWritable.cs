using System.Text.Json;

namespace WinForward.E2E.Contracts.Json;

/// <summary>
/// A value that writes itself into the JSON a <see cref="JsonlSink"/> is building.
/// </summary>
/// <remarks>
/// The writer is a method on the value rather than a serializer over it because the records are read
/// as text by the analyzer and by the frozen Python reference implementation: which keys exist, in
/// which order, at which nesting level, and which of them are <see langword="null"/> instead of
/// absent, is the contract. Handing the value an <see cref="Utf8JsonWriter"/> keeps that order in
/// the source that declares the type, so a field name and its place in the record are read together.
/// </remarks>
public interface IJsonWritable
{
    /// <summary>
    /// Writes this value in declaration order. The value is whole: when it is an object it writes its
    /// own braces and every member between them, exactly as it would appear in a record, so a caller
    /// can write it anywhere a JSON value is expected -- after a property name, or as one element of
    /// a dictionary an arm has not finished building.
    /// </summary>
    void WriteTo(Utf8JsonWriter writer);
}
