using System.Globalization;

namespace WinForward.E2E.Client.Arms;

/// <summary>
/// One kind a plan can declare: the keys an arm of that kind may declare, the validation the shared
/// numeric domains do not cover, and the arm body itself. The table below is the single definition
/// point for both the loader's whitelist and the dispatcher's choice of arm, so a kind cannot exist
/// in one and be missing from the other, and a key an arm reads cannot be rejected by the loader.
/// </summary>
internal sealed class ArmKind
{
    private static readonly string[] s_identityKeys = ["name", "kind", "seconds"];

    private static readonly ArmKind[] s_all =
    [
        new("idle", s_identityKeys, IdleArm.RunAsync),
        new(
            "latency",
            [.. s_identityKeys, "ratePerSecond", "payloadBytes", "protocol", "window", "lanes"],
            LatencyArm.RunAsync),
        new(
            "loss",
            [.. s_identityKeys, "ratePerSecond", "payloadBytes", "window", "lossWindowMs"],
            LossArm.RunAsync,
            ValidateLoss),
        new(
            "mix",
            [.. s_identityKeys, "desktops", "lossWindowMs"],
            MixArm.RunAsync,
            ValidateMix),
        new(
            "dns",
            [.. s_identityKeys, "ratePerSecond", "tcpPercent", "cnameEvery", "dnsPort"],
            DnsArm.RunAsync),
        new(
            "reliability",
            [.. s_identityKeys, "connectionsPerSecond", "modeMix", "expectedBytes"],
            ReliabilityArm.RunAsync,
            ValidateReliability),
        new(
            "throughput",
            [.. s_identityKeys, "streams", "targetBytesPerSecond"],
            ThroughputArm.RunAsync),
        new(
            "persistent",
            [.. s_identityKeys, "expectedBytes", "idleSeconds", "intervalMs", "payloadBytes"],
            PersistentArm.RunAsync),
        new(
            "base",
            [.. s_identityKeys, "ratePerSecond", "payloadBytes", "protocol", "window", "lanes", "lossWindowMs"],
            ControlArm.RunAsync,
            ValidateBase),
    ];

    private static readonly Dictionary<string, ArmKind> s_byName = BuildIndex();

    private readonly Func<ArmSpec, string?>? _validate;

    private ArmKind(string name, string[] keys, Func<ArmContext, Task<ArmOutcome>> run, Func<ArmSpec, string?>? validate = null)
    {
        Name = name;
        Keys = keys;
        Run = run;
        _validate = validate;
    }

    internal string Name { get; }

    /// <summary>
    /// Every key this kind reads, <c>name</c> / <c>kind</c> / <c>seconds</c> included. A key outside
    /// the list is a load error rather than a value the arm silently ignores.
    /// </summary>
    internal string[] Keys { get; }

    internal Func<ArmContext, Task<ArmOutcome>> Run { get; }

    internal static ArmKind? Find(string name) => s_byName.GetValueOrDefault(name);

    internal static string KnownNames() => string.Join(", ", Array.ConvertAll(s_all, static kind => kind.Name));

    private static Dictionary<string, ArmKind> BuildIndex()
    {
        var index = new Dictionary<string, ArmKind>(s_all.Length, StringComparer.Ordinal);
        foreach (var kind in s_all)
        {
            index.Add(kind.Name, kind);
        }

        return index;
    }

    /// <summary>
    /// The kind's own load-time validation, or <see langword="null"/> when the shared numeric domains are all it
    /// needs. The message completes <c>arm '&lt;name&gt;' (kind '&lt;kind&gt;'): …</c>.
    /// </summary>
    internal string? Validate(ArmSpec spec) => _validate?.Invoke(spec);

    private static string? ValidateReliability(ArmSpec spec) =>
        PlanFile.TryParseModeMix(spec.ModeMix, out _, out var error) ? null : error;

    private static string? ValidateLoss(ArmSpec spec) => ValidateOfferedSequences(spec.RatePerSecond, spec.Seconds);

    // Each desktop runs its own tracker at this rate, so the offered sequence count is the per-desktop
    // one, not the sum over desktops.
    private static string? ValidateMix(ArmSpec spec) => ValidateOfferedSequences(MixArm.UdpPacketsPerSecond, spec.Seconds);

    private static string? ValidateBase(ArmSpec spec) =>
        ValidateOfferedSequences(spec.RatePerSecond > 0 ? spec.RatePerSecond : ControlArm.DefaultLossRatePerSecond, spec.Seconds);

    /// <summary>
    /// The three kinds that hold a <see cref="UdpReliabilityTracker"/> must fit the tracker's bounded
    /// sequence space. Latency, dns, reliability, throughput and persistent do not index arrays by
    /// sequence, so they must not be given this check "for consistency".
    /// </summary>
    private static string? ValidateOfferedSequences(double effectiveRatePerSecond, double seconds)
    {
        var highestSequence = (long)Math.Ceiling(effectiveRatePerSecond * seconds);
        return highestSequence <= UdpReliabilityTracker.MaxSequence
            ? null
            : string.Create(
                CultureInfo.InvariantCulture,
                $"the schedule offers sequences up to {highestSequence}, past the tracker's MaxSequence of {UdpReliabilityTracker.MaxSequence}");
    }
}
