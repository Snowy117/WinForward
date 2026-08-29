using System.Runtime.Versioning;
using WinForward.NdisApi;
using WinForward.Runtime.Capture;

namespace WinForward.Runtime.TcpRedirect;

[SupportedOSPlatform("windows")]
public sealed class TcpRedirectInjector(IPacketReinjector reinjector, NdisPacketBufferPool? bufferPool = null) : ITcpRedirectInjector
{
    private readonly NdisPacketBufferPool _bufferPool = bufferPool ?? NdisPacketBufferPool.Shared;

    public ValueTask InjectAsync(ReadOnlyMemory<byte> rewrittenFrame, bool towardMstcp, nint adapterHandle, CancellationToken cancellationToken)
    {
        using var buffer = _bufferPool.Rent();
        // Per the WinpkFilter pass/revert matrix (design §1): an ON_RECEIVE frame simulates a
        // receive from the interface upward into MSTCP, and an ON_SEND frame injects toward the
        // interface. SendToMstcp therefore tags ON_RECEIVE; the forwarded-direction adapter path
        // (towardMstcp == false) must tag ON_SEND (H3). The previous hard-coded ON_RECEIVE was
        // correct only for the host (toward-MSTCP) path and mis-tagged Hyper-V forwarded injection.
        buffer.SetFrame(rewrittenFrame.Span, towardMstcp ? NdisApiAbi.PacketFlagOnReceive : NdisApiAbi.PacketFlagOnSend, adapterHandle);
        if (towardMstcp) reinjector.SendToMstcp(adapterHandle, buffer);
        else reinjector.SendToAdapter(adapterHandle, buffer);
        return ValueTask.CompletedTask;
    }
}
