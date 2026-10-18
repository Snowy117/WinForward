using WinForward.E2E.Analysis.Release;

namespace WinForward.E2E.Analysis.Cli;

/// <summary>
/// The analysis's command line: the published options, name for name and default for default.
/// </summary>
/// <remarks>
/// The defaults are not conveniences: <c>--resamples 10000</c> and <c>--seed 20261006</c> are the
/// numbers every bootstrap interval in <c>verdict.json</c> is drawn with, and
/// <c>--warmup-seconds 5.0</c> is the window every CPU and memory cell discards. The oracle calls
/// both sides without any of them, which is exactly why they must agree.
/// </remarks>
internal sealed record AnalysisOptions
{
    internal const string DefaultRaw = "../raw";
    internal const string DefaultOut = "..";
    internal const double DefaultWarmupSeconds = 5.0;
    internal const int DefaultResamples = 10000;
    internal const int DefaultSeed = 20261006;

    /// <summary>The minimum pass count an aggregate is allowed to be built from.</summary>
    internal const int DefaultMinPasses = 3;

    internal const string Usage =
        "e2e-analysis [--raw <dir>] [--out <dir>] [--ledger <path>] [--flat] "
        + "[--warmup-seconds S] [--resamples N] [--seed N] "
        + "[--release --thresholds <file> [--min-passes N] [--discard-pass N]]";

    private static readonly string[] s_flags = ["--flat", "--release"];

    private static readonly string[] s_valued =
    [
        "--raw",
        "--out",
        "--ledger",
        "--warmup-seconds",
        "--resamples",
        "--seed",
        "--thresholds",
        "--min-passes",
        "--discard-pass",
    ];

    /// <summary>The campaign tree: <c>&lt;raw&gt;/pass&lt;N&gt;/&lt;row&gt;/</c> plus the ledgers beside it.</summary>
    internal string Raw { get; private init; } = DefaultRaw;

    /// <summary>Where <c>tables.md</c>, <c>verdict.json</c> and <c>plots/</c> go.</summary>
    internal string Out { get; private init; } = DefaultOut;

    /// <summary>The ledgers to read, in order; empty means search the tree the way the reference does.</summary>
    internal IReadOnlyList<string> Ledgers { get; private init; } = [];

    /// <summary>Treat <c>--raw</c>'s immediate subdirectories as the rows of one implicit pass.</summary>
    internal bool Flat { get; private init; }

    internal double WarmupSeconds { get; private init; } = DefaultWarmupSeconds;

    internal int Resamples { get; private init; } = DefaultResamples;

    internal int Seed { get; private init; } = DefaultSeed;

    /// <summary>
    /// The release flags the invocation declared, or a default value for a legacy run. The release
    /// walk owns their rules; the legacy walk below only has to accept their names.
    /// </summary>
    internal ReleaseOptions Release { get; private init; } = new();

    /// <summary>
    /// The options one release dataset read needs: the campaign tree, the calibrated warm-up window and
    /// the bootstrap parameters. The output directory is unused because the read writes nothing, so it is
    /// the tree itself.
    /// </summary>
    /// <param name="raw">The campaign tree to read.</param>
    /// <param name="warmupSeconds">The warm-up window the release inputs were calibrated with.</param>
    /// <param name="resamples">How many bootstrap resamples the comparison will draw.</param>
    /// <param name="seed">The base seed each pair's generator is derived from.</param>
    /// <returns>The options a <c>ReleaseDataset</c> read passes to the campaign loader.</returns>
    internal static AnalysisOptions ForReleaseDataset(string raw, double warmupSeconds, int resamples, int seed) =>
        new()
        {
            Raw = raw,
            Out = raw,
            WarmupSeconds = warmupSeconds,
            Resamples = resamples,
            Seed = seed,
        };

    /// <summary>
    /// Reads the release vector, then walks the legacy arguments once. Every refusal names the argument
    /// it refuses, on the <paramref name="error"/> channel the caller already reports usage errors on.
    /// </summary>
    internal static bool TryParse(string[] args, out AnalysisOptions options, out string? error)
    {
        ArgumentNullException.ThrowIfNull(args);

        options = new AnalysisOptions();
        if (!ReleaseOptions.TryParse(args, out var release, out error)
            || !TryWalk(args, release, out options, out var ledgers, out error))
        {
            return false;
        }

        if (release.Release && options.Flat)
        {
            error = "argument '--release': a release run reads pass directories and cannot be combined with '--flat'";
            return false;
        }

        options = options with { Ledgers = ledgers };
        return true;
    }

    /// <summary>Walks every argument except the release flags, which the release walk already owns.</summary>
    private static bool TryWalk(
        string[] args,
        ReleaseOptions release,
        out AnalysisOptions options,
        out List<string> ledgers,
        out string? error)
    {
        options = new AnalysisOptions();
        ledgers = [];
        error = null;
        var index = 0;
        while (index < args.Length)
        {
            var argument = args[index];
            var separator = argument.IndexOf('=', StringComparison.Ordinal);
            var name = separator >= 0 ? argument[..separator] : argument;
            var value = separator >= 0 ? argument[(separator + 1)..] : null;

            if (Array.IndexOf(s_flags, name) >= 0)
            {
                if (value is not null)
                {
                    error = $"argument '{name}' takes no value";
                    return false;
                }

                if (string.Equals(name, "--flat", StringComparison.Ordinal))
                {
                    options = options with { Flat = true };
                }
                else if (string.Equals(name, "--release", StringComparison.Ordinal))
                {
                    // The release vector was parsed up front, in full; the flag records that this run
                    // asked for it.
                    options = options with { Release = release };
                }

                index++;
                continue;
            }

            if (Array.IndexOf(s_valued, name) < 0)
            {
                error = $"unknown argument '{argument}'";
                return false;
            }

            if (!TryTakeValue(args, ref index, name, value, out value, out error))
            {
                return false;
            }

            if (!Apply(options, name, value, ledgers, out options, out error))
            {
                return false;
            }

            index++;
        }

        return true;
    }

    /// <summary>One valued argument's value: its inline form, or the next argument, or a missing-value refusal.</summary>
    private static bool TryTakeValue(
        string[] args,
        ref int index,
        string name,
        string? inline,
        out string value,
        out string? error)
    {
        error = null;
        if (inline is not null)
        {
            value = inline;
            return true;
        }

        if (++index < args.Length)
        {
            value = args[index];
            return true;
        }

        value = string.Empty;
        error = $"missing value for '{name}'";
        return false;
    }

    /// <summary>Assigns one value, refusing it the way a caller can act on.</summary>
    private static bool Apply(
        AnalysisOptions current,
        string name,
        string value,
        List<string> ledgers,
        out AnalysisOptions options,
        out string? error)
    {
        options = current;
        error = null;
        switch (name)
        {
            case "--raw":
                options = current with { Raw = value };
                return true;
            case "--out":
                options = current with { Out = value };
                return true;
            case "--ledger":
                ledgers.Add(value);
                return true;
            case "--flat":
                options = current with { Flat = true };
                return true;
            case "--warmup-seconds":
                if (!double.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out var warmup))
                {
                    error = $"argument --warmup-seconds: invalid float value: '{value}'";
                    return false;
                }

                options = current with { WarmupSeconds = warmup };
                return true;
            case "--resamples":
                if (!int.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out var resamples))
                {
                    error = $"argument --resamples: invalid int value: '{value}'";
                    return false;
                }

                options = current with { Resamples = resamples };
                return true;
            case "--seed":
                if (!int.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out var seed))
                {
                    error = $"argument --seed: invalid int value: '{value}'";
                    return false;
                }

                options = current with { Seed = seed };
                return true;
            case "--thresholds":
            case "--min-passes":
            case "--discard-pass":
                // ReleaseOptions.TryParse parsed and cross-checked the value; consuming it here keeps
                // the known-argument table in one place.
                return true;
            default:
                error = $"unknown argument '{name}'";
                return false;
        }
    }
}
