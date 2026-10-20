namespace WinForward.Userland.Policy;

/// <summary>
/// 生产用的进程路径归一化：DOS 路径 → ALE_APP_ID 的设备路径（UTF-16）→ ASCII 小写。
/// </summary>
/// <remarks>
/// 内核在 <c>ALE_CONNECT_REDIRECT</c> 层拿不到进程镜像路径，只有 <c>ALE_APP_ID</c>，也就是
/// <c>\device\harddiskvolumeN\...</c> 形式。两边必须比较同一种表示，解析放在编译期做。
///
/// 真实解析调用 <c>fwpuclnt!FwpmGetAppIdFromFileName0</c>，只能在 Windows 上跑；Linux 测试
/// 用注入的解析器（见测试工程的 <c>TestAppId</c>），因此这个类型本身不依赖平台。
/// </remarks>
public sealed class FwpmAppIdNormalizer : IProcessPathNormalizer
{
    private readonly Func<string, string?> _resolve;

    /// <summary>注入解析器：入参是调用方写的 DOS 路径，返回设备路径；解析不出来返回 null。</summary>
    public FwpmAppIdNormalizer(Func<string, string?> resolve)
    {
        ArgumentNullException.ThrowIfNull(resolve);
        _resolve = resolve;
    }

    /// <summary>真实的 Windows 实现。非 Windows 平台上调用会抛 <see cref="PlatformNotSupportedException"/>。</summary>
    public static FwpmAppIdNormalizer ForWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "FwpmGetAppIdFromFileName0 只在 Windows 上可用；离线环境请注入解析器");
        }

        return new FwpmAppIdNormalizer(WindowsAppId.Resolve);
    }

    /// <summary>解析不出来时返回 null，由编译器带着规则序号报错。</summary>
    public string? Normalize(string path)
    {
        string? devicePath = _resolve(path);
        if (string.IsNullOrEmpty(devicePath))
        {
            return null;
        }

        // 内核侧收到的设备路径是 UTF-16 再转 UTF-8；这里只折叠 ASCII，非 ASCII 原样保留。
        return AsciiLowercaseNormalizer.Instance.Normalize(devicePath);
    }
}
