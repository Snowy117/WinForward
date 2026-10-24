using System.Net;

namespace WinForward.Probe;

internal sealed class FixtureRule
{
    public byte Action { get; init; }
    public byte ProtocolMask { get; init; }
    public byte FamilyMask { get; init; }
    public List<FixtureNetwork> Networks { get; init; } = new();
    public List<TestPortRange> Ports { get; init; } = new();
    public List<TestProcessSelector> Processes { get; init; } = new();
    public uint ProcessFirst;
    public uint ProcessCount;
    public uint NetworkFirst;
    public uint NetworkCount;
    public uint PortFirst;
    public uint PortCount;
}

internal readonly record struct FixtureNetwork(byte Family, byte PrefixLength, byte[] Address)
{
    public string Text =>
        $"{new IPAddress(Address.AsSpan(0, Family == Protocol.RuleFamilyIpv4 ? 4 : 16))}/{PrefixLength}";
}
