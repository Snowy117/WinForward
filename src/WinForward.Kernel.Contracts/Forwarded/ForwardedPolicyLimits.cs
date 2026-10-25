namespace WinForward.Userland.Forwarded;

/// <summary>forwarded 规则层的容量上限。改这里必须同步 C++ 侧的说明与 spec。</summary>
/// <remarks>
/// 上限只在编译期生效：内核解码按缓冲区长度判界，不重复做条目数上限 —— blob 只能由本编译器
/// 产出，编译期拒绝后内核永远见不到超限的规则表。
/// </remarks>
public static class ForwardedPolicyLimits
{
    /// <summary>规则条数上限（含）。</summary>
    public const int MaxRules = 4096;

    /// <summary>网段池条目数上限（去重后，含）。</summary>
    public const int MaxNetworkPoolEntries = 4096;
}
