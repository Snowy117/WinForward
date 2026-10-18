namespace WinForward.E2E.Analysis.Model;

/// <summary>
/// The product-order generator the orchestrator shuffles each pass with: one 31-bit linear
/// congruential stream per pass, consumed by a Fisher-Yates shuffle.
/// </summary>
/// <remarks>
/// <para><b>The replay contract.</b> <c>orchestrator.ps1</c> computes the order a campaign runs in;
/// this class replays it so the analysis can say whether the recorded permutation is the one the
/// recorded seed produces. PowerShell and C# do not share code, so the agreement is pinned by the
/// reference orders in <c>Fixtures/orders/</c>.</para>
/// <para><b>Why these constants.</b> The multiplier stays below 2^31 so every product fits a 64-bit
/// integer in both languages; the modulo keeps the state below 2^31 for the same reason. The draw's low
/// byte is discarded before the modulo because an LCG's low bits are the part with the shortest period.
/// A change to any of these numbers is a new <see cref="Algorithm"/> identifier, not an edit: the
/// identifier is written beside every recorded permutation and compared before the replay is trusted.</para>
/// <para><b>Public for the test project</b>, which pins the reference orders without
/// <c>InternalsVisibleTo</c>; everything else in this assembly stays internal.</para>
/// </remarks>
public static class OrderPermutation
{
    /// <summary>The identifier written into <c>order.json</c> and <c>environment.json</c>.</summary>
    public const string Algorithm = "lcg1103515245-fisher-yates/1";

    private const long Mask = 0x7FFFFFFF;

    private const long Multiplier = 1103515245;

    private const long Increment = 12345;

    /// <summary>The seed a pass's own stream starts from, mixed from the campaign's base seed.</summary>
    /// <param name="baseSeed">The campaign's order seed, from <c>-OrderSeed</c> or <c>-Seed</c>.</param>
    /// <param name="pass">The pass ordinal, starting at 1.</param>
    public static int MixedSeed(int baseSeed, int pass)
    {
        var state = baseSeed & Mask;
        state = (state + (pass * Multiplier)) & Mask;
        state = ((state * Multiplier) + Increment) & Mask;
        state ^= state >> 16;
        return (int)(state & Mask);
    }

    /// <summary>One pass's product order: a Fisher-Yates shuffle of <paramref name="rows"/>.</summary>
    /// <param name="seed">The pass's seed, as <see cref="MixedSeed"/> produced it.</param>
    /// <param name="rows">The rows in the order the campaign declares them.</param>
    public static IReadOnlyList<string> Apply(int seed, IReadOnlyList<string> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        var items = rows.ToArray();
        var state = seed & Mask;
        for (var index = items.Length - 1; index > 0; index--)
        {
            state = ((state * Multiplier) + Increment) & Mask;
            var target = (int)((state >> 8) % (index + 1));
            (items[index], items[target]) = (items[target], items[index]);
        }

        return items;
    }
}
