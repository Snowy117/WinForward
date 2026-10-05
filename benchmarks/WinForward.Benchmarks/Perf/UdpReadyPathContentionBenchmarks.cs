using System.Globalization;
using BenchmarkDotNet.Attributes;
using WinForward.Configuration;
using WinForward.Core;
using WinForward.Protocols;
using WinForward.Runtime;
using WinForward.Runtime.UdpProxy;
namespace WinForward.Benchmarks.Perf;

/// <summary>
/// The UDP ready path's cost and its contention curve (research F2.4): one coordinator gate, one
/// session lookup, one transport send — the locks a *ready* datagram pays before any socket is
/// touched. <see cref="Workers"/> pre-established flows are driven in parallel on dedicated threads
/// released from a barrier, each sending its own flow's datagrams, so a per-datagram cost that grows
/// with <see cref="Workers"/> is contention on those gates and a flat cost is clean scaling.
/// <para>
/// The existing UDP scenarios cannot answer this: <c>udp.lossRate</c> and <c>udp.rawBaseline</c> both
/// run at the ~25k pps loopback ceiling, where the per-datagram lock cost is invisible against the
/// socket work. This row removes the sockets (a counting fake transport) and leaves the gates.
/// </para>
/// <para>
/// In-process only, and deliberately so: the measurement is the coordinator's bookkeeping, not the
/// SOCKS5 transport's send gate, which the soak scenarios cover on the wire.
/// </para>
/// </summary>
[MemoryDiagnoser]
public class UdpReadyPathContentionBenchmarks
{
    /// <summary>Datagrams per benchmark invocation, split across the workers; a constant so BDN can normalise per-datagram cost across worker counts.</summary>
    private const int TotalSends = 4_096;

    private static readonly byte[] s_payload = new byte[512];

    [Params(1, 2, 4)]
    public int Workers { get; set; }

    private BenchmarkUdpTransportFactory _factory = null!;
    private UdpProxyCoordinator _coordinator = null!;
    private NativeBufferPool _setupQueuePool = null!;
    private NativeBufferPool _receiveWindowPool = null!;
    private SetupExecutor _setupExecutor = null!;
    private FlowKey[] _flows = null!;
    private Socks5Server _server = null!;
    private Barrier _barrier = null!;
    private Thread[] _threads = null!;
    private volatile bool _stop;

    [GlobalSetup]
    public void Setup()
    {
        const int maximumFrameSize = UdpFrameBuilder.DefaultMaximumEthernetFrame;
        _factory = new BenchmarkUdpTransportFactory();
        _setupQueuePool = new NativeBufferPool(maximumFrameSize);
        _receiveWindowPool = new NativeBufferPool(UdpProxyCoordinator.ReceiveWindowSize(maximumFrameSize));
        _setupExecutor = new SetupExecutor();
        _coordinator = new UdpProxyCoordinator(
            _factory,
            NoopUdpResponseSink.Instance,
            _setupQueuePool,
            _receiveWindowPool,
            _setupExecutor,
            new UdpProxyOptions { Capacity = Workers });
        _server = new Socks5Server("benchmark", "127.0.0.1", 1080, Username: null, Password: null);
        _flows = new FlowKey[Workers];
        for (var index = 0; index < Workers; index++) _flows[index] = BenchmarkShared.CreateFlowKey(index);
        EstablishReadySessions();

        // One barrier per worker plus the benchmark thread: the benchmark releases the work, waits for
        // it to finish, and reports the round. Threads are created once so thread-start cost never
        // lands inside a measured invocation.
        _barrier = new Barrier(Workers + 1);
        _threads = new Thread[Workers];
        for (var index = 0; index < Workers; index++)
        {
            var worker = index;
            _threads[worker] = new Thread(() => RunWorkerLoop(worker))
            {
                IsBackground = true,
                Name = string.Create(CultureInfo.InvariantCulture, $"udp-ready-{worker}"),
            };
            _threads[worker].Start();
        }
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _stop = true;
        _barrier.SignalAndWait();
        foreach (var thread in _threads) thread.Join();
        _barrier.Dispose();
        _setupQueuePool.Dispose();
        _receiveWindowPool.Dispose();
        _setupExecutor.Dispose();
    }

    /// <summary>
    /// One measured round: release the workers, wait for every datagram of the round. Per-datagram cost
    /// is <c>time / TotalSends</c>, so a value that grows with <see cref="Workers"/> is the gate
    /// contention F2.4 describes.
    /// </summary>
    [Benchmark(OperationsPerInvoke = TotalSends)]
    public void ReadySend()
    {
        _barrier.SignalAndWait();
        _barrier.SignalAndWait();
    }

    private void RunWorkerLoop(int worker)
    {
        var perWorker = TotalSends / Workers;
        var flow = _flows[worker];
        while (true)
        {
            _barrier.SignalAndWait();
            if (_stop) return;
            for (var index = 0; index < perWorker; index++) SendOne(flow);

            _barrier.SignalAndWait();
        }
    }

    /// <summary>
    /// One ready-path datagram. The steady shape completes synchronously (the coordinator's send entry
    /// is deliberately non-async), so the result is read directly; the genuinely-pending branch blocks
    /// the calling worker the same way the capture pump blocks on a pending handler.
    /// </summary>
    private void SendOne(FlowKey flow)
    {
        var pending = _coordinator.TrySendSpanAsync(flow, ProxyTarget.FromServer(_server), s_payload, default, CancellationToken.None);
        if (pending.IsCompletedSuccessfully)
        {
            // Steady state: the send completed inline, so consuming its result is a plain read.
#pragma warning disable VSTHRD002 // Mirrors NdisCapture.InvokeHandler: the completion check above makes this a plain read.
            pending.GetAwaiter().GetResult();
#pragma warning restore VSTHRD002
            return;
        }

        // The dedicated workers intentionally block until the still-pending send completes: no thread-pool
        // thread is parked, and the round cannot be measured until every datagram is in.
#pragma warning disable VSTHRD002 // Mirrors NdisCapture.InvokeHandler: dedicated thread, the measured shape.
        pending.AsTask().GetAwaiter().GetResult();
#pragma warning restore VSTHRD002
    }

    /// <summary>
    /// Sends one datagram per flow until the fake transport has been handed every one of them: a
    /// flushed datagram means that flow's session reached the ready state, which is the state under
    /// measurement. The setup window itself is a different shape (and is covered by the UDP soak rows).
    /// </summary>
    private void EstablishReadySessions()
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (_factory.Sends < Workers)
        {
            if (DateTime.UtcNow > deadline) throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture, $"Only {_factory.Sends} of {Workers} UDP sessions became ready."));
            foreach (var flow in _flows) SendOne(flow);

            Thread.Sleep(1);
        }
    }
}
