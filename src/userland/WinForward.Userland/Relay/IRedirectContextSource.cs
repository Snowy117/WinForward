using System.Diagnostics.CodeAnalysis;
using System.Net.Sockets;

namespace WinForward.Userland.Relay;

/// <summary>
/// 中继接受连接后读回原始目的地的接缝。host 路径读 WFP redirect context，
/// forwarded 路径查驱动 redirect 表；RelayServer 依次尝试，全部失败就拒绝连接。
/// </summary>
public interface IRedirectContextSource
{
    string Name { get; }

    bool TryRead(Socket client, [NotNullWhen(true)] out RedirectDestination? destination, out string error);
}
