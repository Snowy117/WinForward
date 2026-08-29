using System.Buffers;
using System.Net.Sockets;
using WinForward.Configuration;
using WinForward.Runtime;

namespace WinForward.Core.Tests;

/// <summary>
/// Coordinator-lifecycle fakes: a factory whose first CreateAsync blocks until failed
/// (cancelled waiter), one that stalls until cancelled (disposal semantics), one that reuses a
/// colliding relay alias, a counting array pool, and a mutable time provider for expiry sweeps.
/// </summary>
internal sealed class GatedTransportFactory : IUdpProxyTransportFactory
{
    private readonly TaskCompletionSource<IUdpProxyTransport> _create = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _calls;
    public TaskCompletionSource<bool> CreateStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource<bool> CreateFinished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public List<FakeTransport> CreatedTransports { get; } = [];

    public async ValueTask<IUdpProxyTransport> CreateAsync(Socks5Server server, CancellationToken cancellationToken)
    {
        if (Interlocked.Increment(ref _calls) != 1)
        {
            var transport = new FakeTransport(AddressFamily.InterNetwork, 40001);
            CreatedTransports.Add(transport);
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
    private int _nextLocalPort = 41000;
    private int _calls;

    /// <summary>Counts CreateAsync attempts from method entry, so in-flight (gated) setups are visible.</summary>
    public int CreateCalls => Volatile.Read(ref _calls);
    public List<FakeTransport> Transports { get; } = [];

    public async ValueTask<IUdpProxyTransport> CreateAsync(Socks5Server server, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _calls);
        // The gate models a stalled SOCKS5 handshake, but coordinator shutdown must still be
        // able to interrupt the pending setup, so the wait honors the cancellation token.
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (minimumDelay is { } delay) await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        var transport = new FakeTransport(System.Net.Sockets.AddressFamily.InterNetwork, Interlocked.Increment(ref _nextLocalPort));
        lock (Transports) Transports.Add(transport);
        return transport;
    }
}

/// <summary>A factory whose every CreateAsync attempt fails, modeling an unreachable SOCKS5 server.</summary>
internal sealed class FailingTransportFactory : IUdpProxyTransportFactory
{
    private int _calls;
    public int CreateCalls => Volatile.Read(ref _calls);

    public ValueTask<IUdpProxyTransport> CreateAsync(Socks5Server server, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _calls);
        throw new IOException("SOCKS5 server is unreachable (synthetic).");
    }
}

internal sealed class CancellationAwareTransportFactory : IUdpProxyTransportFactory
{
    public TaskCompletionSource<bool> CreateStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async ValueTask<IUdpProxyTransport> CreateAsync(Socks5Server server, CancellationToken cancellationToken)
    {
        CreateStarted.TrySetResult(true);
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
        throw new InvalidOperationException("Cancellation must interrupt the pending setup.");
    }
}

internal sealed class CollidingAliasTransportFactory : IUdpProxyTransportFactory
{
    public List<FakeTransport> Transports { get; } = [];

    public ValueTask<IUdpProxyTransport> CreateAsync(Socks5Server server, CancellationToken cancellationToken)
    {
        var transport = new FakeTransport(AddressFamily.InterNetwork, 40000);
        lock (Transports) Transports.Add(transport);
        return ValueTask.FromResult<IUdpProxyTransport>(transport);
    }
}

internal sealed class TrackingArrayPool : ArrayPool<byte>
{
    public int LastMinimumLength { get; private set; }
    public int ReturnCount { get; private set; }

    public override byte[] Rent(int minimumLength)
    {
        LastMinimumLength = minimumLength;
        return new byte[minimumLength];
    }

    public override void Return(byte[] array, bool clearArray = false) => ReturnCount++;
}

internal sealed class MutableTimeProvider(DateTimeOffset initial) : TimeProvider
{
    private DateTimeOffset _now = initial;
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan duration) => _now = _now.Add(duration);
}
