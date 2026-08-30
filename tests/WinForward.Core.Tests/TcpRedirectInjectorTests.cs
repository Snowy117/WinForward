using System.Runtime.Versioning;
using WinForward.NdisApi;
using WinForward.Runtime.Capture;
using WinForward.Runtime.TcpRedirect;
using Xunit;

namespace WinForward.Core.Tests;

public sealed class TcpRedirectInjectorTests
{
    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task HostDirectionInjectsTowardMstcpWithOnReceiveFlag()
    {
        // H3: a frame injected toward MSTCP simulates an interface receive (ON_RECEIVE). The TCP
        // redirect injector must tag the buffer with PacketFlagOnReceive for the host (toward-MSTCP)
        // direction — the correct half of the WinpkFilter pass/revert matrix.
        var reinjector = new RecordingRedirectReinjector();
        var injector = new TcpRedirectInjector(reinjector);
        var frame = new byte[] { 0x01, 0x02, 0x03 };

        await injector.InjectAsync(frame, towardMstcp: true, 7, CancellationToken.None);

        Assert.Equal(1, reinjector.ToMstcpCount);
        Assert.Equal(0, reinjector.ToAdapterCount);
        Assert.Equal(NdisApiAbi.PacketFlagOnReceive, reinjector.DeviceFlags);
        Assert.Equal((nint)7, reinjector.Handle);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task ForwardedDirectionInjectsTowardAdapterWithOnSendFlag()
    {
        // H3: a frame injected toward an adapter leaves the stack for the interface, which is an
        // ON_SEND. The forwarded (Hyper-V) direction previously reused ON_RECEIVE and could not
        // reach the VM; the injector must tag PacketFlagOnSend for the toward-adapter path.
        var reinjector = new RecordingRedirectReinjector();
        var injector = new TcpRedirectInjector(reinjector);
        var frame = new byte[] { 0x01, 0x02, 0x03 };

        await injector.InjectAsync(frame, towardMstcp: false, 7, CancellationToken.None);

        Assert.Equal(0, reinjector.ToMstcpCount);
        Assert.Equal(1, reinjector.ToAdapterCount);
        Assert.Equal(NdisApiAbi.PacketFlagOnSend, reinjector.DeviceFlags);
        Assert.Equal((nint)7, reinjector.Handle);
    }

    private sealed class RecordingRedirectReinjector : IPacketReinjector
    {
        public int ToAdapterCount { get; private set; }
        public int ToMstcpCount { get; private set; }
        public uint DeviceFlags { get; private set; }
        public nint Handle { get; private set; }

        public void SendToAdapter(nint adapterHandle, NdisPacketBuffer buffer)
        {
            ToAdapterCount++;
            record(adapterHandle, buffer);
        }

        public void SendToMstcp(nint adapterHandle, NdisPacketBuffer buffer)
        {
            ToMstcpCount++;
            record(adapterHandle, buffer);
        }

        /// <summary>
        /// The TCP redirect injector only ever sends single packets (per-flow SYN/RST, cold path);
        /// batched calls are interface completeness and behave as per-packet singles.
        /// </summary>
        public void SendPacketsToAdapter(nint adapterHandle, NdisPacketBuffer[] buffers, int count)
        {
            for (var index = 0; index < count; index++) SendToAdapter(adapterHandle, buffers[index]);
        }

        public void SendPacketsToMstcp(nint adapterHandle, NdisPacketBuffer[] buffers, int count)
        {
            for (var index = 0; index < count; index++) SendToMstcp(adapterHandle, buffers[index]);
        }

        private void record(nint adapterHandle, NdisPacketBuffer buffer)
        {
            Handle = adapterHandle;
            DeviceFlags = buffer.DeviceFlags;
        }
    }
}