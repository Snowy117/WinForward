namespace WinForward.Userland.Policy;

/// <summary>取值与 rule_blob.h 的 <c>rule_protocol_*</c> 一致：v1 只认「任意」与 TCP。</summary>
public enum RuleProtocol : byte
{
    Any = 0,
    Tcp = 6,
}
