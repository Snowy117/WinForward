namespace WinForward.E2E.Client.Arms;

internal static class ArmDispatch
{
    internal static Task<ArmOutcome> RunAsync(ArmContext context) => context.Spec.Kind switch
    {
        "latency" => LatencyArm.RunAsync(context),
        "loss" => LossArm.RunAsync(context),
        "reliability" => ReliabilityArm.RunAsync(context),
        "throughput" => ThroughputArm.RunAsync(context),
        "dns" => DnsArm.RunAsync(context),
        "mix" => MixArm.RunAsync(context),
        "idle" => IdleArm.RunAsync(context),
        "persistent" => PersistentArm.RunAsync(context),
        "base" => BaseArm.RunAsync(context),
        _ => throw new InvalidOperationException($"unknown arm kind '{context.Spec.Kind}'"),
    };
}
