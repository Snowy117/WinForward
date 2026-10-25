using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using WinForward.Cli.Backends.Kernel;
using WinForward.Configuration;
using WinForward.Core;
using WinForward.Userland.Policy;
using Xunit;
using HostPolicyRule = WinForward.Core.PolicyRule;

namespace WinForward.Integration.Tests;

/// <summary>
/// The Kernel policy compiler's mapping contract: configured host rule <c>i</c> becomes blob rule
/// <c>i</c>, every selector maps onto the v2 host rule blob encoding, and the rule-index target table
/// stays aligned with both. The tests decode the blob through the Contracts ABI mirror types, so the
/// layout they read is the layout the compiler writes.
/// </summary>
public sealed class KernelPolicyCompilerTests
{
    private const int RulesExceedingTheBlobLimit = 2_049;
    private const string AppPath = @"C:\Program Files\App\app.exe";
    private const string ToolsPath = @"C:\Tools";
    private const string AppDevicePath = @"\device\harddiskvolume3\Program Files\App\app.exe";
    private const string ToolsDevicePath = @"\device\harddiskvolume3\Tools";

    private const string KernelProxyConfig = """
    {
      "Socks5Servers": [
        { "Name": "Main", "Host": "127.0.0.1", "Port": 1080 },
        { "Name": "Backup", "Host": "127.0.0.1", "Port": 1081 },
        { "Name": "Unused", "Host": "127.0.0.1", "Port": 1082 }
      ],
      "Host": {
        "FallbackAction": "block",
        "Rules": [
          {
            "Process": ["curl.exe"],
            "RemoteCidr": ["10.0.0.0/8"],
            "RemotePort": ["443", "8000-8100"],
            "Protocol": ["tcp"],
            "AddressFamily": ["ipv4"],
            "Action": "proxy",
            "Target": "Main"
          },
          {
            "Process": ["C:\\Program Files\\App\\app.exe", "C:\\Tools"],
            "Protocol": ["tcp"],
            "Action": "proxy",
            "Target": "Backup"
          },
          {
            "RemoteCidr": ["2001:db8::/32"],
            "Protocol": ["tcp", "udp"],
            "AddressFamily": ["ipv6"],
            "Action": "pass"
          },
          {
            "Process": ["blockme.exe"],
            "AddressFamily": ["ipv4", "ipv6"],
            "Action": "block"
          }
        ]
      },
      "BackendDriver": { "Provider": "WinForward.Kernel" }
    }
    """;

    private const string FileNameOnlyConfig = """
    {
      "Socks5Servers": [ { "Name": "Main", "Host": "127.0.0.1", "Port": 1080 } ],
      "Host": {
        "FallbackAction": "pass",
        "Rules": [
          { "Process": ["curl.exe"], "Protocol": ["tcp"], "Action": "proxy", "Target": "Main" },
          { "Process": ["blockme.exe"], "Action": "block" }
        ]
      },
      "BackendDriver": { "Provider": "WinForward.Kernel" }
    }
    """;

    private const string InvalidProcessSelectorConfig = """
    {
      "Socks5Servers": [],
      "Host": {
        "FallbackAction": "pass",
        "Rules": [ { "Process": ["bad\u0001name"], "Action": "pass" } ]
      },
      "BackendDriver": { "Provider": "WinForward.Kernel" }
    }
    """;

    private const string ForwardSlashSelectorConfig = """
    {
      "Socks5Servers": [ { "Name": "Main", "Host": "127.0.0.1", "Port": 1080 } ],
      "Host": {
        "FallbackAction": "pass",
        "Rules": [ { "Process": ["C:/Tools"], "Protocol": ["tcp"], "Action": "proxy", "Target": "Main" } ]
      },
      "BackendDriver": { "Provider": "WinForward.Kernel" }
    }
    """;

    [Fact]
    public void CompilesOneBlobRulePerConfiguredRuleInOrderWithActionsAndFallback()
    {
        var configuration = Load(KernelProxyConfig);
        var compilation = Compile(configuration);

        var header = Header(compilation.HostBlob);
        Assert.Equal(RuleBlobLayout.Magic, header.Magic);
        Assert.Equal(RuleBlobLayout.Version, header.Version);
        Assert.Equal(RuleBlobLayout.HeaderSize, header.HeaderSize);
        Assert.Equal((uint)compilation.HostBlob.Length, header.TotalSize);
        Assert.Equal(4U, header.RuleCount);
        Assert.Equal((uint)RuleAction.Block, header.FallbackAction);
        Assert.Equal(4, compilation.RuleCount);
        Assert.Equal(configuration.Policy.HostRules.Count, compilation.RuleCount);
        Assert.Equal(compilation.RuleCount, compilation.TargetsByRuleIndex.Length);

        Assert.Equal((byte)RuleAction.Redirect, Rule(compilation.HostBlob, 0).Action);
        Assert.Equal((byte)RuleAction.Redirect, Rule(compilation.HostBlob, 1).Action);
        Assert.Equal((byte)RuleAction.Permit, Rule(compilation.HostBlob, 2).Action);
        Assert.Equal((byte)RuleAction.Block, Rule(compilation.HostBlob, 3).Action);

        Assert.Equal("Main", compilation.TargetsByRuleIndex[0]!.Value.Name);
        Assert.NotNull(compilation.TargetsByRuleIndex[0]!.Value.Socks5);
        Assert.Equal("Backup", compilation.TargetsByRuleIndex[1]!.Value.Name);
        Assert.Null(compilation.TargetsByRuleIndex[2]);
        Assert.Null(compilation.TargetsByRuleIndex[3]);
    }

    [Fact]
    public void MapsProcessNetworkPortProtocolAndFamilySelectorsOntoTheBlobEncoding()
    {
        var compilation = Compile(Load(KernelProxyConfig));

        var first = Rule(compilation.HostBlob, 0);
        Assert.Equal(RuleBlobLayout.ProtocolTcpBit, first.ProtocolMask);
        Assert.Equal(RuleBlobLayout.FamilyIpv4Bit, first.FamilyMask);
        Assert.Equal(2U, first.PortCount);
        Assert.Equal(new RuleBlobPortRange { Start = 443, End = 443 }, Port(compilation.HostBlob, first.PortFirst));
        Assert.Equal(new RuleBlobPortRange { Start = 8000, End = 8100 }, Port(compilation.HostBlob, first.PortFirst + 1));
        Assert.Equal(1U, first.NetworkCount);
        var network = Network(compilation.HostBlob, first.NetworkFirst);
        Assert.Equal((byte)8, network.PrefixLength);
        Assert.Equal((byte)RuleAddressFamily.Ipv4, network.Family);
        Assert.Equal([10, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0], NetworkAddress(network));
        var filename = Selector(compilation.HostBlob, first.ProcessFirst);
        Assert.Equal(RuleBlobProcessSelector.KindFileName, filename.Kind);
        Assert.Equal((byte)0, filename.IsDirectory);
        Assert.Equal("curl.exe", ProcessString(compilation.HostBlob, filename.StringOffset));

        // Both protocols and both families are the same encoding as no selector at all.
        var pass = Rule(compilation.HostBlob, 2);
        Assert.Equal((byte)0, pass.ProtocolMask);
        Assert.Equal(RuleBlobLayout.FamilyIpv6Bit, pass.FamilyMask);
        Assert.Equal(0U, pass.PortCount);
        var ipv6 = Network(compilation.HostBlob, pass.NetworkFirst);
        Assert.Equal((byte)32, ipv6.PrefixLength);
        Assert.Equal((byte)RuleAddressFamily.Ipv6, ipv6.Family);
        Assert.Equal([0x20, 0x01, 0x0D, 0xB8, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0], NetworkAddress(ipv6));

        var block = Rule(compilation.HostBlob, 3);
        Assert.Equal((byte)0, block.ProtocolMask);
        Assert.Equal((byte)0, block.FamilyMask);
    }

    [Fact]
    public void ResolvesPathSelectorsThroughTheConfiguredNormalizerAndFoldsAsciiCase()
    {
        var normalizer = new FakeProcessPathNormalizer();
        var compilation = Compile(Load(KernelProxyConfig), normalizer);

        // A configured process list is a set: neither the selector-pool order nor the order the
        // normalizer is asked in is part of the contract, so both are compared as sets.
        var rule = Rule(compilation.HostBlob, 1);
        Assert.Equal(2U, rule.ProcessCount);
        List<(string Path, byte Kind, byte IsDirectory)> selectors = [];
        for (var offset = 0U; offset < rule.ProcessCount; offset++)
        {
            var selector = Selector(compilation.HostBlob, rule.ProcessFirst + offset);
            selectors.Add((ProcessString(compilation.HostBlob, selector.StringOffset), selector.Kind, selector.IsDirectory));
        }

        selectors.Sort(static (left, right) => StringComparer.Ordinal.Compare(left.Path, right.Path));
        Assert.Equal(
            [
                (@"\device\harddiskvolume3\program files\app\app.exe", RuleBlobProcessSelector.KindPath, (byte)0),
                (@"\device\harddiskvolume3\tools", RuleBlobProcessSelector.KindPath, (byte)1),
            ],
            selectors);

        normalizer.Calls.Sort(StringComparer.Ordinal);
        Assert.Equal([AppPath, ToolsPath], normalizer.Calls);

        // A filename selector is never handed to the normalizer; only the two path selectors were.
        Assert.DoesNotContain("curl.exe", normalizer.Calls, StringComparer.Ordinal);
    }

    [Fact]
    public void AFileNameOnlyPolicyCompilesWithoutAWindowsNormalizer()
    {
        var normalizer = new FakeProcessPathNormalizer();
        var compilation = Compile(Load(FileNameOnlyConfig), normalizer);

        Assert.Empty(normalizer.Calls);
        Assert.Equal(2, compilation.RuleCount);
        Assert.Equal((uint)RuleAction.Permit, Header(compilation.HostBlob).FallbackAction);
        Assert.Null(compilation.TargetsByRuleIndex[1]);
    }

    [Fact]
    public void RejectsAPathSelectorWhenTheHostCannotResolveDevicePaths()
    {
        var diagnostics = Reject(Load(KernelProxyConfig), new UnsupportedProcessPathNormalizer());

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("WinForward.Host.Rules[1].Process", diagnostic.Path);
        Assert.Contains("Kernel process path resolution requires Windows", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains(AppPath, diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsAnInvalidProcessSelectorAtTheRuleProcessPath()
    {
        var diagnostics = Reject(Load(InvalidProcessSelectorConfig), new FakeProcessPathNormalizer());

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("WinForward.Host.Rules[0].Process", diagnostic.Path);
        Assert.Contains("U+0001", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AttributesAForwardSlashPathSelectorToItsRule()
    {
        var diagnostics = Reject(Load(ForwardSlashSelectorConfig), new UnsupportedProcessPathNormalizer());

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("WinForward.Host.Rules[0].Process", diagnostic.Path);
        Assert.Contains(@"C:\Tools", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsAMissingTargetAtTheRuleTargetPath()
    {
        var diagnostics = Reject(
            Manual([ProxyRule("Missing")], FlowAction.Pass, ("Main", ProxyTarget.FromServer(Server("Main")))), new FakeProcessPathNormalizer());

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("WinForward.Host.Rules[0].Target", diagnostic.Path);
        Assert.Contains("Missing", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsALocalTargetAtTheRuleTargetPath()
    {
        var local = new ProxyTarget(
            "Local", Socks5: null, new LocalTarget("Local", Endpoint.From(IPAddress.Loopback, 5353)));
        var diagnostics = Reject(Manual([ProxyRule("Local")], FlowAction.Pass, ("Local", local)), new FakeProcessPathNormalizer());

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("WinForward.Host.Rules[0].Target", diagnostic.Path);
        Assert.Contains("no host UDP relay", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsAdapterSelectorsThatTheHostBlobCannotCarry()
    {
        // The capability gate refuses an adapter selector before the compiler runs, so this input is
        // a directly constructed configuration; ignoring the selector here would widen the rule to
        // every adapter instead of failing closed.
        var rule = new HostPolicyRule(
            new RuleMatcher(
                AdapterIds: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "id-a" },
                AdapterNames: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Ethernet" }),
            new FlowDecision(FlowAction.Pass, RuleIndex: null, TargetName: null));

        var diagnostics = Reject(Manual([rule], FlowAction.Pass), new FakeProcessPathNormalizer());

        Assert.Equal(2, diagnostics.Count);
        Assert.Equal("WinForward.Host.Rules[0].AdapterId", diagnostics[0].Path);
        Assert.Contains("no interface index", diagnostics[0].Message, StringComparison.Ordinal);
        Assert.Equal("WinForward.Host.Rules[0].AdapterName", diagnostics[1].Path);
        Assert.Contains("no interface index", diagnostics[1].Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReportsABlobOverTheSizeLimitAtTheHostDomain()
    {
        HostPolicyRule[] rules = [.. Enumerable
            .Range(0, RulesExceedingTheBlobLimit)
            .Select(static _ => new HostPolicyRule(new RuleMatcher(), new FlowDecision(FlowAction.Pass, RuleIndex: null, TargetName: null)))];

        var diagnostics = Reject(Manual(rules, FlowAction.Pass), new FakeProcessPathNormalizer());

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("WinForward.Host", diagnostic.Path);
        Assert.Contains("64 KiB", diagnostic.Message, StringComparison.Ordinal);
    }

    private static ValidatedConfiguration Load(string json)
    {
        Assert.True(ConfigurationLoader.TryParse(json, out var dto, out var parseDiagnostics), string.Join("; ", parseDiagnostics));
        Assert.NotNull(dto);
        Assert.True(ConfigurationLoader.TryValidate(dto, out var configuration, out var diagnostics), string.Join("; ", diagnostics));
        Assert.NotNull(configuration);
        return configuration;
    }

    private static KernelPolicyCompilation Compile(ValidatedConfiguration configuration, IProcessPathNormalizer? normalizer = null)
    {
        Assert.True(
            KernelPolicyCompiler.TryCompile(configuration, normalizer ?? new FakeProcessPathNormalizer(), out var compilation, out var diagnostics),
            string.Join("; ", diagnostics));
        Assert.NotNull(compilation);
        Assert.Empty(diagnostics);
        return compilation;
    }

    private static IReadOnlyList<ConfigDiagnostic> Reject(ValidatedConfiguration configuration, IProcessPathNormalizer normalizer)
    {
        Assert.False(KernelPolicyCompiler.TryCompile(configuration, normalizer, out var compilation, out var diagnostics));
        Assert.Null(compilation);
        Assert.NotEmpty(diagnostics);
        return diagnostics;
    }

    private static ValidatedConfiguration Manual(
        IReadOnlyList<HostPolicyRule> rules,
        FlowAction fallback,
        params (string Name, ProxyTarget Target)[] targets) =>
        new(
            targets.ToDictionary(static entry => entry.Name, static entry => entry.Target, StringComparer.OrdinalIgnoreCase),
            new PolicySnapshot(rules, fallback));

    private static HostPolicyRule ProxyRule(string? targetName) =>
        new(
            new RuleMatcher(Protocols: new HashSet<TransportProtocol> { TransportProtocol.Tcp }),
            new FlowDecision(FlowAction.Proxy, RuleIndex: null, targetName));

    private static Socks5Server Server(string name) => new(name, "127.0.0.1", 1080, Username: null, Password: null);

    private static RuleBlobHeader Header(byte[] blob) => MemoryMarshal.Read<RuleBlobHeader>(blob);

    private static RuleBlobRule Rule(byte[] blob, int index)
    {
        var offset = (int)Header(blob).RulesOffset + (index * RuleBlobLayout.RuleSize);
        return MemoryMarshal.Read<RuleBlobRule>(blob.AsSpan(offset, RuleBlobLayout.RuleSize));
    }

    private static RuleBlobProcessSelector Selector(byte[] blob, uint index)
    {
        var offset = (int)Header(blob).SelectorPoolOffset + ((int)index * RuleBlobLayout.ProcessSelectorSize);
        return MemoryMarshal.Read<RuleBlobProcessSelector>(blob.AsSpan(offset, RuleBlobLayout.ProcessSelectorSize));
    }

    private static RuleBlobPortRange Port(byte[] blob, uint index)
    {
        var offset = (int)Header(blob).PortPoolOffset + ((int)index * RuleBlobLayout.PortRangeSize);
        return MemoryMarshal.Read<RuleBlobPortRange>(blob.AsSpan(offset, RuleBlobLayout.PortRangeSize));
    }

    private static RuleBlobNetworkPrefix Network(byte[] blob, uint index)
    {
        var offset = (int)Header(blob).NetworkPoolOffset + ((int)index * RuleBlobLayout.NetworkPrefixSize);
        return MemoryMarshal.Read<RuleBlobNetworkPrefix>(blob.AsSpan(offset, RuleBlobLayout.NetworkPrefixSize));
    }

    private static byte[] NetworkAddress(RuleBlobNetworkPrefix prefix)
    {
        var address = new byte[RuleBlobLayout.NetworkAddressSize];
        for (var index = 0; index < address.Length; index++) address[index] = prefix.Network[index];
        return address;
    }

    private static string ProcessString(byte[] blob, uint stringOffset)
    {
        var start = (int)Header(blob).ProcessPoolOffset + (int)stringOffset;
        var end = start;
        while (end < blob.Length && blob[end] != 0) end++;
        return Encoding.UTF8.GetString(blob, start, end - start);
    }

    /// <summary>
    /// Resolves the two configured DOS paths the way the production normalizer would, records every
    /// selector it is asked about, and answers null for anything else (the compiler's path-existence
    /// rejection).
    /// </summary>
    private sealed class FakeProcessPathNormalizer : IProcessPathNormalizer
    {
        internal List<string> Calls { get; } = [];

        public ProcessPathResolution? Normalize(string path)
        {
            Calls.Add(path);
            return path switch
            {
                AppPath => new ProcessPathResolution(AppDevicePath, isDirectory: false),
                ToolsPath => new ProcessPathResolution(ToolsDevicePath, isDirectory: true),
                _ => null,
            };
        }
    }
}
