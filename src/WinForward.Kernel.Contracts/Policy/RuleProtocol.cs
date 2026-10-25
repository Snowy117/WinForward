namespace WinForward.Userland.Policy;

/// <summary>取值与 rule_blob.h 的 <c>rule_protocol_*</c> 一致：0 任意 / 6 TCP / 17 UDP。</summary>
public enum RuleProtocol : byte
{
    Any = 0,
    Tcp = 6,
    Udp = 17,
}
