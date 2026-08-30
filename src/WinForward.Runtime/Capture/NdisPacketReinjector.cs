using System.Runtime.Versioning;
using WinForward.NdisApi;

namespace WinForward.Runtime.Capture;

/// <summary>
/// The NDISAPI reinjection primitive used by a pass disposition. Captured frames are sent back
/// toward the adapter (ON_SEND packets) or up toward MSTCP (ON_RECEIVE packets) on the adapter they
/// were captured, matching the WinpkFilter pass/revert matrix. The batched overloads inject a
/// whole accumulated flush in one kernel crossing; the caller must preserve per-direction append
/// order, the buffers array is valid only for the duration of the call, and the batch is
/// all-or-nothing (a failure throws, like the single sends).
/// </summary>
public interface IPacketReinjector
{
    void SendToAdapter(nint adapterHandle, NdisPacketBuffer buffer);
    void SendToMstcp(nint adapterHandle, NdisPacketBuffer buffer);
    void SendPacketsToAdapter(nint adapterHandle, NdisPacketBuffer[] buffers, int count);
    void SendPacketsToMstcp(nint adapterHandle, NdisPacketBuffer[] buffers, int count);
}

/// <summary>
/// The production reinjector that drives the NDISAPI driver. The driver stays owned by the caller.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class NdisPacketReinjector : IPacketReinjector
{
    private readonly NdisApiDriver _driver;

    public NdisPacketReinjector(NdisApiDriver driver) => _driver = driver ?? throw new ArgumentNullException(nameof(driver));

    public void SendToAdapter(nint adapterHandle, NdisPacketBuffer buffer) => _driver.SendPacketToAdapter(adapterHandle, buffer);

    public void SendToMstcp(nint adapterHandle, NdisPacketBuffer buffer) => _driver.SendPacketToMstcp(adapterHandle, buffer);

    public void SendPacketsToAdapter(nint adapterHandle, NdisPacketBuffer[] buffers, int count) => _driver.SendPacketsToAdapter(adapterHandle, buffers, count);

    public void SendPacketsToMstcp(nint adapterHandle, NdisPacketBuffer[] buffers, int count) => _driver.SendPacketsToMstcp(adapterHandle, buffers, count);
}