using System.Net;
using System.Runtime.InteropServices;
using WinForward.Userland.Device;

namespace WinForward.Userland.Relay;

/// <summary>
/// 从 <c>SIO_QUERY_WFP_CONNECTION_REDIRECT_CONTEXT</c> 读回的字节里解析出的原始目的地。
/// </summary>
public sealed class RedirectDestination
{
    private readonly byte _family;
    private readonly byte _protocol;

    private RedirectDestination(byte family, byte protocol, IPEndPoint endPoint, int processId, int ruleIndex)
    {
        _family = family;
        _protocol = protocol;
        EndPoint = endPoint;
        ProcessId = processId;
        RuleIndex = ruleIndex;
    }

    public IPEndPoint EndPoint { get; }

    public int ProcessId { get; }

    /// <summary>命中的规则序号；未命中（兜底动作）时驱动写的是 0xFFFFFFFF。</summary>
    public int RuleIndex { get; }

    /// <summary>IPPROTO_TCP 或 IPPROTO_UDP，来自 redirect context 的 protocol 字段。</summary>
    public byte Protocol => _protocol;

    public static bool TryParse(ReadOnlySpan<byte> raw, out RedirectDestination? destination, out string error)
    {
        destination = null;
        if (raw.Length < DeviceLayout.RedirectContextSize)
        {
            error = $"redirect context 只有 {raw.Length} 字节，期望 {DeviceLayout.RedirectContextSize}";
            return false;
        }

        RedirectContext context = MemoryMarshal.Read<RedirectContext>(raw);
        if (context.Magic != RedirectContext.MagicValue)
        {
            error = $"redirect context 魔数 0x{context.Magic:X8} 不匹配";
            return false;
        }

        if (context.Version != RedirectContext.VersionValue)
        {
            error = $"redirect context 版本 {context.Version}，期望 {RedirectContext.VersionValue}";
            return false;
        }

        if (context.Protocol != RedirectContext.ProtocolTcp && context.Protocol != RedirectContext.ProtocolUdp)
        {
            error = $"原始目的地协议 {context.Protocol} 不是 TCP/UDP";
            return false;
        }

        byte[] address;
        if (context.Family == RedirectContext.FamilyIpv4)
        {
            address = new byte[4];
            for (int index = 0; index < 4; index++)
            {
                address[index] = context.OriginalAddress[index];
            }
        }
        else if (context.Family == RedirectContext.FamilyIpv6)
        {
            address = new byte[16];
            for (int index = 0; index < 16; index++)
            {
                address[index] = context.OriginalAddress[index];
            }
        }

        else
        {
            error = $"redirect context 地址族 {context.Family} 不是 IPv4/IPv6";
            return false;
        }

        if (context.OriginalPort == 0)
        {
            error = "redirect context 的原始端口是 0";
            return false;
        }

        destination = new RedirectDestination(
            context.Family, context.Protocol, new IPEndPoint(new IPAddress(address), context.OriginalPort),
            (int)context.ProcessId, (int)context.RuleIndex);
        error = string.Empty;
        return true;
    }

    public override string ToString() => $"{EndPoint.Address}:{EndPoint.Port}";

    public string FamilyText => _family == RedirectContext.FamilyIpv6 ? "IPv6" : "IPv4";

    public string ProtocolText => _protocol == RedirectContext.ProtocolUdp ? "UDP" : "TCP";
}
