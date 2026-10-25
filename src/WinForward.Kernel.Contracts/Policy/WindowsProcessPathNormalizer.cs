namespace WinForward.Userland.Policy;

/// <summary>
/// 生产用的路径归一化：DOS 路径 → 设备路径（含目录）→ ASCII 小写。
/// </summary>
/// <remarks>
/// 真实解析走 <see cref="WindowsDevicePath"/> 的句柄法，只能在 Windows 上跑；Linux 测试
/// 注入假解析器，因此这个类型本身不依赖平台。
/// </remarks>
public sealed class WindowsProcessPathNormalizer : IProcessPathNormalizer
{
    private readonly Func<string, ProcessPathResolution?> _resolve;

    /// <summary>注入解析器：入参是调用方写的 DOS 路径，解析不出来返回 null。</summary>
    public WindowsProcessPathNormalizer(Func<string, ProcessPathResolution?> resolver)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        _resolve = resolver;
    }

    /// <summary>真实的 Windows 实现。非 Windows 平台上调用会抛 <see cref="PlatformNotSupportedException"/>。</summary>
    public static WindowsProcessPathNormalizer ForWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "路径型进程选择器需要 Windows 句柄法解析；离线环境请注入解析器");
        }

        return new WindowsProcessPathNormalizer(WindowsDevicePath.Resolve);
    }

    /// <summary>解析不出来时返回 null，由编译器带着规则序号报错。</summary>
    public ProcessPathResolution? Normalize(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        var resolution = _resolve(path);
        if (resolution is null || string.IsNullOrEmpty(resolution.DevicePath))
        {
            return null;
        }

        // 内核侧收到的设备路径是 UTF-16 再转 UTF-8；这里只折叠 ASCII，非 ASCII 原样保留。
        return new ProcessPathResolution(
            AsciiLowercaseNormalizer.Fold(resolution.DevicePath), resolution.IsDirectory);
    }
}
