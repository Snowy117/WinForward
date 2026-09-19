using System.Globalization;

namespace WinForward.Benchmarks.Stability;

internal enum SoakScenario
{
    All,
    Udp,
    Tcp,
    TcpThroughput,
    Footprint,
    Baseline,
    Burst,
    GcSoak,
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
        if (roll < Clean + ClientRst + RelayCancel) return AbortKind.RelayCancel;
        return AbortKind.UpstreamTruncate;
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

    public SoakScenario Scenario { get; init; } = SoakScenario.All;
    public int DurationSeconds { get; init; } = 60;
    public int Pps { get; init; } = 25_000;
    public int PayloadBytes { get; init; } = 512;
    public int Flows { get; init; } = 256;
    public int TcpConcurrency { get; init; } = 64;
    public int TcpTransferBytes { get; init; } = 1_048_576;
    public TcpRelayMode TcpRelayMode { get; init; } = TcpRelayMode.Socks5;
    public AbortMix AbortMix { get; init; } = AbortMix.Default;
    public int Seed { get; init; } = 42;
    public string? OutputPath { get; init; }
    public bool Quick { get; init; }
    public int BurstFlows { get; init; } = 48;
    public int DialDelayMs { get; init; }

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
            case "--flows":
                return options with { Flows = PositiveInt("--flows", Value(args, ref index)) };
            case "--tcp-concurrency":
                return options with { TcpConcurrency = PositiveInt("--tcp-concurrency", Value(args, ref index)) };
            case "--tcp-transfer-bytes":
                return options with { TcpTransferBytes = PositiveInt("--tcp-transfer-bytes", Value(args, ref index)) };
            case "--tcp-relay-mode":
                return options with { TcpRelayMode = ParseTcpRelayMode(Value(args, ref index)) };
            case "--abort-mix":
                return options with { AbortMix = AbortMix.Parse(Value(args, ref index)) };
            case "--seed":
                return options with { Seed = AnyInt("--seed", Value(args, ref index)) };
            case "--output":
                return options with { OutputPath = Value(args, ref index) };
            case "--burst-flows":
                return options with { BurstFlows = PositiveInt("--burst-flows", Value(args, ref index)) };
            case "--dial-delay-ms":
                return options with { DialDelayMs = AtLeast("--dial-delay-ms", Value(args, ref index), 0) };
            default:
                throw new ArgumentException($"Unknown stability argument '{args[index]}'.", nameof(args));
        }
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
        "gc-soak" or "gcsoak" => SoakScenario.GcSoak,
        _ => throw new ArgumentException($"Unknown scenario '{raw}'; expected all, udp, udpburst, tcp, tcpthroughput, footprint, baseline, or gc-soak.", nameof(raw)),
    };

    private static TcpRelayMode ParseTcpRelayMode(string raw) => raw.ToLowerInvariant() switch
    {
        "socks5" => TcpRelayMode.Socks5,
        "bare" => TcpRelayMode.Bare,
        _ => throw new ArgumentException($"Unknown TCP relay mode '{raw}'; expected socks5 or bare.", nameof(raw)),
    };

    private static int PositiveInt(string name, string raw) =>
        int.TryParse(raw, CultureInfo.InvariantCulture, out var value) && value > 0 ? value : throw new ArgumentException($"{name} must be a positive integer.", nameof(raw));

    private static int AtLeast(string name, string raw, int minimum) =>
        int.TryParse(raw, CultureInfo.InvariantCulture, out var value) && value >= minimum ? value : throw new ArgumentException(string.Create(CultureInfo.InvariantCulture, $"{name} must be an integer >= {minimum}."), nameof(raw));

    private static int AnyInt(string name, string raw) =>
        int.TryParse(raw, CultureInfo.InvariantCulture, out var value) ? value : throw new ArgumentException($"{name} must be an integer.", nameof(raw));
}
