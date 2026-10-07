using WinForward.E2E.Contracts;
using WinForward.E2E.Contracts.Json;
using WinForward.E2E.Contracts.Metrics;

namespace WinForward.E2E.Client.Arms;

internal static class IdleArm
{
    /// <summary>
    /// An idle arm offers no traffic, so it has no send-side counter to derive this gate from: the
    /// value is zero by construction, not a measured zero. The record cannot say so -- <c>gates</c> is
    /// a name/value map of numbers and has no third state -- so it keeps 0 and the analysis renders
    /// this arm's client send loss as <c>n/a</c> instead of a zero that would read as a measurement.
    /// </summary>
    private const long NoTrafficClientSendLoss = 0;

    internal static async Task<ArmOutcome> RunAsync(ArmContext context)
    {
        var startTicks = Clock.Now;
        await Task.Delay(TimeSpan.FromSeconds(context.Spec.Seconds), context.CancellationToken).ConfigureAwait(false);

        // The metrics object is built from the measurement it publishes, so every required property
        // of the record is filled at the one point that knows the value.
        return new ArmOutcome
        {
            Parameters = new ArmParameters
            {
                Seconds = context.Spec.Seconds,
                Traffic = "none",
            },
            Metrics = new IdleMetrics
            {
                ElapsedSeconds = NumberFormat.Round(Clock.ToSeconds(Clock.Now - startTicks)),
            },
            Gates =
            {
                [ArmKeys.Common.Gates.ClientSendLoss] = NoTrafficClientSendLoss,
                [ArmKeys.Common.Gates.WindowMs] = 0L,
            },
            Notes = { "no traffic is generated; only the 1 Hz resource samples attached to this arm carry information." },
        };
    }
}
