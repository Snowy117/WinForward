using System.Net.Sockets;

namespace WinForward.Socks5Tool;

/// <summary>一个方向的字节搬运：读到 EOF 就给对面半关闭，失败只结束这一方向。</summary>
internal static class SocketCopy
{
    internal static async Task<long> CopyAsync(Socket from, Socket to, CancellationToken token)
    {
        byte[] buffer = new byte[64 * 1024];
        long total = 0;
        try
        {
            while (true)
            {
                int read = await from.ReceiveAsync(buffer, SocketFlags.None, token);
                if (read == 0)
                {
                    break;
                }

                int offset = 0;
                while (offset < read)
                {
                    offset += await to.SendAsync(buffer.AsMemory(offset, read - offset), SocketFlags.None, token);
                }

                total += read;
            }

            try
            {
                to.Shutdown(SocketShutdown.Send);
            }
            catch (Exception exception) when (exception is SocketException or ObjectDisposedException)
            {
                // 对面已经关了，半关闭没有意义。
            }
        }
        catch (Exception exception) when (exception is SocketException or OperationCanceledException
                                               or ObjectDisposedException)
        {
            // 连接被任一侧关闭：已搬运的字节仍然有效。
        }

        return total;
    }
}
