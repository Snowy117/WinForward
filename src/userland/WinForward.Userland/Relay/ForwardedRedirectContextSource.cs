using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using WinForward.Userland.Device;

namespace WinForward.Userland.Relay;

/// <summary>
/// forwarded 路径：按 accepted socket 的本地端口（listener）与对端（client）查驱动的 redirect 表。
/// 查不到（STATUS_NOT_FOUND）不是异常，而是「这条连接不是 forwarded 重定向来的」，交给下一个 source。
/// </summary>
public sealed class ForwardedRedirectContextSource : IRedirectContextSource
{
    private const int ErrorNotFound = 1168;

    private readonly WfpDeviceClient _device;

    public ForwardedRedirectContextSource(WfpDeviceClient device) =>
        _device = device ?? throw new ArgumentNullException(nameof(device));

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

        ProbeForwardedRedirectQuery query =
            ProbeForwardedRedirectQuery.ForClient((ushort)local.Port, remote);
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
            error = $"forwarded redirect 查询失败：Win32={exception.NativeErrorCode} {exception.Message}";
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
