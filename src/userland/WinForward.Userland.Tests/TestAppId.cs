using WinForward.Userland.Policy;

namespace WinForward.Userland.Tests;

/// <summary>
/// 测试用的 ALE_APP_ID 解析表。真实解析只能在 Windows 上做（FwpmGetAppIdFromFileName0），
/// 离线测试注入这张表：规则侧写 DOS 路径，连接侧用内核真正会看到的设备路径。
/// </summary>
internal static class TestAppId
{
    internal const string SvchostDos = @"c:\windows\system32\svchost.exe";
    internal const string SvchostNoExtensionDos = @"c:\windows\system32\svchost";
    internal const string SvchostBakDos = SvchostDos + ".bak";
    internal const string AppDos = @"c:\program files\app\app.exe";
    internal const string OtherDos = @"c:\other.exe";
    internal const string UnicodeDos = @"c:\users\张三\工具.exe";

    internal const string SvchostDevice = @"\device\harddiskvolume1\windows\system32\svchost.exe";
    internal const string SvchostDeviceUpper = @"\DEVICE\HARDDISKVOLUME1\WINDOWS\SYSTEM32\SVCHOST.EXE";
    internal const string SvchostNoExtensionDevice = @"\device\harddiskvolume1\windows\system32\svchost";
    internal const string SvchostBakDevice = SvchostDevice + ".bak";
    internal const string AppDevice = @"\device\harddiskvolume3\program files\app\app.exe";
    internal const string OtherDevice = @"\device\harddiskvolume1\other.exe";
    internal const string UnicodeDevice = @"\device\harddiskvolume1\users\张三\工具.exe";
    internal const string UnicodeDeviceUpper = @"\DEVICE\HARDDISKVOLUME1\USERS\张三\工具.EXE";

    private static readonly IReadOnlyDictionary<string, string> Map =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [SvchostDos] = SvchostDevice,
            [SvchostNoExtensionDos] = SvchostNoExtensionDevice,
            [SvchostBakDos] = SvchostBakDevice,
            [AppDos] = AppDevice,
            [OtherDos] = OtherDevice,
            [UnicodeDos] = UnicodeDevice,
        };

    internal static FwpmAppIdNormalizer Normalizer { get; } = new(Resolve);

    internal static string? Resolve(string dosPath) =>
        Map.TryGetValue(dosPath, out string? devicePath) ? devicePath : null;

    internal static byte[] Compile(PolicyTable table) => RuleBlobCompiler.Compile(table, Normalizer);
}
