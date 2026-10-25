using System.Text;
using WinForward.Userland.Policy;

namespace WinForward.Userland.Tests;

internal static class ProcessPathTests
{
    internal static void Register()
    {
        TestRunner.Add("process/恰好 511 字节可以编译", MaxLengthAccepted);
        TestRunner.Add("process/512 字节拒绝编译", OverLimitRejected);
        TestRunner.Add("process/标准代理对按 4 字节 UTF-8 落盘", SurrogatePairAccepted);
        TestRunner.Add("process/孤立代理项拒绝编译", LoneSurrogateRejected);
        TestRunner.Add("process/归一化结果必须是设备路径", NonDevicePathRejected);
        TestRunner.Add("process/不以 \\device\\ 开头的路径拒绝编译", MissingDevicePrefixRejected);
        TestRunner.Add("process/\\device\\ 前缀比较大小写不敏感", DevicePrefixCaseInsensitive);
        TestRunner.Add("process/归一化结果不能以反斜杠结尾", TrailingBackslashRejected);
    }

    private static void MaxLengthAccepted()
    {
        string devicePath = @"\device\" + new string('a', PolicyLimits.MaxProcessPathBytes - 8);
        byte[] blob = RuleBlobCompiler.Compile(Table(), FileNormalizer(devicePath));
        RuleBlobHeader header = BlobReader.Header(blob);

        Check.Equal(511, PolicyLimits.MaxProcessPathBytes, "常量必须是 511");
        Check.Equal(Encoding.UTF8.GetByteCount(devicePath), 511, "设备路径恰好 511 字节");
        Check.Equal(PolicyLimits.MaxProcessPathBytes + 1, (int)header.ProcessPoolSize, "511 字节 + NUL = 512");
        Check.Equal(devicePath, BlobReader.ProcessString(blob, 0), "池内是 511 字节的路径");
    }

    private static void OverLimitRejected()
    {
        string devicePath = @"\device\" + new string('a', PolicyLimits.MaxProcessPathBytes + 1 - 8);

        Check.ThrowsContaining<PolicyCompileException>(
            () => RuleBlobCompiler.Compile(Table(), FileNormalizer(devicePath)),
            "511 字节上限",
            "512 字节必须拒绝");
    }

    private static void SurrogatePairAccepted()
    {
        const string devicePath = @"\device\harddiskvolume1\工具😀.exe";
        byte[] blob = RuleBlobCompiler.Compile(Table(), FileNormalizer(devicePath));
        RuleBlobHeader header = BlobReader.Header(blob);

        Check.Equal(4, Encoding.UTF8.GetByteCount("😀"), "代理对占 4 字节 UTF-8");
        Check.Equal(Encoding.UTF8.GetByteCount(devicePath) + 1, (int)header.ProcessPoolSize, "池长按 UTF-8 字节算");
        Check.Equal(devicePath, BlobReader.ProcessString(blob, 0), "代理对原样落盘");
    }

    private static void LoneSurrogateRejected()
    {
        Check.Throws<PolicyCompileException>(
            () => RuleBlobCompiler.Compile(Table(), FileNormalizer("\\device\\\ud800\\x.exe")),
            "孤立代理项必须拒绝");
    }

    private static void NonDevicePathRejected()
    {
        Check.ThrowsContaining<PolicyCompileException>(
            () => RuleBlobCompiler.Compile(Table(), FileNormalizer(@"c:\app.exe")),
            "设备路径",
            "归一化结果不是设备路径时必须拒绝");
    }

    private static void MissingDevicePrefixRejected()
    {
        Check.ThrowsContaining<PolicyCompileException>(
            () => RuleBlobCompiler.Compile(Table(), FileNormalizer(@"\harddiskvolume1\app.exe")),
            @"\device\",
            "路径以反斜杠开头但不在 \\device\\ 下时必须拒绝");
    }

    private static void DevicePrefixCaseInsensitive()
    {
        const string devicePath = @"\DEVICE\HARDDISKVOLUME1\APP.EXE";
        byte[] blob = RuleBlobCompiler.Compile(Table(), FileNormalizer(devicePath));

        Check.Equal(@"\device\harddiskvolume1\app.exe",
                    BlobReader.ProcessString(blob, 0),
                    "大写设备前缀与路径折叠成小写后落盘");
    }

    private static void TrailingBackslashRejected()
    {
        Check.ThrowsContaining<PolicyCompileException>(
            () => RuleBlobCompiler.Compile(Table(), FileNormalizer(@"\device\harddiskvolume1\dir\")),
            "不以 \\ 结尾",
            "设备路径不能以反斜杠结尾");
    }

    private static WindowsProcessPathNormalizer FileNormalizer(string devicePath) =>
        new(_ => new ProcessPathResolution(devicePath, isDirectory: false));

    private static PolicyTable Table() => new(
        new[] { new PolicyRule { Processes = new[] { ProcessSelector.Parse(@"c:\app.exe") }, Action = RuleAction.Block } },
        RuleAction.Permit);
}
