using WinForward.Userland.Policy;

namespace WinForward.Userland.Tests;

/// <summary>离线测试用的 ALE_APP_ID 解析表：规则侧写 DOS 路径，连接侧用设备路径。</summary>
internal static class TestAppId
{
    internal const string SvchostDos = @"c:\windows\system32\svchost.exe";
    internal const string SvchostNoExtensionDos = @"c:\windows\system32\svchost";
    internal const string SvchostBakDos = SvchostDos + ".bak";
    internal const string AppDos = @"c:\program files\app\app.exe";
    internal const string OtherDos = @"c:\other.exe";
    internal const string UnicodeDos = @"c:\users\张三\工具.exe";

    internal const string System32Dos = @"c:\windows\system32";
    internal const string MyAppDos = @"c:\program files\myapp";
    internal const string MyAppExeDos = @"c:\program files\myapp\app.exe";
    internal const string MyAppBinDos = @"c:\program files\myapp\bin";
    internal const string MyAppBinExeDos = @"c:\program files\myapp\bin\tool.exe";
    internal const string MyApp2ExeDos = @"c:\program files\myapp2\x.exe";
    internal const string ToolsDos = @"c:\tools";
    internal const string ToolsExeDos = @"c:\tools\curl.exe";
    internal const string Tools2ExeDos = @"c:\tools2\curl.exe";

    internal const string SvchostDevice = @"\device\harddiskvolume1\windows\system32\svchost.exe";
    internal const string SvchostDeviceUpper = @"\DEVICE\HARDDISKVOLUME1\WINDOWS\SYSTEM32\SVCHOST.EXE";
    internal const string SvchostNoExtensionDevice = @"\device\harddiskvolume1\windows\system32\svchost";
    internal const string SvchostBakDevice = SvchostDevice + ".bak";
    internal const string AppDevice = @"\device\harddiskvolume3\program files\app\app.exe";
    internal const string OtherDevice = @"\device\harddiskvolume1\other.exe";
    internal const string UnicodeDevice = @"\device\harddiskvolume1\users\张三\工具.exe";
    internal const string UnicodeDeviceUpper = @"\DEVICE\HARDDISKVOLUME1\USERS\张三\工具.EXE";

    internal const string System32Device = @"\device\harddiskvolume1\windows\system32";
    internal const string MyAppDevice = @"\device\harddiskvolume3\program files\myapp";
    internal const string MyAppExeDevice = @"\device\harddiskvolume3\program files\myapp\app.exe";
    internal const string MyAppBinDevice = @"\device\harddiskvolume3\program files\myapp\bin";
    internal const string MyAppBinExeDevice = @"\device\harddiskvolume3\program files\myapp\bin\tool.exe";
    internal const string MyApp2ExeDevice = @"\device\harddiskvolume3\program files\myapp2\x.exe";
    internal const string ToolsDevice = @"\device\harddiskvolume2\tools";
    internal const string ToolsExeDevice = @"\device\harddiskvolume2\tools\curl.exe";
    internal const string Tools2ExeDevice = @"\device\harddiskvolume2\tools2\curl.exe";

    private static readonly IReadOnlyDictionary<string, ProcessPathResolution> Map =
        new Dictionary<string, ProcessPathResolution>(StringComparer.OrdinalIgnoreCase)
        {
            [SvchostDos] = new(SvchostDevice, isDirectory: false),
            [SvchostNoExtensionDos] = new(SvchostNoExtensionDevice, isDirectory: false),
            [SvchostBakDos] = new(SvchostBakDevice, isDirectory: false),
            [AppDos] = new(AppDevice, isDirectory: false),
            [OtherDos] = new(OtherDevice, isDirectory: false),
            [UnicodeDos] = new(UnicodeDevice, isDirectory: false),
            [System32Dos] = new(System32Device, isDirectory: true),
            [MyAppDos] = new(MyAppDevice, isDirectory: true),
            [MyAppExeDos] = new(MyAppExeDevice, isDirectory: false),
            [MyAppBinDos] = new(MyAppBinDevice, isDirectory: true),
            [MyAppBinExeDos] = new(MyAppBinExeDevice, isDirectory: false),
            [MyApp2ExeDos] = new(MyApp2ExeDevice, isDirectory: false),
            [ToolsDos] = new(ToolsDevice, isDirectory: true),
            [ToolsExeDos] = new(ToolsExeDevice, isDirectory: false),
            [Tools2ExeDos] = new(Tools2ExeDevice, isDirectory: false),
        };

    internal static WindowsProcessPathNormalizer Normalizer { get; } = new(Resolve);

    internal static ProcessPathResolution? Resolve(string dosPath)
    {
        string key = dosPath.Length > 1 ? dosPath.TrimEnd('\\') : dosPath;
        return Map.TryGetValue(key, out ProcessPathResolution? resolution) ? resolution : null;
    }

    internal static byte[] Compile(PolicyTable table) => RuleBlobCompiler.Compile(table, Normalizer);
}
