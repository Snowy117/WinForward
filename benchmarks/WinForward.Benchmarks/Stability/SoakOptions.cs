using System.Globalization;

namespace WinForward.Benchmarks.Stability;

internal enum SoakScenario
{
    All,
    Udp,
    Tcp,
    Footprint,
    Baseline,
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
            var separator = entry.IndexOf('=');
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
    public SoakScenario Scenario { get; init; } = SoakScenario.All;
    public int DurationSeconds { get; init; } = 60;
    public int Pps { get; init; } = 25_000;
    public int PayloadBytes { get; init; } = 512;
    public int Flows { get; init; } = 256;
    public int TcpConcurrency { get; init; } = 64;
    public int TcpTransferBytes { get; init; } = 1_048_576;
    public AbortMix AbortMix { get; init; } = AbortMix.Default;
    public int Seed { get; init; } = 42;
    public string? OutputPath { get; init; }
    public bool Quick { get; init; }

    public static SoakOptions Parse(string[] args)
    {
        var options = new SoakOptions();
        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--quick":
                    options = options with { Quick = true, DurationSeconds = 15, Pps = 10_000, TcpConcurrency = 16, Flows = 64 };
                    break;
                case "--scenario":
                    options = options with { Scenario = ParseScenario(Value(args, ref index)) };
                    break;
                case "--duration":
                    options = options with { DurationSeconds = PositiveInt("--duration", Value(args, ref index)) };
                    break;
                case "--pps":
                    options = options with { Pps = PositiveInt("--pps", Value(args, ref index)) };
                    break;
                case "--payload-bytes":
                    options = options with { PayloadBytes = AtLeast("--payload-bytes", Value(args, ref index), 12) };
                    break;
                case "--flows":
                    options = options with { Flows = PositiveInt("--flows", Value(args, ref index)) };
                    break;
                case "--tcp-concurrency":
                    options = options with { TcpConcurrency = PositiveInt("--tcp-concurrency", Value(args, ref index)) };
                    break;
                case "--tcp-transfer-bytes":
                    options = options with { TcpTransferBytes = PositiveInt("--tcp-transfer-bytes", Value(args, ref index)) };
                    break;
                case "--abort-mix":
                    options = options with { AbortMix = AbortMix.Parse(Value(args, ref index)) };
                    break;
                case "--seed":
                    options = options with { Seed = AnyInt("--seed", Value(args, ref index)) };
                    break;
                case "--output":
                    options = options with { OutputPath = Value(args, ref index) };
                    break;
                default:
                    throw new ArgumentException($"Unknown stability argument '{args[index]}'.", nameof(args));
            }
        }

        if (options.AbortMix.Total == 0)
        {
            throw new ArgumentException("The abort mix must have at least one non-zero weight.", nameof(args));
        }

        return options;
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
        "footprint" => SoakScenario.Footprint,
        "baseline" => SoakScenario.Baseline,
        _ => throw new ArgumentException($"Unknown scenario '{raw}'; expected all, udp, tcp, footprint, or baseline.", nameof(raw)),
    };

    private static int PositiveInt(string name, string raw) =>
        int.TryParse(raw, CultureInfo.InvariantCulture, out var value) && value > 0 ? value : throw new ArgumentException($"{name} must be a positive integer.", nameof(raw));

    private static int AtLeast(string name, string raw, int minimum) =>
        int.TryParse(raw, CultureInfo.InvariantCulture, out var value) && value >= minimum ? value : throw new ArgumentException($"{name} must be an integer >= {minimum}.", nameof(raw));

    private static int AnyInt(string name, string raw) =>
        int.TryParse(raw, CultureInfo.InvariantCulture, out var value) ? value : throw new ArgumentException($"{name} must be an integer.", nameof(raw));
}
