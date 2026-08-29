#pragma warning disable CA1416 // TcpProxyRelay is platform-neutral; only its production factory is Windows-specific.
using System.Net;
using System.Net.Sockets;
using BenchmarkDotNet.Attributes;
using WinForward.Runtime.TcpRedirect;

namespace WinForward.Benchmarks.Perf;

public class TcpRelayBenchmarks
{
    private static long s_sink;

    [Params(1, 1024, 8192, 65536)]
    public int ChunkBytes { get; set; }

    [Benchmark]
    public async Task OneWayAsync()
    {
        var transferBytes = ChunkBytes == 1 ? 256 * 1024 : 16 * 1024 * 1024;
        var (localPeer, relayLocal) = await CreateSocketPairAsync().ConfigureAwait(false);
        var (upstreamPeer, relayUpstream) = await CreateSocketPairAsync().ConfigureAwait(false);
        using (localPeer)
        using (upstreamPeer)
        using (var relayUpstreamStream = new NetworkStream(relayUpstream, ownsSocket: true))
        await using (var relay = new TcpProxyRelay(relayLocal, relayUpstreamStream, NoopAsyncDisposable.Instance))
        {
            var sendBuffer = new byte[ChunkBytes];
            var receiveBuffer = new byte[Math.Max(ChunkBytes, 8192)];
            var sender = Task.Run(async () =>
            {
                var remaining = transferBytes;
                while (remaining > 0)
                {
                    var count = Math.Min(sendBuffer.Length, remaining);
                    await localPeer.SendAsync(sendBuffer.AsMemory(0, count), SocketFlags.None).ConfigureAwait(false);
                    remaining -= count;
                }
                localPeer.Shutdown(SocketShutdown.Send);
            });
            var received = 0;
            while (true)
            {
                var count = await upstreamPeer.ReceiveAsync(receiveBuffer, SocketFlags.None).ConfigureAwait(false);
                if (count == 0) break;
                received += count;
            }
            await sender.ConfigureAwait(false);
            upstreamPeer.Shutdown(SocketShutdown.Send);
            await relay.Completion.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            Volatile.Write(ref s_sink, received);
            if (received != transferBytes) throw new InvalidOperationException($"Relay copied {received} of {transferBytes} bytes.");
        }
    }

    private static async Task<(Socket Peer, Socket Relay)> CreateSocketPairAsync()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var peer = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            await peer.ConnectAsync((IPEndPoint)listener.LocalEndpoint!).ConfigureAwait(false);
            return (peer, await listener.AcceptSocketAsync().ConfigureAwait(false));
        }
        finally
        {
            listener.Stop();
        }
    }
}
