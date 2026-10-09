using System.Text.Json;
using WinForward.E2E.Analysis.Checks;

namespace WinForward.E2E.Analysis.Model;

/// <summary>
/// Reads a number, a string, a boolean or a histogram out of one arm's <c>result</c> record, with the
/// reason a reading failed carried beside it so every cell that cannot be computed can print why.
/// </summary>
/// <remarks>
/// <para><b>A reading is a value and a reason, never a default.</b> Every caller in the reference
/// receives a <c>(value, why)</c> pair; the reason is what turns into <c>n/a (reason)</c>, and a
/// missing counter and a counter whose UDP identity was violated are two different reasons.</para>
/// <para><b>A violated UDP identity blocks the arm's UDP fields.</b> Those fields are excluded from
/// every aggregate rather than averaged over, which is stated in the reading's own reason.</para>
/// </remarks>
internal static class ArmAccess
{
    /// <summary>The reason a rate the harness wrote as JSON null has no value.</summary>
    internal const string NullRateReason = "null rate: the harness wrote null because the denominator was zero";

    /// <summary>One arm's <c>result</c> record, or the reason the arm has none.</summary>
    internal static (JsonElement? Result, string? Reason) ArmResult(ClientRun run, string armName)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(armName);

        var arm = run.Arms.Find(armName);
        if (arm is null)
        {
            return (null, $"no {armName} arm");
        }

        return arm.Result is { } result ? (result, null) : (null, $"{armName} arm has no result record");
    }

    /// <summary>A numeric metric at a <c>/</c> path inside one arm's result.</summary>
    internal static Measured<double?> Number(ClientRun run, string armName, string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        var (result, why) = ArmResult(run, armName);
        if (result is null)
        {
            return new(Value: null, Reason: why);
        }

        var (blocked, detail) = IdentityChecks.BlockedPrefix(run, armName);
        if (blocked is not null && path.StartsWith(blocked, StringComparison.Ordinal))
        {
            return new(Value: null, Reason: $"harness error: UDP identity violated ({detail}); excluded rather than averaged over");
        }

        var (present, value) = JsonValue.DigPresent(result, path);
        var dotted = path.Replace(JsonValue.Separator, '.');
        if (!present)
        {
            return new(Value: null, Reason: $"{armName} {dotted} missing");
        }

        if (value is { ValueKind: JsonValueKind.Null })
        {
            return new(Value: null, Reason: NullRateReason);
        }

        var number = JsonValue.AsNumber(value);
        return number is not null ? new(Value: number, Reason: null) : new(Value: null, Reason: $"{armName} {dotted} is not a number");
    }

    /// <summary>A string (or any non-null scalar) at a <c>/</c> path inside one arm's result.</summary>
    internal static Measured<JsonElement?> Text(ClientRun run, string armName, string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        var (result, why) = ArmResult(run, armName);
        if (result is null)
        {
            return new(Value: null, Reason: why);
        }

        var (present, value) = JsonValue.DigPresent(result, path);
        if (!present || value is null or { ValueKind: JsonValueKind.Null })
        {
            return new(Value: null, Reason: $"{armName} {path.Replace(JsonValue.Separator, '.')} missing");
        }

        return new(Value: value, Reason: null);
    }

    /// <summary>
    /// A boolean metric — <c>metrics.survivedIdle</c> is the only one — which is a fact about the pass
    /// rather than a number that could be averaged.
    /// </summary>
    /// <remarks>
    /// A boolean is not read through the UDP identity gate: the gate exists to keep a violated identity
    /// from being averaged over, and a flag is never averaged.
    /// </remarks>
    /// <param name="run">The run the arm belongs to.</param>
    /// <param name="armName">The arm to read.</param>
    /// <param name="path">The <c>/</c>-separated path inside the arm's result.</param>
    internal static Measured<bool?> Flag(ClientRun run, string armName, string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        var (result, why) = ArmResult(run, armName);
        if (result is null)
        {
            return new(Value: null, Reason: why);
        }

        var dotted = path.Replace(JsonValue.Separator, '.');
        var (present, value) = JsonValue.DigPresent(result, path);
        if (!present)
        {
            return new(Value: null, Reason: $"{armName} {dotted} missing");
        }

        return value?.ValueKind switch
        {
            JsonValueKind.True => new(Value: true, Reason: null),
            JsonValueKind.False => new(Value: false, Reason: null),
            _ => new(Value: null, Reason: $"{armName} {dotted} is not a boolean"),
        };
    }

    /// <summary>
    /// One counter divided by another, with a zero denominator reported as its own reason rather than as
    /// an infinity or a zero.
    /// </summary>
    /// <param name="run">The run the arm belongs to.</param>
    /// <param name="armName">The arm to read.</param>
    /// <param name="numeratorPath">The <c>/</c>-separated path of the numerator.</param>
    /// <param name="denominatorPath">The <c>/</c>-separated path of the denominator.</param>
    internal static Measured<double?> Ratio(
        ClientRun run,
        string armName,
        string numeratorPath,
        string denominatorPath)
    {
        var (numerator, why) = Number(run, armName, numeratorPath);
        if (numerator is null)
        {
            return new(Value: null, Reason: why);
        }

        var (denominator, denominatorWhy) = Number(run, armName, denominatorPath);
        if (denominator is null)
        {
            return new(Value: null, Reason: denominatorWhy);
        }

#pragma warning disable S1244 // An exact zero: a near-zero denominator still yields a ratio.
        return denominator.Value == 0.0
#pragma warning restore S1244
            ? new(Value: null, Reason: $"{armName} {denominatorPath.Replace(JsonValue.Separator, '.')} is zero")
            : new(Value: numerator.Value / denominator.Value, Reason: null);
    }

    /// <summary>One histogram statistic, straight from the harness's own histogram.</summary>
    internal static Measured<double?> Latency(ClientRun run, string armName, string latencyClass, string stat)
    {
        ArgumentNullException.ThrowIfNull(latencyClass);
        ArgumentNullException.ThrowIfNull(stat);

        var (result, why) = ArmResult(run, armName);
        if (result is null)
        {
            return new(Value: null, Reason: why);
        }

        var histogram = JsonValue.Dig(result, $"latency/{latencyClass}");
        if (histogram is not { ValueKind: JsonValueKind.Object })
        {
            return new(Value: null, Reason: $"{armName} has no {latencyClass} histogram");
        }

        var value = JsonValue.Number(histogram, stat);
        return value is not null
            ? new(Value: value, Reason: null)
            : new(Value: null, Reason: $"{armName} {latencyClass}.{stat} missing");
    }
}
