using System.Runtime.Versioning;
using WinForward.NdisApi;

namespace WinForward.Runtime;

[SupportedOSPlatform("windows")]
public sealed class TcpRedirectInjector(IPacketReinjector reinjector) : ITcpRedirectInjector
{
    public ValueTask InjectAsync(ReadOnlyMemory<byte> rewrittenFrame, bool isOnSend, nint adapterHandle, CancellationToken cancellationToken)
    {
        using var buffer = new NdisPacketBuffer();
        var deviceFlags = isOnSend ? NdisApiAbi.PacketFlagOnSend : NdisApiAbi.PacketFlagOnReceive;
        buffer.SetFrame(rewrittenFrame.Span, deviceFlags, adapterHandle);
        reinjector.SendToMstcp(adapterHandle, buffer);
        return ValueTask.CompletedTask;
    }
}
