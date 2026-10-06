using System.Net.Sockets;
using WinForward.Configuration;
using WinForward.Core;
using WinForward.Runtime;
using WinForward.Runtime.UdpProxy;

namespace WinForward.TestSupport;

/// <summary>
/// Builds a UDP coordinator whose borrowed pools and setup executor default to the process-wide
/// <see cref="TestPools"/> instances; tests that assert pool accounting pass their own.
/// </summary>
internal static class UdpCoordinatorFakes
{
    internal static UdpProxyCoordinator CreateCoordinator(
        IUdpProxyTransportFactory transportFactory,
        IUdpResponseSink responseSink,
        UdpProxyOptions? options = null,
        NativeBufferPool? setupQueuePool = null,
        NativeBufferPool? receiveWindowPool = null,
        ISetupExecutor? setupExecutor = null)
        => new(
            transportFactory,
            responseSink,
            setupQueuePool ?? TestPools.UdpSetupQueuePool,
            receiveWindowPool ?? TestPools.UdpReceiveWindowPool,
            setupExecutor ?? TestPools.SetupExecutor,
            options);
}

/// <summary>
/// Coordinator-lifecycle fakes: a factory whose first CreateAsync blocks until failed
/// (cancelled waiter), one that stalls until cancelled (disposal semantics), one that reuses a
/// colliding relay alias, a counting array pool, and a mutable time provider for expiry sweeps.
/// </summary>
internal sealed class GatedTransportFactory : IUdpProxyTransportFactory
{
    private readonly TaskCompletionSource<IUdpProxyTransport> _create = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Lock _gate = new();
    private readonly List<FakeTransport> _created = [];
    private int _calls;
    public TaskCompletionSource<bool> CreateStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource<bool> CreateFinished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Snapshot taken under the writer's lock: the setup worker publishes from its own thread, and <see cref="List{T}.Add"/> makes a count visible before its element.</summary>
    public IReadOnlyList<FakeTransport> CreatedTransports
    {
        get
        {
            lock (_gate) return [.. _created];
        }
    }

    public async ValueTask<IUdpProxyTransport> CreateAsync(ProxyTarget target, CancellationToken cancellationToken)
    {
        if (Interlocked.Increment(ref _calls) != 1)
        {
            var transport = new FakeTransport(AddressFamily.InterNetwork, 40001);
            lock (_gate) _created.Add(transport);
            return transport;
        }

        CreateStarted.TrySetResult(true);
        try
        {
            return await _create.Task.ConfigureAwait(false);
        }
        finally
        {
            CreateFinished.TrySetResult(true);
        }
    }

    public void Fail(Exception exception) => _create.TrySetException(exception);
}

/// <summary>
/// A factory whose CreateAsync stalls until a gate task completes (optionally at least a minimum
/// delay, modeling a slow SOCKS5 UDP ASSOCIATE), then returns transports with distinct local
/// ports so the relay alias collision guard never rejects distinct flows.
/// </summary>
internal sealed class DelayedTransportFactory(Task gate, TimeSpan? minimumDelay = null) : IUdpProxyTransportFactory
{
    private readonly Lock _gate = new();
    private readonly List<FakeTransport> _transports = [];
    private int _nextLocalPort = 41000;
    private int _calls;

    /// <summary>Counts CreateAsync attempts from method entry, so in-flight (gated) setups are visible.</summary>
    public int CreateCalls => Volatile.Read(ref _calls);

    /// <summary>Snapshot taken under the writer's lock: the setup worker publishes from its own thread, and <see cref="List{T}.Add"/> makes a count visible before its element.</summary>
    public IReadOnlyList<FakeTransport> Transports
    {
        get
        {
            lock (_gate) return [.. _transports];
        }
    }

    public async ValueTask<IUdpProxyTransport> CreateAsync(ProxyTarget target, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _calls);
        // The gate models a stalled SOCKS5 handshake, but coordinator shutdown must still be
        // able to interrupt the pending setup, so the wait honors the cancellation token.
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (minimumDelay is { } delay) await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        var transport = new FakeTransport(AddressFamily.InterNetwork, Interlocked.Increment(ref _nextLocalPort));
        lock (_gate) _transports.Add(transport);
        return transport;
    }
}

/// <summary>A factory whose every CreateAsync attempt fails, modeling an unreachable SOCKS5 server.</summary>
internal sealed class FailingTransportFactory : IUdpProxyTransportFactory
{
    private int _calls;
    public int CreateCalls => Volatile.Read(ref _calls);

    public ValueTask<IUdpProxyTransport> CreateAsync(ProxyTarget target, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _calls);
        throw new IOException("SOCKS5 server is unreachable (synthetic).");
    }
}

internal sealed class CancellationAwareTransportFactory : IUdpProxyTransportFactory
{
    public TaskCompletionSource<bool> CreateStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async ValueTask<IUdpProxyTransport> CreateAsync(ProxyTarget target, CancellationToken cancellationToken)
    {
        CreateStarted.TrySetResult(true);
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
        throw new InvalidOperationException("Cancellation must interrupt the pending setup.");
    }
}

internal sealed class CollidingAliasTransportFactory : IUdpProxyTransportFactory
{
    private readonly Lock _gate = new();
    private readonly List<FakeTransport> _transports = [];

    /// <summary>Snapshot taken under the writer's lock: the setup worker publishes from its own thread, and <see cref="List{T}.Add"/> makes a count visible before its element.</summary>
    public IReadOnlyList<FakeTransport> Transports
    {
        get
        {
            lock (_gate) return [.. _transports];
        }
    }

    public ValueTask<IUdpProxyTransport> CreateAsync(ProxyTarget target, CancellationToken cancellationToken)
    {
        var transport = new FakeTransport(AddressFamily.InterNetwork, 40000);
        lock (_gate) _transports.Add(transport);
        return ValueTask.FromResult<IUdpProxyTransport>(transport);
    }
}

internal sealed class MutableTimeProvider(DateTimeOffset initial) : TimeProvider
{
    private DateTimeOffset _now = initial;
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan duration) => _now = _now.Add(duration);
}

/// <summary>
/// Counts clock reads and can be armed to throw on the next one, so a warm path that still reads the
/// clock fails its fact instead of quietly passing on a quantised value.
/// </summary>
internal sealed class CountingTimeProvider(DateTimeOffset initial) : TimeProvider
{
    private readonly MutableTimeProvider _inner = new(initial);
    private int _reads;

    public int Reads => Volatile.Read(ref _reads);

    public bool ThrowOnRead { get; set; }

    public override DateTimeOffset GetUtcNow()
    {
        Interlocked.Increment(ref _reads);
        return ThrowOnRead
            ? throw new InvalidOperationException("the driven path read the activity clock")
            : _inner.GetUtcNow();
    }
}
