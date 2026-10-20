using WinForward.Userland.Policy;

namespace WinForward.Userland.Tests;

internal sealed record DifferentialCase(
    string Name,
    string Description,
    PolicyTable Table,
    IReadOnlyList<DifferentialConnection> Connections,
    IReadOnlyList<DifferentialExpectation> Expectations);

internal static class Differential
{
    internal const string Svchost = TestAppId.SvchostDos;
    internal const string App = TestAppId.AppDos;
    internal const string Other = TestAppId.OtherDos;

    internal static PolicyRule Rule(
        RuleAction action,
        string? process = null,
        string? network = null,
        PortRange? ports = null,
        RuleProtocol protocol = RuleProtocol.Any,
        RuleAddressFamily family = RuleAddressFamily.Any) => new()
        {
            Process = process is null ? null : ProcessSelector.Parse(process),
            RemoteNetwork = network is null ? null : RemoteNetwork.Parse(network),
            RemotePorts = ports ?? PortRange.Any,
            Protocol = protocol,
            Family = family,
            Action = action,
        };

    internal static DifferentialCase Case(
        string name,
        string description,
        RuleAction fallbackAction,
        PolicyRule[] rules,
        DifferentialExpectation[] expectations) =>
        new(name, description, new PolicyTable(rules, fallbackAction), DifferentialConnections.All, expectations);

    internal static DifferentialExpectation[] Expectations(
        RuleAction fallbackAction,
        params (int Connection, RuleAction Action, uint RuleIndex)[] hits)
    {
        DifferentialExpectation[] expectations = new DifferentialExpectation[DifferentialConnections.All.Count];
        for (int index = 0; index < expectations.Length; index++)
        {
            expectations[index] = new DifferentialExpectation(false, RuleBlobLayout.PoolIndexAny, fallbackAction);
        }

        foreach ((int connection, RuleAction action, uint ruleIndex) in hits)
        {
            if (connection < 0 || connection >= expectations.Length)
            {
                throw new TestFailure($"用例表引用了不存在的连接序号 {connection}");
            }

            if (expectations[connection].Matched)
            {
                throw new TestFailure($"用例表重复列出连接 {connection}");
            }

            expectations[connection] = new DifferentialExpectation(true, ruleIndex, action);
        }

        return expectations;
    }
}
