using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using WinForward.Userland.Device;

namespace WinForward.Userland.Relay;

/// <summary>
/// forwarded 路径：按 listener 端口与对端（client）查驱动的 redirect 表。TCP 用 accepted socket 的对端，
/// UDP 用收到 datagram 的源地址；同一个 listener 端口上的两类表项由查询里的 protocol 区分。
/// 查不到（STATUS_NOT_FOUND）不是异常，而是「这条流量不是 forwarded 重定向来的」，交给下一个 source。
/// </summary>
public sealed class ForwardedRedirectContextSource(WfpDeviceClient device) : IRedirectContextSource
{
    private const int ErrorNotFound = 1168;

    private readonly WfpDeviceClient _device = device ?? throw new ArgumentNullException(nameof(device));

    public string Name => "forwarded";

    public bool TryRead(Socket client, [NotNullWhen(true)] out RedirectDestination? destination, out string error)
    {
        ArgumentNullException.ThrowIfNull(client);
        destination = null;

        if (client.LocalEndPoint is not IPEndPoint local || client.RemoteEndPoint is not IPEndPoint remote)
        {
            error = "accepted socket 没有 IP 端点";
            return false;
        }

        return TryQuery(ProbeForwardedRedirectQuery.ForClient(local, remote), out destination, out error);

    }

    public bool TryReadUdp(EndPoint client, IPEndPoint listener,
                           [NotNullWhen(true)] out RedirectDestination? destination, out string error)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(listener);
        destination = null;

        if (client is not IPEndPoint remote)
        {
            error = "UDP 客户端端点不是 IP";
            return false;
        }

        if (listener.Port is <= 0 or > 65535)
        {
            error = string.Create(CultureInfo.InvariantCulture, $"listener 端口 {listener.Port} 越界");
            return false;
        }

        return TryQuery(ProbeForwardedRedirectQuery.ForClientUdp(listener, remote), out destination, out error);
    }


    private bool TryQuery(ProbeForwardedRedirectQuery query, out RedirectDestination? destination, out string error)
    {
        destination = null;
        ProbeForwardedRedirect redirect;
        try
        {
            redirect = _device.QueryForwardedRedirect(query);
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == ErrorNotFound)
        {
            error = "forwarded redirect 表没有该客户端";
            return false;
        }
        catch (Win32Exception exception)
        {
            error = string.Create(CultureInfo.InvariantCulture, $"forwarded redirect 查询失败：Win32={exception.NativeErrorCode} {exception.Message}");
            return false;
        }
        catch (InvalidOperationException exception)
        {
            error = $"forwarded redirect 查询失败：{exception.Message}";
            return false;
        }

        return RedirectDestination.TryFromForwarded(redirect, out destination, out error);
    }
}
