using System.Globalization;
using WinForward.E2E.Client;
using Xunit;

namespace WinForward.E2E.Tests;

/// <summary>
/// The Tier 0 load-time contract: every plan that crashes an arm, silently keeps an arm's default or
/// truncates another arm's records is refused by <see cref="PlanFile.TryLoad"/> with a message that
/// names the arm and the value. The exit code those errors produce is checked against the published
/// binary, not here (D14.19).
/// </summary>
public sealed class PlanFileValidationTests
{
    [Fact]
    public void ASchedulePastTheBoundedSequenceSpaceIsRefusedWithItsHighestSequence()
    {
        Assert.False(TryLoadFixture("overrun-loss-20000x20.json", out var error));

        Assert.Contains("LOSSOVERRUN", error, StringComparison.Ordinal);
        Assert.Contains("400000", error, StringComparison.Ordinal);
        Assert.Contains(
            UdpReliabilityTracker.MaxSequence.ToString(CultureInfo.InvariantCulture),
            error,
            StringComparison.Ordinal);
    }

    // D14.4's boundary, one kind per side: the check has to reject exactly what the tracker cannot
    // hold, so the last sequence inside the space loads and the first one past it does not.
    [Theory]
    [InlineData("max-sequence-loss-at.json", true)]
    [InlineData("max-sequence-loss-over.json", false)]
    [InlineData("max-sequence-base-at.json", true)]
    [InlineData("max-sequence-base-over.json", false)]
    public void TheSequenceBoundaryIsExact(string fixture, bool loads)
    {
        Assert.Equal(loads, TryLoadFixture(fixture, out var error));
        if (!loads)
        {
            Assert.Contains("past the tracker's MaxSequence", error, StringComparison.Ordinal);
        }
    }

    // D14.4's scope, from the other side: only the three kinds that index arrays by sequence carry
    // the check. A latency arm offering 300000 sequences has to load, and base and mix reach their
    // effective rate without a key of their own -- base from its declaration or its loss phase's
    // 500/s, mix from UdpPacketsPerSecond -- so each row states the rate the bound is measured
    // against rather than assuming one formula covers all three.
    [Theory]
    [InlineData("LATLONG", """ "kind":"latency","seconds":600,"ratePerSecond":500""", true, "")]
    [InlineData("BASEFALLBACK", """ "kind":"base","seconds":524""", true, "")]
    [InlineData("BASEFALLBACK", """ "kind":"base","seconds":525""", false, "262500")]
    [InlineData("MIXLONG", """ "kind":"mix","seconds":8738""", true, "")]
    [InlineData("MIXLONG", """ "kind":"mix","seconds":8739""", false, "262170")]
    public void TheSequenceBoundIsMeasuredOnlyWhereSequencesAreIndexed(string name, string armFields, bool loads, string highestSequence)
    {
        Assert.Equal(loads, TryLoadJson(name, armFields, out var error));
        if (loads)
        {
            return;
        }

        Assert.Contains($"'{name}'", error, StringComparison.Ordinal);
        Assert.Contains(highestSequence, error, StringComparison.Ordinal);
        Assert.Contains("past the tracker's MaxSequence", error, StringComparison.Ordinal);
    }

    // D5's rule for the two text keys: a value of another JSON type must not be read as the key's
    // default, because that publishes a run nothing asked for (`"protocol": 5` becoming a tcp arm).
    [Theory]
    [InlineData("LATBADPROTO", """ "kind":"latency","protocol":5""", "protocol", "5")]
    [InlineData("RELBADMIX", """ "kind":"reliability","modeMix":null""", "modeMix", "null")]
    public void ATextKeyOfAnotherTypeIsRefusedWithTheValueAsWritten(string name, string armFields, string key, string raw)
    {
        Assert.False(TryLoadJson(name, armFields, out var error));

        Assert.Contains($"'{name}'", error, StringComparison.Ordinal);
        Assert.Contains($"'{key}' is {raw}, which is not a string", error, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownKeyIsRefusedAndNamesTheArmAndTheKey()
    {
        Assert.False(TryLoadFixture("unknown-key.json", out var error));

        Assert.Contains("'LOSS'", error, StringComparison.Ordinal);
        Assert.Contains("ratePerSeconds", error, StringComparison.Ordinal);
    }

    [Fact]
    public void TwoArmNamesThatMapToTheSameFileAreRefused()
    {
        Assert.False(TryLoadFixture("colliding-file-names.json", out var error));

        Assert.Contains("'A/B'", error, StringComparison.Ordinal);
        Assert.Contains("'A_B'", error, StringComparison.Ordinal);
        Assert.Contains("A_B.jsonl", error, StringComparison.Ordinal);
    }

    [Fact]
    public void AnArmNameTooLongToBeAFileNameIsRefusedWithBothNames()
    {
        Assert.False(TryLoadFixture("arm-name-too-long.json", out var error));

        Assert.Contains(new string('A', 265) + "_____", error, StringComparison.Ordinal);
        Assert.Contains("270-character file name", error, StringComparison.Ordinal);
        Assert.Contains("128-character limit", error, StringComparison.Ordinal);
    }

    [Fact]
    public void AnOutOfRangeDnsPortIsRefused()
    {
        Assert.False(TryLoadFixture("dns-port-out-of-range.json", out var error));

        Assert.Contains("DNSBAD", error, StringComparison.Ordinal);
        Assert.Contains("'dnsPort' is 99999, which is outside 0..65535", error, StringComparison.Ordinal);
    }

    // The codec refuses a frame above its own bound, so a plan declaring a larger payload could only
    // measure a run in which every frame is malformed. The bound is asserted as the literal the codec
    // publishes rather than through the constant, so widening it in one place cannot pass here.
    [Theory]
    [InlineData("LATPAYLOADAT", 4194304, true)]
    [InlineData("LATPAYLOADOVER", 4194305, false)]
    public void ThePayloadBoundIsTheFrameCodecs(string name, int payloadBytes, bool loads)
    {
        Assert.Equal(loads, TryLoadJson(name, $""" "kind":"latency","payloadBytes":{payloadBytes}""", out var error));
        if (loads)
        {
            return;
        }

        Assert.Contains($"'{name}'", error, StringComparison.Ordinal);
        Assert.Contains($"'payloadBytes' is {payloadBytes}, which is outside 0..4194304", error, StringComparison.Ordinal);
    }

    // Zero stays "not declared" for the payload key too, which is what a plan that never names it
    // gets: the bound above is a ceiling, not a minimum.
    [Fact]
    public void APayloadOfZeroMeansUndeclared()
    {
        Assert.True(TryLoadJson("LATNOPAYLOAD", """ "kind":"latency","payloadBytes":0""", out var error), error);
    }

    // D5: a fractional value on an integer key is refused with the value as written, rather than
    // falling back to the arm's default and publishing a number the plan never declared.
    [Fact]
    public void AFractionalValueOnAnIntegerKeyIsRefusedWithTheValueAsWritten()
    {
        Assert.False(TryLoadFixture("fractional-window.json", out var error));

        Assert.Contains("LATFRACTION", error, StringComparison.Ordinal);
        Assert.Contains("'window' is 100.5, which is not an integer", error, StringComparison.Ordinal);
    }

    // An integral value written with a decimal point stays an integer: the range check happens after
    // the fraction check, and 100.0 has no fraction to refuse.
    [Fact]
    public void AnIntegralValueWrittenAsADecimalIsAccepted()
    {
        Assert.True(PlanFile.TryLoad(RepoPaths.Tier0Plan("integral-double-window.json"), out var arms, out _, out var error), error);

        Assert.Equal(100, arms[0].Window);
    }

    // A value past int's bounds has no fractional part, so it must be reported as out of range:
    // "3000000000 is not an integer" would name the wrong defect.
    [Fact]
    public void AValuePastIntsBoundsIsRefusedAsOutOfRangeRatherThanAsAFraction()
    {
        Assert.False(TryLoadFixture("beyond-int-window.json", out var error));

        Assert.Contains("LATBEYONDINT", error, StringComparison.Ordinal);
        Assert.Contains("'window' is 3000000000, which is outside 0..2147483647", error, StringComparison.Ordinal);
    }

    [Fact]
    public void ANegativeValueIsRefused()
    {
        Assert.False(TryLoadFixture("negative-rate.json", out var error));

        Assert.Contains("LOSSNEGATIVE", error, StringComparison.Ordinal);
        Assert.Contains("'ratePerSecond' is -1, which is outside 0..", error, StringComparison.Ordinal);
    }

    // Zero is "not declared" rather than "port zero": the arm then uses the run's --dns-port, which is
    // what every shipped plan relies on.
    [Fact]
    public void ADnsPortOfZeroMeansUndeclared()
    {
        Assert.True(PlanFile.TryLoad(RepoPaths.Tier0Plan("dns-port-zero.json"), out var arms, out _, out var error), error);

        Assert.Equal(0, arms[0].DnsPort);
    }

    [Theory]
    [InlineData("mode-mix-text-key.json", "reliability")]
    [InlineData("protocol-text-key.json", "latency")]
    public void TextValuedKeysAreAcceptedByTheKindsThatReadThem(string fixture, string kind)
    {
        Assert.True(PlanFile.TryLoad(RepoPaths.Tier0Plan(fixture), out var arms, out _, out var error), error);

        Assert.Equal(kind, arms[0].Kind);
    }

    // D14.4's boundary is the loader's; this one is not. A plan the loader cannot reject still has to
    // fail inside the arm, which is what makes the arm loop's catch-all -- not the loader, and not the
    // process -- the last thing between a bad plan and a lost run. The arm body turns 1e18 seconds
    // into a TimeSpan and overflows; the harness is expected to book that as an arm failure.
    [Fact]
    public void ALegalPlanWhoseArmThrowsIsStillLegalForTheLoader()
    {
        Assert.True(TryLoadFixture("arm-level-exception.json", out var error), error);
    }

    private static bool TryLoadFixture(string fixture, out string? error) =>
        PlanFile.TryLoad(RepoPaths.Tier0Plan(fixture), out _, out _, out error);

    // A one-arm plan written where the loader can read it, so a numeric boundary can be swept without
    // a fixture per value. `armFields` is the arm body without its braces.
    private static bool TryLoadJson(string name, string armFields, out string? error)
    {
        var path = Path.Combine(Path.GetTempPath(), $"wf-e2e-plan-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, $$"""{"arms":[{"name":"{{name}}",{{armFields}}}]}""");
            return PlanFile.TryLoad(path, out _, out _, out error);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
