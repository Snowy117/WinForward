namespace WinForward.Userland.Policy;

/// <summary>
/// 取值与 rule_blob.h 的 <c>rule_family_*</c> 及 matcher.h 的 <c>address_family</c> 一致：
/// 0 不限制，4 / 6 分别是 IPv4 / IPv6。
/// </summary>
public enum RuleAddressFamily : byte
{
    Any = 0,
    Ipv4 = 4,
    Ipv6 = 6,
}
