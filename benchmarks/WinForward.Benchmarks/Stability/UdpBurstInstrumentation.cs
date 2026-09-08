using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using WinForward.Configuration;
using WinForward.Core;
using WinForward.Runtime.Socks5;
using WinForward.Runtime.UdpProxy;

namespace WinForward.Benchmarks.Stability;

/// <summary>The measurement windows a background datagram can be attributed to, in phase order.</summary>
internal enum BackgroundWindow
{
    Control = 0,
    Burst = 1,
    Post = 2,
}

/// <summary>A per-datagram in-flight stamp: the window it was sent in and the send timestamp.</summary>
[StructLayout(LayoutKind.Auto)]
internal readonly record struct InFlightStamp(byte Window, long SendTicks);

/// <summary>Everything the three measurement windows produced, plus the sender holding their counters.</summary>
internal sealed record PhaseOutcome(BurstResult Burst, BackgroundSender Sender, long[] WindowTicks);

/// <summary>Per-burst outcome: admission counts, first-response distribution, and issue-loop cost.</summary>
internal sealed record BurstResult(
    long BurstAccepted,
    long BurstRejected,
    long FirstResponses,
    double EstablishmentLossRate,
    LatencyDistribution FirstResponseMs,
    double TimeToIssueMs);

/// <summary>
/// The bounded in-flight stamp table shared by the background sender and the response
/// sink: the sender stamps every outgoing datagram, the sink matches responses by
/// (flow, sequence) and removes the stamp. At capacity a new stamp is refused and its
/// response degrades to the unattributed counter — the send loop is never blocked.
/// </summary>
internal sealed class InFlightTracker(int capacity)
{
    private readonly ConcurrentDictionary<long, InFlightStamp> _stamps = new();
    private readonly int _capacity = capacity;
    private long _count;
    private volatile bool _active;

    /// <summary>Warmup datagrams and their responses predate attribution and stay untracked.</summary>
    public void BeginAttribution() => _active = true;

    public bool AttributionActive => _active;

    public void Stamp(int flowId, long sequence, BackgroundWindow window, long sendTicks)
    {
        if (!_active) return;
        if (Interlocked.Increment(ref _count) > _capacity)
        {
            Interlocked.Decrement(ref _count);
            return;
        }

        _stamps[Key(flowId, sequence)] = new InFlightStamp((byte)window, sendTicks);
    }

    public void Remove(int flowId, long sequence)
    {
        if (!_active) return;
        if (_stamps.TryRemove(Key(flowId, sequence), out _)) Interlocked.Decrement(ref _count);
    }

    public bool TryTake(int flowId, long sequence, out InFlightStamp stamp)
    {
        if (_stamps.TryRemove(Key(flowId, sequence), out stamp))
        {
            Interlocked.Decrement(ref _count);
            return true;
        }

        return false;
    }

    private static long Key(int flowId, long sequence) => ((long)flowId << 32) | (uint)sequence;
}

/// <summary>
/// The paced background sender round-robin over the pre-established flows. It runs
/// through the control, burst, and post windows; per-window sent counts and
/// send-accept latencies are written only by this sender's loop and read only after
/// its task completes.
/// </summary>
internal sealed class BackgroundSender(
    UdpProxyCoordinator coordinator,
    Socks5Server socksServer,
    FlowKey[] flows,
    int payloadBytes,
    int pps,
    InFlightTracker tracker)
{
    private const int TickMilliseconds = 10;

    // Sequences continue after the warmup datagram (sequence 1 per flow), keeping the
    // in-flight (flow, sequence) keys globally unique across the whole scenario.
    private readonly long[] _sequences = CreateSequencesAfterWarmup(flows.Length);
    private readonly byte[] _payload = new byte[payloadBytes];
    private readonly List<double>[] _sendLatencies = [new(), new(), new()];
    private readonly long[] _sentPerWindow = new long[3];
    private volatile int _currentWindow = (int)BackgroundWindow.Control;

    private static long[] CreateSequencesAfterWarmup(int flowCount)
    {
        var sequences = new long[flowCount];
        for (var flow = 0; flow < flowCount; flow++) sequences[flow] = 1;
        return sequences;
    }

    public void EnterWindow(BackgroundWindow window) => _currentWindow = (int)window;

    public long SentIn(BackgroundWindow window) => Volatile.Read(ref _sentPerWindow[(int)window]);

    public IReadOnlyList<double> SendLatenciesIn(BackgroundWindow window) => _sendLatencies[(int)window];

    public async Task RunLoopAsync(CancellationToken cancellation)
    {
        var perTick = pps * TickMilliseconds / 1000.0;
        var accumulator = 0.0;
        var tick = TimeSpan.FromMilliseconds(TickMilliseconds);
        var stopwatch = Stopwatch.StartNew();
        var nextDeadline = stopwatch.Elapsed;
        var nextFlow = 0;
        try
        {
            while (true)
            {
                accumulator += perTick;
                var datagrams = (int)accumulator;
                accumulator -= datagrams;
                for (var index = 0; index < datagrams; index++)
                {
                    var flow = nextFlow++ % flows.Length;
                    _sequences[flow]++;
                    var window = _currentWindow;
                    var sendTicks = Stopwatch.GetTimestamp();
                    DatagramHeader.Write(_payload, _sequences[flow], flow);
                    tracker.Stamp(flow, _sequences[flow], (BackgroundWindow)window, sendTicks);
                    if (await coordinator.TrySendAsync(flows[flow], socksServer, _payload, cancellation).ConfigureAwait(false))
                    {
                        _sentPerWindow[window]++;
                        _sendLatencies[window].Add(StabilityShared.TicksToMilliseconds(Stopwatch.GetTimestamp() - sendTicks));
                    }
                    else
                    {
                        tracker.Remove(flow, _sequences[flow]);
                    }
                }

                nextDeadline += tick;
                var remaining = nextDeadline - stopwatch.Elapsed;
                if (remaining > TimeSpan.Zero)
                {
                    await Task.Delay(remaining, cancellation).ConfigureAwait(false);
                }
                else
                {
                    nextDeadline = stopwatch.Elapsed;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The window sequence completed; the drain phase collects straggler responses.
        }
    }
}

/// <summary>
/// The response sink carrying both measurements: burst flows' first responses are
/// CAS-recorded per flow against a zero-initialized timestamp array; background
/// responses are matched against the in-flight stamps for window attribution. Anything
/// it cannot attribute (overflow-degraded stamps, unexpected flow ids) lands in the
/// unattributed counter instead of vanishing silently.
/// </summary>
internal sealed class BurstCountingSink(int backgroundFlows, int burstFlows, InFlightTracker tracker) : IUdpResponseSink
{
    private readonly long[] _burstFirstResponseTicks = new long[burstFlows];
    private readonly long[] _injectedPerWindow = new long[3];
    private long _burstFirstResponses;
    private long _warmupResponses;
    private long _unattributed;

    public long BurstFirstResponses => Interlocked.Read(ref _burstFirstResponses);

    /// <summary>Warmup datagrams whose response has already returned; the warmup wait polls this.</summary>
    public long WarmupResponses => Interlocked.Read(ref _warmupResponses);

    public long UnattributedResponses => Interlocked.Read(ref _unattributed);

    public long InjectedIn(BackgroundWindow window) => Volatile.Read(ref _injectedPerWindow[(int)window]);

    /// <summary>The burst flow's first-response timestamp, or null when no response was ever observed.</summary>
    public long? TryGetBurstFirstResponseTicks(int burstIndex)
    {
        var ticks = Volatile.Read(ref _burstFirstResponseTicks[burstIndex]);
        return ticks == 0 ? null : ticks;
    }

    public ValueTask InjectAsync(FlowKey originalFlow, Endpoint remoteSource, ReadOnlyMemory<byte> payload, byte[]? clientMac, CancellationToken cancellationToken)
    {
        if (!DatagramHeader.TryRead(payload.Span, out var sequence, out var flowId))
        {
            Interlocked.Increment(ref _unattributed);
        }
        else if (flowId >= backgroundFlows && flowId < backgroundFlows + burstFlows)
        {
            if (Interlocked.CompareExchange(ref _burstFirstResponseTicks[flowId - backgroundFlows], Stopwatch.GetTimestamp(), 0) == 0)
            {
                Interlocked.Increment(ref _burstFirstResponses);
            }
        }
        else if ((uint)flowId < (uint)backgroundFlows)
        {
            if (!tracker.AttributionActive)
            {
                Interlocked.Increment(ref _warmupResponses);
            }
            else if (tracker.TryTake(flowId, sequence, out var stamp))
            {
                Interlocked.Increment(ref _injectedPerWindow[stamp.Window]);
            }
            else
            {
                // A stamp degraded by the in-flight bound or a lost entry — a real miss.
                Interlocked.Increment(ref _unattributed);
            }
        }
        else
        {
            Interlocked.Increment(ref _unattributed);
        }

        return ValueTask.CompletedTask;
    }
}
