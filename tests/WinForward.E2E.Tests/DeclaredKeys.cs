using System.Diagnostics.CodeAnalysis;
using System.Reflection;

namespace WinForward.E2E.Tests;

/// <summary>
/// Reads an <c>ArmKeys</c> subtree as the canonical paths it declares, with the same alphabet the
/// published bytes are flattened with (<see cref="JsonPaths"/>): one path per constant, and a
/// constant's value is one whole member name even when it contains a dot (D5/D14.6).
/// </summary>
/// <remarks>
/// Every subtree arrives as a <c>typeof(...)</c> literal. The trim and AOT analyzers only follow a
/// type they can see, so the parameters below carry the member annotations the reflection needs and
/// every call site names one of those literals: an unannotated <see cref="Type"/> would be an IL2075
/// build error under <c>TreatWarningsAsErrors</c> (D14.20). A generic type parameter cannot carry the
/// subtree because C# forbids a static class as a type argument (CS0718), which every ArmKeys shard
/// is.
/// </remarks>
internal static class DeclaredKeys
{
    /// <summary>
    /// The paths declared by the subtree in <paramref name="keysType"/> under
    /// <paramref name="prefix"/>, in declaration order. An empty prefix means the members sit at the
    /// record root, which is how <c>ArmKeys.Common.Record</c> publishes them.
    /// </summary>
    internal static List<string> Under(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicFields)] Type keysType,
        string prefix)
    {
        var paths = new List<string>();
        foreach (var field in keysType.GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (!field.IsLiteral || field.FieldType != typeof(string) || field.GetRawConstantValue() is not string key)
            {
                continue;
            }

            paths.Add(prefix.Length == 0 ? key : $"{prefix}/{key}");
        }

        return paths;
    }

    /// <summary>
    /// How many public properties the record in <paramref name="metricsType"/> declares. Property
    /// order is not read here; the count is what pairs a record with the key set that declares its
    /// members.
    /// </summary>
    internal static int PropertyCount(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] Type metricsType) =>
        metricsType.GetProperties().Length;

    /// <summary>
    /// How many of the record's own properties in <paramref name="metricsType"/> are nullable, i.e.
    /// how many readings publish JSON <see langword="null"/> when they are unknown rather than
    /// leaving their key out.
    /// </summary>
    internal static int NullablePropertyCount(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] Type metricsType) =>
        metricsType.GetProperties().Count(static property => Nullable.GetUnderlyingType(property.PropertyType) is not null);

    /// <summary>
    /// The declared paths against the published ones, in both directions, as the lines a failing
    /// assertion prints: a path that is declared but never written and a path that is written but
    /// never declared are different defects, so they are reported apart rather than as one set
    /// difference.
    /// </summary>
    internal static IEnumerable<string> Differences(string what, IReadOnlyCollection<string> declared, IReadOnlyCollection<string> actual)
    {
        var missing = declared.Except(actual).Order(StringComparer.Ordinal).ToArray();
        var extra = actual.Except(declared).Order(StringComparer.Ordinal).ToArray();
        if (missing.Length == 0 && extra.Length == 0)
        {
            return [];
        }

        return
        [
            $"{what}: {declared.Count} declared path(s), {actual.Count} written",
            $"  declared but not written: {string.Join(", ", missing)}",
            $"  written but not declared: {string.Join(", ", extra)}",
        ];
    }
}
