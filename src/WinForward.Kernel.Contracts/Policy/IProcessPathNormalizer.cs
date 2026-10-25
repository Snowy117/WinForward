namespace WinForward.Userland.Policy;

/// <summary>
/// 把路径型进程选择器变成写进进程池的设备路径（不含 NUL），并报告它是不是目录。
/// </summary>
/// <remarks>
/// <para>
/// 编译期唯一一处「路径表示」的决策点：内核在 ALE_CONNECT_REDIRECT 层拿到的是 ALE_APP_ID
/// 的设备路径，编译期就必须解析成同一种形式，否则规则静默不命中。文件名型选择器不经过
/// 这里，只做 ASCII 小写折叠。
/// </para>
/// <para>
/// 返回 null 表示「这条路径无法表示成内核会比较的形式」，编译器会带着规则序号拒绝编译。
/// </para>
/// </remarks>
public interface IProcessPathNormalizer
{
    ProcessPathResolution? Normalize(string path);
}
