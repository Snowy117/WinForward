using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;

namespace WinForward.Probe;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 0)
        {
            PrintUsage();
            return 1;
        }

        Options options;
        try
        {
            options = new Options(args[1..]);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }

        try
        {
            ProtocolLayout.Verify();

            switch (args[0])
            {
                case "control":
                    return Control(options);
                case "stats":
                    return PrintStats();
                case "reset":
                    return ResetStats();
                case "events":
                    return PrintEvents(options);
                case "pooltag":
                    return PrintPoolTag(options);
                case "appid":
                    return PrintAppId(options);
                case "listen":
                    return await ListenAsync(options);
                case "client":
                    return await ClientAsync(options);
                case "relay":
                    return await Relay.RunAsync(options);
                case "blockprobe":
                    return await BlockProbe.RunAsync(options);
                case "selftest":
                    return await SelfTest.RunAsync(options);
                case "rules":
                    return await RuleSuite.RunAsync(options);
                case "gates":
                    return await GateSuite.RunAsync(options);
                case "udp-listen":
                    return UdpCommands.Listen(options);
                case "udp-client":
                    return UdpCommands.Client(options);
                case "udp-relay":
                    return UdpCommands.Relay(options);
                default:
                    PrintUsage();
                    return 1;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[wfprobe] 失败：{ex.Message}");
            return 3;
        }
    }

    private static void PrintUsage()
    {
        Console.WriteLine(@"用法:
  wfprobe control [--enabled 0|1] [--port N] [--listener-pid PID]
                  [--self-pid PID]... [--clear-self]
  wfprobe stats | reset | pooltag [--tag WfPr]
  wfprobe events [--count N]
  wfprobe appid [--path FILE]
  wfprobe listen --bind A.B.C.D --port N --count K
  wfprobe client --remote HOST:PORT [--bind A.B.C.D] [--payload TEXT] [--delay-ms N] [--timeout-ms N]
  wfprobe relay --bind A.B.C.D --port N [--expect-accepts K] [--accept-window-ms N]
                 [--outbound HOST:PORT] [--outbound-records 0|1] [--linger-ms N]
  wfprobe selftest --remote HOST:PORT [--bind A.B.C.D] [--dual] [--iterations K] [--settle-ms N]
  wfprobe rules [--iterations K]
  wfprobe gates
  wfprobe udp-listen --bind A.B.C.D --port N [--count K] [--timeout-ms M]
  wfprobe udp-client --remote HOST:PORT [--mode connected|unconnected] [--payload TEXT]
                     [--count K] [--timeout-ms M]
  wfprobe udp-relay --bind A.B.C.D --port N [--count K] [--linger-ms M]
");
    }

    internal static uint ParseAction(string text) => text switch
    {
        "permit" => Protocol.RuleActionPermit,
        "block" => Protocol.RuleActionBlock,
        "redirect" => Protocol.RuleActionRedirect,
        _ => throw new ArgumentException($"未知动作：{text}"),
    };

    internal static uint IpToNetworkUInt32(string text)
    {
        byte[] bytes = IPAddress.Parse(text).GetAddressBytes();
        if (bytes.Length != 4)
            throw new ArgumentException("只支持 IPv4");
        return BitConverter.ToUInt32(bytes, 0);
    }

    internal static IPEndPoint ParseEndPoint(string text)
    {
        int separator = text.LastIndexOf(':');
        if (separator <= 0)
            throw new ArgumentException($"地址应为 A.B.C.D:PORT：{text}");
        return new IPEndPoint(IPAddress.Parse(text[..separator]),
                              int.Parse(text[(separator + 1)..], CultureInfo.InvariantCulture));
    }

    internal static void PrintConfig(string prefix, ProbeConfig config)
    {
        var ids = new List<uint>();
        unsafe
        {
            for (int index = 0; index < config.SelfProcessCount; index++)
                ids.Add(config.SelfProcessIds[index]);
        }
        Console.WriteLine($"[{prefix}] config: abi={config.AbiVersion} enabled={config.Enabled} " +
                          $"listener-pid={config.ListenerProcessId} port={config.ListenerPort} " +
                          $"self-pids=[{string.Join(",", ids)}]");
    }

    private static int Control(Options options)
    {
        using var device = new WfpDevice();
        ProbeConfig config = device.GetConfig();
        config.AbiVersion = Protocol.AbiVersion;

        if (options.Has("enabled"))
            config.Enabled = options.GetUInt("enabled", 1);
        if (options.Has("port"))
            config.ListenerPort = (ushort)options.GetUInt("port", 0);
        if (options.Has("listener-pid"))
            config.ListenerProcessId = options.GetUInt("listener-pid", 0);
        if (options.Has("clear-self"))
            config.SelfProcessCount = 0;

        List<string?> selfPids = options.GetAll("self-pid");
        if (selfPids.Count > 0)
        {
            if (selfPids.Count > Protocol.MaxSelfProcessIds)
                throw new ArgumentException($"self-pid 最多 {Protocol.MaxSelfProcessIds} 个");
            unsafe
            {
                for (int index = 0; index < Protocol.MaxSelfProcessIds; index++)
                    config.SelfProcessIds[index] = index < selfPids.Count
                                                       ? uint.Parse(selfPids[index]!, CultureInfo.InvariantCulture)
                                                       : 0;
            }
            config.SelfProcessCount = (uint)selfPids.Count;
        }

        device.SetConfig(config);
        PrintConfig("control", device.GetConfig());
        return 0;
    }

    private static int PrintStats()
    {
        using var device = new WfpDevice();
        ProbeStats stats = device.GetStats();
        Console.WriteLine($"[stats] classify={stats.ClassifyCalls} permitted={stats.Permitted} blocked={stats.Blocked} " +
                          $"redirected={stats.Redirected} rules_replaced={stats.RulesReplaced} " +
                          $"rejected_blobs={stats.RejectedBlobs}");
        Console.WriteLine($"[stats] contexts_allocated={stats.ContextsAllocated} " +
                          $"alloc_failures={stats.ContextAllocFailures} apply_failures={stats.ApplyFailures} " +
                          $"redirect_failures={stats.RedirectFailures} deferred_blocks={stats.DeferredBlocks} " +
                          $"process_path_metadata={stats.ProcessPathMetadataSeen} " +
                          $"last_status=0x{stats.LastStatus:X8}");
        Console.WriteLine($"[stats] permitted: disabled={stats.PermittedDisabled} no_rules={stats.PermittedNoRules} " +
                          $"no_write_right={stats.PermittedNoWriteRight} unsupported={stats.PermittedUnsupported} " +
                          $"non_tcp={stats.PermittedNonTcp} listener_port={stats.PermittedListenerPort} " +
                          $"redirect_state={stats.PermittedRedirectState} condition_flags={stats.PermittedConditionFlags} " +
                          $"already_redirected={stats.PermittedAlreadyRedirected} self_process={stats.PermittedSelfProcess}");
        Console.WriteLine($"[stats] matched: permit={stats.MatchedPermit} block={stats.MatchedBlock} " +
                          $"redirect={stats.MatchedRedirect} fallback: permit={stats.FallbackPermit} " +
                          $"block={stats.FallbackBlock} redirect={stats.FallbackRedirect}");
        return 0;
    }

    private static int ResetStats()
    {
        using var device = new WfpDevice();
        device.ResetStats();
        Console.WriteLine("[reset] 计数与事件环已清零");
        return 0;
    }

    private static int PrintEvents(Options options)
    {
        using var device = new WfpDevice();
        int count = options.GetInt("count", 32);
        ProbeEvent[] events = device.GetEvents(count);
        Console.WriteLine($"[events] {events.Length} 条（最多 {count}）");
        foreach (ProbeEvent item in events)
            PrintEvent(item);
        return 0;
    }

    internal static unsafe void PrintEvent(ProbeEvent item)
    {
        string remote = AddressText(item.RemoteAddress, item.Family);
        string local = AddressText(item.LocalAddress, item.Family);
        string rule = item.MatchedRule == Protocol.InvalidRuleIndex ? "-" : item.MatchedRule.ToString(CultureInfo.InvariantCulture);
        Console.WriteLine($"[event] seq={item.Sequence} pid={item.ProcessId} decision={DecisionName(item.Decision)} " +
                          $"action={ActionName(item.Action)} branch={BranchName(item.Branch)} family={item.Family} " +
                          $"rule={rule} remote={remote}:{item.RemotePort} local={local}:{item.LocalPort} " +
                          $"redirect-port={item.RedirectPort}");
    }

    internal static unsafe string AddressText(byte* address, byte family)
    {
        int length = family == 6 ? 16 : 4;
        return new IPAddress(new ReadOnlySpan<byte>(address, length)).ToString();
    }

    internal static string DecisionName(byte decision) => decision switch
    {
        Protocol.DecisionPermittedDisabled => "permitted_disabled",
        Protocol.DecisionPermittedNoWriteRight => "permitted_no_write_right",
        Protocol.DecisionPermittedNonTcp => "permitted_non_tcp",
        Protocol.DecisionPermittedListenerPort => "permitted_listener_port",
        Protocol.DecisionPermittedConditionFlags => "permitted_condition_flags",
        Protocol.DecisionPermittedRedirectState => "permitted_redirect_state",
        Protocol.DecisionPermittedAlreadyRedirected => "permitted_already_redirected",
        Protocol.DecisionPermittedSelfProcess => "permitted_self_process",
        Protocol.DecisionPermittedNoRules => "permitted_no_rules",
        Protocol.DecisionPermittedUnsupported => "permitted_unsupported",
        Protocol.DecisionPermittedFallback => "permitted_fallback",
        Protocol.DecisionPermittedRule => "permitted_rule",
        Protocol.DecisionBlockedRule => "blocked_rule",
        Protocol.DecisionBlockedFallback => "blocked_fallback",
        Protocol.DecisionRedirected => "redirected",
        Protocol.DecisionRedirectFailed => "redirect_failed",
        Protocol.DecisionContextAllocFailed => "context_alloc_failed",
        Protocol.DecisionPermittedDeferredBlock => "permitted_deferred_block",
        _ => $"unknown({decision})",
    };

    internal static string ActionName(byte action) => action switch
    {
        Protocol.ActionPermit => "permit",
        Protocol.ActionBlock => "block",
        Protocol.ActionRedirect => "redirect",
        _ => $"unknown({action})",
    };

    internal static string BranchName(byte branch) => branch switch
    {
        0 => "none",
        1 => "any_to_loopback",
        2 => "local_address",
        _ => $"unknown({branch})",
    };

    private static int PrintAppId(Options options)
    {
        string path = options.Get("path") ?? Environment.ProcessPath ??
                      throw new InvalidOperationException("拿不到当前进程路径");
        Console.WriteLine($"[appid] path={path}");
        Console.WriteLine($"[appid] normalized={AppId.ForPath(path)}");
        return 0;
    }

    private static int PrintPoolTag(Options options)
    {
        string tag = options.Get("tag") ?? Protocol.PoolTag;
        PoolTagUsage? usage = PoolTags.Find(tag);
        if (usage is null)
        {
            Console.WriteLine($"[pooltag] 未找到 tag={tag}");
            return 0;
        }

        PoolTagUsage value = usage.Value;
        Console.WriteLine($"[pooltag] tag={value.Tag} nonpaged_allocs={value.NonPagedAllocs} " +
                          $"nonpaged_frees={value.NonPagedFrees} nonpaged_outstanding={value.NonPagedOutstanding} " +
                          $"nonpaged_bytes={value.NonPagedUsed} paged_allocs={value.PagedAllocs} paged_frees={value.PagedFrees}");
        return 0;
    }

    private static async Task<int> ListenAsync(Options options)
    {
        string bind = options.Get("bind") ?? "127.0.0.1";
        int port = options.GetInt("port", 18080);
        int count = options.GetInt("count", 1);

        var listener = new TcpListener(IPAddress.Parse(bind), port);
        listener.Start();
        Console.WriteLine($"[listen] pid={Environment.ProcessId} 监听 {bind}:{port}，等待 {count} 条连接");

        try
        {
            for (int index = 0; index < count; index++)
            {
                using Socket server = await listener.AcceptSocketAsync();
                Console.WriteLine($"[listen] #{index} local={server.LocalEndPoint} remote={server.RemoteEndPoint}");

                try
                {
                    byte[] records = WfpSockets.QueryRedirectRecords(server);
                    Console.WriteLine($"[listen] #{index} redirect-records={records.Length}B {Convert.ToHexString(records)}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[listen] #{index} redirect-records 失败：{ex.Message}");
                }

                try
                {
                    byte[] raw = WfpSockets.QueryRedirectContext(server);
                    Console.WriteLine($"[listen] #{index} redirect-context={raw.Length}B {Convert.ToHexString(raw)}");
                    PrintContext(raw);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[listen] #{index} redirect-context 失败：{ex.Message}");
                }

                try
                {
                    byte[] buffer = new byte[4096];
                    int received = server.Receive(buffer);
                    if (received > 0)
                        server.Send(buffer, received, SocketFlags.None);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[listen] #{index} 回显失败：{ex.Message}");
                }
            }
        }
        finally
        {
            listener.Stop();
        }

        return 0;
    }

    internal static void PrintContext(byte[] raw)
    {
        if (raw.Length != 36)
        {
            Console.WriteLine($"[context] 长度 {raw.Length}，不是 redirect_context");
            return;
        }

        RedirectContext context = MemoryMarshal.Read<RedirectContext>(raw);
        Console.WriteLine($"[context] magic=0x{context.Magic:X8} version={context.Version} pid={context.ProcessId} " +
                          $"rule={context.RuleIndex} family={context.Family} protocol={context.Protocol} " +
                          $"port={context.OriginalPort}");
        unsafe
        {
            Console.WriteLine($"[context] original={AddressText(context.OriginalAddress, context.Family)}:{context.OriginalPort}");
        }
    }

    private static async Task<int> ClientAsync(Options options)
    {
        IPEndPoint remote = ParseEndPoint(options.Require("remote"));
        int timeoutMs = options.GetInt("timeout-ms", 5000);
        int delayMs = options.GetInt("delay-ms", 0);
        byte[] payload = Encoding.UTF8.GetBytes(options.Get("payload") ?? "ping");

        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        client.NoDelay = true;
        if (options.Get("bind") is { } bind)
            client.Bind(new IPEndPoint(IPAddress.Parse(bind), 0));

        if (delayMs > 0)
        {
            Console.WriteLine($"[client] pid={Environment.ProcessId} delay-ms={delayMs} target={remote}");
            await Task.Delay(delayMs);
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(timeoutMs));
        await client.ConnectAsync(remote, timeout.Token);
        Console.WriteLine($"[client] pid={Environment.ProcessId} 已连接 {remote}（本地 {client.LocalEndPoint}）");

        client.Send(payload);
        byte[] buffer = new byte[4096];
        int received = client.Receive(buffer);
        Console.WriteLine($"[client] echo={received}B {Encoding.UTF8.GetString(buffer, 0, received)}");
        return received > 0 ? 0 : 4;
    }
}

internal sealed class Options
{
    private readonly Dictionary<string, List<string?>> values = new(StringComparer.OrdinalIgnoreCase);

    public Options(string[] args)
    {
        for (int index = 0; index < args.Length; index++)
        {
            string argument = args[index];
            if (!argument.StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException($"无法识别参数：{argument}");

            string key = argument[2..];
            string? value = null;
            int equals = key.IndexOf('=');
            if (equals >= 0)
            {
                value = key[(equals + 1)..];
                key = key[..equals];
            }
            else if (index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                value = args[++index];
            }

            if (!values.TryGetValue(key, out List<string?>? list))
            {
                list = new List<string?>();
                values.Add(key, list);
            }
            list.Add(value);
        }
    }

    public bool Has(string key) => values.ContainsKey(key);

    public string? Get(string key) => values.TryGetValue(key, out List<string?>? list) ? list[^1] : null;

    public List<string?> GetAll(string key) =>
        values.TryGetValue(key, out List<string?>? list) ? list : new List<string?>();

    public string Require(string key) => Get(key) ?? throw new ArgumentException($"缺少 --{key}");

    public int GetInt(string key, int fallback) =>
        Get(key) is { } value ? int.Parse(value, CultureInfo.InvariantCulture) : fallback;

    public uint GetUInt(string key, uint fallback) =>
        Get(key) is { } value ? uint.Parse(value, CultureInfo.InvariantCulture) : fallback;
}
