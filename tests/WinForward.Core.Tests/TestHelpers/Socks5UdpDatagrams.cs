using System.Net;
using WinForward.Core;
using WinForward.Protocols;

namespace WinForward.Core.Tests;

/// <summary>
/// Test-side materialization of a SOCKS5 UDP datagram for socket sends. The production encoder
/// writes into a caller-supplied buffer; tests that need a byte[] build one here.
/// </summary>
internal static class Socks5UdpDatagrams
{
    public static byte[] Encode(IPAddress destinationAddress, ushort destinationPort, ReadOnlySpan<byte> payload)
    {
        var datagram = new byte[6 + 16 + payload.Length];
        if (!Socks5UdpCodec.TryEncode(IPAddressValue.From(destinationAddress), destinationPort, payload, datagram, out var written))
        {
            throw new InvalidOperationException("The SOCKS5 UDP encode buffer was too small.");
        }

        return datagram.AsSpan(0, written).ToArray();
    }
}
