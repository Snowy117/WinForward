using System.Net.Sockets;

namespace WinForward.Userland.Relay;

/// <summary>一个方向的搬运结果；<see cref="CleanEof"/> 为 false 表示对端异常断开或被取消。</summary>
internal readonly record struct PumpOutcome(long Bytes, bool CleanEof);

/// <summary>
/// 把两个 socket 双向接起来，并负责会话收尾：
/// 正常 EOF → 半关闭（对侧继续）；异常结束 → 取消兄弟泵；半关闭后对端不关 → 超时强制收尾。
/// </summary>
/// <remarks>
/// 收尾规则单独放在这里是为了可测：单方向 RST 时兄弟泵必须马上退出，不能靠进程退出来兜底。
/// <see cref="RelayServer"/> 只负责读 redirect context、建出站连接与日志。
/// </remarks>
internal static class PumpPair
{
    internal static async Task<(PumpOutcome FirstToSecond, PumpOutcome SecondToFirst, string Reason)> RunAsync(
        Socket first,
        Socket second,
        TimeSpan drainTimeout,
        CancellationToken token)
    {
        using CancellationTokenSource session = CancellationTokenSource.CreateLinkedTokenSource(token);
        Task<PumpOutcome> forward = PumpAsync(first, second, session.Token);
        Task<PumpOutcome> backward = PumpAsync(second, first, session.Token);

        Task<PumpOutcome> firstDone = await Task.WhenAny(forward, backward);
        PumpOutcome firstOutcome = await firstDone;
        string reason = firstOutcome.CleanEof ? "peer-closed" : "pump-failed";
        if (!firstOutcome.CleanEof)
        {
            // 对端 RST / 发送失败 / 控制器停止：另一方向不可能再有意义，取消它。
            session.Cancel();
        }

        Task<PumpOutcome> secondDone = ReferenceEquals(firstDone, forward) ? backward : forward;
        try
        {
            await secondDone.WaitAsync(drainTimeout, CancellationToken.None);
        }
        catch (TimeoutException)
        {
            // 半关闭之后对端可能永远不关连接：主动取消并显式关掉两端。
            reason = "drain-timeout";
            session.Cancel();
            CloseQuietly(first);
            CloseQuietly(second);
            await secondDone;
        }

        return (forward.Result, backward.Result, reason);
    }

    /// <summary>
    /// 把一个方向上的字节搬到对面；读到 EOF 后对对面做半关闭，让另一方向继续把剩余数据送完。
    /// <see cref="PumpOutcome.CleanEof"/> 区分「正常读到 EOF」与「异常结束」，调用方据此决定是否取消兄弟泵。
    /// </summary>
    internal static async Task<PumpOutcome> PumpAsync(Socket from, Socket to, CancellationToken token)
    {
        byte[] buffer = new byte[64 * 1024];
        long total = 0;
        bool cleanEof = false;
        try
        {
            while (true)
            {
                int read = await from.ReceiveAsync(buffer, SocketFlags.None, token);
                if (read == 0)
                {
                    cleanEof = true;
                    break;
                }

                int offset = 0;
                while (offset < read)
                {
                    int sent = await to.SendAsync(buffer.AsMemory(offset, read - offset), SocketFlags.None, token);
                    offset += sent;
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
        catch (Exception exception) when (exception is SocketException or OperationCanceledException or ObjectDisposedException)
        {
            // 连接被任一侧关闭或被 shutdown：已搬运的字节仍然有效，但不是正常 EOF。
        }

        return new PumpOutcome(total, cleanEof);
    }

    private static void CloseQuietly(Socket socket)
    {
        try
        {
            socket.Shutdown(SocketShutdown.Both);
        }
        catch (Exception exception) when (exception is SocketException or ObjectDisposedException)
        {
            // 已经关了。
        }
    }
}
