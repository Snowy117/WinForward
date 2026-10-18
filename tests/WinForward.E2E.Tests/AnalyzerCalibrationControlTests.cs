using System.Text.Json.Nodes;
using Xunit;

namespace WinForward.E2E.Tests;

/// <summary>
/// The analysis half of the BASE-singbox control: a one-row control pass loads, section 1 reports it
/// as a calibration control, and it stays out of the product row table and the pairwise comparisons.
/// </summary>
[Collection(CalibrationControlCollection.Name)]
public sealed class AnalyzerCalibrationControlTests
{
    [Fact]
    public void TheCalibrationControlRowLoadsAndIsReportedAsReportOnly()
    {
        using var campaign = new SyntheticCampaign();
        var raw = campaign.Copy("calibration-control");
        BuildControlOnlyTree(raw);

        var output = campaign.Output("calibration-control-out");
        SyntheticCampaign.Run(raw, output);

        var tables = File.ReadAllText(Path.Combine(output, "tables.md"));
        Assert.Contains("sing-box 1.14.2-singfix (SOCKS5 hop)", tables, StringComparison.Ordinal);
        Assert.Contains("calibration control: no product loaded", tables, StringComparison.Ordinal);
        Assert.Contains("not run (TCP-only control)", tables, StringComparison.Ordinal);
        Assert.Contains("not run (the calibration control is TCP only: CONNECT cannot carry UDP or DNS)", tables, StringComparison.Ordinal);

        var verdict = SyntheticCampaign.Json(output, "verdict.json");
        Assert.Contains(
            "BASE-singbox",
            verdict["rows"]!.AsArray().Select(row => row!.GetValue<string>()));
        Assert.False(
            verdict["row_profiles"]!.AsObject().ContainsKey("BASE-singbox"),
            "the calibration control must not join the product row table");

        var findings = verdict["findings"]!.AsArray();
        Assert.True(
            findings.Count == 1,
            string.Join("\n", findings.Select(finding => finding!["kind"]!.GetValue<string>() + " | " + finding["detail"])));
        var only = findings[0]!;
        Assert.Equal("informational", only["severity"]!.GetValue<string>());
        Assert.Equal("BASE-singbox", only["scope"]!.GetValue<string>());

        var latency = verdict["metrics"]!.AsObject()["lat.tcp_rtt.p50"]!.AsObject();
        Assert.Equal("ok", latency["rows"]!.AsObject()["BASE-singbox"]!.AsObject()["status"]!.GetValue<string>());
        Assert.Empty(latency["pairs"]!.AsArray());

        var udp = verdict["metrics"]!.AsObject()["lat.udp_rtt.p50"]!.AsObject();
        Assert.Equal("not-run", udp["rows"]!.AsObject()["BASE-singbox"]!.AsObject()["status"]!.GetValue<string>());

        var dns = verdict["metrics"]!.AsObject()["dns.answerRate"]!.AsObject();
        Assert.Equal("not-in-plan", dns["rows"]!.AsObject()["BASE-singbox"]!.AsObject()["status"]!.GetValue<string>());
    }

    /// <summary>
    /// Reduces the synthetic tree to the calibration control's own shape: one pass, one
    /// BASE-singbox row with a real LAT result and no order files; the frozen ledgers stay so the
    /// cross-check has its second opinion. The product sample records are dropped because the
    /// control loads no product.
    /// </summary>
    private static void BuildControlOnlyTree(string raw)
    {
        var pass = Path.Combine(raw, "pass1");
        var source = Path.Combine(pass, "wf-aot-nativeudp");
        var latency = File.ReadAllLines(Path.Combine(source, "LAT.jsonl"))
            .Where(line => !line.Contains("\"type\":\"sample\"", StringComparison.Ordinal)
                && !line.Contains("\"type\":\"samplerError\"", StringComparison.Ordinal))
            .ToArray();
        var run = JsonNode.Parse(File.ReadAllText(Path.Combine(source, "run.json")))!.AsObject();
        run["label"] = "BASE-singbox-p1";
        run["samplerProcesses"] = new JsonArray();
        var latencyArm = run["arms"]!.AsArray()
            .First(entry => string.Equals(entry!["name"]!.GetValue<string>(), "LAT", StringComparison.Ordinal))!
            .DeepClone();
        run["arms"] = new JsonArray(latencyArm);

        foreach (var entry in Directory.GetFileSystemEntries(pass))
        {
            if (Directory.Exists(entry))
            {
                Directory.Delete(entry, recursive: true);
            }
            else
            {
                File.Delete(entry);
            }
        }

        var target = Path.Combine(pass, "BASE-singbox");
        Directory.CreateDirectory(target);
        File.WriteAllLines(Path.Combine(target, "LAT.jsonl"), latency);
        File.WriteAllText(Path.Combine(target, "run.json"), run.ToJsonString());

        foreach (var other in Directory.GetDirectories(raw, "pass*"))
        {
            if (!string.Equals(other, pass, StringComparison.Ordinal))
            {
                Directory.Delete(other, recursive: true);
            }
        }
    }
}
