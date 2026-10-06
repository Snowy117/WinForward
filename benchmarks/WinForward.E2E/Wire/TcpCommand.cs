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

    internal static string Name(TcpMode mode) => mode switch
    {
        TcpMode.Clean => "clean",
        TcpMode.ResetAfterN => "resetAfterN",
        TcpMode.PartialFin => "partialFin",
        TcpMode.HalfClose => "halfClose",
        TcpMode.Stall => "stall",
        _ => "unknown",
    };

    internal static string Name(TcpVerdict verdict) => verdict switch
    {
        TcpVerdict.Clean => "clean",
        TcpVerdict.Reset => "reset",
        TcpVerdict.PartialFin => "partialFin",
        TcpVerdict.HalfClose => "halfClose",
        TcpVerdict.Stall => "stall",
        TcpVerdict.ClientClosedEarly => "clientClosedEarly",
        TcpVerdict.ProtocolError => "protocolError",
        _ => "error",
    };
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
