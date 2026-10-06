using WinForward.E2E.Contracts;
using WinForward.E2E.Contracts.Json;
using WinForward.E2E.Contracts.Metrics;

namespace WinForward.E2E.Client.Arms;

internal static class IdleArm
{
    internal static async Task<ArmOutcome> RunAsync(ArmContext context)
    {
        var startTicks = Clock.Now;
        await Task.Delay(TimeSpan.FromSeconds(context.Spec.Seconds), context.CancellationToken).ConfigureAwait(false);

        // The metrics object is built from the measurement it publishes, so every required property
        // of the record is filled at the one point that knows the value.
        return new ArmOutcome
        {
            Parameters =
            {
                [ArmKeys.Common.Parameters.Seconds] = context.Spec.Seconds,
                [ArmKeys.Common.Parameters.Traffic] = "none",
            },
            Metrics = new IdleMetrics
            {
                ElapsedSeconds = NumberFormat.Round(Clock.ToSeconds(Clock.Now - startTicks)),
            },
            Gates =
            {
                [ArmKeys.Common.Gates.ClientSendLoss] = 0L,
                [ArmKeys.Common.Gates.WindowMs] = 0L,
            },
            Notes = { "no traffic is generated; only the 1 Hz resource samples attached to this arm carry information." },
        };
    }
}
