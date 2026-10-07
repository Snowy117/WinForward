namespace WinForward.E2E.Analysis.Cli;

/// <summary>
/// The analysis's command line: the reference implementation's own options, name for name and
/// default for default, so switching a campaign's invocation between the two implementations is not
/// part of the migration.
/// </summary>
/// <remarks>
/// The defaults are not conveniences: <c>--resamples 10000</c> and <c>--seed 20261006</c> are the
/// numbers every bootstrap interval in <c>verdict.json</c> is drawn with, and
/// <c>--warmup-seconds 5.0</c> is the window every CPU and memory cell discards. The oracle calls
/// both sides without any of them, which is exactly why they must agree (D20.5).
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
        "analyze.py [--raw <dir>] [--out <dir>] [--ledger <path>] [--flat] "
        + "[--warmup-seconds S] [--resamples N] [--seed N]";

    private static readonly string[] s_flags = ["--flat"];

    private static readonly string[] s_valued =
        ["--raw", "--out", "--ledger", "--warmup-seconds", "--resamples", "--seed"];

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
    /// Walks <paramref name="args"/> once. Every refusal names the argument it refuses, on the
    /// <paramref name="error"/> channel the caller already reports usage errors on.
    /// </summary>
    internal static bool TryParse(string[] args, out AnalysisOptions options, out string? error)
    {
        ArgumentNullException.ThrowIfNull(args);

        options = new AnalysisOptions();
        error = null;
        var ledgers = new List<string>();
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

                index++;
                continue;
            }

            if (Array.IndexOf(s_valued, name) < 0)
            {
                error = $"unknown argument '{argument}'";
                return false;
            }

            if (value is null)
            {
                if (++index >= args.Length)
                {
                    error = $"missing value for '{name}'";
                    return false;
                }

                value = args[index];
            }

            if (!Apply(options, name, value, ledgers, out options, out error))
            {
                return false;
            }

            index++;
        }

        options = options with { Ledgers = ledgers };
        return true;
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
            default:
                error = $"unknown argument '{name}'";
                return false;
        }
    }
}
