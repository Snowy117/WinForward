using System.Text.Json.Nodes;
using WinForward.E2E.Analysis.Model;
using Xunit;

namespace WinForward.E2E.Tests;

/// <summary>
/// The product-order generator against the orchestrator's own reference orders: a fixed seed must
/// reproduce the campaign's permutation, and the C# replay must agree with the PowerShell one.
/// </summary>
/// <remarks>
/// The fixture is the cross-language pin. <c>orchestrator.ps1</c> owns the order a campaign runs in;
/// this assembly replays it, and the two cannot share code, so a change to either side turns this red
/// rather than letting the analysis silently disagree with what the campaign recorded.
/// </remarks>
public sealed class OrderPermutationTests
{
    [Fact]
    public void TheReplayReproducesEveryRecordedReferenceOrder()
    {
        var fixture = Fixture();
        var rows = Strings(fixture["rows"]!.AsArray());
        Assert.NotEmpty(rows);
        Assert.Equal(OrderPermutation.Algorithm, fixture["algorithm"]!.GetValue<string>());

        foreach (var entry in fixture["cases"]!.AsArray())
        {
            var baseSeed = entry!["baseSeed"]!.GetValue<int>();
            var pass = entry["pass"]!.GetValue<int>();
            var seed = entry["seed"]!.GetValue<int>();
            var expected = Strings(entry["order"]!.AsArray());

            Assert.Equal(seed, OrderPermutation.MixedSeed(baseSeed, pass));
            Assert.Equal(expected, OrderPermutation.Apply(seed, rows));
        }
    }

    [Fact]
    public void OneSeedReplaysOneOrderAndEachPassGetsItsOwn()
    {
        var rows = Strings(Fixture()["rows"]!.AsArray());
        var first = OrderPermutation.Apply(OrderPermutation.MixedSeed(20261006, 1), rows);
        var again = OrderPermutation.Apply(OrderPermutation.MixedSeed(20261006, 1), rows);
        Assert.Equal(first, again);

        var passes = Enumerable.Range(1, 4)
            .Select(pass => string.Join(",", OrderPermutation.Apply(OrderPermutation.MixedSeed(20261006, pass), rows)))
            .ToList();
        Assert.Equal(passes.Count, passes.Distinct(StringComparer.Ordinal).Count());

        var sorted = string.Join(",", rows.Order(StringComparer.Ordinal));
        Assert.All(passes, pass => Assert.Equal(sorted, string.Join(",", pass.Split(',').Order(StringComparer.Ordinal))));
    }

    private static JsonNode Fixture() => JsonNode.Parse(File.ReadAllText(RepoPaths.OrderFixture))!;

    private static List<string> Strings(JsonArray array) => [.. array.Select(node => node!.GetValue<string>())];
}
