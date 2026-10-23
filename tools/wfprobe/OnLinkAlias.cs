using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace WinForward.Probe;

/// <summary>
/// 测试用的 on-link 地址别名：在入口适配器上临时加一个与客户端同网段的地址，让外部客户端把
/// “原始服务器”当成 on-link 目标。退出时删除；地址已经存在或就是本机地址时不做任何事。
/// 注意：这里只保证 Windows 侧地址可 bind（DAD/Preferred），不证明外部客户端可达；
/// 真实路径由 ForwardedUdpSuite 的 listener/decoy 数据面验证。
/// </summary>
internal sealed class OnLinkAlias : IDisposable
{
    private readonly string? _adapterName;
    private readonly string _address;
    private readonly bool _added;
    private readonly Process? _cleanupWatchdog;
    private readonly string? _leasePath;

    private OnLinkAlias(string? adapterName, string address, bool added, bool ready, string? error,
                        Process? cleanupWatchdog = null, string? leasePath = null)
    {
        _adapterName = adapterName;
        _address = address;
        _added = added;
        _cleanupWatchdog = cleanupWatchdog;
        _leasePath = leasePath;
        Ready = ready;
        Error = error;
    }

    public bool Ready { get; }

    public string? Error { get; }

    public static OnLinkAlias Ensure(string remoteAddress, string localIp, uint ifIndex, bool enabled = true)
    {
        if (!IPAddress.TryParse(remoteAddress, out IPAddress? remote) ||
            remote.AddressFamily != AddressFamily.InterNetwork ||
            !IPAddress.TryParse(localIp, out IPAddress? local))
        {
            return new OnLinkAlias(null, remoteAddress, false, false, "远端地址或本地地址不是 IPv4");
        }

        bool alreadyLocal = remote.Equals(local) || IsLocalAddress(remote);
        if (!enabled)
        {
            if (!alreadyLocal || !WaitBindable(remote, TimeSpan.FromSeconds(10)))
            {
                return new OnLinkAlias(null, remoteAddress, false, false,
                                       $"--no-alias 但 {remoteAddress} 不是可 bind 的本机地址");
            }

            return new OnLinkAlias(null, remoteAddress, false, true, null);
        }

        if (alreadyLocal)
        {
            if (!WaitBindable(remote, TimeSpan.FromSeconds(10)))
            {
                return new OnLinkAlias(null, remoteAddress, false, false,
                                       $"{remoteAddress} 已是本机地址但一直不可 bind（可能仍是 Tentative）");
            }

            return new OnLinkAlias(null, remoteAddress, false, true, null);
        }

        NetworkInterface? adapter = NetworkInterface.GetAllNetworkInterfaces()
            .FirstOrDefault(candidate => candidate.GetIPProperties().GetIPv4Properties()?.Index == (int)ifIndex);
        if (adapter is null)
        {
            return new OnLinkAlias(null, remoteAddress, false, false,
                                   $"找不到 ifIndex={ifIndex} 的适配器，无法添加 {remoteAddress}");
        }

        const string mask = "255.255.255.0";
        if (!RunNetsh($"interface ip add address name=\"{adapter.Name}\" address={remoteAddress} mask={mask}"))
        {
            return new OnLinkAlias(null, remoteAddress, false, false,
                                   $"netsh 添加 {remoteAddress} 到 {adapter.Name} 失败");
        }

        Console.WriteLine($"[alias] 已添加 {remoteAddress}/24 到 {adapter.Name}（测试后删除）");
        if (!WaitBindable(remote, TimeSpan.FromSeconds(15)))
        {
            return new OnLinkAlias(adapter.Name, remoteAddress, true, false,
                                   $"{remoteAddress} 添加后 15s 仍不可 bind（DAD/Tentative）");
        }

        string lease = Path.Combine(Path.GetTempPath(), $"wfprobe-alias-{remoteAddress}.lease");
        Process? watchdog = StartCleanupWatchdog(adapter.Name, remoteAddress, lease);
        return new OnLinkAlias(adapter.Name, remoteAddress, true, true, null, watchdog, lease);
    }

    private static bool WaitBindable(IPAddress address, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (CanBind(address))
            {
                return true;
            }

            Thread.Sleep(100);
        }

        return CanBind(address);
    }

    private static bool CanBind(IPAddress address)
    {
        try
        {
            using Socket socket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Bind(new IPEndPoint(address, 0));
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private static Process? StartCleanupWatchdog(string adapterName, string address, string leasePath)
    {
        string? command = Environment.GetEnvironmentVariable("COMSPEC");
        if (command is null)
        {
            return null;
        }

        try
        {
            File.WriteAllText(leasePath, $"{adapterName}\n{address}\n");
            var startInfo = new ProcessStartInfo(command)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                Arguments = $"/c ping -n 601 127.0.0.1 >nul & if exist \"{leasePath}\" " +
                            $"(netsh interface ip delete address name=\"{adapterName}\" addr={address} >nul 2>&1 & " +
                            $"del \"{leasePath}\" >nul 2>&1)",
            };
            Process? process = Process.Start(startInfo);
            if (process is not null)
            {
                Console.WriteLine($"[alias] 清理 watchdog 已启动：异常退出时最多 10 分钟后删除 {address}");
            }

            return process;
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            Console.WriteLine($"[alias] 清理 watchdog 启动失败：{exception.Message}");
            return null;
        }
    }

    private static bool IsLocalAddress(IPAddress address)
    {
        foreach (NetworkInterface adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            foreach (UnicastIPAddressInformation unicast in adapter.GetIPProperties().UnicastAddresses)
            {
                if (unicast.Address.Equals(address))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool RunNetsh(string arguments)
    {
        try
        {
            using Process process = Process.Start(new ProcessStartInfo("netsh", arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            })!;
            process.WaitForExit(10000);
            if (process.ExitCode != 0)
            {
                Console.WriteLine($"[alias] netsh {arguments} -> exit={process.ExitCode} {process.StandardError.ReadToEnd()}");
                return false;
            }

            return true;
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Console.WriteLine($"[alias] netsh 不可用：{exception.Message}");
            return false;
        }
    }

    public void Dispose()
    {
        if (!_added || _adapterName is null)
        {
            return;
        }

        if (_leasePath is not null)
        {
            try
            {
                File.Delete(_leasePath);
            }
            catch (IOException)
            {
                // watchdog 仍会按 lease 删除；这里不阻断。
            }
        }

        if (_cleanupWatchdog is not null && !_cleanupWatchdog.HasExited)
        {
            _cleanupWatchdog.Kill(true);
        }

        if (RunNetsh($"interface ip delete address name=\"{_adapterName}\" addr={_address}"))
        {
            Console.WriteLine($"[alias] 已删除 {_address}（{_adapterName}）");
        }
    }
}
