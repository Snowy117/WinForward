using WinForward.E2E.Wire;
using Xunit;

namespace WinForward.E2E.Tests;

public sealed class FillerTests
{
    // Recorded from an independent implementation of the documented generator, not from a run of
    // Filler.Fill: a golden vector captured from the code under test proves nothing.
    private static readonly byte[] s_goldenFirstSixteen =
    [
        0x32, 0x7B, 0xA5, 0x7A, 0x52, 0x05, 0x58, 0xC2,
        0x52, 0x2F, 0xC6, 0x1A, 0x71, 0x51, 0xAC, 0x02,
    ];

    private static readonly byte[] s_goldenZeroSeedFirstSixteen =
    [
        0x19, 0x3E, 0x3A, 0xB5, 0x1F, 0x37, 0xD0, 0xBF,
        0x39, 0xB8, 0xEE, 0xB4, 0xD3, 0x3C, 0xB8, 0x5F,
    ];

    [Fact]
    public void TheFirstSixteenBytesOfAKnownPairAreTheRecordedVector()
    {
        var payload = new byte[16];

        Filler.Fill(0x11223344u, 42, payload);

        Assert.Equal(s_goldenFirstSixteen, payload);
    }

    [Fact]
    public void AConnectionAndSequenceThatSeedToZeroUseTheSubstituteConstant()
    {
        var payload = new byte[16];

        Filler.Fill(0u, 0, payload);

        Assert.Equal(s_goldenZeroSeedFirstSixteen, payload);
    }

    [Fact]
    public void FillAndMatchesAgreeOnEveryLength()
    {
        foreach (var length in new[] { 0, 1, 7, 64, 1024 })
        {
            var payload = new byte[length];

            Filler.Fill(0x0A0B0C0Du, 1_000_003, payload);

            Assert.True(Filler.Matches(0x0A0B0C0Du, 1_000_003, payload), $"length {length}");
        }
    }

    [Fact]
    public void MatchesRejectsAChangedByte()
    {
        var payload = new byte[64];
        Filler.Fill(7u, 9, payload);
        payload[63] ^= 0x01;

        Assert.False(Filler.Matches(7u, 9, payload));
    }

    [Fact]
    public void MatchesRejectsADifferentSequenceOrConnection()
    {
        var payload = new byte[32];
        Filler.Fill(7u, 9, payload);

        Assert.False(Filler.Matches(7u, 10, payload));
        Assert.False(Filler.Matches(8u, 9, payload));
    }

    // The seed folds the sequence into 32 bits, so two sequences 2^32 apart are the same on the
    // wire; a change that made the high bits significant would invalidate replay/filler checks
    // that today cannot distinguish them.
    [Fact]
    public void OnlyTheLowThirtyTwoBitsOfTheSequenceReachTheSeed()
    {
        var low = new byte[64];
        var wrapped = new byte[64];

        Filler.Fill(0x11223344u, 5, low);
        Filler.Fill(0x11223344u, 5 + (1UL << 32), wrapped);

        Assert.Equal(low, wrapped);
    }
}
