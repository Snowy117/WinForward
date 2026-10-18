using System.Security.Cryptography;
using System.Text.Json;
using WinForward.E2E.Analysis.Model;
using WinForward.E2E.Analysis.Release;
using Xunit;

namespace WinForward.E2E.Tests;

/// <summary>
/// The thresholds file is the pre-registered contract behind a release claim, so its parser is tested
/// through the public seam the release path offers: each refusal is a checked-in negative fixture, and
/// the valid example is the one file the release documentation points at. No test reaches an internal
/// member, and the analysis grants no <c>InternalsVisibleTo</c>.
/// </summary>
public sealed class AnalyzerReleaseThresholdsTests
{
    private const string ReleasePlan = "benchmarks/WinForward.E2E/scripts/plans-release/full-plan.json";

    private const string ExpectedReferenceRow = "wf-aot-nativeudp";

    [Fact]
    public void TheCheckedInExampleDeclaresTheReleaseContract()
    {
        var file = ParseExample();

        Assert.Equal(ThresholdsFile.SupportedVersion, file.Version);
        Assert.Equal(ReleasePlan, file.Caliber.Path);
        Assert.Equal(64, file.Caliber.Sha256.Length);
        Assert.Equal(10, file.Caliber.Seconds.Count);
        Assert.Equal(120, file.Caliber.Seconds["LOSS"]);
        Assert.Equal(ExpectedReferenceRow, file.ReferenceRow);
        Assert.Equal("proxifyre, proxifier, proxybridge", string.Join(", ", file.Opponents));

        Assert.Equal(4, file.Primary.Count);
        Assert.Equal("lat.tcp_rtt.p50", file.Primary[0].Metric);
        Assert.Equal(ThresholdKind.Ratio, file.Primary[0].Kind);
        Assert.Equal(0.18, file.Primary[0].Band);
        Assert.Equal(0.05, file.Primary[0].Business);
        Assert.Equal(0.18, file.Primary[0].Mdd);
        Assert.Equal(ThresholdKind.PercentagePoints, file.Primary[3].Kind);
        Assert.Equal(0.50, file.Primary[3].Band);

        Assert.Equal(16, file.Exploratory.Count);
        Assert.DoesNotContain("lat.tcp_rtt.p50", file.Exploratory);
        Assert.DoesNotContain("rel.unexpectedEofRate", file.Exploratory);
        Assert.Contains("lat.tcp_rtt.p99", file.Exploratory);

        var capability = Assert.Single(file.Capability);
        Assert.Equal("rel.unexpectedEofRate", capability.Key);
        Assert.Equal(ThresholdKind.PercentagePoints, capability.Value.Kind);
        Assert.Equal(0.25, capability.Value.UpperBound);
        Assert.Equal("connectAttempts", capability.Value.NFrom);

        Assert.Empty(file.Equivalence);
        Assert.Equal(6, file.MinPasses);
        Assert.Equal(1, file.DiscardPass);
        Assert.Equal(5.0, file.WarmupSeconds);
        Assert.False(file.Clock.Require);
        Assert.Equal(100.0, file.Clock.MaxDriftMs);
        Assert.Equal(64, file.Sha256.Length);
    }

    [Fact]
    public void TheExampleNamesTheCurrentReleasePlan()
    {
        // The example is a real declaration, not a schema illustration: its caliber digest has to be the
        // digest a release run verifies. A plan edit therefore updates this file or fails here, which is
        // the same rule the run itself enforces.
        var example = ParseExample();
        var plan = Path.Combine(RepoPaths.Root, ReleasePlan);

        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(plan))), example.Caliber.Sha256);
    }

    [Fact]
    public void TheReferenceRowConstantIsTheDefaultConfiguration()
    {
        Assert.Equal(ExpectedReferenceRow, RowProfiles.ReferenceRow);
        Assert.Equal(RowProfiles.ReferenceRow, ParseExample().ReferenceRow);
    }

    [Fact]
    public void TheSha256IsComputedOverTheFilesOwnBytes()
    {
        var path = RepoPaths.ReleaseThresholdsExample;
        var example = ParseExample();

        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))), example.Sha256);
    }

    [Theory]
    [InlineData("unknown-key.json", "unknown key 'duration'")]
    [InlineData("missing-primary.json", "'primary' is required")]
    [InlineData("duplicate-primary.json", "'primary[1].metric' repeats metric 'lat.tcp_rtt.p50'")]
    [InlineData("unknown-primary.json", "'primary[0].metric' names an unknown metric 'lat.tcp_rtt.p95'")]
    [InlineData("band-zero.json", "'primary[0].band' must be > 0")]
    [InlineData("kind-mismatch.json", "'primary[3].kind' is 'ratio', but 'loss.lossRate' is measured in pp")]
    [InlineData("equivalence-not-primary.json", "'equivalence.families[0].metric' is not a primary metric")]
    [InlineData("unknown-reference.json", "'referenceRow' names an undeclared row 'wf-aot-default'")]
    [InlineData("unknown-opponent.json", "'opponents[3]' names an undeclared row 'sing-box'")]
    [InlineData("calibration-control-opponent.json", "names the calibration control 'BASE-singbox', which is report-only and cannot be a product opponent")]
    [InlineData("calibration-control-reference.json", "names the calibration control 'BASE-singbox', which is report-only and cannot be a product reference")]
    [InlineData("missing-opponents.json", "'opponents' is required")]
    [InlineData("min-passes-one.json", "'minPasses' is 1, below the minimum of 2")]
    [InlineData("discard-pass-negative.json", "'discardPass' is -1, below the minimum of 0")]
    [InlineData("bad-version.json", "'version' is 2")]
    [InlineData("duplicate-key.json", "duplicate key 'version'")]
    [InlineData("capability-primary.json", "'capability.lat.tcp_rtt.p50' names a metric that is also in 'primary'")]
    public void ARejectedThresholdsFileNamesTheField(string fixture, string expected)
    {
        var parsed = ThresholdsFile.TryParse(RepoPaths.ThresholdFixture(fixture), out var file, out var error);

        Assert.False(parsed);
        Assert.Null(file);
        Assert.NotNull(error);
        Assert.Contains(expected, error, StringComparison.Ordinal);
    }

    [Fact]
    public void AThresholdsFileThatCannotBeReadIsRefusedWithItsPath()
    {
        var path = Path.Combine(RepoPaths.Root, "no-such-thresholds.json");

        Assert.False(ThresholdsFile.TryParse(path, out var file, out var error));
        Assert.Null(file);
        Assert.NotNull(error);
        Assert.Contains(path, error, StringComparison.Ordinal);
        Assert.Contains("cannot read", error, StringComparison.Ordinal);
    }

    [Fact]
    public void AFileThatIsNotJsonIsRefusedBeforeAnyFieldIsRead()
    {
        var path = RepoPaths.ThresholdFixture("unknown-key.json");
        var content = File.ReadAllText(path);
        var broken = Path.Combine(Path.GetTempPath(), $"wf-thresholds-{Guid.NewGuid():N}.json");

        try
        {
            File.WriteAllText(broken, content[..(content.Length / 2)]);
            Assert.False(ThresholdsFile.TryParse(broken, out _, out var error));
            Assert.NotNull(error);
            Assert.Contains("not valid JSON", error, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(broken);
        }
    }

    [Fact]
    public void TheExampleDeclaresThePlanArmDurations()
    {
        // The digest pins the plan's bytes; this pins the report metadata to the same bytes, so a
        // mistyped MIX, REL or PERSIST cannot be printed beside numbers the plan did not declare.
        var example = ParseExample();
        using var plan = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(RepoPaths.Root, ReleasePlan)));
        using var caliber = JsonDocument.Parse(
            File.ReadAllBytes(Path.Combine(RepoPaths.ReleasePlansDirectory, "caliber.json")));

        var planSeconds = plan.RootElement.GetProperty("arms").EnumerateArray()
            .ToDictionary(arm => arm.GetProperty("name").GetString()!, arm => arm.GetProperty("seconds").GetInt32());
        var caliberSeconds = caliber.RootElement.GetProperty("seconds").EnumerateObject()
            .ToDictionary(member => member.Name, member => member.Value.GetInt32());

        Assert.Equal(10, example.Caliber.Seconds.Count);
        foreach (var declared in example.Caliber.Seconds)
        {
            Assert.Equal(declared.Value, planSeconds[declared.Key]);
            Assert.Equal(declared.Value, caliberSeconds[declared.Key]);
        }
    }

    [Fact]
    public void AFileThatOmitsTheOptionalRulesFallsBackToTheDocumentedDefaults()
    {
        var file = Parse(RepoPaths.ThresholdFixture("minimal.json"));

        Assert.Null(file.MinPasses);
        Assert.Null(file.DiscardPass);
        Assert.Equal(16, file.Exploratory.Count);
        Assert.DoesNotContain("lat.tcp_rtt.p50", file.Exploratory);
        Assert.DoesNotContain("rel.unexpectedEofRate", file.Exploratory);
        Assert.Contains("lat.tcp_rtt.p99", file.Exploratory);
        Assert.Equal(ExpectedReferenceRow, file.ReferenceRow);
    }

    private static ThresholdsFile ParseExample() => Parse(RepoPaths.ReleaseThresholdsExample);

    private static ThresholdsFile Parse(string path)
    {
        var parsed = ThresholdsFile.TryParse(path, out var file, out var error);

        Assert.True(parsed, error);
        Assert.NotNull(file);
        return file;
    }
}
