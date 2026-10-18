using System.Globalization;
using System.Text;

namespace WinForward.E2E.Analysis.Release;

/// <summary>Which of a campaign tree's passes a release run measures, and whether that is enough to decide anything.</summary>
/// <param name="Total">How many numbered pass directories the tree holds.</param>
/// <param name="Discarded">The pass numbers the run removes before any metric is computed; empty when it removes none.</param>
/// <param name="Measured">The pass numbers that contribute a value, in ascending order.</param>
/// <param name="N">How many passes contribute.</param>
/// <param name="Required">How many measured passes the release rules demand.</param>
/// <param name="Insufficient">Whether <paramref name="N"/> is below <paramref name="Required"/>.</param>
/// <remarks>
/// <b>This is a selection, not a decision.</b> A short campaign is recorded as insufficient rather than
/// refused, because the release writer — not the input reader — owns what a short campaign can still
/// say; the legacy path keeps every pass, discarded one included, exactly as it always did.
/// </remarks>
public sealed record ReleasePasses(
    int Total,
    IReadOnlyList<int> Discarded,
    IReadOnlyList<int> Measured,
    int N,
    int Required,
    bool Insufficient)
{
    /// <summary>
    /// Maps a campaign's pass directories onto the measuring set: <c>passN</c> becomes <c>N</c>, the
    /// discarded pass is removed, and the remainder is counted against the required floor.
    /// </summary>
    /// <param name="passIds">The campaign's pass ids, in any order.</param>
    /// <param name="discardPass">The pass number to remove, or 0 for none.</param>
    /// <param name="required">The measured-pass floor the release rules demand.</param>
    /// <param name="passes">The selection, when the tree can be read.</param>
    /// <param name="error">Why it cannot be, when it cannot.</param>
    /// <returns>True when every pass is numbered and the discarded pass, if any, exists.</returns>
    public static bool TrySelect(
        IReadOnlyList<string> passIds,
        int discardPass,
        int required,
        out ReleasePasses? passes,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(passIds);

        passes = null;
        error = null;
        if (required < ReleaseOptions.MinimumMinPasses)
        {
            error = string.Create(
                CultureInfo.InvariantCulture,
                $"passes: 'minPasses' is {required}, below the minimum of {ReleaseOptions.MinimumMinPasses}");
            return false;
        }

        var numbers = new List<int>(passIds.Count);
        var seen = new HashSet<int>();
        foreach (var passId in passIds)
        {
            if (!TryNumber(passId, out var number))
            {
                error = $"passes: '{passId}' is not a numbered pass directory (pass<N>)";
                return false;
            }

            if (!seen.Add(number))
            {
                error = string.Create(CultureInfo.InvariantCulture, $"passes: pass {number} is listed twice");
                return false;
            }

            numbers.Add(number);
        }

        if (numbers.Count == 0)
        {
            error = "passes: the campaign tree holds no pass directories";
            return false;
        }

        numbers.Sort();
        if (discardPass != 0 && !seen.Contains(discardPass))
        {
            error = string.Create(
                CultureInfo.InvariantCulture,
                $"passes: 'discardPass' is {discardPass}, which is not a pass in the campaign tree ({Numbers(numbers)})");
            return false;
        }

        var measured = discardPass == 0 ? numbers : [.. numbers.Where(number => number != discardPass)];
        passes = new ReleasePasses(
            Total: numbers.Count,
            Discarded: discardPass == 0 ? [] : [discardPass],
            Measured: measured,
            N: measured.Count,
            Required: required,
            Insufficient: measured.Count < required);
        return true;
    }

    /// <summary>One <c>passN</c> directory's number, when it is spelled that way and above zero.</summary>
    private static bool TryNumber(string passId, out int number)
    {
        number = 0;
        if (!passId.StartsWith("pass", StringComparison.Ordinal))
        {
            return false;
        }

        var digits = passId.AsSpan(4);
        if (digits.IsEmpty
            || !int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out number)
            || number < 1)
        {
            number = 0;
            return false;
        }

        return true;
    }

    private static string Numbers(List<int> numbers)
    {
        var text = new StringBuilder();
        for (var index = 0; index < numbers.Count; index++)
        {
            if (index > 0)
            {
                text.Append(", ");
            }

            text.Append(numbers[index].ToString(CultureInfo.InvariantCulture));
        }

        return text.ToString();
    }
}
