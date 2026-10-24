using WinForward.Userland.Policy;

namespace WinForward.Userland.Tests;

internal static class CompilerLimitTests
{
    internal static void Register()
    {
        TestRunner.Add("compiler-limit/blob 总大小 64 KiB 边界", BlobSizeBoundary);
        TestRunner.Add("compiler-limit/block filter 预算 128 接受 129 拒绝", BlockBudgetBoundary);
        TestRunner.Add("compiler-limit/block 预算把协议与族展开计入", BlockBudgetCountsLayers);
    }

    private static void BlobSizeBoundary()
    {
        const int selectorBytes = 500;

        byte[] accepted = RuleBlobCompiler.Compile(
            LongPathTable(128, selectorBytes), LongPathNormalizer(selectorBytes));
        Check.Equal(128U, BlobReader.Rule(accepted, 0).ProcessCount, "128 条 500 字节选择器");
        Check.True(accepted.Length <= PolicyLimits.RuleBlobMaxSize,
                   $"接受时 blob 长度 {accepted.Length} 不超过 64 KiB");

        PolicyTable rejected = LongPathTable(129, selectorBytes);
        Check.ThrowsContaining<PolicyCompileException>(
            () => RuleBlobCompiler.Compile(rejected, LongPathNormalizer(selectorBytes)),
            "64 KiB", "129 条选择器越过 64 KiB 上限");
    }

    private static void BlockBudgetBoundary()
    {
        byte[] accepted = RuleBlobCompiler.Compile(BudgetTable(128), ExactFileNormalizer.Instance);
        Check.Equal(128U, BlobReader.Rule(accepted, 0).ProcessCount, "128 条精确路径");

        PolicyTable rejected = BudgetTable(129);
        Check.ThrowsContaining<PolicyCompileException>(
            () => RuleBlobCompiler.Compile(rejected, ExactFileNormalizer.Instance),
            "预算超限", "129 条精确路径超过 block filter 预算");
    }

    private static void BlockBudgetCountsLayers()
    {
        // 预算 = 层数 × 协议数 × 进程数 × 端口数；单族（不限定网段）时为 1 层。
        // 协议因子：64 路径 × 2 协议 = 128，恰好接受；65 路径 × 2 协议 = 130，拒绝。
        // 129 路径即使用 1 协议也已超限，无法证明协议因子被计入。
        PolicyTable acceptedProtocols = BudgetTable(64, protocols: new[] { RuleProtocol.Tcp, RuleProtocol.Udp });
        byte[] protocolBlob = RuleBlobCompiler.Compile(acceptedProtocols, ExactFileNormalizer.Instance);
        RuleBlobRule protocolRule = BlobReader.Rule(protocolBlob, 0);
        Check.Equal(64U, protocolRule.ProcessCount, "64 条精确路径");
        Check.Equal(1U, protocolRule.PortCount, "1 条端口区间");
        Check.Equal((byte)3, protocolRule.ProtocolMask, "协议掩码 TCP|UDP");

        PolicyTable rejectedProtocols = BudgetTable(65, protocols: new[] { RuleProtocol.Tcp, RuleProtocol.Udp });
        Check.ThrowsContaining<PolicyCompileException>(
            () => RuleBlobCompiler.Compile(rejectedProtocols, ExactFileNormalizer.Instance),
            "共需要 130 条", "65 路径 × 2 协议展开成 130 条 filter，超过 128 预算");

        // 族因子：双族且不限定网段时每族各展开 1 条 filter。
        // 64 路径 × 2 族 = 128，恰好接受；65 × 2 = 130，拒绝。
        PolicyTable acceptedFamilies = BudgetTable(
            64, families: new[] { RuleAddressFamily.Ipv4, RuleAddressFamily.Ipv6 });
        byte[] familyBlob = RuleBlobCompiler.Compile(acceptedFamilies, ExactFileNormalizer.Instance);
        RuleBlobRule familyRule = BlobReader.Rule(familyBlob, 0);
        Check.Equal(64U, familyRule.ProcessCount, "64 条精确路径（双族）");
        Check.Equal((byte)3, familyRule.FamilyMask, "族掩码 IPv4|IPv6");

        PolicyTable rejectedFamilies = BudgetTable(
            65, families: new[] { RuleAddressFamily.Ipv4, RuleAddressFamily.Ipv6 });
        Check.ThrowsContaining<PolicyCompileException>(
            () => RuleBlobCompiler.Compile(rejectedFamilies, ExactFileNormalizer.Instance),
            "共需要 130 条", "65 路径 × 2 族展开成 130 条 filter，超过 128 预算");
    }

    private static PolicyTable LongPathTable(int count, int bytes) => new(
        new[]
        {
            new PolicyRule
            {
                Processes = Enumerable.Range(0, count)
                    .Select(index => ProcessSelector.Parse($@"c:\p{index}.exe"))
                    .ToArray(),
            },
        },
        RuleAction.Permit);

    private static WindowsProcessPathNormalizer LongPathNormalizer(int bytes) => new(path =>
    {
        int begin = path.IndexOf('p', StringComparison.Ordinal) + 1;
        int end = path.IndexOf('.', begin);
        int index = int.Parse(path[begin..end], System.Globalization.CultureInfo.InvariantCulture);
        string number = index.ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
        return new ProcessPathResolution(@"\device\" + number + new string('a', bytes - 8 - number.Length), false);
    });

    private static PolicyTable BudgetTable(int count,
                                           RuleProtocol[]? protocols = null,
                                           RuleAddressFamily[]? families = null) => new(
        new[]
        {
            new PolicyRule
            {
                Processes = BudgetProcesses(count),
                Protocols = protocols ?? new[] { RuleProtocol.Tcp },
                Families = families ?? new[] { RuleAddressFamily.Ipv4 },
                RemotePorts = new[] { PortRange.Single(80) },
                Action = RuleAction.Block,
            },
        },
        RuleAction.Permit);

    private static ProcessSelector[] BudgetProcesses(int count) =>
        Enumerable.Range(0, count)
            .Select(index => ProcessSelector.Parse($@"c:\f{index}.exe"))
            .ToArray();

    private sealed class ExactFileNormalizer : IProcessPathNormalizer
    {
        internal static ExactFileNormalizer Instance { get; } = new();

        public ProcessPathResolution? Normalize(string path) =>
            new(@"\device\" + path[3..].Replace('\\', '_'), isDirectory: false);
    }
}
