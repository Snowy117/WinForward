using System.Net.Sockets;
using WinForward.E2E.Wire;

namespace WinForward.E2E.Client.Arms;

internal static class MixBulkLoop
{
    internal const long BulkBitsPerSecond = 5_000_000;
    private const int BulkPayloadBytes = 32 * 1024;

    internal static async Task BulkLoopAsync(
        ArmContext context,
        MixCounters counters,
        int desktopIndex,
        long startTicks,
        long deadlineTicks,
        CancellationToken cancellationToken)
    {
        const int frameLength = FrameCodec.HeaderSize + BulkPayloadBytes + FrameCodec.TrailerSize;
        using var socket = context.CreateTcpSocket();
        if (!(await SocketOps.TryConnectAsync(socket, context.TcpEndPoint, cancellationToken).ConfigureAwait(false)).Ok)
        {
            Interlocked.Increment(ref counters._bulkErrors);
            return;
        }

        var frame = new FrameBuffer(BulkPayloadBytes);
        var reader = new FrameStreamReader(socket, 128 * 1024);
        var limiter = new AggregateRateLimiter(BulkBitsPerSecond / 8, long.MaxValue, startTicks);
        var connectionId = 0x4D42_0000u + (uint)desktopIndex;
        ulong sequence = 0;

        try
        {
            await SocketOps.SendCommandAsync(socket, connectionId, TcpMode.Clean, 0, cancellationToken).ConfigureAwait(false);
            while (Clock.Now < deadlineTicks)
            {
                if (!limiter.TryReserve(frameLength, out var waitUntilTicks))
                {
                    break;
                }

                if (waitUntilTicks > 0)
                {
                    await Pacer.WaitUntilAsync(waitUntilTicks, cancellationToken).ConfigureAwait(false);
                }

                var length = frame.Build(connectionId, ++sequence, Clock.Now);
                await socket.SendAsync(frame.Memory[..length], SocketFlags.None, cancellationToken).ConfigureAwait(false);
                Interlocked.Add(ref counters._bulkBytesSent, length);

                var status = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                if (status != FrameReadStatus.Frame)
                {
                    Interlocked.Increment(ref counters._bulkErrors);
                    break;
                }

                Interlocked.Add(ref counters._bulkBytes, reader.Raw.Length);
                Interlocked.Increment(ref counters.BulkFramesPerDesktop[desktopIndex]);
            }

            SocketOps.ShutdownQuietly(socket, SocketShutdown.Send);
        }
        catch (SocketException)
        {
            Interlocked.Increment(ref counters._bulkErrors);
        }
        catch (OperationCanceledException)
        {
            /* the arm deadline or a shutdown request ended the loop */
        }
        catch (ObjectDisposedException)
        {
            /* teardown closed the socket first: a teardown is not a bulk error and books nothing (D19.2 ⑨) */
        }
    }
}
