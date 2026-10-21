using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace WinForward.Userland.Device;

/// <summary>
/// <c>src/common/redirect_context.h</c> 的 <c>redirect_context</c> 镜像：36 字节，Pack = 4。
/// 驱动在 classify 里把原始目的地挂到连接上，用户态用 SIO_QUERY_WFP_CONNECTION_REDIRECT_CONTEXT 读回。
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct RedirectContext
{
    public const uint MagicValue = 0x31434657; // 内存中为 "WFC1"
    public const ushort VersionValue = 1;
    public const byte FamilyIpv4 = 4;
    public const byte FamilyIpv6 = 6;
    public const byte ProtocolTcp = 6;
    public const byte ProtocolUdp = 17;
    public const int AddressSize = 16;

    public uint Magic;
    public uint RuleIndex;
    public uint ProcessId;
    public ushort Version;
    public ushort OriginalPort;
    public byte Family;
    public byte Protocol;
    public ReservedBytes Reserved;
    public AddressStorage OriginalAddress;

    [InlineArray(2)]
    public struct ReservedBytes
    {
        private byte _element0;
    }

    [InlineArray(AddressSize)]
    public struct AddressStorage
    {
        private byte _element0;
    }
}
