using System.Globalization;
using WinForward.E2E.Contracts;
using WinForward.E2E.Contracts.Json;
using WinForward.E2E.Contracts.Metrics;

namespace WinForward.E2E.Client.Arms;

/// <summary>
/// The <c>"base"</c> kind: the no-product control, which runs the latency arm and then the loss arm back
/// to back inside one record. The type is named for the record it produces -- the control -- while the
/// plan kind keeps the name its plans and records publish.
/// </summary>
internal static class ControlArm
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
                [ArmKeys.Common.Parameters.Seconds] = phaseSeconds * 2,
                [ArmKeys.Common.Parameters.PhaseSeconds] = phaseSeconds,
                [ArmKeys.Common.Parameters.Phases] = s_phases,
                [ArmKeys.Common.Parameters.Latency] = latency.Parameters,
                [ArmKeys.Common.Parameters.Loss] = loss.Parameters,
            },
            Metrics = new ControlMetrics
            {
                ElapsedSeconds = NumberFormat.Round(Clock.ToSeconds(elapsedTicks), 4),

                // LatencyArm always publishes the shared typed record, so the phase contract is the
                // narrowing rather than a check.
                Latency = (LatencyMetrics)latency.Metrics,
                Loss = loss.Metrics,
            },
            Gates =
            {
                [ArmKeys.Common.Gates.ClientSendLoss] = ReadCount(latency.Gates, ArmKeys.Common.Gates.ClientSendLoss) + ReadCount(loss.Gates, ArmKeys.Common.Gates.ClientSendLoss),
                [ArmKeys.Common.Gates.WindowOverflow] = ReadCount(latency.Gates, ArmKeys.Common.Gates.WindowOverflow) + ReadCount(loss.Gates, ArmKeys.Common.Gates.WindowOverflow),
                [ArmKeys.Common.Gates.BacklogDrops] = ReadCount(latency.Gates, ArmKeys.Common.Gates.BacklogDrops) + ReadCount(loss.Gates, ArmKeys.Common.Gates.BacklogDrops),
                [ArmKeys.Common.Gates.SendFailures] = ReadCount(latency.Gates, ArmKeys.Common.Gates.SendFailures) + ReadCount(loss.Gates, ArmKeys.Common.Gates.SendFailures),
                [ArmKeys.Common.Gates.LaneShortfall] = ReadCount(latency.Gates, ArmKeys.Common.Gates.LaneShortfall) + ReadCount(loss.Gates, ArmKeys.Common.Gates.LaneShortfall),
                [ArmKeys.Common.Gates.ScheduleTruncated] = Math.Max(ReadCount(latency.Gates, ArmKeys.Common.Gates.ScheduleTruncated), ReadCount(loss.Gates, ArmKeys.Common.Gates.ScheduleTruncated)),
                [ArmKeys.Common.Gates.InFlightCeilingMs] = Math.Max(ReadMilliseconds(latency.Gates, ArmKeys.Common.Gates.InFlightCeilingMs), ReadMilliseconds(loss.Gates, ArmKeys.Common.Gates.InFlightCeilingMs)),
                [ArmKeys.Common.Gates.WindowMs] = ReadMilliseconds(loss.Gates, ArmKeys.Common.Gates.WindowMs),
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
