using System.Globalization;
using System.Text.Json;
using WinForward.E2E.Cli;

namespace WinForward.E2E.Target;

/// <summary>
/// The target's audit note for the deterministic response delay, written beside its ledger. The
/// ledger contract is frozen: a calibration control reads this sidecar instead of a published ledger
/// key, and the analyzer is never given a dependency on it.
/// </summary>
internal static class TargetInjectionFile
{
    /// <summary>The suffix appended to the ledger's base name, e.g. <c>target-ledger.injection.json</c>.</summary>
    private const string Suffix = ".injection.json";

    internal static string PathFor(string ledgerPath) => Path.ChangeExtension(ledgerPath, Suffix);

    internal static async ValueTask WriteAsync(TargetOptions options)
    {
        await using var buffer = new MemoryStream();
        await using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("injectDelayMs", options.InjectDelayMs);
            writer.WriteString("label", options.Label);
            writer.WriteString("ledger", Path.GetFullPath(options.LedgerPath));
            writer.WriteString("writtenUtc", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            writer.WriteEndObject();
        }

        await File.WriteAllBytesAsync(PathFor(options.LedgerPath), buffer.ToArray()).ConfigureAwait(false);
    }
}
