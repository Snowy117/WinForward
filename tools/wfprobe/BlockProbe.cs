using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace WinForward.Probe;

// 诊断模式：只验证“FWP_ACTION_BLOCK 在 ALE_CONNECT_REDIRECT 层是否真的拦住连接”。
// 单独拿出来是因为这个结论影响 block 规则的产品语义，证据要能独立复现。
internal static class BlockProbe
{
    public static Task<int> RunAsync(Options options)
    {
        int rulePort = options.GetInt("rule-port", 0);
        int timeoutMs = options.GetInt("timeout-ms", 4000);
        IPEndPoint remote = Program.ParseEndPoint(options.Require("remote"));

        using var device = new WfpDevice();
        device.ResetStats();
        ProbeConfig config = device.GetConfig();
        config.AbiVersion = Protocol.AbiVersion;
        config.Enabled = 1;
        config.ListenerProcessId = (uint)Environment.ProcessId;
        config.ListenerPort = (ushort)options.GetInt("port", 19090);
        config.SelfProcessCount = 0;
        device.SetConfig(config);

        // rulePort == 0 时不放 block 规则：auth 层 filter 不挂，用来做“防火墙是否被截胡”的对照。
        var rules = rulePort == 0
                        ? new[] { TestRule.PermitPort(1) }
                        : new[] { TestRule.BlockPort(rulePort) };
        device.SetRuleBlob(BlobBuilder.Build(rules, Protocol.RuleActionPermit));
        var watch = Stopwatch.StartNew();
        bool connected = SocketHelpers.TryConnect(remote, timeoutMs, out Socket? socket, out string error);
        watch.Stop();
        socket?.Dispose();

        ProbeStats stats = device.GetStats();
        ConfigOff(device);
        Console.WriteLine($"[blockprobe] remote={remote} rule-port={rulePort} connected={connected} " +
                          $"elapsed={watch.ElapsedMilliseconds}ms blocked={stats.Blocked} " +
                          $"matched_block={stats.MatchedBlock} error={error}");
        return Task.FromResult(0);
    }

    private static void ConfigOff(WfpDevice device)
    {
        ProbeConfig config = device.GetConfig();
        config.AbiVersion = Protocol.AbiVersion;
        config.Enabled = 0;
        device.SetConfig(config);
    }
}
