using WinForward.E2E.Analysis.Stats;
using Xunit;

namespace WinForward.E2E.Tests;

/// <summary>
/// Pins the seed a comparison's bootstrap draws with: the run's base seed plus the low six digits of a
/// stable hash of the comparison's key. The key is spelled by the analysis and the hash has to stay put,
/// because the <c>--seed</c> promise is that the same campaign re-analysed derives the same seeds — these
/// three values are the anchor that a rewrite of either would move.
/// </summary>
public sealed class AnalyzerComparisonSeedTests
{
    [Fact]
    public void TheDerivedSeedIsTheStableHashFormula()
    {
        // The base seed plus the low six digits of the SHA-256's first four bytes, read big-endian, over
        // "metric|row-a|row-b".
        const string metricKey = "lat.tcp_rtt.p50|wf-aot-opt|proxifier";
        Assert.Equal(1973867047u, ComparisonSeed.StableHash(metricKey));
        Assert.Equal(867047u, ComparisonSeed.StableHash(metricKey) % 1000000u);
        Assert.Equal(21128053, ComparisonSeed.DeriveSeed(20261006, metricKey));
    }
}
