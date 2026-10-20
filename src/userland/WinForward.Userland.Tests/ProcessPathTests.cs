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
    }

    private static void MaxLengthAccepted()
    {
        string devicePath = new('a', PolicyLimits.MaxProcessPathBytes);
        byte[] blob = RuleBlobCompiler.Compile(Table(), new FwpmAppIdNormalizer(_ => devicePath));
        RuleBlobHeader header = BlobReader.Header(blob);

        Check.Equal(511, PolicyLimits.MaxProcessPathBytes, "常量必须是 511");
        Check.Equal(PolicyLimits.MaxProcessPathBytes + 1, (int)header.ProcessPoolSize, "511 字节 + NUL = 512");
        Check.Equal(devicePath, BlobReader.ProcessString(blob, 0), "池内是 511 字节的路径");
    }

    private static void OverLimitRejected()
    {
        string devicePath = new('a', PolicyLimits.MaxProcessPathBytes + 1);

        Check.ThrowsContaining<PolicyCompileException>(
            () => RuleBlobCompiler.Compile(Table(), new FwpmAppIdNormalizer(_ => devicePath)),
            "进程路径超过 511 字节上限",
            "512 字节必须拒绝");
    }

    private static void SurrogatePairAccepted()
    {
        const string devicePath = @"\device\harddiskvolume1\工具😀.exe";
        byte[] blob = RuleBlobCompiler.Compile(Table(), new FwpmAppIdNormalizer(_ => devicePath));
        RuleBlobHeader header = BlobReader.Header(blob);

        Check.Equal(4, Encoding.UTF8.GetByteCount("😀"), "代理对占 4 字节 UTF-8");
        Check.Equal(Encoding.UTF8.GetByteCount(devicePath) + 1, (int)header.ProcessPoolSize, "池长按 UTF-8 字节算");
        Check.Equal(devicePath, BlobReader.ProcessString(blob, 0), "代理对原样落盘");
    }

    private static void LoneSurrogateRejected()
    {
        Check.Throws<PolicyCompileException>(
            () => RuleBlobCompiler.Compile(Table(), new FwpmAppIdNormalizer(_ => "c:\\\ud800\\x.exe")),
            "孤立代理项必须拒绝");
    }

    private static PolicyTable Table() => new(
        new[] { new PolicyRule { Process = ProcessSelector.Parse(@"c:\app.exe"), Action = RuleAction.Block } },
        RuleAction.Permit);
}
