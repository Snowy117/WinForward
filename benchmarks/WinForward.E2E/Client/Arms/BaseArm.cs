using System.Globalization;
using WinForward.E2E.Contracts.Json;

namespace WinForward.E2E.Client.Arms;

internal static class BaseArm
{
    // BASE is the no-product control: a latency phase and then a loss phase, back to back. Each
    // phase keeps its own default load; a plan parameter the BASE entry declares overrides both.
    private const int DefaultLatencyRatePerSecond = 20;
    private const int DefaultLatencyPayloadBytes = 120;
    internal const int DefaultLossRatePerSecond = 500;
    private const int DefaultLossPayloadBytes = 200;

    private static readonly string[] s_phases = ["latency", "loss"];

    internal static async Task<ArmOutcome> RunAsync(ArmContext context)
    {
        var spec = context.Spec;
        var phaseSeconds = spec.Seconds;
        var latencyContext = context.WithSpec(LatencyPhaseSpec(spec));
        var lossContext = context.WithSpec(LossPhaseSpec(spec));

        var startTicks = Clock.Now;
        var latency = await LatencyArm.RunAsync(latencyContext).ConfigureAwait(false);
        var loss = await LossArm.RunAsync(lossContext).ConfigureAwait(false);
        var elapsedTicks = Clock.Now - startTicks;

        // The record spans both phases, so the declared seconds must be their sum: a consumer that
        // derives an offered or achieved rate from parameters.seconds would otherwise be short by
        // the whole second phase.
        var outcome = new ArmOutcome
        {
            Parameters =
            {
                ["seconds"] = phaseSeconds * 2,
                ["phaseSeconds"] = phaseSeconds,
                ["phases"] = s_phases,
                ["latency"] = latency.Parameters,
                ["loss"] = loss.Parameters,
            },
            Metrics = new DictionaryMetrics
            {
                ["elapsedSeconds"] = NumberFormat.Round(Clock.ToSeconds(elapsedTicks), 4),
                ["latency"] = latency.Metrics,
                ["loss"] = loss.Metrics,
            },
            Gates =
            {
                ["clientSendLoss"] = ReadCount(latency.Gates, "clientSendLoss") + ReadCount(loss.Gates, "clientSendLoss"),
                ["windowOverflow"] = ReadCount(latency.Gates, "windowOverflow") + ReadCount(loss.Gates, "windowOverflow"),
                ["backlogDrops"] = ReadCount(latency.Gates, "backlogDrops") + ReadCount(loss.Gates, "backlogDrops"),
                ["sendFailures"] = ReadCount(latency.Gates, "sendFailures") + ReadCount(loss.Gates, "sendFailures"),
                ["laneShortfall"] = ReadCount(latency.Gates, "laneShortfall") + ReadCount(loss.Gates, "laneShortfall"),
                ["scheduleTruncated"] = Math.Max(ReadCount(latency.Gates, "scheduleTruncated"), ReadCount(loss.Gates, "scheduleTruncated")),
                ["inFlightCeilingMs"] = Math.Max(ReadMilliseconds(latency.Gates, "inFlightCeilingMs"), ReadMilliseconds(loss.Gates, "inFlightCeilingMs")),
                ["windowMs"] = ReadMilliseconds(loss.Gates, "windowMs"),
            },
        };
        outcome.Notes.Add("base runs the latency arm and then the loss arm back to back inside one record, with no proxifier loaded: it is the harness floor.");
        outcome.Notes.Add(string.Create(
            CultureInfo.InvariantCulture,
            $"base is two {phaseSeconds} s phases, so parameters.seconds is their sum and parameters.latency/parameters.loss carry the effective per-phase parameters; metrics.elapsedSeconds is the measured wall time."));
        outcome.Notes.Add("both phases take every load parameter the plan's BASE entry declares and fall back per phase only where it declares none.");
        outcome.Notes.Add(string.Concat(
            "the latency phase runs the plan's protocol (",
            latencyContext.Spec.Protocol,
            ") and the loss phase always runs udp, so a BASE entry that wants a udp latency floor has to declare protocol."));
        outcome.Notes.AddRange(latency.Notes);
        outcome.Notes.AddRange(loss.Notes);
        return outcome;
    }

    // The latency phase keeps its own default load because it is a probe and the loss phase because
    // it is a flood; a parameter the plan's BASE entry declares overrides both.
    private static ArmSpec LatencyPhaseSpec(ArmSpec spec) => new()
    {
        Name = $"{spec.Name}-latency",
        Kind = "latency",
        Seconds = spec.Seconds,
        RatePerSecond = spec.RatePerSecond > 0 ? spec.RatePerSecond : DefaultLatencyRatePerSecond,
        PayloadBytes = spec.PayloadBytes > 0 ? spec.PayloadBytes : DefaultLatencyPayloadBytes,
        Protocol = spec.Protocol,
        Window = spec.Window,
        Lanes = spec.Lanes,
    };

    private static ArmSpec LossPhaseSpec(ArmSpec spec) => new()
    {
        Name = $"{spec.Name}-loss",
        Kind = "loss",
        Seconds = spec.Seconds,
        RatePerSecond = spec.RatePerSecond > 0 ? spec.RatePerSecond : DefaultLossRatePerSecond,
        PayloadBytes = spec.PayloadBytes > 0 ? spec.PayloadBytes : DefaultLossPayloadBytes,
        Protocol = "udp",
        Window = spec.Window,
        LossWindowMs = spec.LossWindowMs,
    };

    private static long ReadCount(Dictionary<string, object?> gates, string key) =>
        gates.TryGetValue(key, out var value)
            ? value switch
            {
                long number => number,
                double number => (long)number,
                _ => 0,
            }
            : 0;

    private static double ReadMilliseconds(Dictionary<string, object?> gates, string key) =>
        gates.TryGetValue(key, out var value)
            ? value switch
            {
                double number => number,
                long number => number,
                _ => 0,
            }
            : 0;
}
