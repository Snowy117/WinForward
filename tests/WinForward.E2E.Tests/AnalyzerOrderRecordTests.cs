using WinForward.E2E.Analysis.Model;
using Xunit;

namespace WinForward.E2E.Tests;

/// <summary>
/// The analysis's half of the per-pass product order: the recorded permutation is replayed from its
/// seed, compared with <c>order.txt</c>, and a generator it cannot replay is disclosed instead of
/// guessed at.
/// </summary>
public sealed class AnalyzerOrderRecordTests
{
    private static readonly string[] s_products =
        ["wf-aot-opt", "wf-fdd-opt", "wf-aot-nativeudp", "wf-aot-dnsrelay", "proxifyre", "proxifier", "proxybridge"];

    private static readonly string[] s_otherSubsetOrder =
        ["wf-fdd-opt", "wf-aot-opt", "wf-aot-nativeudp"];

    private const int BaseSeed = 20261006;

    private static int PassTwoSeed => OrderPermutation.MixedSeed(BaseSeed, 2);

    private static IReadOnlyList<string> PassTwoOrder => OrderPermutation.Apply(PassTwoSeed, s_products);

    [Fact]
    public void ARecordedOrderIsReplayedAndItsOrderTxtFollowsIt()
    {
        using var campaign = new SyntheticCampaign();
        var raw = campaign.Copy("order-ok");
        var order = PassTwoOrder;
        SyntheticCampaign.WriteOrder(raw, "pass2", PassTwoSeed, OrderPermutation.Algorithm, order);
        SyntheticCampaign.WriteOrderText(raw, "pass2", ["control-pre", .. order, "control-post"]);
        var output = campaign.Output("order-ok-out");

        SyntheticCampaign.Run(raw, output);

        Assert.Empty(SyntheticCampaign.FindingsOfKind(output, "order-unreproducible"));
        Assert.Empty(SyntheticCampaign.FindingsOfKind(output, "order-mismatch"));
        Assert.Empty(SyntheticCampaign.FindingsOfKind(output, "order-algorithm-unknown"));
        Assert.Contains(
            "control-pre -> " + string.Join(" -> ", order),
            File.ReadAllText(Path.Combine(output, "tables.md")),
            StringComparison.Ordinal);
    }

    [Fact]
    public void ARestrictedSmokeRunIsReplayedOverItsOwnRows()
    {
        using var campaign = new SyntheticCampaign();
        var raw = campaign.Copy("order-subset");
        SyntheticCampaign.WriteOrder(raw, "pass2", PassTwoSeed, OrderPermutation.Algorithm, ["wf-aot-opt"]);
        SyntheticCampaign.WriteOrderText(raw, "pass2", ["control-pre", "wf-aot-opt", "control-post"]);
        var output = campaign.Output("order-subset-out");

        SyntheticCampaign.Run(raw, output);

        Assert.Empty(SyntheticCampaign.FindingsOfKind(output, "order-unreproducible"));
        Assert.Empty(SyntheticCampaign.FindingsOfKind(output, "order-rows-mismatch"));
        Assert.Empty(SyntheticCampaign.FindingsOfKind(output, "order-mismatch"));
    }

    [Fact]
    public void AWrongSeedIsReportedAsUnreproducible()
    {
        using var campaign = new SyntheticCampaign();
        var raw = campaign.Copy("order-wrong-seed");
        var order = PassTwoOrder;
        SyntheticCampaign.WriteOrder(raw, "pass2", PassTwoSeed + 1, OrderPermutation.Algorithm, order);
        SyntheticCampaign.WriteOrderText(raw, "pass2", ["control-pre", .. order, "control-post"]);
        var output = campaign.Output("order-wrong-seed-out");

        SyntheticCampaign.Run(raw, output);

        var finding = Assert.Single(SyntheticCampaign.FindingsOfKind(output, "order-unreproducible"));
        Assert.Equal("pass2", finding["scope"]!.GetValue<string>());
        Assert.Contains((PassTwoSeed + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), finding["detail"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public void AnOrderTxtThatDisagreesWithOrderJsonIsReported()
    {
        using var campaign = new SyntheticCampaign();
        var raw = campaign.Copy("order-mismatch");
        var order = PassTwoOrder;
        SyntheticCampaign.WriteOrder(raw, "pass2", PassTwoSeed, OrderPermutation.Algorithm, order);
        SyntheticCampaign.WriteOrderText(raw, "pass2", ["control-pre", .. s_products, "control-post"]);
        var output = campaign.Output("order-mismatch-out");

        SyntheticCampaign.Run(raw, output);

        var finding = Assert.Single(SyntheticCampaign.FindingsOfKind(output, "order-mismatch"));
        Assert.Equal("pass2", finding["scope"]!.GetValue<string>());
        Assert.Contains("disagree", finding["detail"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownGeneratorIsDisclosedRatherThanReplayed()
    {
        using var campaign = new SyntheticCampaign();
        var raw = campaign.Copy("order-unknown");
        SyntheticCampaign.WriteOrder(raw, "pass2", 1, "some-other-generator/2", PassTwoOrder);
        var output = campaign.Output("order-unknown-out");

        SyntheticCampaign.Run(raw, output);

        var finding = Assert.Single(SyntheticCampaign.FindingsOfKind(output, "order-algorithm-unknown"));
        Assert.Equal("pass2", finding["scope"]!.GetValue<string>());
        Assert.Empty(SyntheticCampaign.FindingsOfKind(output, "order-unreproducible"));
    }

    [Fact]
    public void AnOrderJsonThatNamesUndeclaredRowsIsReported()
    {
        using var campaign = new SyntheticCampaign();
        var raw = campaign.Copy("order-rows");
        SyntheticCampaign.WriteOrder(raw, "pass2", PassTwoSeed, OrderPermutation.Algorithm, ["wf-aot-opt", "mystery-row"]);
        var output = campaign.Output("order-rows-out");

        SyntheticCampaign.Run(raw, output);

        var finding = Assert.Single(SyntheticCampaign.FindingsOfKind(output, "order-rows-mismatch"));
        Assert.Equal("pass2", finding["scope"]!.GetValue<string>());
        Assert.Contains("mystery-row", finding["detail"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public void ARestrictedSmokeRunReplaysItsSubsetInDeclarationOrder()
    {
        using var campaign = new SyntheticCampaign();
        var raw = campaign.Copy("order-subset-multi");
        string[] subset = ["wf-aot-opt", "wf-fdd-opt", "wf-aot-nativeudp"];
        var order = OrderPermutation.Apply(PassTwoSeed, subset);
        var otherOrder = OrderPermutation.Apply(PassTwoSeed, s_otherSubsetOrder);
        Assert.NotEqual(string.Join(",", order), string.Join(",", otherOrder));

        SyntheticCampaign.WriteOrder(raw, "pass2", PassTwoSeed, OrderPermutation.Algorithm, order);
        SyntheticCampaign.WriteOrderText(raw, "pass2", ["control-pre", .. order, "control-post"]);
        var output = campaign.Output("order-subset-multi-out");

        SyntheticCampaign.Run(raw, output);

        Assert.Empty(SyntheticCampaign.FindingsOfKind(output, "order-unreproducible"));
        Assert.Empty(SyntheticCampaign.FindingsOfKind(output, "order-rows-mismatch"));
        Assert.Empty(SyntheticCampaign.FindingsOfKind(output, "order-mismatch"));
    }

    [Fact]
    public void AnUnreadableOrderJsonIsDisclosedAndTheOrderTxtStillPrints()
    {
        using var campaign = new SyntheticCampaign();
        var raw = campaign.Copy("order-broken");
        File.WriteAllText(Path.Combine(raw, "pass2", "order.json"), "{not json");
        var output = campaign.Output("order-broken-out");

        SyntheticCampaign.Run(raw, output);

        var finding = Assert.Single(SyntheticCampaign.FindingsOfKind(output, "order-unreadable"));
        Assert.Equal("pass2", finding["scope"]!.GetValue<string>());
        Assert.Contains("order.json unreadable", finding["detail"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Contains("pass2 run order (order.txt)", File.ReadAllText(Path.Combine(output, "tables.md")), StringComparison.Ordinal);
    }

}
