using System.Globalization;
using System.Text.Json;
using WinForward.E2E.Contracts;

namespace WinForward.E2E.Analysis.Model;

/// <summary>
/// The bridge between the client's own stopwatch and the ledger's wall clock: a run's tick frequency,
/// one arm's tick window, and that window as two UTC instants.
/// </summary>
/// <remarks>
/// <para><b>The frequency is derived, never hardcoded.</b> <c>(endedTicks - startedTicks) / wallSeconds</c>
/// is computed per run, so a machine with a different stopwatch frequency still converts.</para>
/// <para><b>The run's own <c>startedUtc</c> and <c>startedTicks</c> are written next to each other</b>,
/// which is the only offset that turns an arm's tick window into the ledger's wall clock.</para>
/// </remarks>
internal static class RunClocks
{
    /// <summary>The tick frequency the run published, or null when it did not publish enough to derive one.</summary>
    internal static double? TickFrequency(ClientRun run)
    {
        ArgumentNullException.ThrowIfNull(run);

        var started = JsonValue.Number(run.Document, ArmKeys.Run.StartedTicks);
        var ended = JsonValue.Number(run.Document, ArmKeys.Run.EndedTicks);
        var wall = JsonValue.Number(run.Document, ArmKeys.Run.WallSeconds);
        // An exactly-zero wall time is the case to reject rather than a near-zero one: a near-zero wall
        // time still yields a frequency estimate.
#pragma warning disable S1244 // An exact zero: a near-zero wall time still yields a frequency estimate.
        if (started is null || ended is null || wall is null || wall.Value == 0.0)
#pragma warning restore S1244
        {
            return null;
        }

        var ticks = ended.Value - started.Value;
        return ticks > 0.0 ? ticks / wall.Value : null;
    }

    /// <summary>One arm's entry in the run's own <c>arms</c> roster, or null.</summary>
    private static JsonElement? ArmEntry(ClientRun run, string armName)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(armName);

        foreach (var entry in JsonValue.Array(run.Document, ArmKeys.Run.Arms) ?? [])
        {
            if (string.Equals(JsonValue.String(entry, ArmKeys.Run.Arm.Name), armName, StringComparison.Ordinal))
            {
                return entry;
            }
        }

        return null;
    }

    /// <summary>
    /// The tick an arm started at, which is what turns a warmup window in seconds into the cut a
    /// sample's own tick is compared against.
    /// </summary>
    internal static double? ArmStartTicks(ClientRun run, string armName) =>
        JsonValue.Number(ArmEntry(run, armName), ArmKeys.Run.Arm.StartedTicks);

    /// <summary>How long one arm ran, from the run's own tick deltas.</summary>
    internal static double? ArmWindowSeconds(ClientRun run, string armName)
    {
        var entry = ArmEntry(run, armName);
        var frequency = TickFrequency(run);
        var started = JsonValue.Number(entry, ArmKeys.Run.Arm.StartedTicks);
        var ended = JsonValue.Number(entry, ArmKeys.Run.Arm.EndedTicks);
        if (frequency is null || started is null || ended is null)
        {
            return null;
        }

        return (ended.Value - started.Value) / frequency.Value;
    }

    /// <summary>The UTC span one arm's tick window covers, or null when the run cannot be placed in time.</summary>
    internal static (DateTimeOffset Start, DateTimeOffset End)? ArmUtcWindow(ClientRun run, string armName)
    {
        ArgumentNullException.ThrowIfNull(run);

        var entry = ArmEntry(run, armName);
        var frequency = TickFrequency(run);
        var baseUtc = ParseUtc(run.Document, ArmKeys.Run.StartedUtc);
        var baseTicks = JsonValue.Number(run.Document, ArmKeys.Run.StartedTicks);
        var started = JsonValue.Number(entry, ArmKeys.Run.Arm.StartedTicks);
        var ended = JsonValue.Number(entry, ArmKeys.Run.Arm.EndedTicks);
        if (frequency is null || baseUtc is null || baseTicks is null || started is null || ended is null)
        {
            return null;
        }

        return (
            baseUtc.Value.AddSeconds((started.Value - baseTicks.Value) / frequency.Value),
            baseUtc.Value.AddSeconds((ended.Value - baseTicks.Value) / frequency.Value));
    }

    /// <summary>The run's own label, when it is a string.</summary>
    internal static string? Label(ClientRun run) => JsonValue.String(run.Document, ArmKeys.Run.Label);

    /// <summary>
    /// One ISO-8601 UTC stamp as the reference parses it, truncated to microseconds: a stamp with no
    /// offset is UTC, and a fractional part longer than the reference keeps is cut rather than rounded.
    /// </summary>
    private static DateTimeOffset? ParseUtc(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        if (!DateTimeOffset.TryParse(
                text,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind | DateTimeStyles.AssumeUniversal,
                out var stamp))
        {
            return null;
        }

        var ticks = stamp.UtcTicks - (stamp.UtcTicks % 10);
        return new DateTimeOffset(ticks, TimeSpan.Zero);
    }

    /// <summary>One ISO-8601 UTC stamp out of a record's own member.</summary>
    internal static DateTimeOffset? ParseUtc(JsonElement? element, string name) => ParseUtc(JsonValue.String(element, name));
}
