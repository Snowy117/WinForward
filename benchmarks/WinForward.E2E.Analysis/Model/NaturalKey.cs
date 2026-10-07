namespace WinForward.E2E.Analysis.Model;

/// <summary>
/// The reference's <c>natural_key</c>: a name is split into digit and non-digit runs, a digit run
/// compares as the number it spells and everything else compares as text, so <c>pass2</c> sorts
/// before <c>pass10</c>.
/// </summary>
/// <remarks>
/// The order is not cosmetic: it is the order passes are listed in <c>verdict.json</c>, the order rows
/// are read in, and therefore the order every table's lines are written in. Reproducing it with
/// <see cref="string.CompareOrdinal(string, string)"/> alone would put <c>pass10</c> before
/// <c>pass2</c>.
/// </remarks>
internal static class NaturalKey
{
    /// <summary>Orders two names the way the reference's key does.</summary>
    internal static int Compare(string left, string right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        var leftIndex = 0;
        var rightIndex = 0;
        while (leftIndex < left.Length && rightIndex < right.Length)
        {
            var leftDigit = char.IsAsciiDigit(left[leftIndex]);
            var rightDigit = char.IsAsciiDigit(right[rightIndex]);
            if (leftDigit && rightDigit)
            {
                var leftEnd = EndOfDigits(left, leftIndex);
                var rightEnd = EndOfDigits(right, rightIndex);
                var digits = CompareDigits(left.AsSpan(leftIndex, leftEnd - leftIndex), right.AsSpan(rightIndex, rightEnd - rightIndex));
                if (digits != 0)
                {
                    return digits;
                }

                leftIndex = leftEnd;
                rightIndex = rightEnd;
                continue;
            }

            if (left[leftIndex] != right[rightIndex])
            {
                return left[leftIndex] < right[rightIndex] ? -1 : 1;
            }

            leftIndex++;
            rightIndex++;
        }

        return (left.Length - leftIndex).CompareTo(right.Length - rightIndex);
    }

    /// <summary>The names in natural-key order; the reference's <c>sorted(..., key=natural_key)</c>.</summary>
    internal static List<string> Sort(IEnumerable<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);

        var sorted = new List<string>(names);
        sorted.Sort(Compare);
        return sorted;
    }

    private static int EndOfDigits(string text, int start)
    {
        var index = start;
        while (index < text.Length && char.IsAsciiDigit(text[index]))
        {
            index++;
        }

        return index;
    }

    /// <summary>
    /// Compares two digit runs as the integers they spell, without converting them: <c>007</c> equals
    /// <c>7</c>, and a run too long for <see cref="long"/> still orders correctly.
    /// </summary>
    private static int CompareDigits(ReadOnlySpan<char> left, ReadOnlySpan<char> right)
    {
        var leftSignificant = left.TrimStart('0');
        var rightSignificant = right.TrimStart('0');
        if (leftSignificant.Length != rightSignificant.Length)
        {
            return leftSignificant.Length < rightSignificant.Length ? -1 : 1;
        }

        return leftSignificant.SequenceCompareTo(rightSignificant);
    }
}
