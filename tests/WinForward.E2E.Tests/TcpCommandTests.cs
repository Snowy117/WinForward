using WinForward.E2E.Wire;
using Xunit;

namespace WinForward.E2E.Tests;

public sealed class TcpCommandTests
{
    // Regression guard (green today by construction): the arm publishes Name(mode) as a metric and
    // the analyzer keys on those strings, so a new enum member that shares a name, or a defined
    // member without a name of its own, would silently merge two rows. An unnamed member cannot reach
    // a published record at all: Name throws instead of returning a fallback literal.
    [Fact]
    public void EveryDefinedModeHasItsOwnName()
    {
        var modes = Enum.GetValues<TcpMode>();
        var names = modes.Select(TcpCommand.Name).ToArray();

        Assert.Equal(modes.Length, names.Distinct(StringComparer.Ordinal).Count());
        Assert.DoesNotContain("unknown", names);
    }

    [Fact]
    public void EveryDefinedVerdictHasItsOwnName()
    {
        var verdicts = Enum.GetValues<TcpVerdict>();
        var names = verdicts.Select(TcpCommand.Name).ToArray();

        Assert.Equal(verdicts.Length, names.Distinct(StringComparer.Ordinal).Count());
        // TcpVerdict.Error is the one member whose published name is the literal "error", so it is
        // excluded; every other member reaching "error" would mean two verdicts share a ledger key.
        Assert.DoesNotContain("error", verdicts.Where(verdict => verdict != TcpVerdict.Error).Select(TcpCommand.Name));
    }

    [Fact]
    public void AnUndefinedModeHasNoPublishedName() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => TcpCommand.Name((TcpMode)255));

    [Fact]
    public void AnUndefinedVerdictHasNoPublishedName() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => TcpCommand.Name((TcpVerdict)255));

    [Fact]
    public void ACleanCommandRoundTripsThroughTheParser() => AssertRoundTrip(TcpMode.Clean, 0u);

    [Fact]
    public void AResetCommandRoundTripsThroughTheParser() => AssertRoundTrip(TcpMode.ResetAfterN, 4096u);

    [Fact]
    public void AStallCommandRoundTripsThroughTheParser() => AssertRoundTrip(TcpMode.Stall, uint.MaxValue);

    private static void AssertRoundTrip(TcpMode mode, uint expectedBytes)
    {
        var payload = new byte[TcpCommand.PayloadLength];

        TcpCommand.Write(payload, mode, expectedBytes);

        Assert.True(TcpCommand.TryParse(payload, out var parsedMode, out var parsedBytes));
        Assert.Equal(mode, parsedMode);
        Assert.Equal(expectedBytes, parsedBytes);
    }

    [Fact]
    public void AnUndefinedModeByteIsRejected()
    {
        var payload = new byte[TcpCommand.PayloadLength];
        TcpCommand.Write(payload, TcpMode.Stall, 1);
        payload[0] = (byte)TcpMode.Stall + 1;

        Assert.False(TcpCommand.TryParse(payload, out _, out _));
    }

    [Fact]
    public void APayloadOfTheWrongLengthIsRejected()
    {
        var payload = new byte[TcpCommand.PayloadLength];
        TcpCommand.Write(payload, TcpMode.Clean, 1);

        Assert.False(TcpCommand.TryParse(payload.AsSpan(1), out _, out _));
        Assert.False(TcpCommand.TryParse(payload.AsSpan(0, TcpCommand.PayloadLength - 1), out _, out _));
    }
}
