using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;

namespace WinForward.Configuration;

/// <summary>One configuration layer that was loaded, in application order: later wins.</summary>
public sealed record ConfigurationSource(string Name, string Path);

/// <summary>What loading the configuration documents produced.</summary>
public enum ConfigurationLoadOutcome
{
    /// <summary>The effective configuration parsed and validated.</summary>
    Loaded,

    /// <summary>No source file exists; the caller reports a usage error.</summary>
    NoSource,

    /// <summary>A source could not be read or parsed, or the effective configuration is invalid.</summary>
    Invalid,
}

/// <summary>
/// The effective configuration and the layers it was merged from. <see cref="Configuration"/> is
/// built from the merged document too, so the logging section and the WinForward section are always
/// the same document's — there is no second reading of any file.
/// </summary>
public sealed record LoadedConfiguration(
    IConfigurationRoot Configuration,
    IReadOnlyList<ConfigurationSource> Sources,
    ValidatedConfiguration Validated);

public static partial class ConfigurationLoader
{
    /// <summary>WinForward's first layer, resolved beside the executable and owned by the operator.</summary>
    public const string AppSettingsFileName = "appsettings.json";

    /// <summary>The option that supplies the second layer.</summary>
    public const string ConfigOptionName = "--config";

    /// <summary>
    /// Loads the two optional layers as JSON documents, merges them in application order (objects
    /// recursively, arrays and scalars replacing wholesale, an explicit <see langword="null"/> preserved as
    /// null), validates the effective <see cref="SectionName"/> section with the strict reader, and
    /// binds the logging section from the very same merged document.
    /// </summary>
    public static ConfigurationLoadOutcome TryLoad(string baseDirectory, string? configPath, out LoadedConfiguration? loaded, out IReadOnlyList<ConfigDiagnostic> diagnostics)
    {
        ArgumentException.ThrowIfNullOrEmpty(baseDirectory);
        loaded = null;

        var layers = new List<(string Name, string Path, bool Optional)>(2)
        {
            // The exe-directory file is optional by design: an operator who never wrote one keeps
            // the code defaults. A path the operator named is not optional, and is reported below.
            (AppSettingsFileName, Path.Combine(baseDirectory, AppSettingsFileName), true),
        };
        if (configPath is { Length: > 0 }) layers.Add((ConfigOptionName, Path.GetFullPath(configPath), false));

        JsonObject? document = null;
        ConfigDiagnostic? missingNamedSource = null;
        var sources = new List<ConfigurationSource>(layers.Count);
        foreach (var (name, path, optional) in layers)
        {
            if (!File.Exists(path))
            {
                if (!optional) missingNamedSource = new ConfigDiagnostic(name, $"Configuration file '{path}' was not found.");
                continue;
            }

            if (!TryReadLayer(name, path, out var layer, out var readError))
            {
                diagnostics = [readError];
                return ConfigurationLoadOutcome.Invalid;
            }

            document = Merge(document, layer);
            sources.Add(new ConfigurationSource(name, path));
        }

        if (document is null)
        {
            diagnostics = [];
            return ConfigurationLoadOutcome.NoSource;
        }

        // A named path that resolves to nothing is a configuration error even though another layer
        // loaded: the operator's rules would otherwise silently differ from the ones they wrote.
        if (missingNamedSource is not null)
        {
            diagnostics = [missingNamedSource];
            return ConfigurationLoadOutcome.Invalid;
        }

        if (!TryReadEffective(document, out var validated, out diagnostics)) return ConfigurationLoadOutcome.Invalid;

        loaded = new LoadedConfiguration(BuildConfiguration(document), sources, validated);
        diagnostics = [];
        return ConfigurationLoadOutcome.Loaded;
    }

    /// <summary>
    /// Validates the merged document's <see cref="SectionName"/> section: the wrapper must be there,
    /// and the strict reader and the validator must accept what it holds.
    /// </summary>
    private static bool TryReadEffective(JsonObject document, [NotNullWhen(true)] out ValidatedConfiguration? validated, out IReadOnlyList<ConfigDiagnostic> diagnostics)
    {
        validated = null;
        if (!document.TryGetPropertyValue(SectionName, out var section) || section is not JsonObject)
        {
            diagnostics = [new ConfigDiagnostic(SectionName, "No WinForward section. Settings live under a WinForward object and key names are PascalCase, for example WinForward.TcpFlowCapacity.")];
            return false;
        }

        if (!TryParse(section.ToJsonString(), out var dto, out var parseDiagnostics))
        {
            diagnostics = parseDiagnostics;
            return false;
        }

        if (!TryValidate(dto, out validated, out var validationDiagnostics))
        {
            diagnostics = validationDiagnostics;
            return false;
        }

        diagnostics = [];
        return true;
    }

    /// <summary>
    /// Reads one layer. The document must be a JSON object: a top-level array or scalar is not a
    /// configuration, and a duplicate key is rejected here rather than being silently collapsed to
    /// its last spelling.
    /// </summary>
    private static bool TryReadLayer(string name, string path, [NotNullWhen(true)] out JsonObject? layer, out ConfigDiagnostic error)
    {
        layer = null;
        error = null!;
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            error = new ConfigDiagnostic(name, $"Cannot read configuration: {exception.Message}");
            return false;
        }

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(text);
            Materialize(root);
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException)
        {
            error = new ConfigDiagnostic(name, $"Invalid JSON: {exception.Message}");
            return false;
        }

        if (root is not JsonObject parsed)
        {
            error = new ConfigDiagnostic(name, "Configuration must be a JSON object.");
            return false;
        }

        layer = parsed;
        return true;
    }

    /// <summary>
    /// Walks the parsed document so that every object is materialised. The node tree is lazy, and a
    /// duplicate key surfaces only when an object's members are enumerated: without the walk a
    /// document naming one key twice would keep its last spelling in silence.
    /// </summary>
    private static void Materialize(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject document:
                foreach (var property in document) Materialize(property.Value);
                break;
            case JsonArray array:
                foreach (var item in array) Materialize(item);
                break;
        }
    }

    /// <summary>
    /// The merge rule of the layered configuration: two objects merge key by key, and anything else
    /// — an array, a scalar, an explicit null — replaces what the earlier layer had, so an operator
    /// can shorten a rule list rather than only lengthen it.
    /// </summary>
    private static JsonObject Merge(JsonObject? target, JsonObject overlay)
    {
        if (target is null) return (JsonObject)overlay.DeepClone();

        foreach (var property in overlay)
        {
            var merged = target.TryGetPropertyValue(property.Key, out var existing) && existing is JsonObject existingObject && property.Value is JsonObject overlayObject
                ? Merge(existingObject, overlayObject)
                : property.Value?.DeepClone();
            target[property.Key] = merged;
        }

        return target;
    }

    /// <summary>
    /// The one <see cref="IConfiguration"/> MEL binds: the merged document served from memory.
    /// No file is registered as a source, so the logging section cannot pick up a key the
    /// WinForward section did not see.
    /// </summary>
    private static IConfigurationRoot BuildConfiguration(JsonObject document)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(document.ToJsonString()));
        return new ConfigurationBuilder().AddJsonStream(stream).Build();
    }
}
