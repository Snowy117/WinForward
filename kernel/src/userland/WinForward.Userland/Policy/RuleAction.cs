namespace WinForward.Userland.Policy;

/// <summary>取值即 rule_blob.h 中 <c>rule_action</c> 的编码，也是头部 <c>fallback_action</c> 的白名单。</summary>
public enum RuleAction : byte
{
    Permit = 0,
    Block = 1,
    Redirect = 2,
}
