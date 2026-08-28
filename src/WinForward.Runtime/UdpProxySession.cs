using System.Buffers;
using System.Net;
using WinForward.Configuration;
using WinForward.Core;

namespace WinForward.Runtime;

internal sealed class UdpProxySession : IAsyncDisposable
{
    private readonly FlowKey _flow;
    private readonly long _flowGeneration;
    private readonly UdpAssociation _association;
    private readonly IUdpProxyTransport _transport;
    private readonly IUdpResponseSink _sink;
    private readonly CancellationToken _shutdown;
    private readonly TimeProvider _timeProvider;
    private readonly Action<UdpAssociation, DateTimeOffset> _activityObserver;
    private readonly IRuntimeLogger _logger;
    private readonly ArrayPool<byte> _receiveBufferPool;
    private readonly int _receiveBufferSize;
    private readonly Lock _activityGate = new();
    private readonly Lock _disposeGate = new();
    private Task? _receiveLoop;
    private Task? _disposeTask;
    private Exception? _receiveFailure;
    private long _lastActivityTicks;
    private bool _expiring;
    private int _activeSends;

    public UdpProxySession(
        FlowKey flow,
        long flowGeneration,
        UdpAssociation association,
        IUdpProxyTransport transport,
        IUdpResponseSink sink,
        byte[]? clientMac,
        CancellationToken shutdown,
        TimeProvider timeProvider,
        Action<UdpAssociation, DateTimeOffset> activityObserver,
        IRuntimeLogger logger,
        ArrayPool<byte> receiveBufferPool,
        int receiveBufferSize)
    {
        _flow = flow;
        _flowGeneration = flowGeneration;
        _association = association;
        _transport = transport;
        _sink = sink;
        ClientMac = clientMac;
        _shutdown = shutdown;
        _timeProvider = timeProvider;
        _activityObserver = activityObserver;
        _logger = logger;
        _receiveBufferPool = receiveBufferPool;
        _receiveBufferSize = receiveBufferSize;
        _lastActivityTicks = timeProvider.GetUtcNow().UtcTicks;
    }

    public FlowKey Flow => _flow;
    public long FlowGeneration => _flowGeneration;
    public UdpAssociation Association => _association;
    public DateTimeOffset LastActivityUtc => new(Interlocked.Read(ref _lastActivityTicks), TimeSpan.Zero);

    /// <summary>
    /// The client's Ethernet source MAC recorded from the first datagram of the flow. Forwarded
    /// (VM-originated) flows use it as the destination MAC of rebuilt responses so the vSwitch
    /// delivers them to the client instead of the host stack.
    /// </summary>
    public byte[]? ClientMac { get; }

    public void Start(Func<UdpProxySession, Task> receiveFailureHandler, Task registered)
    {
        ArgumentNullException.ThrowIfNull(receiveFailureHandler);
        ArgumentNullException.ThrowIfNull(registered);
        var receiveLoop = ReceiveLoopAsync();
        _receiveLoop = receiveLoop;
        _ = ObserveReceiveLoopAsync(receiveLoop, receiveFailureHandler, registered);
    }

    public async ValueTask SendAsync(Endpoint destination, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        lock (_activityGate)
        {
            var failure = Volatile.Read(ref _receiveFailure);
            if (failure is not null) throw new IOException("SOCKS5 UDP relay session is no longer usable.", failure);
            if (_expiring) throw new IOException("SOCKS5 UDP relay session is expiring.");
            _activeSends++;
        }

        try
        {
            await _transport.SendAsync(new IPEndPoint(destination.Address, destination.Port), payload, cancellationToken).ConfigureAwait(false);
            TouchActivity();
        }
        finally
        {
            lock (_activityGate) _activeSends--;
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposeGate)
        {
            _disposeTask ??= DisposeCoreAsync();
            return new ValueTask(_disposeTask);
        }
    }

    public bool TryBeginExpiry(DateTimeOffset now, TimeSpan idleTimeout)
    {
        lock (_activityGate)
        {
            if (_expiring || _activeSends != 0 || now - LastActivityUtc < idleTimeout) return false;
            _expiring = true;
            return true;
        }
    }

    public void CancelExpiry()
    {
        lock (_activityGate) _expiring = false;
    }

    private async Task DisposeCoreAsync()
    {
        await _transport.DisposeAsync().ConfigureAwait(false);
        if (_receiveLoop is not null)
        {
            try { await _receiveLoop.ConfigureAwait(false); }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
            {
                // Cancellation is the expected shutdown path.
            }
            catch (ObjectDisposedException)
            {
                return;
            }
        }
    }

    private async Task ReceiveLoopAsync()
    {
        var buffer = _receiveBufferPool.Rent(_receiveBufferSize);
        try
        {
            while (!_shutdown.IsCancellationRequested)
            {
                var response = await _transport.ReceiveAsync(buffer.AsMemory(0, _receiveBufferSize), _shutdown).ConfigureAwait(false);
                TouchActivity();
                if (response.DestinationAddress is null) continue;
                var source = Endpoint.From(response.DestinationAddress, response.DestinationPort);
                await _sink.InjectAsync(_flow, source, response.Payload, ClientMac, _shutdown).ConfigureAwait(false);
                if (_logger.IsEnabled(RuntimeLogLevel.Trace))
                {
                    _logger.Event(RuntimeLogLevel.Trace, "udp.packet.received",
                        new("flow", _flowGeneration == 0 ? null : _flowGeneration),
                        new("udpAssociation", _association.Generation), new("source", source),
                        new("destination", _flow.Local), new("bytes", response.Payload.Length));
                }
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            // Normal shutdown path.
        }
        catch (ObjectDisposedException) when (_shutdown.IsCancellationRequested)
        {
            // Disposal closes the receive socket during shutdown.
        }
        catch (Exception exception)
        {
            Volatile.Write(ref _receiveFailure, exception);
        }
        finally
        {
            _receiveBufferPool.Return(buffer);
        }
    }

    private void TouchActivity()
    {
        var now = _timeProvider.GetUtcNow();
        lock (_activityGate)
        {
            if (_expiring) return;
            Interlocked.Exchange(ref _lastActivityTicks, now.UtcTicks);
        }
        _activityObserver(_association, now);
    }

    private async Task ObserveReceiveLoopAsync(Task receiveLoop, Func<UdpProxySession, Task> receiveFailureHandler, Task registered)
    {
        await registered.ConfigureAwait(false);
        await receiveLoop.ConfigureAwait(false);
        if (Volatile.Read(ref _receiveFailure) is not null)
        {
            await receiveFailureHandler(this).ConfigureAwait(false);
        }
    }
}
