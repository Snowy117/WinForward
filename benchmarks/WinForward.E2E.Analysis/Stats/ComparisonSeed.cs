using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace WinForward.E2E.Analysis.Stats;

/// <summary>
/// The seed one pairwise comparison's bootstrap draws with: the campaign's <c>--seed</c> plus the low
/// six digits of a stable hash of the comparison's key.
/// </summary>
/// <remarks>
/// <para><b>The seed is derived, not drawn.</b> The key spells the metric and the two row ids, so the
/// same campaign re-analysed derives the same seed for the same comparison and its interval can be
/// recomputed from the command line that produced it. The generator that seed feeds is
/// <see cref="Random"/>, so the intervals are this analysis's own rather than a reproduction of another
/// implementation's; it is the derivation, which the <c>--seed</c> promise rests on, that has to stay
/// put.</para>
/// <para>The type is public because the test project drives it value by value, and the analyzer keeps
/// no <c>InternalsVisibleTo</c>.</para>
/// </remarks>
public static class ComparisonSeed
{
    /// <summary>
    /// The hash a comparison's key is folded with: the first four bytes of the SHA-256 of the UTF-8 text,
    /// read big-endian. It is deterministic across processes, unlike a string hash, which is what lets a
    /// comparison's interval be recomputed from the command line that produced it.
    /// </summary>
    /// <param name="text">The text to hash.</param>
    /// <returns>The hash, as an unsigned 32-bit value.</returns>
    public static uint StableHash(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(text), digest);
        return BinaryPrimitives.ReadUInt32BigEndian(digest);
    }

    /// <summary>
    /// The seed one comparison's bootstrap draws with: the run's base seed plus the low six digits of
    /// <see cref="StableHash"/> over the text the comparison is keyed by.
    /// </summary>
    /// <param name="baseSeed">The <c>--seed</c> the analysis was called with.</param>
    /// <param name="text">The comparison's key, spelled the way the analysis spells it.</param>
    /// <returns>The seed to construct a generator with.</returns>
    public static int DeriveSeed(int baseSeed, string text) => baseSeed + (int)(StableHash(text) % 1000000u);
}
