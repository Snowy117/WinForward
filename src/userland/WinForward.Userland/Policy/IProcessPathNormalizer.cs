namespace WinForward.Userland.Policy;

/// <summary>
/// 把调用方写的进程路径变成写进进程池的字符串（不含 NUL）。
/// </summary>
/// <remarks>
/// 编译期唯一一处「路径表示」的决策点：内核在 ALE_CONNECT_REDIRECT 层拿到的是 ALE_APP_ID
/// 的设备路径，编译期就必须解析成同一种形式，否则规则静默不命中。
///
/// 返回 null 表示「这条路径无法表示成内核会比较的形式」，编译器会带着规则序号拒绝编译。
/// </remarks>
public interface IProcessPathNormalizer
{
    string? Normalize(string path);
}
