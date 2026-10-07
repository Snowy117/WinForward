using System.Net.Sockets;
using WinForward.E2E.Wire;

namespace WinForward.E2E.Client.Arms;

internal static class MixPageLoop
{
    internal const int PageConnections = 13;
    internal const int PageTotalRequests = 73;
    internal const int PageMessageBytes = 38_000;
    internal const int DnsQueriesPerPage = 4;
    internal static readonly TimeSpan s_pageInterval = TimeSpan.FromSeconds(20);

    internal static async Task PageLoopAsync(
        ArmContext context,
        MixCounters counters,
        int desktopIndex,
        long startTicks,
        long deadlineTicks,
        CancellationToken cancellationToken)
    {
        var pacer = new Pacer(1.0 / s_pageInterval.TotalSeconds, startTicks);
        long pageIndex = 0;
        try
        {
            while (Clock.Now < deadlineTicks)
            {
                var intended = pacer.IntendedTicks(pageIndex);
                if (intended >= deadlineTicks)
                {
                    return;
                }

                if (pageIndex > 0)
                {
                    await Pacer.WaitUntilAsync(intended, cancellationToken).ConfigureAwait(false);
                }

                var page = new Task[PageConnections];
                for (var connection = 0; connection < PageConnections; connection++)
                {
                    page[connection] = PageConnectionAsync(context, counters, desktopIndex, connection, intended, cancellationToken);
                }

                await Task.WhenAll(page).ConfigureAwait(false);
                Interlocked.Increment(ref counters._pages);
                await PageDnsAsync(context, counters, desktopIndex, pageIndex, cancellationToken).ConfigureAwait(false);
                pageIndex++;
            }
        }
        catch (OperationCanceledException)
        {
            /* the arm deadline or a shutdown request ended the loop */
        }
    }

    private static int RequestsForConnection(int connectionIndex)
    {
        const int baseRequests = PageTotalRequests / PageConnections;
        const int remainder = PageTotalRequests % PageConnections;
        return connectionIndex < remainder ? baseRequests + 1 : baseRequests;
    }

    private static async Task PageConnectionAsync(
        ArmContext context,
        MixCounters counters,
        int desktopIndex,
        int connectionIndex,
        long intendedTicks,
        CancellationToken cancellationToken)
    {
        using var socket = context.CreateTcpSocket();
        if (!(await SocketOps.TryConnectAsync(socket, context.TcpEndPoint, cancellationToken).ConfigureAwait(false)).Ok)
        {
            Interlocked.Increment(ref counters._pageErrors);
            return;
        }

        Interlocked.Increment(ref counters.PageConnectionsPerDesktop[desktopIndex]);
        context.Latency.TcpConnect.Record(Clock.ToNanoseconds(Clock.Now - intendedTicks));

        var frame = new FrameBuffer(PageMessageBytes);
        var reader = new FrameStreamReader(socket, 128 * 1024);
        var connectionId = 0x4D49_0000u + (uint)(desktopIndex << 8) + (uint)connectionIndex;
        var requests = RequestsForConnection(connectionIndex);
        ulong sequence = 0;

        try
        {
            await SocketOps.SendCommandAsync(socket, connectionId, TcpMode.Clean, 0, cancellationToken).ConfigureAwait(false);
            for (var request = 0; request < requests; request++)
            {
                var requestIntended = Clock.Now;
                var length = frame.Build(connectionId, ++sequence, requestIntended);
                await socket.SendAsync(frame.Memory[..length], SocketFlags.None, cancellationToken).ConfigureAwait(false);
                Interlocked.Add(ref counters._pageBytes, length);

                var status = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                if (status != FrameReadStatus.Frame)
                {
                    Interlocked.Increment(ref counters._pageErrors);
                    return;
                }

                Interlocked.Increment(ref counters._pageMessages);
                context.Latency.TcpRtt.Record(Clock.ToNanoseconds(Clock.Now - requestIntended));
            }

            SocketOps.ShutdownQuietly(socket, SocketShutdown.Send);
        }
        catch (SocketException)
        {
            Interlocked.Increment(ref counters._pageErrors);
        }
        catch (OperationCanceledException)
        {
            /* the arm deadline or a shutdown request ended the loop */
        }
        catch (ObjectDisposedException)
        {
            /* teardown closed the socket first: a teardown is not a page error and books nothing (D19.2 ⑨) */
        }
    }

    private static async Task PageDnsAsync(
        ArmContext context,
        MixCounters counters,
        int desktopIndex,
        long pageIndex,
        CancellationToken cancellationToken)
    {
        using var socket = context.CreateUdpSocket();
        if (!(await SocketOps.TryConnectAsync(socket, context.DnsEndPoint, cancellationToken).ConfigureAwait(false)).Ok)
        {
            // This desktop's page dns phase never reached the resolver, so none of its queries were
            // handed to a socket: booked in the phase's own catch-all bucket and the desktop's other
            // phases carry on.
            Interlocked.Increment(ref counters._dnsOther);
            return;
        }

        var sendBuffer = new byte[512];
        var receiveBuffer = new byte[4096];

        try
        {
            for (var query = 0; query < DnsQueriesPerPage; query++)
            {
                var index = (pageIndex * DnsQueriesPerPage) + query;
                var transactionId = (ushort)((desktopIndex * 1024) + index + 1);
                await SendPageDnsQueryAsync(context, socket, counters, sendBuffer, receiveBuffer, desktopIndex, index, transactionId, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (SocketException)
        {
            Interlocked.Increment(ref counters._dnsOther);
        }
        catch (OperationCanceledException)
        {
            /* the arm deadline or a shutdown request ended the loop */
        }
        catch (ObjectDisposedException)
        {
            /* teardown closed the socket first */
        }
    }

    private static async Task SendPageDnsQueryAsync(
        ArmContext context,
        Socket socket,
        MixCounters counters,
        byte[] sendBuffer,
        byte[] receiveBuffer,
        int desktopIndex,
        long index,
        ushort transactionId,
        CancellationToken cancellationToken)
    {
        var queryType = (index % 4) switch
        {
            0 => DnsWire.TypeA,
            1 => DnsWire.TypeAaaa,
            2 => DnsWire.TypeHttps,
            _ => DnsWire.TypeTxt,
        };

        var length = DnsWire.BuildQuery(
            sendBuffer,
            transactionId,
            string.Create(System.Globalization.CultureInfo.InvariantCulture, $"p{index}.bench.local"),
            queryType);
        if (length <= 0)
        {
            Interlocked.Increment(ref counters._dnsOther);
            return;
        }

        var intendedTicks = Clock.Now;
        await socket.SendAsync(sendBuffer.AsMemory(0, length), SocketFlags.None, cancellationToken).ConfigureAwait(false);
        Interlocked.Increment(ref counters.DnsSentPerDesktop[desktopIndex]);

        if (!await WaitForResponseAsync(socket, receiveBuffer, cancellationToken).ConfigureAwait(false))
        {
            Interlocked.Increment(ref counters._dnsTimeout);
            return;
        }

        context.Latency.DnsRtt.Record(Clock.ToNanoseconds(Clock.Now - intendedTicks));
        if (!DnsWire.TryParseResponse(receiveBuffer, out var responseId, out var rcode, out _) || responseId != transactionId)
        {
            Interlocked.Increment(ref counters._dnsOther);
            return;
        }

        if (rcode == 0)
        {
            Interlocked.Increment(ref counters._dnsAnswered);
        }
        else
        {
            Interlocked.Increment(ref counters._dnsServfail);
        }
    }

    private static async Task<bool> WaitForResponseAsync(Socket socket, byte[] receiveBuffer, CancellationToken cancellationToken)
    {
        var deadline = Clock.Now + Clock.FromSeconds(1);
        while (Clock.Now < deadline)
        {
            if (socket.Available > 0)
            {
                await socket.ReceiveAsync(receiveBuffer, SocketFlags.None, cancellationToken).ConfigureAwait(false);
                return true;
            }

            await Task.Delay(1, cancellationToken).ConfigureAwait(false);
        }

        return false;
    }
}
