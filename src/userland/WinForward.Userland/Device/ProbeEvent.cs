using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace WinForward.Userland.Device;

/// <summary>
/// <c>probe_event</c> 的逐字段镜像：64 字节，Pack = 8。环形事件日志的一条，
/// 地址按原样保存（IPv4 只用前 4 字节），端口是主机字节序。
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 8)]
public struct ProbeEvent
{
    public ulong Timestamp100ns;
    public uint Sequence;
    public uint ProcessId;
    public AddressBytes RemoteAddress;
    public AddressBytes LocalAddress;
    public ushort RemotePort;
    public ushort LocalPort;
    public ushort RedirectPort;
    public byte Decision;
    public byte Family;
    public byte Action;
    public byte Branch;
    public uint MatchedRule;

    [InlineArray(16)]
    public struct AddressBytes
    {
        private byte _element0;
    }
}
