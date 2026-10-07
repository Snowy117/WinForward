using System.Buffers.Binary;

namespace WinForward.E2E.Wire;

internal enum TcpMode : byte
{
    Clean = 0,
    ResetAfterN = 1,
    PartialFin = 2,
    HalfClose = 3,
    Stall = 4,
}

internal static class TcpCommand
{
    internal const int PayloadLength = 5;

    /// <summary>
    /// The published name of every mode, keyed by the member itself. The name is a table entry rather
    /// than a switch arm because the arm that would cover "anything else" is the one thing that must
    /// not exist: an undefined member has no name, so a miss throws instead of publishing a mode the
    /// run never used (which the analyzer's per-mode row would silently absorb, D14.23).
    /// </summary>
    private static readonly Dictionary<TcpMode, string> s_modeNames = new()
    {
        [TcpMode.Clean] = "clean",
        [TcpMode.ResetAfterN] = "resetAfterN",
        [TcpMode.PartialFin] = "partialFin",
        [TcpMode.HalfClose] = "halfClose",
        [TcpMode.Stall] = "stall",
    };

    /// <summary>
    /// The published name of every verdict. <see cref="TcpVerdict.Error"/> spells "error" out like
    /// every other member does, so the ledger's verdict keys are exactly the enum's (D14.23).
    /// </summary>
    private static readonly Dictionary<TcpVerdict, string> s_verdictNames = new()
    {
        [TcpVerdict.Clean] = "clean",
        [TcpVerdict.Reset] = "reset",
        [TcpVerdict.PartialFin] = "partialFin",
        [TcpVerdict.HalfClose] = "halfClose",
        [TcpVerdict.Stall] = "stall",
        [TcpVerdict.ClientClosedEarly] = "clientClosedEarly",
        [TcpVerdict.ProtocolError] = "protocolError",
        [TcpVerdict.Error] = "error",
    };

    internal static void Write(Span<byte> destination, TcpMode mode, uint expectedBytes)
    {
        destination[0] = (byte)mode;
        BinaryPrimitives.WriteUInt32BigEndian(destination[1..], expectedBytes);
    }

    internal static bool TryParse(ReadOnlySpan<byte> payload, out TcpMode mode, out uint expectedBytes)
    {
        mode = default;
        expectedBytes = 0;

        if (payload.Length != PayloadLength || payload[0] > (byte)TcpMode.Stall)
        {
            return false;
        }

        mode = (TcpMode)payload[0];
        expectedBytes = BinaryPrimitives.ReadUInt32BigEndian(payload[1..]);
        return true;
    }

    /// <summary>
    /// The published name of <paramref name="mode"/>, which must be a defined member: there is no
    /// fallback name for anything else.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not a defined <see cref="TcpMode"/>.</exception>
    internal static string Name(TcpMode mode) =>
        s_modeNames.TryGetValue(mode, out var name)
            ? name
            : throw new ArgumentOutOfRangeException(nameof(mode), mode, "an undefined tcp mode has no published name");

    /// <summary>
    /// The published name of <paramref name="verdict"/>, which must be a defined member: there is no
    /// fallback name for anything else.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not a defined <see cref="TcpVerdict"/>.</exception>
    internal static string Name(TcpVerdict verdict) =>
        s_verdictNames.TryGetValue(verdict, out var name)
            ? name
            : throw new ArgumentOutOfRangeException(nameof(verdict), verdict, "an undefined tcp verdict has no published name");
}

internal enum TcpVerdict
{
    Clean,
    Reset,
    PartialFin,
    HalfClose,
    Stall,
    ClientClosedEarly,
    ProtocolError,
    Error,
}
