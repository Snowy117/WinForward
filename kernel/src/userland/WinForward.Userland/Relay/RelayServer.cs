using System.Collections.Concurrent;
using System.Net.Sockets;
using WinForward.Userland.Configuration;
using WinForward.Userland.Device;

namespace WinForward.Userland.Relay;

/// <summary>
/// 接受被重定向的连接，读回原始目的地，建出站连接并双向转发。
/// </summary>
/// <remarks>
/// 每个会话按 <see cref="IRedirectContextSource"/> 的顺序读原始目的地：通常是 forwarded 表在前、
/// host redirect context 在后；都失败就拒绝。拿到 forwarded 的 upstream_id 后按
/// <see cref="ForwardedUpstream"/> 有序表选 SOCKS5 端点，缺表项/缺上游一律拒绝，不回退直连。
/// </remarks>
public sealed class RelayServer : IAsyncDisposable
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan SessionDrainTimeout = TimeSpan.FromSeconds(30);

    private readonly ListenerSet _listeners;
    private readonly TextWriter _report;
    private readonly object _reportGate = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ConcurrentDictionary<int, Task> _sessions = new();
    private readonly Task[] _acceptLoops;
    private readonly bool _attachRedirectRecords;
    private readonly OutboundConnector _outbound;
    private readonly IReadOnlyList<IRedirectContextSource> _redirectSources;
    private readonly IReadOnlyList<ForwardedUpstream> _forwardedUpstreams;
    private readonly Socks5Endpoint? _defaultSocks5;

    private int _nextSessionId;
    private long _accepted;
    private long _refused;
    private long _bytesClientToServer;
    private long _bytesServerToClient;

    private RelayServer(ListenerSet listeners,
                        TextWriter report,
                        bool attachRedirectRecords,
                        Socks5Endpoint? socks5,
                        IReadOnlyList<IRedirectContextSource> redirectSources,
                        IReadOnlyList<ForwardedUpstream> forwardedUpstreams)
    {
        _listeners = listeners;
        _report = report;
        _attachRedirectRecords = attachRedirectRecords;
        _outbound = new OutboundConnector(socks5);
        _redirectSources = redirectSources;
        _forwardedUpstreams = forwardedUpstreams;
        _defaultSocks5 = socks5;
        _acceptLoops = listeners.Listeners.Select(listener => AcceptLoopAsync(listener)).ToArray();
    }

    /// <param name="attachRedirectRecords">
    /// true：把原连接的 redirect records 挂到出站 socket（走驱动闸 2）；
    /// false：不带 records，靠 self-PID 闸（闸 4）排除自流量——E2E 用它真正走一遍闸 4。
    /// </param>
    /// <param name="socks5">null（默认）表示直连；非 null 时所有出站连接先 CONNECT 到该上游。</param>
    /// <param name="redirectSources">原始目的地来源，按顺序尝试；null 时只走 host redirect context。</param>
    /// <param name="forwardedUpstreams">forwarded upstream_id → SOCKS5 端点的有序表。</param>
    public static RelayServer Start(ListenerSet listeners,
                                    TextWriter report,
                                    bool attachRedirectRecords = true,
                                    Socks5Endpoint? socks5 = null,
                                    IReadOnlyList<IRedirectContextSource>? redirectSources = null,
                                    IReadOnlyList<ForwardedUpstream>? forwardedUpstreams = null)
    {
        ArgumentNullException.ThrowIfNull(listeners);
        ArgumentNullException.ThrowIfNull(report);
        return new RelayServer(listeners,
                               report,
                               attachRedirectRecords,
                               socks5,
                               redirectSources ?? new IRedirectContextSource[] { new HostRedirectContextSource() },
                               forwardedUpstreams ?? Array.Empty<ForwardedUpstream>());
    }

    public int Port => _listeners.Port;

    public long Accepted => Interlocked.Read(ref _accepted);

    public long Refused => Interlocked.Read(ref _refused);

    public long BytesClientToServer => Interlocked.Read(ref _bytesClientToServer);

    public long BytesServerToClient => Interlocked.Read(ref _bytesServerToClient);

    internal static string? RelayRefusalReason(RedirectDestination destination) =>
        destination.Protocol == RedirectContext.ProtocolTcp ? null : "non-tcp-redirect-context";

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        _listeners.Dispose();

        try
        {
            await Task.WhenAll(_acceptLoops);
        }
        catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException or SocketException)
        {
            // 关闭监听时 accept 抛异常是正常路径。
        }

        Task pending = Task.WhenAll(_sessions.Values.ToArray());
        Task finished = await Task.WhenAny(pending, Task.Delay(ShutdownTimeout));
        if (finished != pending)
        {
            Report($"relay shutdown forced sessions={_sessions.Count}");
        }

        _shutdown.Dispose();
    }

    private async Task AcceptLoopAsync(TcpListener listener)
    {
        while (!_shutdown.IsCancellationRequested)
        {
            Socket socket;
            try
            {
                socket = await listener.AcceptSocketAsync(_shutdown.Token);
            }
            catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }

            int id = Interlocked.Increment(ref _nextSessionId);
            Interlocked.Increment(ref _accepted);
            Task session = RunSessionAsync(id, socket);
            _sessions[id] = session;
            _ = session.ContinueWith(
                task =>
                {
                    _ = task.Exception;
                    _sessions.TryRemove(id, out _);
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    private async Task RunSessionAsync(int id, Socket client)
    {
        using (client)
        {
            string clientText = client.RemoteEndPoint?.ToString() ?? "?";
            RedirectDestination? destination = null;
            string sourceName = "none";
            var failures = new List<string>(_redirectSources.Count);
            foreach (IRedirectContextSource source in _redirectSources)
            {
                if (source.TryRead(client, out destination, out string readError) && destination is not null)
                {
                    sourceName = source.Name;
                    break;
                }
                failures.Add($"{source.Name}: {readError}");
            }

            if (destination is null)
            {
                Interlocked.Increment(ref _refused);
                Report($"relay refused #{id} client={clientText} reason=no-redirect-context " +
                       $"detail={string.Join("; ", failures)}");
                return;
            }

            string? refusal = RelayRefusalReason(destination);
            if (refusal is not null)
            {
                Interlocked.Increment(ref _refused);
                Report($"relay refused #{id} client={clientText} reason={refusal} protocol={destination.ProtocolText}");
                return;
            }

            if (!TryResolveUpstream(destination, out Socks5Endpoint? upstream, out string upstreamError))
            {
                Interlocked.Increment(ref _refused);
                // 已经 accept 的连接必须显式中止：关闭而不是半关闭，客户端看到 RST 而不是挂到超时。
                client.LingerState = new LingerOption(true, 0);
                Report($"relay refused #{id} client={clientText} reason={upstreamError} original={destination}");
                return;
            }

            byte[] records = _attachRedirectRecords ? SocketIoctl.TryQueryRedirectRecords(client) : Array.Empty<byte>();
            string upstreamText = upstream is null ? "direct" : $"socks5:{upstream}";
            Report($"relay accept #{id} original={destination} source={sourceName} family={destination.FamilyText} " +
                   $"protocol={destination.ProtocolText} pid={destination.ProcessId} " +
                   $"rule={destination.RuleIndex} upstream_id={destination.UpstreamId?.ToString() ?? "-"} " +
                   $"ingress={destination.IngressIfIndex} upstream={upstreamText} client={clientText} records={records.Length}");

            try
            {
                using Socket outbound =
                    await _outbound.ConnectAsync(destination.EndPoint, records, upstream, ConnectTimeout, _shutdown.Token);

                Report($"relay connected #{id} to={destination} via={upstreamText}");

                (PumpOutcome up, PumpOutcome down, string reason) =
                    await PumpPair.RunAsync(client, outbound, SessionDrainTimeout, _shutdown.Token);
                long clientBytes = up.Bytes;
                long serverBytes = down.Bytes;
                Interlocked.Add(ref _bytesClientToServer, clientBytes);
                Interlocked.Add(ref _bytesServerToClient, serverBytes);
                if (up.CleanEof && down.CleanEof)
                {
                    Report($"relay close #{id} in={clientBytes} out={serverBytes}");
                }
                else
                {
                    Report($"relay abort #{id} in={clientBytes} out={serverBytes} reason={reason}");
                }
            }
            catch (Exception exception) when (exception is SocketException or OperationCanceledException or IOException
                                           or ObjectDisposedException or System.ComponentModel.Win32Exception
                                           or Socks5Exception or InvalidOperationException)
            {
                // 上游连接/中继失败：用 RST 结束客户端连接，不把失败伪装成正常半关闭。
                client.LingerState = new LingerOption(true, 0);
                Report($"relay error #{id} to={destination} {exception.GetType().Name}: {exception.Message}");
            }
        }
    }

    /// <summary>
    /// forwarded 目的地按 upstream_id 从有序表取端点；host 目的地走全局 socks5（或直连）。
    /// 返回 false 表示配置缺失，调用方必须拒绝连接，绝不能回退直连。
    /// </summary>
    private bool TryResolveUpstream(RedirectDestination destination,
                                    out Socks5Endpoint? upstream,
                                    out string error)
    {
        upstream = null;
        error = string.Empty;
        if (destination.Source != RedirectSource.Forwarded)
        {
            upstream = _defaultSocks5;
            return true;
        }

        if (destination.UpstreamId is not uint upstreamId)
        {
            error = "forwarded-redirect-without-upstream";
            return false;
        }

        foreach (ForwardedUpstream candidate in _forwardedUpstreams)
        {
            if (candidate.Id == upstreamId)
            {
                upstream = candidate.Endpoint;
                return true;
            }
        }

        error = $"forwarded-upstream-missing-id-{upstreamId}";
        return false;
    }

    private void Report(string line)
    {
        lock (_reportGate)
        {
            _report.WriteLine(line);
            _report.Flush();
        }
    }
}
