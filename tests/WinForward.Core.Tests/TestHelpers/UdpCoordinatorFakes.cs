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
    public ValueTask<IUdpProxyTransport> CreateAsync(Socks5Server server, CancellationToken cancellationToken) =>
        ValueTask.FromResult<IUdpProxyTransport>(new FakeTransport(AddressFamily.InterNetwork, 40000));
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
