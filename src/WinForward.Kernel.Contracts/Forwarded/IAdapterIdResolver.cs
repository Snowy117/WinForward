namespace WinForward.Userland.Forwarded;

/// <summary>
/// 把配置里的入口适配器选择器解析成内核使用的接口编号。
/// </summary>
/// <remarks>
/// <para>
/// B2 只在配置解析阶段调用这个接口；真实实现属于 B4（Windows 上把稳定 id / 接口显示名
/// 映射到接口索引）。配置层不猜编号：没有 resolver 时，带 adapter 选择器的 forwarded 规则
/// 直接报错。
/// </para>
/// <para>
/// 调用约定：<paramref name="adapterId"/> 与 <paramref name="adapterName"/> 至少有一个非空，
/// 实现只需解析非空的那个；两个都给时配置层会分别调用一次并比对结果，因此实现不必自己处理
/// 两个选择器冲突的情况。
/// </para>
/// </remarks>
public interface IAdapterIdResolver
{
    /// <summary>
    /// 尝试解析一个适配器选择器。失败时返回 <see langword="false"/>，并通过 <paramref name="error"/>
    /// 给出可读原因（例如“没找到该接口”）。
    /// </summary>
    /// <param name="adapterId">稳定的适配器 id 字符串，未提供时为 <see langword="null"/>。</param>
    /// <param name="adapterName">适配器显示名，未提供时为 <see langword="null"/>。</param>
    /// <param name="id">解析结果；仅在返回 <see langword="true"/> 时有意义。</param>
    /// <param name="error">失败原因；仅在返回 <see langword="false"/> 时有意义。</param>
    bool TryResolve(string? adapterId, string? adapterName, out uint id, out string error);
}
