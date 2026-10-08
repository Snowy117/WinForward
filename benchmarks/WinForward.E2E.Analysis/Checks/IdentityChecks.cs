using System.Globalization;
using System.Text.Json;
using WinForward.E2E.Analysis.Json;
using WinForward.E2E.Analysis.Model;

namespace WinForward.E2E.Analysis.Checks;

/// <summary>One per-record assertion's outcome: which arm, which identity, and the detail when it did not hold.</summary>
/// <param name="Arm">The arm whose record was asserted.</param>
/// <param name="Kind">The identity's name, e.g. <c>udp-identity</c>.</param>
/// <param name="Ok">True when it held, false when it did not, null when the record does not publish the fields.</param>
/// <param name="Detail">What did not hold, or what could not be checked.</param>
internal sealed record IdentityCheck(string Arm, string Kind, bool? Ok, string? Detail);

/// <summary>
/// The per-record invariants the reference asserts over a campaign's arms, and the lane witnesses that
/// say whether a multi-lane arm ran every lane it planned.
/// </summary>
/// <remarks>
/// <para><b>Three states, not two.</b> An identity that does not hold is a harness error; one whose
/// fields the record does not publish could not be checked, which is a different statement and is
/// printed as its own caveat rather than as a pass.</para>
/// <para><b>A violated UDP identity blocks the arm's UDP fields.</b> Those fields are excluded from
/// the affected aggregates instead of being averaged over, which is why
/// <see cref="BlockedPrefix"/> is consulted by every UDP reading of such an arm.</para>
/// </remarks>
internal static class IdentityChecks
{
    private const string UdpIdentity = "udp-identity";

    private const string DnsPartition = "dns-partition";

    private const string ScheduledAttempts = "scheduled-attempts";

    /// <summary>The metric prefixes a violated UDP identity makes unreadable, per arm.</summary>
    private static readonly Dictionary<string, string[]> s_blockedPrefixes = new(StringComparer.Ordinal)
    {
        ["LOSS"] = ["metrics/"],
        ["MIX"] = ["metrics/classes/udp/", "metrics/udp.sent", "metrics/udp.lossRate", "metrics/clientSendLoss"],
        ["BASE"] = ["metrics/loss/"],
    };

    /// <summary>Every identity this analysis asserts over one run's arms.</summary>
    internal static List<IdentityCheck> Checks(ClientRun run)
    {
        ArgumentNullException.ThrowIfNull(run);

        var checks = new List<IdentityCheck>();
        foreach (var (armName, prefix) in new[] { ("LOSS", "metrics"), ("MIX", "metrics/classes/udp"), ("BASE", "metrics/loss") })
        {
            var (result, _) = ArmAccess.ArmResult(run, armName);
            if (result is null)
            {
                continue;
            }

            var (ok, detail) = UdpIdentityOf(result.Value, prefix);
            if (ok is not null)
            {
                checks.Add(new IdentityCheck(armName, UdpIdentity, ok, detail));
            }
        }

        foreach (var armName in new[] { "DNS", "DNSALT" })
        {
            var (result, _) = ArmAccess.ArmResult(run, armName);
            if (result is null)
            {
                continue;
            }

            var (ok, detail) = DnsPartitionOf(result.Value);
            if (ok is not null)
            {
                checks.Add(new IdentityCheck(armName, DnsPartition, ok, detail));
            }
        }

        var (reliability, _) = ArmAccess.ArmResult(run, "REL");
        if (reliability is not null)
        {
            var (ok, detail) = ReliabilityInvariantsOf(reliability.Value);
            if (ok is not null)
            {
                checks.Add(new IdentityCheck("REL", ScheduledAttempts, ok, detail));
            }
        }

        return checks;
    }

    /// <summary>The metric prefix a failed UDP identity blocks for one arm, with the identity's detail.</summary>
    internal static (string? Prefix, string? Detail) BlockedPrefix(ClientRun run, string armName)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(armName);

        if (!s_blockedPrefixes.TryGetValue(armName, out var prefixes))
        {
            return (null, null);
        }

        var violated = Checks(run).FirstOrDefault(check =>
            string.Equals(check.Arm, armName, StringComparison.Ordinal)
            && string.Equals(check.Kind, UdpIdentity, StringComparison.Ordinal)
            && check.Ok is false);
        return violated is null ? (null, null) : (prefixes[0], violated.Detail);
    }

    /// <summary>
    /// The lane witnesses of one arm: a zero witness means a lane never ran, so the record must not read
    /// as a smaller aggregate of a complete run. Null when the arm publishes no witnesses.
    /// </summary>
    internal static List<(string Label, bool Ok)>? LaneWitnesses(ClientRun run, string armName)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(armName);

        var (result, _) = ArmAccess.ArmResult(run, armName);
        if (result is null)
        {
            return null;
        }

        var kind = JsonValue.String(result, "kind");
        List<(string Label, bool Ok)> witnesses;
        if (string.Equals(kind, "latency", StringComparison.Ordinal))
        {
            witnesses = LatencyWitnesses(result.Value);
        }
        else
        {
            witnesses = string.Equals(kind, "mix", StringComparison.Ordinal) ? MixWitnesses(result.Value) : [];
        }

        return witnesses.Count > 0 ? witnesses : null;
    }

    private static List<(string Label, bool Ok)> LatencyWitnesses(JsonElement result)
    {
        var witnesses = new List<(string Label, bool Ok)>();
        var planned = JsonValue.AsNumber(JsonValue.Dig(result, "gates/lanesPlanned"));
        var started = JsonValue.AsNumber(JsonValue.Dig(result, "gates/lanesStarted"));
        if (planned is not null)
        {
            witnesses.Add((
                $"gates.lanesStarted == gates.lanesPlanned ({VerbatimNumber.Cell(started, 0)} of {VerbatimNumber.Cell(planned, 0)})",
                started.Equals(planned)));
        }

        if (JsonValue.Dig(result, "metrics/tcp.laneSupplied") is { ValueKind: JsonValueKind.Array } supplied)
        {
            var index = 0;
            foreach (var value in supplied.EnumerateArray())
            {
                var number = JsonValue.AsNumber(value);
                witnesses.Add((
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"tcp.laneSupplied[{index}] > 0 (={VerbatimNumber.Cell(number, 0)})"),
                    (number ?? 0.0) > 0.0));
                index++;
            }
        }

        var udpStarted = JsonValue.AsNumber(JsonValue.Dig(result, "metrics/udp.laneStarted"));
        var protocol = JsonValue.String(JsonValue.Dig(result, "parameters"), "protocol");
        if (udpStarted is not null && protocol is not null && protocol.Contains("udp", StringComparison.Ordinal))
        {
            witnesses.Add((
                $"udp.laneStarted > 0 (={VerbatimNumber.Cell(udpStarted, 0)})",
                udpStarted > 0.0));
        }

        return witnesses;
    }

    private static List<(string Label, bool Ok)> MixWitnesses(JsonElement result)
    {
        var witnesses = new List<(string Label, bool Ok)>();
        if (JsonValue.Dig(result, "metrics/desktops") is not { ValueKind: JsonValueKind.Array } entries)
        {
            return witnesses;
        }

        foreach (var entry in entries.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var desktop = JsonText.Of(JsonValue.Member(entry, "desktop"));
            foreach (var field in new[] { "udp.sent", "pageConnections", "bulkFrames", "dnsSent" })
            {
                var value = JsonValue.Number(entry, field);
                witnesses.Add((
                    $"desktop {desktop} {field} > 0 (={VerbatimNumber.Cell(value, 0)})",
                    (value ?? 0.0) > 0.0));
            }
        }

        return witnesses;
    }

    /// <summary>Every non-zero <c>foreignConnection</c> counter of one run, arm by arm.</summary>
    internal static List<(string Arm, string Label, double Count)> ForeignConnectionSources(ClientRun run)
    {
        ArgumentNullException.ThrowIfNull(run);

        var sources = new List<(string Arm, string Label, double Count)>();
        foreach (var (armName, path, label) in new[]
        {
            ("LOSS", "metrics/foreignConnection", "LOSS.foreignConnection"),
            ("MIX", "metrics/classes/udp/foreignConnection", "MIX.udp.foreignConnection"),
            ("LAT", "metrics/udp.foreignConnection", "LAT.udp.foreignConnection"),
            ("LATLOAD", "metrics/udp.foreignConnection", "LATLOAD.udp.foreignConnection"),
            ("BASE", "metrics/loss/foreignConnection", "BASE.loss.foreignConnection"),
        })
        {
            var value = ArmAccess.Number(run, armName, path).Value;
            if (value is > 0.0)
            {
                sources.Add((armName, label, value.Value));
            }
        }

        var (mix, _) = ArmAccess.ArmResult(run, "MIX");
        var mixTotal = ArmAccess.Number(run, "MIX", "metrics/classes/udp/foreignConnection").Value;
        if (mix is not null && mixTotal is null)
        {
            var desktops = JsonValue.Dig(mix, "metrics/desktops");
            if (desktops is { ValueKind: JsonValueKind.Array } entries)
            {
                foreach (var entry in entries.EnumerateArray())
                {
                    if (entry.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    var value = JsonValue.Number(entry, "udp.foreignConnection");
                    if (value is > 0.0)
                    {
                        sources.Add((
                            "MIX",
                            $"MIX.desktop {JsonText.Of(JsonValue.Member(entry, "desktop"))} udp.foreignConnection",
                            value.Value));
                    }
                }
            }
        }

        return sources;
    }

    private static (bool? Ok, string? Detail) UdpIdentityOf(JsonElement record, string prefix)
    {
        var fields = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var name in new[] { "sent", "arrived", "late", "never", "abandonedAtTeardown", "corruptDatagrams" })
        {
            var (present, value) = JsonValue.DigPresent(record, $"{prefix}/{name}");
            var number = JsonValue.AsNumber(value);
            if (!present || number is null)
            {
                return (null, $"{name} missing from the record");
            }

            fields[name] = number.Value;
        }

        var total = fields["arrived"] + fields["late"] + fields["never"] + fields["abandonedAtTeardown"]
            + fields["corruptDatagrams"];
        if (!total.Equals(fields["sent"]))
        {
            return (false, $"arrived({Int(fields["arrived"])}) + late({Int(fields["late"])}) + never({Int(fields["never"])}) "
                + $"+ abandonedAtTeardown({Int(fields["abandonedAtTeardown"])}) "
                + $"+ corruptDatagrams({Int(fields["corruptDatagrams"])}) = {Int(total)} != sent({Int(fields["sent"])})");
        }

        return (true, null);
    }

    private static (bool? Ok, string? Detail) DnsPartitionOf(JsonElement record)
    {
        var values = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var name in new[] { "sent", "answered", "servfail", "timeout", "other" })
        {
            var number = JsonValue.Number(JsonValue.Dig(record, "metrics"), name);
            if (number is null)
            {
                return (null, $"{name} missing from the record");
            }

            values[name] = number.Value;
        }

        var total = values["answered"] + values["servfail"] + values["timeout"] + values["other"];
        if (!total.Equals(values["sent"]))
        {
            return (false, $"answered({Int(values["answered"])}) + servfail({Int(values["servfail"])}) "
                + $"+ timeout({Int(values["timeout"])}) + other({Int(values["other"])}) "
                + $"= {Int(total)} != sent({Int(values["sent"])})");
        }

        return (true, null);
    }

    private static (bool? Ok, string? Detail) ReliabilityInvariantsOf(JsonElement record)
    {
        var metrics = JsonValue.Dig(record, "metrics");
        var attempts = JsonValue.Number(metrics, "connectAttempts");
        if (attempts is null)
        {
            return (null, "metrics.connectAttempts missing from the record");
        }

        var scheduled = JsonValue.Number(metrics, "scheduledAttempts");
        if (scheduled is null)
        {
            return (null, "metrics.scheduledAttempts not published (older record)");
        }

        if (!scheduled.Value.Equals(attempts.Value))
        {
            return (false,
                $"connectAttempts({Int(attempts.Value)}) != scheduledAttempts({Int(scheduled.Value)}): "
                + "the arm ended with work in flight");
        }

        var outcomes = JsonValue.Dig(record, "metrics/outcomes");
        if (outcomes is not { ValueKind: JsonValueKind.Object } published)
        {
            return (null, "metrics.outcomes missing from the record");
        }

        var total = published.EnumerateObject().Sum(property => JsonValue.AsNumber(property.Value) ?? 0.0);
        return total.Equals(attempts.Value)
            ? (true, null)
            : (false, $"outcomes sum to {Int(total)} != connectAttempts({Int(attempts.Value)})");
    }

    /// <summary>Python's <c>%d</c> on a double: the integer part, toward zero.</summary>
    private static string Int(double value) =>
        ((long)Math.Truncate(value)).ToString(CultureInfo.InvariantCulture);
}
