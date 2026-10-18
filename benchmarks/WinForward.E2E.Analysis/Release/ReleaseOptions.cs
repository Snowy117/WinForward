using System.Globalization;

namespace WinForward.E2E.Analysis.Release;

/// <summary>
/// The release invocation's own arguments: whether the run is a release run, the thresholds file that
/// run is judged against, and the two pass-selection overrides.
/// </summary>
/// <remarks>
/// <para><b>One walk owns the release flags.</b> The analysis's legacy walk calls <see cref="TryParse"/>
/// before it reads a single legacy argument, so every combination rule is refused before anything is
/// loaded and the legacy walk only has to keep the names in its known-argument table. A release flag
/// without its companion is a usage error rather than a silently ignored argument: a campaign that
/// spelled <c>--thresholds</c> and forgot <c>--release</c> would otherwise produce the legacy report and
/// look successful.</para>
/// <para><b>A null override means "not given".</b> The two counts keep the difference between an
/// invocation that did not mention them and one that named a value, so the thresholds file's own
/// declaration applies in between and the constants here are the last resort. A release run's
/// pre-registered file is the intended source; the flags exist so a staged tree can be analysed with
/// the file's values restated explicitly.</para>
/// </remarks>
public sealed record ReleaseOptions
{
    /// <summary>How many measured passes a release campaign needs when neither the invocation nor the file says.</summary>
    public const int DefaultMinPasses = 6;

    /// <summary>Which pass is discarded when neither the invocation nor the file says.</summary>
    public const int DefaultDiscardPass = 0;

    /// <summary>The floor a measured pass count has to clear before an interval can be drawn.</summary>
    public const int MinimumMinPasses = 2;

    /// <summary>Whether this invocation asked for the release path at all.</summary>
    public bool Release { get; init; }

    /// <summary>The thresholds file the release run is judged against, as the caller spelled it.</summary>
    public string? ThresholdsPath { get; init; }

    /// <summary>The measured-pass floor this invocation declared, or null when it left the choice to the file.</summary>
    public int? MinPasses { get; init; }

    /// <summary>The pass this invocation declared discarded, or null when it left the choice to the file.</summary>
    public int? DiscardPass { get; init; }

    /// <summary>
    /// Reads the release flags out of an argument vector, refusing every combination the release path
    /// cannot honour.
    /// </summary>
    /// <param name="args">The process arguments, walked in order; arguments that are not release flags are skipped.</param>
    /// <param name="options">The parsed release options, when the vector is accepted.</param>
    /// <param name="error">The refusal, when the vector is not.</param>
    /// <returns>True when the release flags and their combinations were accepted.</returns>
    public static bool TryParse(string[] args, out ReleaseOptions options, out string? error)
    {
        ArgumentNullException.ThrowIfNull(args);

        options = new ReleaseOptions();
        if (!TryWalk(args, out var release, out var thresholds, out var minPasses, out var discardPass, out error)
            || !TryCheckCombinations(release, thresholds, minPasses, discardPass, out error))
        {
            return false;
        }

        options = new ReleaseOptions
        {
            Release = release,
            ThresholdsPath = thresholds,
            MinPasses = minPasses,
            DiscardPass = discardPass,
        };
        return true;
    }

    private static bool TryWalk(
        string[] args,
        out bool release,
        out string? thresholds,
        out int? minPasses,
        out int? discardPass,
        out string? error)
    {
        release = false;
        thresholds = null;
        minPasses = null;
        discardPass = null;
        error = null;
        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            var separator = argument.IndexOf('=', StringComparison.Ordinal);
            var name = separator >= 0 ? argument[..separator] : argument;
            var value = separator >= 0 ? argument[(separator + 1)..] : null;
            switch (name)
            {
                case "--release":
                    if (value is not null)
                    {
                        error = "argument '--release' takes no value";
                        return false;
                    }

                    release = true;
                    break;
                case "--thresholds":
                case "--min-passes":
                case "--discard-pass":
                    if (!TryTakeValue(args, ref index, name, value, out var parsed, out error)
                        || !TryApply(name, parsed, ref thresholds, ref minPasses, ref discardPass, out error))
                    {
                        return false;
                    }

                    break;
            }
        }

        return true;
    }

    private static bool TryCheckCombinations(
        bool release,
        string? thresholds,
        int? minPasses,
        int? discardPass,
        out string? error)
    {
        error = null;
        if (thresholds is not null && !release)
        {
            error = "argument '--thresholds' requires '--release'";
            return false;
        }

        if (release && thresholds is null)
        {
            error = "argument '--release' requires '--thresholds <file>'";
            return false;
        }

        if (minPasses is not null && !release)
        {
            error = "argument '--min-passes' is release-only and requires '--release'";
            return false;
        }

        if (discardPass is not null && !release)
        {
            error = "argument '--discard-pass' is release-only and requires '--release'";
            return false;
        }

        return true;
    }

    /// <summary>One release flag's value: its inline form, or the next argument, or a missing-value refusal.</summary>
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

    private static bool TryApply(
        string name,
        string value,
        ref string? thresholds,
        ref int? minPasses,
        ref int? discardPass,
        out string? error)
    {
        switch (name)
        {
            case "--thresholds":
                if (value.Length == 0)
                {
                    error = "argument '--thresholds': the path is empty";
                    return false;
                }

                thresholds = value;
                error = null;
                return true;
            case "--min-passes":
                if (!TryCount(value, name, MinimumMinPasses, out var minimum, out error))
                {
                    return false;
                }

                minPasses = minimum;
                return true;
            case "--discard-pass":
                if (!TryCount(value, name, 0, out var discarded, out error))
                {
                    return false;
                }

                discardPass = discarded;
                return true;
            default:
                error = $"unknown argument '{name}'";
                return false;
        }
    }

    private static bool TryCount(string value, string name, int minimum, out int count, out string? error)
    {
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out count))
        {
            error = $"argument {name}: invalid int value: '{value}'";
            return false;
        }

        if (count >= minimum)
        {
            error = null;
            return true;
        }

        error = string.Create(
            CultureInfo.InvariantCulture,
            $"argument {name}: {count} is below the minimum of {minimum}");
        return false;
    }
}
