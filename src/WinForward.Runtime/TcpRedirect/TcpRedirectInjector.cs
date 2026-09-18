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

    public void Inject(NdisPacketBuffer stagedFrame, bool towardMstcp, nint adapterHandle, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stagedFrame);
        // The caller staged the frame (bytes, direction flag, adapter handle) into the buffer it
        // rented — the same tag matrix as the copy-based overload applies through its CompleteFrame
        // — so this path only performs the native send and hands the buffer straight back. The
        // token is intentionally unread: the native send is synchronous and unconditional, exactly
        // like the copy-based overload's.
        if (towardMstcp) reinjector.SendToMstcp(adapterHandle, stagedFrame);
        else reinjector.SendToAdapter(adapterHandle, stagedFrame);
    }
}
