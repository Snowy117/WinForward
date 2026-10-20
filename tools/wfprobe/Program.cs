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
                case "listen":
                    return await ListenAsync(options);
                case "client":
                    return await ClientAsync(options);
                case "selftest":
                    return await SelfTest.RunAsync(options);
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
  wfprobe control [--enabled 0|1] [--mode documented|loopback|local] [--port N]
                  [--target-pid PID] [--listener-pid PID] [--local-address A.B.C.D]
  wfprobe stats
  wfprobe reset
  wfprobe events [--count N]
  wfprobe pooltag [--tag WfPr]
  wfprobe listen --bind A.B.C.D --port N --count K
  wfprobe client --remote A.B.C.D:PORT [--bind A.B.C.D] [--payload TEXT] [--delay-ms N] [--timeout-ms N]
  wfprobe selftest --remote A.B.C.D:PORT --bind A.B.C.D --port N
                  [--mode documented|loopback|local] [--source A.B.C.D]
                  [--local-address A.B.C.D] [--iterations K] [--expect-address A.B.C.D]
                  [--expect-branch loopback|local] [--settle-ms N] [--timeout-ms N]");
    }

    internal static uint ParseMode(string text) => text switch
    {
        "documented" => Protocol.RedirectModeDocumented,
        "loopback" => Protocol.RedirectModeForceLoopback,
        "local" => Protocol.RedirectModeForceLocal,
        _ => throw new ArgumentException($"未知 mode：{text}"),
    };

    internal static string ModeName(uint mode) => mode switch
    {
        Protocol.RedirectModeDocumented => "documented",
        Protocol.RedirectModeForceLoopback => "loopback",
        Protocol.RedirectModeForceLocal => "local",
        _ => $"unknown({mode})",
    };

    internal static uint IpToNetworkUInt32(string text)
    {
        byte[] bytes = IPAddress.Parse(text).GetAddressBytes();
        if (bytes.Length != 4)
            throw new ArgumentException("只支持 IPv4");
        return BitConverter.ToUInt32(bytes, 0);
    }

    internal static IPAddress NetworkUInt32ToIp(uint value) => new(BitConverter.GetBytes(value));

    internal static IPEndPoint ParseEndPoint(string text)
    {
        int separator = text.LastIndexOf(':');
        if (separator <= 0)
            throw new ArgumentException($"地址应为 A.B.C.D:PORT：{text}");
        return new IPEndPoint(IPAddress.Parse(text[..separator]), int.Parse(text[(separator + 1)..], CultureInfo.InvariantCulture));
    }

    internal static void PrintConfig(string prefix, ProbeConfig config)
    {
        Console.WriteLine(
            $"[{prefix}] config: abi={config.AbiVersion} enabled={config.Enabled} mode={ModeName(config.Mode)} " +
            $"target-pid={config.TargetProcessId} listener-pid={config.ListenerProcessId} port={config.ListenerPort} " +
            $"local-override={NetworkUInt32ToIp(config.LocalAddressOverride)}");
    }

    private static int Control(Options options)
    {
        using var device = new WfpDevice();
        ProbeConfig config = device.GetConfig();
        config.AbiVersion = Protocol.AbiVersion;

        if (options.Has("enabled"))
            config.Enabled = options.GetUInt("enabled", 1);
        if (options.Has("mode"))
            config.Mode = ParseMode(options.Require("mode"));
        if (options.Has("port"))
            config.ListenerPort = (ushort)options.GetUInt("port", 0);
        if (options.Has("target-pid"))
            config.TargetProcessId = options.GetUInt("target-pid", 0);
        if (options.Has("listener-pid"))
            config.ListenerProcessId = options.GetUInt("listener-pid", 0);
        if (options.Has("local-address"))
            config.LocalAddressOverride = IpToNetworkUInt32(options.Require("local-address"));

        device.SetConfig(config);
        PrintConfig("control", device.GetConfig());
        return 0;
    }

    private static int PrintStats()
    {
        using var device = new WfpDevice();
        ProbeStats stats = device.GetStats();
        Console.WriteLine($"[stats] classify={stats.ClassifyCalls} redirected={stats.Redirected} " +
                          $"contexts_allocated={stats.ContextsAllocated} alloc_failures={stats.ContextAllocFailures} " +
                          $"apply_failures={stats.ApplyFailures} last_status=0x{stats.LastStatus:X8}");
        Console.WriteLine($"[stats] permitted: disabled={stats.PermittedDisabled} wrong_pid={stats.PermittedWrongProcess} " +
                          $"listener_port={stats.PermittedListenerPort} redirect_state={stats.PermittedRedirectState} " +
                          $"already_redirected={stats.PermittedAlreadyRedirected} no_write_right={stats.PermittedNoWriteRight} " +
                          $"non_tcp={stats.PermittedNonTcp}");
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

    private static unsafe void PrintEvent(ProbeEvent item)
    {
        Console.WriteLine(
            $"[event] seq={item.Sequence} pid={item.ProcessId} decision={DecisionName(item.Decision)} " +
            $"mode={ModeName(item.Mode)} branch={BranchName(item.Branch)} " +
            $"original={AddressText(item.OriginalAddress)}:{item.OriginalPort} " +
            $"local={AddressText(item.LocalAddress)}:{item.LocalPort} redirect-port={item.RedirectPort}");
    }

    internal static unsafe string AddressText(byte* address) => new IPAddress(new ReadOnlySpan<byte>(address, 4)).ToString();

    internal static string DecisionName(byte decision) => decision switch
    {
        0 => "permitted_disabled",
        1 => "permitted_wrong_process",
        2 => "permitted_listener_port",
        3 => "permitted_redirect_state",
        4 => "permitted_already_redirected",
        5 => "permitted_no_write_right",
        6 => "permitted_non_tcp",
        7 => "redirected",
        8 => "redirect_failed",
        9 => "context_alloc_failed",
        _ => $"unknown({decision})",
    };

    internal static string BranchName(byte branch) => branch switch
    {
        0 => "none",
        1 => "any_to_loopback",
        2 => "local_address",
        _ => $"unknown({branch})",
    };

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

    private static void PrintContext(byte[] raw)
    {
        if (raw.Length != 36)
        {
            Console.WriteLine($"[context] 长度 {raw.Length}，不是 redirect_context");
            return;
        }

        RedirectContext context = MemoryMarshal.Read<RedirectContext>(raw);
        Console.WriteLine($"[context] magic=0x{context.Magic:X8} version={context.Version} pid={context.ProcessId} " +
                          $"family={context.Family} protocol={context.Protocol} port={context.OriginalPort}");
        unsafe
        {
            Console.WriteLine($"[context] original={AddressText(context.OriginalAddress)}:{context.OriginalPort}");
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
    private readonly Dictionary<string, string?> values = new(StringComparer.OrdinalIgnoreCase);

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

            values[key] = value;
        }
    }

    public bool Has(string key) => values.ContainsKey(key);

    public string? Get(string key) => values.TryGetValue(key, out string? value) ? value : null;

    public string Require(string key) => Get(key) ?? throw new ArgumentException($"缺少 --{key}");

    public int GetInt(string key, int fallback) =>
        Get(key) is { } value ? int.Parse(value, CultureInfo.InvariantCulture) : fallback;

    public uint GetUInt(string key, uint fallback) =>
        Get(key) is { } value ? uint.Parse(value, CultureInfo.InvariantCulture) : fallback;
}
