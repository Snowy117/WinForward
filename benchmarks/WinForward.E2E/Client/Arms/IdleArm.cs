namespace WinForward.E2E.Client.Arms;

internal static class IdleArm
{
    internal static async Task<ArmOutcome> RunAsync(ArmContext context)
    {
        var outcome = new ArmOutcome
        {
            Parameters =
            {
                ["seconds"] = context.Spec.Seconds,
                ["traffic"] = "none",
            },
        };

        var startTicks = Clock.Now;
        await Task.Delay(TimeSpan.FromSeconds(context.Spec.Seconds), context.CancellationToken).ConfigureAwait(false);

        outcome.Metrics["elapsedSeconds"] = JsonValue.Round(Clock.ToSeconds(Clock.Now - startTicks));
        outcome.Gates["clientSendLoss"] = 0L;
        outcome.Gates["windowMs"] = 0L;
        outcome.Notes.Add("no traffic is generated; only the 1 Hz resource samples attached to this arm carry information.");
        return outcome;
    }
}
