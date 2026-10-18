using Xunit;

namespace WinForward.E2E.Tests;

/// <summary>
/// The campaign's failure evidence read by the analysis: a row that failed and left a skeleton, a row
/// that left nothing at all, a refused pass and the failure record itself.
/// </summary>
/// <remarks>
/// Every fact mutates a private copy of the frozen campaign and drives the analyzer's command line, so
/// the assertions read the published documents rather than a re-derivation of them.
/// </remarks>
public sealed class AnalyzerCampaignFailureTests
{
    [Fact]
    public void AFailedRowIsReportedAsAGapAndStillRenders()
    {
        using var campaign = new SyntheticCampaign();
        var raw = campaign.Copy("failed-row");
        SyntheticCampaign.WriteSkeleton(raw, "pass2", "wf-aot-opt", "product-start", "wf-aot-opt did not start");
        var output = campaign.Output("failed-row-out");

        SyntheticCampaign.Run(raw, output);

        var finding = Assert.Single(SyntheticCampaign.FindingsOfKind(output, "failed-row"));
        Assert.Equal("pass2/wf-aot-opt", finding["scope"]!.GetValue<string>());
        var detail = finding["detail"]!.GetValue<string>();
        Assert.Contains("failed=true", detail, StringComparison.Ordinal);
        Assert.Contains("product-start", detail, StringComparison.Ordinal);
        Assert.Contains("did not start", detail, StringComparison.Ordinal);
        Assert.Contains("no arm records", detail, StringComparison.Ordinal);

        var tables = File.ReadAllText(Path.Combine(output, "tables.md"));
        Assert.Contains("| pass2 | wf-aot-opt | wf-aot-opt-p2 |", tables, StringComparison.Ordinal);
        Assert.DoesNotContain("| pass2 | wf-aot-opt | wf-aot-opt-p2 | 2026-10-06T08:10:00.0000000+00:00 | n/a | n/a | 0 | no |", tables, StringComparison.Ordinal);

        var rows = SyntheticCampaign.Json(output, "verdict.json")["rows"]!.AsArray();
        Assert.Contains(rows, row => row!.GetValue<string>() == "wf-aot-opt");
    }

    [Fact]
    public void ARunThatFailedAfterWritingArmsKeepsThemAndIsDisclosed()
    {
        using var campaign = new SyntheticCampaign();
        var raw = campaign.Copy("partial-run");
        SyntheticCampaign.SetRunFailed(raw, "pass2", "wf-aot-opt", "row", "the row exceeded 1800 s and was stopped");
        var output = campaign.Output("partial-run-out");

        SyntheticCampaign.Run(raw, output);

        var finding = Assert.Single(SyntheticCampaign.FindingsOfKind(output, "failed-row"));
        var detail = finding["detail"]!.GetValue<string>();
        Assert.Contains("exceeded 1800 s", detail, StringComparison.Ordinal);
        Assert.Contains("arm records it wrote are kept", detail, StringComparison.Ordinal);
    }

    [Fact]
    public void ADisabledRowWithNoRunJsonAtAllIsADeclaredRowMissing()
    {
        using var campaign = new SyntheticCampaign();
        var raw = campaign.Copy("missing-row");
        SyntheticCampaign.DeleteRow(raw, "pass3", "proxifyre");
        var output = campaign.Output("missing-row-out");

        SyntheticCampaign.Run(raw, output);

        var finding = Assert.Single(SyntheticCampaign.FindingsOfKind(output, "declared-row-missing"));
        Assert.Equal("pass3/proxifyre", finding["scope"]!.GetValue<string>());
        Assert.Contains("no run.json or arm file", finding["detail"]!.GetValue<string>(), StringComparison.Ordinal);

        var tables = File.ReadAllText(Path.Combine(output, "tables.md"));
        Assert.DoesNotContain("| pass3 | proxifyre |", tables, StringComparison.Ordinal);
        Assert.Contains("| pass2 | proxifyre |", tables, StringComparison.Ordinal);
    }

    [Fact]
    public void ACleanSlateRefusalIsReportedAndTheRefusedPassIsNotMeasured()
    {
        using var campaign = new SyntheticCampaign();
        var raw = campaign.Copy("clean-slate");
        SyntheticCampaign.DeletePass(raw, "pass3");
        Directory.CreateDirectory(Path.Combine(raw, "pass3"));
        SyntheticCampaign.WriteCleanSlate(raw, "pass3", passed: false, "firewall profiles are still enabled");
        SyntheticCampaign.WriteFailures(
            raw,
            SyntheticCampaign.Failure("clean-slate", pass: 3, error: "firewall profiles are still enabled", cleanSlate: true));
        var output = campaign.Output("clean-slate-out");

        SyntheticCampaign.Run(raw, output);

        var finding = Assert.Single(SyntheticCampaign.FindingsOfKind(output, "clean-slate-failure"));
        Assert.Equal("pass3", finding["scope"]!.GetValue<string>());
        var detail = finding["detail"]!.GetValue<string>();
        Assert.Contains("firewall profiles are still enabled", detail, StringComparison.Ordinal);
        Assert.Contains("refused", detail, StringComparison.Ordinal);

        var verdict = SyntheticCampaign.Json(output, "verdict.json");
        var passes = verdict["passes"]!.AsArray().Select(pass => pass!.GetValue<string>()).ToList();
        Assert.Equal(["pass1", "pass2"], passes);
        Assert.DoesNotContain("pass3 run order (order.txt)", File.ReadAllText(Path.Combine(output, "tables.md")), StringComparison.Ordinal);
    }

    [Fact]
    public void TheFailureRecordIsAlsoReadFromTheDerivedArray()
    {
        using var campaign = new SyntheticCampaign();
        var raw = campaign.Copy("failure-array");
        SyntheticCampaign.WriteFailureDocument(
            raw,
            SyntheticCampaign.Failure("target", pass: 2, error: "the target is unreachable at 192.168.100.4:40010"));
        var output = campaign.Output("failure-array-out");

        SyntheticCampaign.Run(raw, output);

        var finding = Assert.Single(SyntheticCampaign.FindingsOfKind(output, "campaign-failure"));
        Assert.Equal("pass2", finding["scope"]!.GetValue<string>());
        Assert.Contains("unreachable", finding["detail"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnreadableFailureRecordIsDisclosedRatherThanReadAsClean()
    {
        using var campaign = new SyntheticCampaign();
        var raw = campaign.Copy("failure-broken");
        File.WriteAllText(Path.Combine(raw, "failures.json"), "{not json");
        var output = campaign.Output("failure-broken-out");

        SyntheticCampaign.Run(raw, output);

        var finding = Assert.Single(SyntheticCampaign.FindingsOfKind(output, "failure-log-unreadable"));
        Assert.Equal("campaign", finding["scope"]!.GetValue<string>());
        Assert.Contains("failures.json", finding["detail"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public void ARowStageRecordWhoseRunFileLooksGreenIsStillSurfaced()
    {
        using var campaign = new SyntheticCampaign();
        var raw = campaign.Copy("undisclosed-row");
        SyntheticCampaign.WriteFailures(
            raw,
            SyntheticCampaign.Failure("row", pass: 2, row: "wf-aot-opt", error: "the client exited 1 but its run.json reports no failed arm"));
        var output = campaign.Output("undisclosed-row-out");

        SyntheticCampaign.Run(raw, output);

        var finding = Assert.Single(SyntheticCampaign.FindingsOfKind(output, "campaign-failure"));
        Assert.Equal("pass2/wf-aot-opt", finding["scope"]!.GetValue<string>());
        Assert.Contains("does not report it", finding["detail"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public void ARowStageRecordIsScopedToItsOwnPass()
    {
        using var campaign = new SyntheticCampaign();
        var raw = campaign.Copy("pass-scoped-record");
        SyntheticCampaign.SetRunFailed(raw, "pass1", "wf-aot-opt", "row", "pass 1 failed");
        SyntheticCampaign.WriteFailures(
            raw,
            SyntheticCampaign.Failure("row", pass: 2, row: "wf-aot-opt", error: "the client exited 1 but its run.json reports no failed arm"));
        var output = campaign.Output("pass-scoped-record-out");

        SyntheticCampaign.Run(raw, output);

        var failedRun = Assert.Single(SyntheticCampaign.FindingsOfKind(output, "failed-row"));
        Assert.Equal("pass1/wf-aot-opt", failedRun["scope"]!.GetValue<string>());
        var record = Assert.Single(SyntheticCampaign.FindingsOfKind(output, "campaign-failure"));
        Assert.Equal("pass2/wf-aot-opt", record["scope"]!.GetValue<string>());
    }

    [Fact]
    public void AFailureRecordLineWithoutAStageIsDisclosed()
    {
        using var campaign = new SyntheticCampaign();
        var raw = campaign.Copy("failure-no-stage");
        File.WriteAllText(
            Path.Combine(raw, "failures.jsonl"),
            "{\"when\":\"2026-10-18T21:00:00Z\",\"error\":\"the stage was lost\"}" + Environment.NewLine);
        var output = campaign.Output("failure-no-stage-out");

        SyntheticCampaign.Run(raw, output);

        var finding = Assert.Single(SyntheticCampaign.FindingsOfKind(output, "failure-log-unreadable"));
        Assert.Contains("1 line(s)", finding["detail"]!.GetValue<string>(), StringComparison.Ordinal);
    }

}
