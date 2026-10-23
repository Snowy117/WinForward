using System.Net;
using System.Runtime.InteropServices;
using WinForward.Userland.Device;

namespace WinForward.Userland.Relay;

/// <summary>原始目的地来自哪条接缝：host 的 redirect context，还是 forwarded 的 redirect 表。</summary>
public enum RedirectSource
{
    Host = 0,
    Forwarded = 1,
}

/// <summary>
/// 中继需要的「原始目的地」。host 路径从 <c>SIO_QUERY_WFP_CONNECTION_REDIRECT_CONTEXT</c> 解析；
/// forwarded 路径从驱动的 redirect 表查询，额外带 <see cref="UpstreamId"/> 与 <see cref="IngressIfIndex"/>。
/// </summary>
public sealed class RedirectDestination
{
    private readonly byte _family;
    private readonly byte _protocol;

    private RedirectDestination(byte family,
                                byte protocol,
                                IPEndPoint endPoint,
                                int processId,
                                int ruleIndex,
                                RedirectSource source,
                                uint? upstreamId,
                                uint ingressIfIndex)
    {
        _family = family;
        _protocol = protocol;
        EndPoint = endPoint;
        ProcessId = processId;
        RuleIndex = ruleIndex;
        Source = source;
        UpstreamId = upstreamId;
        IngressIfIndex = ingressIfIndex;
    }

    public IPEndPoint EndPoint { get; }

    public int ProcessId { get; }

    /// <summary>命中的规则序号；未命中（兜底动作）时驱动写的是 0xFFFFFFFF。</summary>
    public int RuleIndex { get; }

    /// <summary>IPPROTO_TCP 或 IPPROTO_UDP，来自 redirect context 的 protocol 字段。</summary>
    public byte Protocol => _protocol;

    public RedirectSource Source { get; }

    /// <summary>forwarded 路径的 upstream_id；host 路径为 null。</summary>
    public uint? UpstreamId { get; }

    /// <summary>forwarded 路径的入口 ifIndex；host 路径为 0。</summary>
    public uint IngressIfIndex { get; }

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

        IPAddress? address = ParseAddress(context.Family, context.OriginalAddress);
        if (address is null)
        {
            error = $"redirect context 地址族 {context.Family} 不是 IPv4/IPv6";
            return false;
        }

        if (context.OriginalPort == 0)
        {
            error = "redirect context 的原始端口是 0";
            return false;
        }

        destination = new RedirectDestination(context.Family,
                                              context.Protocol,
                                              new IPEndPoint(address, context.OriginalPort),
                                              (int)context.ProcessId,
                                              (int)context.RuleIndex,
                                              RedirectSource.Host,
                                              null,
                                              0);
        error = string.Empty;
        return true;
    }

    /// <summary>把驱动 redirect 表的查询结果转成中继用的目的地；字段不合法时拒绝，不猜。</summary>
    public static bool TryFromForwarded(ProbeForwardedRedirect redirect,
                                        out RedirectDestination? destination,
                                        out string error)
    {
        destination = null;
        if (redirect.Magic != ProbeForwardedRedirect.MagicValue ||
            (redirect.Version != ProbeForwardedRedirect.VersionValue &&
             redirect.Version != ProbeForwardedRedirect.LegacyVersionValue))
        {
            error = $"forwarded redirect 魔数/版本不匹配（0x{redirect.Magic:X8}/{redirect.Version}）";
            return false;
        }

        if (redirect.Protocol != RedirectContext.ProtocolTcp && redirect.Protocol != RedirectContext.ProtocolUdp)
        {
            error = $"forwarded redirect 协议 {redirect.Protocol} 不是 TCP/UDP";
            return false;
        }

        if (redirect.Family != 4 && redirect.Family != 6)
        {
            error = $"forwarded redirect 地址族 {redirect.Family} 不是 IPv4/IPv6";
            return false;
        }

        if (redirect.ServerPort == 0 || redirect.ClientPort == 0 || redirect.ListenerPort == 0)
        {
            error = "forwarded redirect 的端口是 0";
            return false;
        }

        IPAddress server = redirect.ServerIP;
        destination = new RedirectDestination(redirect.Family,
                                              redirect.Protocol,
                                              new IPEndPoint(server, redirect.ServerPort),
                                              0,
                                              (int)redirect.RuleIndex,
                                              RedirectSource.Forwarded,
                                              redirect.UpstreamId,
                                              redirect.IngressIfIndex);
        error = string.Empty;
        return true;
    }

    private static IPAddress? ParseAddress(byte family, RedirectContext.AddressStorage address)
    {
        Span<byte> bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref address, 1));
        return family switch
        {
            RedirectContext.FamilyIpv4 => new IPAddress(bytes[..4]),
            RedirectContext.FamilyIpv6 => new IPAddress(bytes),
            _ => null,
        };
    }

    public override string ToString() => $"{EndPoint.Address}:{EndPoint.Port}";

    public string FamilyText => _family == RedirectContext.FamilyIpv6 ? "IPv6" : "IPv4";

    public string ProtocolText => _protocol == RedirectContext.ProtocolUdp ? "UDP" : "TCP";
}
