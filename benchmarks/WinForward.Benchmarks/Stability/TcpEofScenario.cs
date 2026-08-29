#pragma warning disable CA1416 // TcpProxyRelay carries SupportedOSPlatform(windows) but is platform-neutral managed code; only its production factory is Windows-specific.

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using WinForward.Runtime.TcpRedirect;

namespace WinForward.Benchmarks.Stability;

internal static class TcpEofScenario
{
    private static readonly TimeSpan RelaySettleTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ReceiverTimeout = TimeSpan.FromSeconds(10);
    private const int ChunkBytes = 65_536;
    private const int MaximumErrorSamples = 8;

    public static async Task RunAsync(StabilityContext context, SoakOptions options)
    {
        var random = new Random(options.Seed);
        var counters = new TransferCounters();
        var clock = Stopwatch.StartNew();
        var duration = TimeSpan.FromSeconds(options.DurationSeconds);
        var workers = new Task[options.TcpConcurrency];
        for (var index = 0; index < workers.Length; index++)
        {
            workers[index] = Task.Run(() => WorkerLoopAsync(options, random, counters, clock, duration));
        }

        await Task.WhenAll(workers).ConfigureAwait(false);
        context.WriteResult(
            "tcp.unexpectedEof",
            new { concurrency = options.TcpConcurrency, transferBytes = options.TcpTransferBytes, durationSeconds = options.DurationSeconds, abortMix = options.AbortMix },
            counters.Snapshot());
    }

    private static async Task WorkerLoopAsync(SoakOptions options, Random random, TransferCounters counters, Stopwatch clock, TimeSpan duration)
    {
        while (clock.Elapsed < duration)
        {
            await RunTransferAsync(options, random, counters).ConfigureAwait(false);
        }
    }

    private static async Task RunTransferAsync(SoakOptions options, Random random, TransferCounters counters)
    {
        var stopwatch = Stopwatch.StartNew();
        var kind = PickKind(options, random);
        var fraction = PickFraction(random);
        Socket? localPeer = null;
        Socket? upstreamPeer = null;
        NetworkStream? upstreamStream = null;
        TcpProxyRelay? relay = null;
        try
        {
            var localPair = await CreateSocketPairAsync().ConfigureAwait(false);
            localPeer = localPair.Peer;
            var upstreamPair = await CreateSocketPairAsync().ConfigureAwait(false);
            upstreamPeer = upstreamPair.Peer;
            upstreamStream = new NetworkStream(upstreamPair.Relay, ownsSocket: true);
            relay = new TcpProxyRelay(localPair.Relay, upstreamStream, new UpstreamOwner(upstreamStream));

            var receiver = Task.Run(() => ReceiveAsync(upstreamPeer, options.TcpTransferBytes));
            var sender = Task.Run(() => SendAsync(localPeer, options.TcpTransferBytes, kind, fraction, relay, upstreamPeer));
            await sender.ConfigureAwait(false);
            try
            {
                await relay.Completion.WaitAsync(RelaySettleTimeout).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is TimeoutException or SocketException or ObjectDisposedException or IOException)
            {
                // An adversarial event can leave a pump blocked on a dead peer or fault the
                // completion; the receiver teardown below still yields a receiver-side classification.
            }

            await relay.DisposeAsync().ConfigureAwait(false);
            var completed = await Task.WhenAny(receiver, Task.Delay(ReceiverTimeout)).ConfigureAwait(false);
            if (completed != receiver)
            {
                upstreamPeer.Dispose();
            }

            var outcome = await receiver.ConfigureAwait(false);
            counters.Record(outcome.Outcome, outcome.Received, stopwatch.Elapsed.TotalMilliseconds, null);
        }
        catch (Exception exception) when (exception is SocketException or ObjectDisposedException or IOException)
        {
            counters.Record(TransferOutcome.Other, 0, stopwatch.Elapsed.TotalMilliseconds, $"{exception.GetType().Name}: {exception.Message}");
        }
        finally
        {
            if (relay is not null) await relay.DisposeAsync().ConfigureAwait(false);
            if (upstreamStream is not null) await upstreamStream.DisposeAsync().ConfigureAwait(false);
            upstreamPeer?.Dispose();
            localPeer?.Dispose();
        }
    }

    private static AbortKind PickKind(SoakOptions options, Random random)
    {
        lock (random)
        {
            return options.AbortMix.Pick(random);
        }
    }

    private static double PickFraction(Random random)
    {
        lock (random)
        {
            return random.Next(20, 81) / 100.0;
        }
    }

    private static async Task SendAsync(Socket localPeer, long transferBytes, AbortKind kind, double fraction, TcpProxyRelay relay, Socket upstreamPeer)
    {
        try
        {
            var buffer = new byte[ChunkBytes];
            var threshold = (long)(transferBytes * fraction);
            long written = 0;
            var fired = false;
            while (written < transferBytes)
            {
                if (!fired && written >= threshold)
                {
                    fired = true;
                    await FireEventAsync(localPeer, kind, relay, upstreamPeer).ConfigureAwait(false);
                    if (kind == AbortKind.ClientRst) return;
                }

                var count = (int)Math.Min(buffer.Length, transferBytes - written);
                await localPeer.SendAsync(buffer.AsMemory(0, count), SocketFlags.None).ConfigureAwait(false);
                written += count;
            }

            localPeer.Shutdown(SocketShutdown.Send);
        }
        catch (Exception exception) when (exception is SocketException or ObjectDisposedException or IOException)
        {
            // The adversarial events intentionally kill the client leg mid-send; classification
            // is receiver-side only.
        }
    }

    private static async Task FireEventAsync(Socket localPeer, AbortKind kind, TcpProxyRelay relay, Socket upstreamPeer)
    {
        switch (kind)
        {
            case AbortKind.ClientRst:
                localPeer.LingerState = new LingerOption(true, 0);
                localPeer.Close();
                break;
            case AbortKind.RelayCancel:
                await relay.DisposeAsync().ConfigureAwait(false);
                break;
            case AbortKind.UpstreamTruncate:
                // Both (not just Send) so the receiver's read side observes the FIN-before-
                // completion this scenario exists to count; a Send-only shutdown would leave the
                // receiver blocked until the forced-teardown timeout mislabels the transfer.
                upstreamPeer.Shutdown(SocketShutdown.Both);
                break;
        }
    }

    private static async Task<(long Received, TransferOutcome Outcome)> ReceiveAsync(Socket upstreamPeer, long expected)
    {
        var buffer = new byte[ChunkBytes];
        long received = 0;
        while (true)
        {
            int count;
            try
            {
                count = await upstreamPeer.ReceiveAsync(buffer, SocketFlags.None).ConfigureAwait(false);
            }
            catch (SocketException exception)
            {
                if (received == expected) return (received, TransferOutcome.Completed);
                return exception.SocketErrorCode is SocketError.ConnectionReset or SocketError.ConnectionAborted
                    ? (received, TransferOutcome.Reset)
                    : (received, TransferOutcome.Other);
            }
            catch (ObjectDisposedException)
            {
                return (received, received == expected ? TransferOutcome.Completed : TransferOutcome.Other);
            }

            if (count == 0)
            {
                try
                {
                    upstreamPeer.Shutdown(SocketShutdown.Send);
                }
                catch (Exception exception) when (exception is SocketException or ObjectDisposedException)
                {
                    // The relay side may already be gone when the final EOF lands.
                }

                return (received, received == expected ? TransferOutcome.Completed : TransferOutcome.UnexpectedEof);
            }

            received += count;
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

    private sealed class UpstreamOwner(Stream upstream) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            upstream.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private enum TransferOutcome
    {
        Completed,
        UnexpectedEof,
        Reset,
        Other,
    }

    private sealed class TransferCounters
    {
        private readonly Lock _gate = new();
        private readonly List<string> _errorSamples = [];
        private long _transfers;
        private long _completed;
        private long _unexpectedEof;
        private long _resets;
        private long _otherErrors;
        private long _bytesCompleted;
        private double _totalMilliseconds;

        public void Record(TransferOutcome outcome, long received, double milliseconds, string? sample)
        {
            lock (_gate)
            {
                _transfers++;
                _totalMilliseconds += milliseconds;
                switch (outcome)
                {
                    case TransferOutcome.Completed:
                        _completed++;
                        _bytesCompleted += received;
                        break;
                    case TransferOutcome.UnexpectedEof:
                        _unexpectedEof++;
                        break;
                    case TransferOutcome.Reset:
                        _resets++;
                        break;
                    default:
                        _otherErrors++;
                        if (sample is not null && _errorSamples.Count < MaximumErrorSamples && !_errorSamples.Contains(sample))
                        {
                            _errorSamples.Add(sample);
                        }

                        break;
                }
            }
        }

        public object Snapshot()
        {
            lock (_gate)
            {
                return new
                {
                    transfers = _transfers,
                    completed = _completed,
                    unexpectedEof = _unexpectedEof,
                    resets = _resets,
                    otherErrors = _otherErrors,
                    bytesCompleted = _bytesCompleted,
                    unexpectedEofRate = _transfers == 0 ? 0.0 : _unexpectedEof / (double)_transfers,
                    meanTransferMilliseconds = _transfers == 0 ? 0.0 : _totalMilliseconds / _transfers,
                    errorSamples = _errorSamples.ToArray(),
                };
            }
        }
    }
}

#pragma warning restore CA1416
