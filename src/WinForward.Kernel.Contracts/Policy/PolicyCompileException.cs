namespace WinForward.Userland.Policy;

/// <summary>
/// 规则模型无法编译成合法 blob 时抛出。用户态是唯一能给出可诊断信息的时刻：
/// 内核侧 try_open_blob 只会整块拒绝，不会指出是哪条规则。
/// </summary>
public sealed class PolicyCompileException : Exception
{
    public PolicyCompileException()
    {
    }

    public PolicyCompileException(string message)
        : base(message)
    {
    }

    public PolicyCompileException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}
