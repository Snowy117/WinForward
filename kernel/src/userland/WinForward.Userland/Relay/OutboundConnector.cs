using System.Net;
using System.Net.Sockets;
using WinForward.Userland.Configuration;

namespace WinForward.Userland.Relay;

/// <summary>中继的出站连接：配了上游就一律经 SOCKS5 CONNECT，没配就直接连原始目的地。</summary>
/// <remarks>
/// 「配了上游」与「没配上游」是两条互斥的路，这里没有「上游失败就直连」的分支，也不要加：
/// 那会把代理失效伪装成成功，是最难被发现的一类坏。失败一路抛给会话级 catch，由它记 <c>relay error</c>。
/// </remarks>
public sealed class OutboundConnector
{
    private readonly Socks5Endpoint? _defaultSocks5;

    public OutboundConnector(Socks5Endpoint? socks5) => _defaultSocks5 = socks5;

    /// <summary>报告用：默认路由（<c>direct</c> 或 <c>socks5:host:port</c>）。</summary>
    public string Route => _defaultSocks5 is null ? "direct" : $"socks5:{_defaultSocks5}";

    public Task<Socket> ConnectAsync(EndPoint destination,
                                     byte[] records,
                                     TimeSpan timeout,
                                     CancellationToken token) =>
        ConnectAsync(destination, records, _defaultSocks5, timeout, token);

    /// <summary>
    /// 按本次会话解析出的上游连接；<paramref name="socks5"/> 为 null 时直连。
    /// forwarded 的 upstream_id 由调用方先映射成 endpoint，缺失时调用方在进入这里之前就拒绝。
    /// </summary>
    public async Task<Socket> ConnectAsync(EndPoint destination,
                                           byte[] records,
                                           Socks5Endpoint? socks5,
                                           TimeSpan timeout,
                                           CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(records);

        if (socks5 is not null)
        {
            return await Socks5Client.ConnectAsync(
                socks5, destination, socket => SocketIoctl.SetRedirectRecords(socket, records), timeout, token);
        }

        if (destination is not IPEndPoint direct)
        {
            throw new ArgumentException("直连只接受 IPEndPoint；域名只能交给 SOCKS5 上游解析", nameof(destination));
        }

        Socket socket = new(direct.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            SocketIoctl.SetRedirectRecords(socket, records);
            using (CancellationTokenSource connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                connectTimeout.CancelAfter(timeout);
                await socket.ConnectAsync(direct, connectTimeout.Token);
            }

            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
