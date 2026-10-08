using System.Text.Json;
using WinForward.E2E.Analysis.Stats;
using Xunit;

namespace WinForward.E2E.Tests;

/// <summary>
/// Holds the analysis's generator to the numbers CPython produced. The bootstrap draws one value per
/// resample and every interval it produces is compared as text, so a generator that is merely
/// well-distributed would still turn every published CI red; the vector table pins the seed
/// derivation, the twist, the bit extraction and the rejection loop value by value.
/// </summary>
public sealed class AnalyzerRandomGoldenTests
{
    /// <summary>How many draws the table is expected to hold, so a truncated table cannot pass.</summary>
    private const int ExpectedDraws = 600;

    [Fact]
    public void EveryGoldenDrawIsReproducedInOrder()
    {
        using var document = JsonDocument.Parse(
            File.ReadAllText(RepoPaths.AnalyzerGolden("cp-random-vectors.json")));

        var draws = 0;
        var seeds = 0;
        foreach (var entry in document.RootElement.GetProperty("seeds").EnumerateArray())
        {
            var generator = new CpRandom(entry.GetProperty("seed").GetInt64());
            seeds++;

            // The first eight 32-bit words pin `init_by_array` on their own: a wrong key derivation
            // moves them all, and no amount of later sampling hides it.
            foreach (var word in entry.GetProperty("words").EnumerateArray())
            {
                Assert.Equal(word.GetUInt32(), generator.NextUInt32());
            }

            foreach (var draw in entry.GetProperty("bits").EnumerateArray())
            {
                Assert.Equal(
                    draw.GetProperty("value").GetUInt64(),
                    generator.GetRandBits(draw.GetProperty("width").GetInt32()));
                draws++;
            }

            foreach (var draw in entry.GetProperty("ranges").EnumerateArray())
            {
                Assert.Equal(
                    draw.GetProperty("value").GetInt64(),
                    generator.RandBelow(draw.GetProperty("stop").GetInt64()));
                draws++;
            }
        }

        Assert.True(seeds >= 18, $"the vector table holds {seeds} seed(s)");
        Assert.True(draws >= ExpectedDraws, $"the vector table holds {draws} draw(s)");
    }

    [Fact]
    public void TheBoundariesOfTheRejectionLoopAgreeWithTheReference()
    {
        // n = 1 draws one bit and rejects every draw but zero; a power of two rejects half of its
        // draws, and one below or above a power of two is where the width changes. The generator is
        // re-seeded per bound so each answer comes from the same starting state, which is what makes
        // this readable as a table rather than as a sequence.
        Assert.Equal(0, new CpRandom(20261006).RandBelow(1));
        Assert.Equal(1, new CpRandom(20261006).RandBelow(2));
        Assert.Equal(1, new CpRandom(20261006).RandBelow(3));
        Assert.Equal(3, new CpRandom(20261006).RandBelow(4));
        Assert.Equal(3, new CpRandom(20261006).RandBelow(7));
        Assert.Equal(7, new CpRandom(20261006).RandBelow(8));
        Assert.Equal(7, new CpRandom(20261006).RandBelow(9));
        Assert.Equal(518836, new CpRandom(20261006).RandBelow(1_000_000));
    }

    [Fact]
    public void ASeedsSignIsNotPartOfItAndZeroIsAKeyOfOneWord()
    {
        // `random.Random(n)` seeds from the absolute value, and a zero that produced an empty key
        // would read past it: the reference pads the key to at least one word, so seed 0, seed -0 and
        // a key of one zero word are the same generator.
        var positive = new CpRandom(41);
        var negative = new CpRandom(-41);
        var zero = new CpRandom(0);
        var other = new CpRandom(0);
        for (var draw = 0; draw < 32; draw++)
        {
            Assert.Equal(positive.NextUInt32(), negative.NextUInt32());
            Assert.Equal(zero.NextUInt32(), other.NextUInt32());
        }
    }

    [Fact]
    public void TheDerivedSeedIsTheReferenceFormula()
    {
        // `ctx.seed + stable_hash("%s|%s|%s" % (key, row_a, row_b)) % 1000000`, where the hash is the
        // first four bytes of the SHA-256 read big-endian.
        const string metricKey = "lat.tcp_rtt.p50|wf-aot-opt|proxifier";
        Assert.Equal(1973867047u, CpRandom.StableHash(metricKey));
        Assert.Equal(867047u, CpRandom.StableHash(metricKey) % 1000000u);
        Assert.Equal(21128053, CpRandom.DeriveSeed(20261006, metricKey));
    }
}
