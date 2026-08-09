using System.Runtime.Versioning;
using WinForward.NdisApi;

namespace WinForward.Runtime;

[SupportedOSPlatform("windows")]
public sealed class TcpRedirectInjector(IPacketReinjector reinjector) : ITcpRedirectInjector
{
    public ValueTask InjectAsync(ReadOnlyMemory<byte> rewrittenFrame, bool towardMstcp, nint adapterHandle, CancellationToken cancellationToken)
    {
        using var buffer = new NdisPacketBuffer();
        // SendToMstcp simulates a receive from the selected interface upward into the Windows
        // TCP/IP stack; SendToAdapter injects toward the interface. SendToMstcp frames are always
        // tagged ON_RECEIVE regardless of the original capture direction.
        buffer.SetFrame(rewrittenFrame.Span, NdisApiAbi.PacketFlagOnReceive, adapterHandle);
        if (towardMstcp) reinjector.SendToMstcp(adapterHandle, buffer);
        else reinjector.SendToAdapter(adapterHandle, buffer);
        return ValueTask.CompletedTask;
    }
}
