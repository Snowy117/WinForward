using System.Collections.Concurrent;
using System.Net.Sockets;
using WinForward.Userland.Configuration;

namespace WinForward.Userland.Relay;

/// <summary>
/// 接受被重定向的连接，读回原始目的地，建出站连接并双向转发。
/// </summary>
/// <remarks>
/// 每个会话：SIO_QUERY_WFP_CONNECTION_REDIRECT_CONTEXT → 原始目的地；
/// 再把 SIO_QUERY_WFP_CONNECTION_REDIRECT_RECORDS 读到的 records 挂到出站 socket 上。
/// 没有 records 时靠驱动的 self-PID 闸排除自流量：中继进程的 PID 在 <c>probe_config</c> 的
/// self-PID 集合里，所以出站连接不会被再次重定向。
///
/// 读不到 redirect context 的连接一律拒绝：那意味着它不是被重定向来的（例如有人直连监听端口，
/// 由闸 1 放行），没有原始目的地可转发，放过去只会变成一次错误的转发。
/// </remarks>
public sealed class RelayServer : IAsyncDisposable
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(2);

    // 一个方向正常 EOF 之后，给另一方向留的收尾窗口：对端可能永远不关连接，不能让会话无限挂着。
    private static readonly TimeSpan SessionDrainTimeout = TimeSpan.FromSeconds(30);

    private readonly ListenerSet _listeners;
    private readonly TextWriter _report;
    private readonly object _reportGate = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ConcurrentDictionary<int, Task> _sessions = new();
    private readonly Task[] _acceptLoops;
    private readonly bool _attachRedirectRecords;
    private readonly OutboundConnector _outbound;

    private int _nextSessionId;
    private long _accepted;
    private long _refused;
    private long _bytesClientToServer;
    private long _bytesServerToClient;

    private RelayServer(ListenerSet listeners, TextWriter report, bool attachRedirectRecords, Socks5Endpoint? socks5)
    {
        _listeners = listeners;
        _report = report;
        _attachRedirectRecords = attachRedirectRecords;
        _outbound = new OutboundConnector(socks5);
        _acceptLoops = listeners.Listeners.Select(listener => AcceptLoopAsync(listener)).ToArray();
    }

    /// <param name="attachRedirectRecords">
    /// true：把原连接的 redirect records 挂到出站 socket（走驱动闸 2）；
    /// false：不带 records，靠 self-PID 闸（闸 4）排除自流量——E2E 用它真正走一遍闸 4。
    /// </param>
    /// <param name="socks5">null（默认）表示直连；非 null 时所有出站连接先 CONNECT 到该上游。</param>
    public static RelayServer Start(ListenerSet listeners,
                                    TextWriter report,
                                    bool attachRedirectRecords = true,
                                    Socks5Endpoint? socks5 = null)
    {
        ArgumentNullException.ThrowIfNull(listeners);
        ArgumentNullException.ThrowIfNull(report);
        return new RelayServer(listeners, report, attachRedirectRecords, socks5);
    }
    public int Port => _listeners.Port;

    public long Accepted => Interlocked.Read(ref _accepted);

    public long Refused => Interlocked.Read(ref _refused);

    public long BytesClientToServer => Interlocked.Read(ref _bytesClientToServer);

    public long BytesServerToClient => Interlocked.Read(ref _bytesServerToClient);

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
            byte[]? rawContext = SocketIoctl.TryQueryRedirectContext(client, out string error);
            if (rawContext is null)
            {
                Interlocked.Increment(ref _refused);
                Report($"relay refused #{id} client={clientText} reason=no-redirect-context detail={error}");
                return;
            }

            if (!RedirectDestination.TryParse(rawContext, out RedirectDestination? destination, out string parseError) ||
                destination is null)
            {
                Interlocked.Increment(ref _refused);
                Report($"relay refused #{id} client={clientText} reason=bad-redirect-context detail={parseError}");
                return;
            }

            byte[] records = _attachRedirectRecords ? SocketIoctl.TryQueryRedirectRecords(client) : Array.Empty<byte>();
            Report($"relay accept #{id} original={destination} family={destination.FamilyText} " +
                   $"pid={destination.ProcessId} rule={destination.RuleIndex} client={clientText} records={records.Length}");

            try
            {
                using Socket outbound =
                    await _outbound.ConnectAsync(destination.EndPoint, records, ConnectTimeout, _shutdown.Token);

                Report($"relay connected #{id} to={destination} via={_outbound.Route}");

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
                                           or Socks5Exception)
            {
                Report($"relay error #{id} to={destination} {exception.GetType().Name}: {exception.Message}");
            }
        }
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
