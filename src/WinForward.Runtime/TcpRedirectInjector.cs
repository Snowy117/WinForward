using System.Runtime.Versioning;
using WinForward.NdisApi;

namespace WinForward.Runtime;

[SupportedOSPlatform("windows")]
public sealed class TcpRedirectInjector(IPacketReinjector reinjector) : ITcpRedirectInjector
{
    public ValueTask InjectAsync(ReadOnlyMemory<byte> rewrittenFrame, bool isOnSend, nint adapterHandle, CancellationToken cancellationToken)
    {
        using var buffer = new NdisPacketBuffer();
        // SendToMstcp simulates a receive from the selected interface upward into the Windows
        // TCP/IP stack, so the frame is always tagged ON_RECEIVE regardless of the original
        // capture direction. The rewritten SYN (dst -> loopback listener) and the reverse packet
        // (src -> original remote) both travel toward the local stack this way.
        buffer.SetFrame(rewrittenFrame.Span, NdisApiAbi.PacketFlagOnReceive, adapterHandle);
        reinjector.SendToMstcp(adapterHandle, buffer);
        return ValueTask.CompletedTask;
    }
}
