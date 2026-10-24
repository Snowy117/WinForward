using WinForward.Userland.Policy;

namespace WinForward.Userland.Tests;

internal static class CompilerSelectorTests
{
    internal static void Register()
    {
        TestRunner.Add("compiler-selector/文件名选择器只折叠大小写", FileNameSelector);
        TestRunner.Add("compiler-selector/文件名选择器不调用路径解析器", FileNameSkipsResolver);
        TestRunner.Add("compiler-selector/精确文件路径产出发设备路径与 is_directory=0", ExactFilePath);
        TestRunner.Add("compiler-selector/目录前缀选择器标记 is_directory=1", DirectorySelector);
        TestRunner.Add("compiler-selector/带尾斜杠的目录与不带斜杠去重", DirectoryTrailingSlash);
        TestRunner.Add("compiler-selector/路径解析失败带规则序号与原文", ResolutionFailure);
        TestRunner.Add("compiler-selector/block 规则接受精确路径数组", BlockExactPathArray);
        TestRunner.Add("compiler-selector/block 规则拒绝文件名选择器", BlockFileNameRejected);
        TestRunner.Add("compiler-selector/block 规则拒绝目录前缀选择器", BlockDirectoryRejected);
        TestRunner.Add("compiler-selector/block 规则允许不写进程选择器", BlockAnyProcess);
        TestRunner.Add("compiler-selector/同一规则内进程选择器去重", SelectorDeduplication);
    }

    private static void FileNameSelector()
    {
        byte[] blob = TestAppId.Compile(new PolicyTable(
            new[] { Permit(new[] { ProcessSelector.Parse("Svchost.EXE") }) },
            RuleAction.Permit));

        RuleBlobProcessSelector selector = BlobReader.Selector(blob, 0);
        Check.Equal(RuleBlobProcessSelector.KindFileName, selector.Kind, "kind = 文件名");
        Check.Equal((byte)0, selector.IsDirectory, "文件名选择器的 is_directory 必须是 0");
        Check.Equal("svchost.exe", BlobReader.ProcessString(blob, selector.StringOffset), "只折叠 ASCII 大小写");
        Check.Equal(1U, BlobReader.Rule(blob, 0).ProcessCount, "进程数");
    }

    private static void FileNameSkipsResolver()
    {
        PolicyTable table = new(
            new[] { Permit(new[] { ProcessSelector.Parse("svchost.exe") }) },
            RuleAction.Permit);

        byte[] blob = RuleBlobCompiler.Compile(table, ThrowingNormalizer.Instance);
        Check.Equal("svchost.exe",
                    BlobReader.ProcessString(blob, BlobReader.Selector(blob, 0).StringOffset),
                    "文件名选择器不经过路径归一化");
    }

    private static void ExactFilePath()
    {
        byte[] blob = TestAppId.Compile(new PolicyTable(
            new[] { Permit(new[] { ProcessSelector.Parse(@"C:\WINDOWS\SYSTEM32\SVCHOST.EXE") }) },
            RuleAction.Permit));

        RuleBlobProcessSelector selector = BlobReader.Selector(blob, 0);
        Check.Equal(RuleBlobProcessSelector.KindPath, selector.Kind, "kind = 路径");
        Check.Equal((byte)0, selector.IsDirectory, "文件路径不是目录");
        Check.Equal(TestAppId.SvchostDevice, BlobReader.ProcessString(blob, selector.StringOffset), "设备路径已折叠 ASCII");
    }

    private static void DirectorySelector()
    {
        byte[] blob = TestAppId.Compile(new PolicyTable(
            new[] { Permit(new[] { ProcessSelector.Parse(@"C:\Program Files\MyApp") }) },
            RuleAction.Permit));

        RuleBlobProcessSelector selector = BlobReader.Selector(blob, 0);
        Check.Equal(RuleBlobProcessSelector.KindPath, selector.Kind, "kind = 路径");
        Check.Equal((byte)1, selector.IsDirectory, "目录标记");
        Check.Equal(TestAppId.MyAppDevice, BlobReader.ProcessString(blob, selector.StringOffset), "目录也按设备路径落盘");
    }

    private static void DirectoryTrailingSlash()
    {
        byte[] blob = TestAppId.Compile(new PolicyTable(
            new[]
            {
                Permit(new[]
                {
                    ProcessSelector.Parse(@"C:\Program Files\MyApp"),
                    ProcessSelector.Parse(@"C:\Program Files\MyApp\"),
                }),
            },
            RuleAction.Permit));

        Check.Equal(1U, BlobReader.Rule(blob, 0).ProcessCount, "尾斜杠归一后与不带斜杠的是同一条");
        Check.Equal((byte)1, BlobReader.Selector(blob, 0).IsDirectory, "仍是目录");
    }

    private static void ResolutionFailure()
    {
        PolicyTable table = new(
            new[]
            {
                new PolicyRule { Action = RuleAction.Permit },
                Permit(new[] { ProcessSelector.Parse(@"c:\nope\missing.exe") }),
            },
            RuleAction.Permit);

        Check.ThrowsContaining<PolicyCompileException>(
            () => TestAppId.Compile(table), "#1", "解析失败要带规则序号");
        Check.ThrowsContaining<PolicyCompileException>(
            () => TestAppId.Compile(table), @"c:\nope\missing.exe", "解析失败要带选择器原文");
    }

    private static void BlockExactPathArray()
    {
        byte[] blob = TestAppId.Compile(new PolicyTable(
            new[]
            {
                new PolicyRule
                {
                    Processes = new[]
                    {
                        ProcessSelector.Parse(TestAppId.SvchostDos),
                        ProcessSelector.Parse(TestAppId.AppDos),
                    },
                    Action = RuleAction.Block,
                },
            },
            RuleAction.Permit));

        Check.Equal(2U, BlobReader.Rule(blob, 0).ProcessCount, "block 允许精确路径数组");
        Check.Equal((byte)1, BlobReader.Selector(blob, 1).Kind, "两条都是路径选择器");
        Check.Equal((byte)0, BlobReader.Selector(blob, 1).IsDirectory, "两条都不是目录");
    }

    private static void BlockFileNameRejected() =>
        Check.ThrowsContaining<PolicyCompileException>(
            () => TestAppId.Compile(new PolicyTable(
                new[] { new PolicyRule { Processes = new[] { ProcessSelector.Parse("svchost.exe") }, Action = RuleAction.Block } },
                RuleAction.Permit)),
            "文件名", "block 规则不能写文件名选择器");

    private static void BlockDirectoryRejected() =>
        Check.ThrowsContaining<PolicyCompileException>(
            () => TestAppId.Compile(new PolicyTable(
                new[] { new PolicyRule { Processes = new[] { ProcessSelector.Parse(TestAppId.MyAppDos) }, Action = RuleAction.Block } },
                RuleAction.Permit)),
            "目录前缀", "block 规则不能写目录前缀选择器");

    private static void BlockAnyProcess()
    {
        byte[] blob = TestAppId.Compile(new PolicyTable(
            new[] { new PolicyRule { Action = RuleAction.Block } },
            RuleAction.Permit));

        Check.Equal(0U, BlobReader.Rule(blob, 0).ProcessCount, "不写进程 = 任意进程");
    }

    private static void SelectorDeduplication()
    {
        byte[] blob = TestAppId.Compile(new PolicyTable(
            new[]
            {
                Permit(new[]
                {
                    ProcessSelector.Parse(TestAppId.SvchostDos),
                    ProcessSelector.Parse(@"C:\WINDOWS\SYSTEM32\SVCHOST.EXE"),
                }),
            },
            RuleAction.Permit));

        Check.Equal(1U, BlobReader.Rule(blob, 0).ProcessCount, "大小写不同的同一路径只保留一条");
        Check.Equal(1U, BlobReader.Header(blob).SelectorPoolSize / RuleBlobLayout.ProcessSelectorSize, "selector 池只有一条");
    }

    private static PolicyRule Permit(ProcessSelector[] processes) => new()
    {
        Processes = processes,
        Action = RuleAction.Permit,
    };

    private sealed class ThrowingNormalizer : IProcessPathNormalizer
    {
        internal static ThrowingNormalizer Instance { get; } = new();

        public ProcessPathResolution? Normalize(string path) =>
            throw new TestFailure($"文件名选择器不应调用路径解析器：{path}");
    }
}
