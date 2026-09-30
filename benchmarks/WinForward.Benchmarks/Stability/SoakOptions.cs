using System.Globalization;
using WinForward.Configuration;

namespace WinForward.Benchmarks.Stability;

internal enum SoakScenario
{
    All,
    Udp,
    Tcp,
    TcpThroughput,
    TcpChurn,
    Footprint,
    Baseline,
    Burst,
    Churn,
    SessionBudget,
    Scaling,
    Sweep,
    GcSoak,
    Pump,
    Residency,
    Attribution,
}

internal enum TcpRelayMode
{
    Socks5,
    Bare,
}

internal enum AbortKind
{
    Clean,
    ClientRst,
    RelayCancel,
    UpstreamTruncate,
}

internal sealed record AbortMix(int Clean, int ClientRst, int RelayCancel, int UpstreamTruncate)
{
    public static readonly AbortMix Default = new(25, 25, 25, 25);

    public int Total => Clean + ClientRst + RelayCancel + UpstreamTruncate;

    public AbortKind Pick(Random random)
    {
        var roll = random.Next(Total);
        if (roll < Clean) return AbortKind.Clean;
        if (roll < Clean + ClientRst) return AbortKind.ClientRst;
        return roll < Clean + ClientRst + RelayCancel ? AbortKind.RelayCancel : AbortKind.UpstreamTruncate;
    }

    public static AbortMix Parse(string raw)
    {
        var clean = 0;
        var clientRst = 0;
        var relayCancel = 0;
        var upstreamTruncate = 0;
        foreach (var entry in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = entry.IndexOf('=', StringComparison.Ordinal);
            if (separator <= 0)
            {
                throw new ArgumentException($"Invalid abort-mix entry '{entry}'; expected 'name=weight'.", nameof(raw));
            }

            var name = entry[..separator];
            if (!int.TryParse(entry[(separator + 1)..], CultureInfo.InvariantCulture, out var weight) || weight < 0)
            {
                throw new ArgumentException($"Invalid abort-mix weight for '{name}'; expected a non-negative integer.", nameof(raw));
            }

            switch (name)
            {
                case "clean":
                    clean = weight;
                    break;
                case "clientRst":
                    clientRst = weight;
                    break;
                case "relayCancel":
                    relayCancel = weight;
                    break;
                case "upstreamTruncate":
                    upstreamTruncate = weight;
                    break;
                default:
                    throw new ArgumentException($"Unknown abort-mix kind '{name}'; expected clean, clientRst, relayCancel, or upstreamTruncate.", nameof(raw));
            }
        }

        return new AbortMix(clean, clientRst, relayCancel, upstreamTruncate);
    }
}

internal sealed record SoakOptions
{
    /// <summary>
    /// Default duration for the long-form <see cref="SoakScenario.GcSoak"/> scenario. Every other
    /// scenario keeps the shared 60 s default (15 s under <c>--quick</c>); <c>gc-soak</c> is the
    /// only one that must prove hour-scale steady-state behavior, so it resolves its own default
    /// when the caller neither passed <c>--duration</c> nor <c>--quick</c>.
    /// </summary>
    public const int GcSoakDefaultDurationSeconds = 1_800;

    public SoakScenario Scenario { get; private init; } = SoakScenario.All;
    public int DurationSeconds { get; private init; } = 60;
    public int Pps { get; private init; } = 25_000;
    public int PayloadBytes { get; private init; } = 512;
    public int Flows { get; private init; } = 256;

    /// <summary>
    /// Residency census (<c>--udp-flows</c>): the live UDP session population, kept separate from
    /// <see cref="Flows"/> so the TCP relay population and the UDP session population can be sized
    /// independently (they are different resources with different per-unit costs).
    /// </summary>
    public int UdpFlows { get; private init; } = 100;

    public int TcpConcurrency { get; private init; } = 64;
    public int TcpTransferBytes { get; private init; } = 1_048_576;
    public TcpRelayMode TcpRelayMode { get; private init; } = TcpRelayMode.Socks5;
    public AbortMix AbortMix { get; private init; } = AbortMix.Default;
    public int Seed { get; private init; } = 42;
    public string? OutputPath { get; private init; }
    public bool Quick { get; private init; }
    public int BurstFlows { get; private init; } = 48;
    public int DialDelayMs { get; private init; }

    /// <summary>UDP churn waves (<c>--churn-waves</c>): K ≥ 1 fires K waves and reports one row per wave; 0 runs sustained churn for <see cref="DurationSeconds"/> and reports one aggregate row.</summary>
    public int ChurnWaves { get; private init; } = 1;

    /// <summary>UDP session-budget soak (<c>--rate</c>): new flows per second during the churn window.</summary>
    public int Rate { get; private init; } = 20;

    /// <summary>UDP session-budget soak (<c>--capacity</c>): the coordinator's session capacity, constrained to the product's own <c>1..16384</c> range; the validated configuration default (16,384) unless overridden.</summary>
    public int Capacity { get; private init; } = ConfigurationLoader.DefaultUdpSessionCapacity;

    /// <summary>
    /// Sweep probe calibration (<c>--sweep-window-control-ms</c>): when positive, the sweep scenario arms
    /// its in-window flag and then waits this long <em>instead of calling the sweep at all</em>, so the
    /// recorded pause figures can be quoted against a run that performed no product work inside the
    /// window. Zero runs the real sweep. This is the noise-floor control the F3 acceptance evidence needs:
    /// the in-window maximum measures host scheduling and lock queueing, not hold length.
    /// </summary>
    public int SweepWindowControlMs { get; private init; }

    /// <summary>
    /// UDP session-budget soak (<c>--churn-seconds</c>): length of the new-flow churn window. The
    /// default clears the retention ceiling's discrimination minimum at the default rate with room
    /// for sample cadence (20 × 90 = 1,800 cumulative flows against the ceiling 1,240 = 20 ×
    /// (idle 30 s + 2 × sweep 15 s) + margin 40), so the shipped invocation cannot silently record a
    /// vacuous retention claim.
    /// </summary>
    public int ChurnSeconds { get; private init; } = 90;

    /// <summary>UDP session-budget soak (<c>--drain-seconds</c>): length of the no-new-flows drain window after the churn.</summary>
    public int DrainSeconds { get; private init; } = 120;

    /// <summary>
    /// UDP session-budget soak (<c>--require-pooling</c>): fail the run unless the pooling coverage
    /// check was evaluated inside the pool's shared head, so a saturated sample cannot skip the only
    /// pooling-discriminating assertion while the run still reports a pass.
    /// </summary>
    public bool RequirePooling { get; private init; }

    /// <summary>Child server mode (<c>--serve-socks5-udp</c>): host the loopback SOCKS5 UDP server for a parent process instead of running a scenario.</summary>
    public bool ServeSocks5Udp { get; private init; }

    /// <summary>
    /// TCP churn (<c>--attribution-delay-ms</c>): a synthetic per-flow stall on the selected fraction
    /// of new connections, standing in for the pump-thread process attribution (research F8) that only
    /// runs on Windows. Zero disables it.
    /// </summary>
    public int AttributionDelayMs { get; private init; }

    /// <summary>
    /// TCP churn (<c>--attribution-delay-percent</c>): the share of connections that pay
    /// <see cref="AttributionDelayMs"/>, modelling the real rule that attribution runs for the flows a
    /// process rule matches rather than for every flow. The delayed class is reported separately.
    /// </summary>
    public int AttributionDelayPercent { get; private init; } = 5;

    /// <summary>
    /// The F8 acceptance arm (<c>--attribution-cost-ms</c>): the modelled cost of one process
    /// attribution — the system-wide owner-table enumeration plus the process open — charged by the
    /// scenario's attributor. Zero is the control arm, which measures the pipeline with no
    /// attribution cost at all.
    /// </summary>
    public int AttributionCostMs { get; private init; }

    /// <summary>
    /// Scaling-contention probe (<c>--threads</c>): pin one worker count instead of the 1/2/4 sweep, so
    /// a single configuration can be reproduced in isolation.
    /// </summary>
    public int Threads { get; private init; }

    /// <summary>
    /// Scaling-contention probe (<c>--shared-key-percent</c>): share of lookups aimed at a shared key
    /// pool (the reverse-leg traffic every adapter sees), which is the part of the workload that
    /// contends hardest. <c>0</c> runs fully disjoint partitions.
    /// </summary>
    public int SharedKeyPercent { get; private init; } = 10;

    /// <summary>UDP churn against the out-of-process server helper instead of the in-process receiver/server (<c>--socks5-external</c>).</summary>
    public bool Socks5External { get; private init; }

    public static SoakOptions Parse(string[] args)
    {
        var options = new SoakOptions();
        // A scenario with its own long default (gc-soak) applies it only when the caller left the
        // duration unspecified; --duration and --quick both count as pinning it.
        var durationSpecified = false;
        for (var index = 0; index < args.Length; index++)
        {
            options = ApplyArgument(options, args, ref index, ref durationSpecified);
        }

        if (options.AbortMix.Total == 0)
        {
            throw new ArgumentException("The abort mix must have at least one non-zero weight.", nameof(args));
        }

        if (options.Scenario == SoakScenario.GcSoak && !durationSpecified)
        {
            options = options with { DurationSeconds = GcSoakDefaultDurationSeconds };
        }

        return options;
    }

    private static SoakOptions ApplyArgument(SoakOptions options, string[] args, ref int index, ref bool durationSpecified)
    {
        switch (args[index])
        {
            case "--quick":
                durationSpecified = true;
                return options with { Quick = true, DurationSeconds = 15, Pps = 10_000, TcpConcurrency = 16, Flows = 64 };
            case "--scenario":
                return options with { Scenario = ParseScenario(Value(args, ref index)) };
            case "--duration":
                durationSpecified = true;
                return options with { DurationSeconds = PositiveInt("--duration", Value(args, ref index)) };
            case "--pps":
                return options with { Pps = PositiveInt("--pps", Value(args, ref index)) };
            case "--payload-bytes":
                return options with { PayloadBytes = AtLeast("--payload-bytes", Value(args, ref index), 12) };
            case "--flows" or "--udp-flows":
                return ApplyPopulationArgument(options, args, ref index);
            case "--tcp-relay-mode":
                return options with { TcpRelayMode = ParseTcpRelayMode(Value(args, ref index)) };
            case "--abort-mix":
                return options with { AbortMix = AbortMix.Parse(Value(args, ref index)) };
            case "--output":
                return options with { OutputPath = Value(args, ref index) };
            case "--capacity":
                return options with { Capacity = UdpCapacity(Value(args, ref index)) };
            case "--serve-socks5-udp":
                return options with { ServeSocks5Udp = true };
            case "--require-pooling":
                return options with { RequirePooling = true };
            case "--socks5-external":
                return options with { Socks5External = true };
            case "--seed":
                return options with { Seed = AnyInt("--seed", Value(args, ref index)) };
            case "--tcp-concurrency" or "--tcp-transfer-bytes" or "--burst-flows" or "--dial-delay-ms"
                or "--churn-waves" or "--rate" or "--sweep-window-control-ms" or "--churn-seconds" or "--drain-seconds":
                return ApplyNumericArgument(options, args, ref index);
            case "--threads":
                return options with { Threads = PositiveInt("--threads", Value(args, ref index)) };
            case "--attribution-delay-ms" or "--attribution-delay-percent":
                return ApplyAttributionDelay(options, args, ref index);
            case "--attribution-cost-ms":
                return options with { AttributionCostMs = AtLeast("--attribution-cost-ms", Value(args, ref index), 0) };
            case "--shared-key-percent":
                return options with { SharedKeyPercent = AtLeast("--shared-key-percent", Value(args, ref index), 0) };
            default:
                throw new ArgumentException($"Unknown stability argument '{args[index]}'.", nameof(args));
        }
    }

    /// <summary>
    /// The plain non-negative numeric knobs, keyed by name: each stores the same value it parses, so
    /// one arm covers them all without listing fifteen otherwise identical cases.
    /// </summary>
    private static SoakOptions ApplyNumericArgument(SoakOptions options, string[] args, ref int index)
    {
        var name = args[index];
        var positive = name is "--rate" or "--churn-seconds" or "--drain-seconds" or "--tcp-concurrency" or "--tcp-transfer-bytes" or "--burst-flows";
        var value = positive ? PositiveInt(name, Value(args, ref index)) : AtLeast(name, Value(args, ref index), 0);
        return name switch
        {
            "--tcp-concurrency" => options with { TcpConcurrency = value },
            "--tcp-transfer-bytes" => options with { TcpTransferBytes = value },
            "--burst-flows" => options with { BurstFlows = value },
            "--dial-delay-ms" => options with { DialDelayMs = value },
            "--churn-waves" => options with { ChurnWaves = value },
            "--rate" => options with { Rate = value },
            "--sweep-window-control-ms" => options with { SweepWindowControlMs = value },
            "--churn-seconds" => options with { ChurnSeconds = value },
            "--drain-seconds" => options with { DrainSeconds = value },
            _ => throw new ArgumentException($"Unknown stability argument '{name}'.", nameof(args)),
        };
    }

    /// <summary>The paired synthetic-attribution knobs, which share one non-negative parse.</summary>
    private static SoakOptions ApplyAttributionDelay(SoakOptions options, string[] args, ref int index)
    {
        var name = args[index];
        var value = AtLeast(name, Value(args, ref index), 0);
        return string.Equals(name, "--attribution-delay-ms", StringComparison.Ordinal)
            ? options with { AttributionDelayMs = value }
            : options with { AttributionDelayPercent = value };
    }

    /// <summary>
    /// The two population selectors. <c>--flows</c> sizes the TCP side (flow table and relays) and
    /// <c>--udp-flows</c> the UDP side, so the census can size either population without the other.
    /// </summary>
    private static SoakOptions ApplyPopulationArgument(SoakOptions options, string[] args, ref int index)
    {
        var flag = args[index];
        var population = PositiveInt(flag, Value(args, ref index));
        return flag switch
        {
            "--udp-flows" => options with { UdpFlows = population },
            _ => options with { Flows = population },
        };
    }

    private static string Value(string[] args, ref int index)
    {
        if (index + 1 >= args.Length)
        {
            throw new ArgumentException($"Missing value for argument '{args[index]}'.", nameof(args));
        }

        index++;
        return args[index];
    }

    private static SoakScenario ParseScenario(string raw) => raw.ToLowerInvariant() switch
    {
        "all" => SoakScenario.All,
        "udp" => SoakScenario.Udp,
        "tcp" => SoakScenario.Tcp,
        "tcpthroughput" => SoakScenario.TcpThroughput,
        "footprint" => SoakScenario.Footprint,
        "baseline" => SoakScenario.Baseline,
        "udpburst" => SoakScenario.Burst,
        "udpchurn" or "churn" => SoakScenario.Churn,
        "udpsessionbudget" or "sessionbudget" or "budget" => SoakScenario.SessionBudget,
        "gc-soak" or "gcsoak" => SoakScenario.GcSoak,
        "tcpchurn" => SoakScenario.TcpChurn,
        "scaling" or "contention" => SoakScenario.Scaling,
        "sweep" or "sweeppause" => SoakScenario.Sweep,
        "pump" or "pumpidlewake" => SoakScenario.Pump,
        "residency" or "residencycensus" => SoakScenario.Residency,
        "attribution" or "attributionoffpump" => SoakScenario.Attribution,
        _ => throw new ArgumentException($"Unknown scenario '{raw}'; expected all, udp, udpburst, udpchurn, udpsessionbudget, scaling, sweep, pump, residency, attribution, tcp, tcpchurn, tcpthroughput, footprint, baseline, or gc-soak.", nameof(raw)),
    };

    private static TcpRelayMode ParseTcpRelayMode(string raw) => raw.ToLowerInvariant() switch
    {
        "socks5" => TcpRelayMode.Socks5,
        "bare" => TcpRelayMode.Bare,
        _ => throw new ArgumentException($"Unknown TCP relay mode '{raw}'; expected socks5 or bare.", nameof(raw)),
    };

    private static int PositiveInt(string name, string raw) =>
        int.TryParse(raw, CultureInfo.InvariantCulture, out var value) && value > 0 ? value : throw new ArgumentException($"{name} must be a positive integer.", nameof(raw));

    /// <summary>
    /// The coordinator's session capacity, constrained to the product's own range
    /// (<c>ConfigurationLimits</c> accepts 1..16,384, the default being the historical maximum). The
    /// soak's "strictly below <c>--capacity</c>" assertion is vacuous for a capacity the product would
    /// refuse, so the out-of-range value is rejected at parse time instead.
    /// </summary>
    private static int UdpCapacity(string raw) =>
        int.TryParse(raw, CultureInfo.InvariantCulture, out var value) && value is >= 1 and <= ConfigurationLoader.DefaultUdpSessionCapacity
            ? value
            : throw new ArgumentException(string.Create(CultureInfo.InvariantCulture, $"--capacity must be an integer in 1..{ConfigurationLoader.DefaultUdpSessionCapacity}, the product's accepted udpSessionCapacity range."), nameof(raw));

    private static int AtLeast(string name, string raw, int minimum) =>
        int.TryParse(raw, CultureInfo.InvariantCulture, out var value) && value >= minimum ? value : throw new ArgumentException(string.Create(CultureInfo.InvariantCulture, $"{name} must be an integer >= {minimum}."), nameof(raw));

    private static int AnyInt(string name, string raw) =>
        int.TryParse(raw, CultureInfo.InvariantCulture, out var value) ? value : throw new ArgumentException($"{name} must be an integer.", nameof(raw));
}
