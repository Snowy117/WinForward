using WinForward.Userland.Policy;

namespace WinForward.Userland.Tests;

internal static class RuleOrderSupport
{
    internal static PolicyRule Rule(RuleAction action,
                                    RuleAddressFamily[]? families = null,
                                    RuleProtocol[]? protocols = null,
                                    PortRange[]? ports = null,
                                    RemoteNetwork[]? networks = null,
                                    ProcessSelector[]? processes = null) =>
        new()
        {
            Action = action,
            Families = families,
            Protocols = protocols,
            RemotePorts = ports,
            RemoteNetworks = networks,
            Processes = processes,
        };

    internal static PolicyTable Table(RuleAction fallback, params PolicyRule[] rules) => new(rules, fallback);

    internal static PolicyTable Permit(params PolicyRule[] rules) => new(rules, RuleAction.Permit);

    internal static PortRange[] Ports(ushort begin, ushort end) => new[] { new PortRange(begin, end) };

    internal static PortRange[] Ports(params ushort[] singles) => singles.Select(PortRange.Single).ToArray();

    internal static RemoteNetwork[] Networks(params string[] cidrs) => cidrs.Select(RemoteNetwork.Parse).ToArray();

    internal static ProcessSelector[] Processes(params string[] texts) =>
        texts.Select(ProcessSelector.Parse).ToArray();

    internal static void ExpectAccepted(PolicyTable table, IProcessPathNormalizer? normalizer = null)
    {
        byte[] blob = RuleBlobCompiler.Compile(table, normalizer ?? TestAppId.Normalizer);
        Check.True(blob.Length >= 56, "编译成功且产出 blob");
        Check.True(Capture(table, normalizer) is null, "这条规则表不应触发顺序冲突");
    }

    internal static void ExpectRejected(PolicyTable table,
                                        int earlier,
                                        int later,
                                        IProcessPathNormalizer? normalizer = null)
    {
        PolicyCompileException? error = Capture(table, normalizer);
        Check.True(error is not null, $"规则 #{earlier} 应遮蔽 #{later}，必须拒绝");
        Check.True(error!.Message.Contains($"#{earlier}", StringComparison.Ordinal), $"消息缺更早的规则序号 #{earlier}");
        Check.True(error.Message.Contains($"#{later}", StringComparison.Ordinal), $"消息缺 block 规则序号 #{later}");
    }

    internal static PolicyCompileException? Capture(PolicyTable table, IProcessPathNormalizer? normalizer = null)
    {
        try
        {
            RuleBlobCompiler.Compile(table, normalizer ?? TestAppId.Normalizer);
            return null;
        }
        catch (PolicyCompileException exception)
        {
            return exception;
        }
    }

    internal static PolicyCompileException? CaptureOrder(PolicyTable table)
    {
        byte[] blob = RuleBlobCompiler.CompileUnchecked(table, TestAppId.Normalizer);
        RuleBlobHeader header = BlobReader.Header(blob);
        RuleBlobRule[] rules = Enumerable.Range(0, (int)header.RuleCount)
            .Select(index => BlobReader.Rule(blob, index))
            .ToArray();
        RuleBlobProcessSelector[] selectors = Enumerable
            .Range(0, (int)(header.SelectorPoolSize / RuleBlobLayout.ProcessSelectorSize))
            .Select(index => BlobReader.Selector(blob, index))
            .ToArray();
        RuleBlobNetworkPrefix[] networks = Enumerable
            .Range(0, (int)(header.NetworkPoolSize / RuleBlobLayout.NetworkPrefixSize))
            .Select(index => BlobReader.NetworkPrefix(blob, index))
            .ToArray();
        RuleBlobPortRange[] ports = Enumerable
            .Range(0, (int)(header.PortPoolSize / RuleBlobLayout.PortRangeSize))
            .Select(index => BlobReader.PortRange(blob, index))
            .ToArray();

        try
        {
            RuleOrderValidator.Validate(rules, BlobReader.ProcessPool(blob), selectors, networks, ports);
            return null;
        }
        catch (PolicyCompileException exception)
        {
            return exception;
        }
    }
}
