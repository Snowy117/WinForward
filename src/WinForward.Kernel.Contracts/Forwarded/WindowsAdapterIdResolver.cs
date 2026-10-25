using System.Net.NetworkInformation;

namespace WinForward.Userland.Forwarded;

/// <summary>
/// Windows 上的 <see cref="IAdapterIdResolver"/>：把配置里的稳定 id 或接口名解析成 ifIndex。
/// </summary>
/// <remarks>
/// <para>
/// 只依赖 .NET 的 <see cref="NetworkInterface"/>，不引入第二套 id 空间：返回的就是
/// <c>GetIPv4Properties().Index</c> / <c>GetIPv6Properties().Index</c>，与 WFP 的
/// <c>FWPS_FIELD_*_MAC_FRAME_NATIVE_INTERFACE_INDEX</c> 是同一个编号。
/// </para>
/// <para>
/// 构造时抓一次接口快照，之后每次解析都走同一份快照：配置层会对 adapterId 与 adapterName
/// 分别调用一次再比对结果，两次之间若重新枚举，接口消失/重新编号会让结果自相矛盾。
/// 快照可以注入，纯映射逻辑因此能在 Linux 上单测。
/// </para>
/// </remarks>
public sealed class WindowsAdapterIdResolver : IAdapterIdResolver
{
    private readonly IReadOnlyList<AdapterSnapshot> _adapters;

    public WindowsAdapterIdResolver()
        : this(Capture())
    {
    }

    internal WindowsAdapterIdResolver(IReadOnlyList<AdapterSnapshot> adapters)
    {
        ArgumentNullException.ThrowIfNull(adapters);
        _adapters = adapters;
    }

    /// <inheritdoc />
    public bool TryResolve(string? adapterId, string? adapterName, out uint id, out string error)
    {
        id = 0;

        if (string.IsNullOrEmpty(adapterId) && string.IsNullOrEmpty(adapterName))
        {
            error = "WindowsAdapterIdResolver: adapterId 与 adapterName 至少需要提供一个";
            return false;
        }

        AdapterSnapshot? byId = null;
        if (!string.IsNullOrEmpty(adapterId))
        {
            byId = Find(adapter => adapter.Id, adapterId);
            if (byId is null)
            {
                error = $"WindowsAdapterIdResolver: 没有 Id 为「{adapterId}」的接口（已枚举 {Describe()}）";
                return false;
            }
        }

        AdapterSnapshot? byName = null;
        if (!string.IsNullOrEmpty(adapterName))
        {
            byName = Find(adapter => adapter.Name, adapterName);
            if (byName is null)
            {
                error = $"WindowsAdapterIdResolver: 没有名为「{adapterName}」的接口（已枚举 {Describe()}）";
                return false;
            }
        }

        if (byId is not null && byName is not null &&
            byId.Value.InterfaceIndex != byName.Value.InterfaceIndex)
        {
            error = $"WindowsAdapterIdResolver: adapterId「{adapterId}」→ {Format(byId.Value)} 与 " +
                    $"adapterName「{adapterName}」→ {Format(byName.Value)} 不是同一个接口";
            return false;
        }

        var selected = byId ?? byName!.Value;
        if (selected.InterfaceIndex is null or 0)
        {
            error = $"WindowsAdapterIdResolver: 接口「{selected.Name}」没有可用的 ifIndex" +
                    "（IPv4/IPv6 属性都取不到，接口可能已禁用）";
            return false;
        }

        id = selected.InterfaceIndex.Value;
        error = string.Empty;
        return true;
    }

    internal readonly record struct AdapterSnapshot(string Id, string Name, uint? InterfaceIndex);

    private AdapterSnapshot? Find(Func<AdapterSnapshot, string> selector, string wanted)
    {
        foreach (var adapter in _adapters)
        {
            if (string.Equals(selector(adapter), wanted, StringComparison.OrdinalIgnoreCase))
            {
                return adapter;
            }
        }

        return null;
    }

    private string Describe() =>
        _adapters.Count == 0
            ? "（没有网络接口）"
            : string.Join(", ", _adapters.Select(adapter => $"{adapter.Name}#{Format(adapter)}"));

    private static string Format(AdapterSnapshot adapter) =>
        adapter.InterfaceIndex is null ? "?" : adapter.InterfaceIndex.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static List<AdapterSnapshot> Capture()
    {
        List<AdapterSnapshot> snapshots = [];
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            uint? index = null;
            try
            {
                var properties = adapter.GetIPProperties();
                index = (uint?)properties.GetIPv4Properties()?.Index ?? (uint?)properties.GetIPv6Properties()?.Index;
            }
            catch (NetworkInformationException)
            {
                // 接口在枚举与查询之间消失：保留快照条目，index 为 null，解析时按「没有 ifIndex」报错。
            }
            catch (PlatformNotSupportedException)
            {
                // 非 Windows 平台；解析器只在 Windows 上构造，这里只是让快照可注入、可测。
            }

            snapshots.Add(new AdapterSnapshot(adapter.Id, adapter.Name, index));
        }

        return snapshots;
    }
}
