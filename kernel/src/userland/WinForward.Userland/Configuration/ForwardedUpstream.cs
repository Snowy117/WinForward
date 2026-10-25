namespace WinForward.Userland.Configuration;

/// <summary>
/// forwarded blob 的 upstream_id 与 SOCKS5 端点之间的有序映射。
/// </summary>
/// <remarks>
/// <see cref="Id"/> 就是 wf_forwarded_blob 规则里的 upstream_id。顺序与解析顺序一致：
/// socks5Servers 数组顺序 0..n-1，旧 socks5 以 default 追加在最后。B4 只靠这张有序表
/// 就能从 id 找到要连接的上游，不依赖名字字典的枚举顺序。
/// </remarks>
public sealed record ForwardedUpstream(uint Id, string Name, Socks5Endpoint Endpoint);
