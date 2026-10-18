using System.Text.Json;
using WinForward.E2E.Analysis.Json;

namespace WinForward.E2E.Analysis.Release;

/// <summary>Reads the calibration's <c>coverage.json</c> beside the thresholds file, when it is there.</summary>
/// <remarks>
/// <b>Coverage is calibration evidence, not a thresholds key.</b> The file is optional by design: a
/// pre-registered band can be written before the coverage simulation is run, and a release verdict that
/// refused the campaign for a missing simulation would invert the dependency. The verdict publishes
/// <see langword="null"/> when the file is absent and a measurement caveat when it is present but unreadable; it
/// never silently substitutes a claimed coverage.
/// </remarks>
internal static class ReleaseCoverage
{
    /// <summary>The file's name beside the thresholds file.</summary>
    private const string FileName = "coverage.json";

    /// <summary>Reads and renders the coverage document.</summary>
    /// <param name="thresholdsPath">The invocation's thresholds path, as it was spelled.</param>
    /// <param name="json">The rendered JSON value, or null when the file is absent.</param>
    /// <param name="error">Why the file could not be read or parsed, when it could not.</param>
    internal static void TryRead(string thresholdsPath, out string? json, out string? error)
    {
        ArgumentException.ThrowIfNullOrEmpty(thresholdsPath);

        json = null;
        error = null;
        var directory = Path.GetDirectoryName(Path.GetFullPath(thresholdsPath));
        if (directory is null)
        {
            return;
        }

        var path = Path.Combine(directory, FileName);
        if (!File.Exists(path))
        {
            return;
        }

        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            error = $"release: cannot read '{path}': {exception.Message}";
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            json = Render(document.RootElement, 1);
        }
        catch (JsonException exception)
        {
            error = $"release: '{path}' is not valid JSON: {exception.Message}";
        }
    }

    private static string Render(JsonElement element, int level) => element.ValueKind switch
    {
        JsonValueKind.Object => VerbatimJson.Object(
            level,
            [.. element.EnumerateObject().Select(member => (member.Name, Render(member.Value, level + 1)))]),
        JsonValueKind.Array => VerbatimJson.Array(
            level,
            [.. element.EnumerateArray().Select(item => Render(item, level + 1))]),
        JsonValueKind.String => VerbatimJson.String(element.GetString() ?? string.Empty),
        JsonValueKind.Number => element.GetRawText(),
        JsonValueKind.True => VerbatimJson.Boolean(value: true),
        JsonValueKind.False => VerbatimJson.Boolean(value: false),
        _ => VerbatimJson.Null,
    };
}
