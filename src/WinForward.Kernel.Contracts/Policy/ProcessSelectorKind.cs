namespace WinForward.Userland.Policy;

/// <summary>
/// 进程选择器的匹配形态。取值与 rule_blob.h 的 <c>process_selector_kind</c> 一致：
/// 0 文件名（匹配连接路径最后一段），1 路径（完整路径相等或目录前缀）。
/// </summary>
public enum ProcessSelectorKind : byte
{
    FileName = 0,
    Path = 1,
}
