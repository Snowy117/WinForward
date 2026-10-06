namespace WinForward.E2E.Wire;

/// <summary>
/// The half-close trailer: what the target writes once the client's FIN arrives, and what the
/// client counts before it calls a <see cref="TcpMode.HalfClose"/> connection clean. Both ends
/// must read the shape from here: a trailer the target writes and the client does not expect is
/// indistinguishable from a product that failed half-close.
/// </summary>
internal static class TrailerProtocol
{
    /// <summary>Payload bytes in one trailer frame.</summary>
    internal const int PayloadBytes = 256;

    /// <summary>Trailer frames the target writes after the FIN. A short read of any of them is a violation.</summary>
    internal const int FrameCount = 3;

    /// <summary>The whole trailer, derived so the two ends cannot disagree about the total.</summary>
    internal const int TotalBytes = FrameCount * PayloadBytes;

    /// <summary>Sequences the target stamps the trailer with, above every data sequence.</summary>
    internal const ulong SequenceBase = 0xFFFF_FFFF_FFFF_FF00u;
}
