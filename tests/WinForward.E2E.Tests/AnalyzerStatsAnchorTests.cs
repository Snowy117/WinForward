using WinForward.E2E.Analysis.Stats;
using Xunit;

namespace WinForward.E2E.Tests;

/// <summary>
/// Pins the analysis's compensated sum on the one value that tells it apart from a plain accumulator.
/// </summary>
/// <remarks>
/// CPython's <c>sum</c> adds floats with Neumaier compensation (3.12 and later), and §7's least-squares
/// slope is a difference of sums large enough for a middle term to vanish: this sum is what keeps the
/// last printed digit of that slope, so it is asserted on its own rather than through a table cell.
/// </remarks>
public sealed class AnalyzerStatsAnchorTests
{
    [Fact]
    public void TheCompensatedSumKeepsTheMiddleTerm()
    {
        double[] values = [1e16, 1.0, -1e16];
        Assert.Equal(1.0, DescriptiveStats.Sum(values));

        // The same three values through a plain accumulator: the two large terms cancel before the 1
        // is ever added, so the anchor above is a statement about the compensation and not about the
        // addition being associative.
        var naive = 0.0;
        foreach (var value in values)
        {
            naive += value;
        }

        Assert.Equal(0.0, naive);
    }
}
