using WinForward.Userland.Forwarded;

namespace WinForward.Userland.Tests;

/// <summary>
/// <see cref="WindowsAdapterIdResolver"/> 的纯映射逻辑：用注入的接口快照，不依赖真实的网络适配器。
/// </summary>
internal static class AdapterResolverTests
{
    private static readonly WindowsAdapterIdResolver.AdapterSnapshot[] Adapters =
    {
        new("{A1B2C3D4-0000-0000-0000-000000000001}", "Internal", 5),
        new("{A1B2C3D4-0000-0000-0000-000000000002}", "External", 14),
        new("{A1B2C3D4-0000-0000-0000-000000000003}", "NoIndex", null),
        new("{A1B2C3D4-0000-0000-0000-000000000004}", "ZeroIndex", 0),
    };

    internal static void Register()
    {
        TestRunner.Add("forwarded-adapter/按稳定 id 解析 ifIndex", ResolveById);
        TestRunner.Add("forwarded-adapter/按接口名解析 ifIndex", ResolveByName);
        TestRunner.Add("forwarded-adapter/id 与名字同时给时要求一致", ResolveBoth);
        TestRunner.Add("forwarded-adapter/找不到 id 时拒绝", MissingId);
        TestRunner.Add("forwarded-adapter/找不到名字时拒绝", MissingName);
        TestRunner.Add("forwarded-adapter/两个选择器指向不同接口时拒绝", Conflict);
        TestRunner.Add("forwarded-adapter/接口没有 ifIndex 时拒绝", MissingIndex);
        TestRunner.Add("forwarded-adapter/ifIndex 为 0 时拒绝", ZeroIndex);
        TestRunner.Add("forwarded-adapter/空选择器拒绝", EmptySelector);
        TestRunner.Add("forwarded-adapter/没有接口时错误可读", EmptySnapshot);
    }

    private static void ResolveById()
    {
        WindowsAdapterIdResolver resolver = new(Adapters);
        bool resolved = resolver.TryResolve("{a1b2c3d4-0000-0000-0000-000000000001}", null, out uint id, out string error);
        Check.True(resolved, $"稳定 id 应解析成功：{error}");
        Check.Equal(5U, id, "id 对应的 ifIndex");
        Check.Equal(string.Empty, error, "成功时不带错误");
    }

    private static void ResolveByName()
    {
        WindowsAdapterIdResolver resolver = new(Adapters);
        bool resolved = resolver.TryResolve(null, "external", out uint id, out string error);
        Check.True(resolved, $"接口名大小写不敏感：{error}");
        Check.Equal(14U, id, "名字对应的 ifIndex");
    }

    private static void ResolveBoth()
    {
        WindowsAdapterIdResolver resolver = new(Adapters);
        bool resolved = resolver.TryResolve(
            "{A1B2C3D4-0000-0000-0000-000000000002}", "External", out uint id, out string error);
        Check.True(resolved, $"同一接口的两种写法应一致：{error}");
        Check.Equal(14U, id, "一致的 ifIndex");
    }

    private static void MissingId()
    {
        WindowsAdapterIdResolver resolver = new(Adapters);
        bool resolved = resolver.TryResolve("{00000000-0000-0000-0000-000000000000}", null, out _, out string error);
        Check.True(!resolved, "未知 id 必须失败");
        Check.True(error.Contains("没有 Id 为", StringComparison.Ordinal), $"错误应点名 id：{error}");
    }

    private static void MissingName()
    {
        WindowsAdapterIdResolver resolver = new(Adapters);
        bool resolved = resolver.TryResolve(null, "Wireless", out _, out string error);
        Check.True(!resolved, "未知名字必须失败");
        Check.True(error.Contains("没有名为", StringComparison.Ordinal), $"错误应点名名字：{error}");
    }

    private static void Conflict()
    {
        WindowsAdapterIdResolver resolver = new(Adapters);
        bool resolved = resolver.TryResolve(
            "{A1B2C3D4-0000-0000-0000-000000000001}", "External", out _, out string error);
        Check.True(!resolved, "id 与名字指向不同接口时必须失败");
        Check.True(error.Contains("不是同一个接口", StringComparison.Ordinal), $"错误应说明冲突：{error}");
    }

    private static void MissingIndex()
    {
        WindowsAdapterIdResolver resolver = new(Adapters);
        bool resolved = resolver.TryResolve(null, "NoIndex", out _, out string error);
        Check.True(!resolved, "没有 ifIndex 的接口必须失败");
        Check.True(error.Contains("没有可用的 ifIndex", StringComparison.Ordinal), $"错误应说明缺 ifIndex：{error}");
    }

    private static void ZeroIndex()
    {
        WindowsAdapterIdResolver resolver = new(Adapters);
        bool resolved = resolver.TryResolve(null, "ZeroIndex", out _, out string error);
        Check.True(!resolved, "ifIndex=0 不能当作有效入口");
        Check.True(error.Contains("没有可用的 ifIndex", StringComparison.Ordinal), $"错误应说明缺 ifIndex：{error}");
    }

    private static void EmptySelector()
    {
        WindowsAdapterIdResolver resolver = new(Adapters);
        bool resolved = resolver.TryResolve(null, null, out _, out string error);
        Check.True(!resolved, "两个选择器都为空必须失败");
        Check.True(error.Contains("至少需要提供一个", StringComparison.Ordinal), $"错误应说明契约：{error}");

        resolved = resolver.TryResolve(string.Empty, string.Empty, out _, out error);
        Check.True(!resolved, "空字符串等同于没有提供");
    }

    private static void EmptySnapshot()
    {
        WindowsAdapterIdResolver resolver = new(Array.Empty<WindowsAdapterIdResolver.AdapterSnapshot>());
        bool resolved = resolver.TryResolve(null, "Internal", out _, out string error);
        Check.True(!resolved, "没有任何接口时必须失败");
        Check.True(error.Contains("（没有网络接口）", StringComparison.Ordinal), $"错误应说明枚举结果为空：{error}");
    }
}
