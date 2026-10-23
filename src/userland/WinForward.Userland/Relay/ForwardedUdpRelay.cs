using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using WinForward.Userland.Configuration;
using WinForward.Userland.Device;

namespace WinForward.Userland.Relay;

/// <summary>
/// forwarded UDP 的 listener 与关联中继。每个 (client, server, upstream_id) 只建一条 SOCKS5 UDP
/// ASSOCIATE；同一个关联内的后续 datagram 复用控制连接与数据 socket。失败一律丢包，不回退直连。
/// </summary>
public sealed class ForwardedUdpRelay : IAsyncDisposable
{
    private const int MaxAssociations = 1024;
    private const int QueueCapacity = 64;
    private const int MaximumDropsToReport = 20;

    private static readonly TimeSpan AssociationIdleTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan EstablishTimeout = TimeSpan.FromSeconds(10);

    private readonly IReadOnlyList<Socket> _listeners;
    private readonly IReadOnlyDictionary<uint, ForwardedUpstream> _upstreams;
    private readonly IReadOnlyList<IRedirectContextSource> _sources;
    private readonly int _listenerPort;
    private readonly TextWriter _report;
    private readonly object _reportGate = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<AssociationKey, Lazy<AssociationSlot>> _slots = new();
    private readonly Task[] _receiveLoops;
    private readonly Task _sweepLoop;

    private long _datagramsIn;
    private long _datagramsRelayed;
    private long _datagramsBack;
    private long _associationsCreated;
    private long _associationFailures;
    private long _drops;
    private int _dropReports;

    private ForwardedUdpRelay(Socket[] listeners,
                              WfpDeviceClient device,
                              IReadOnlyList<ForwardedUpstream> upstreams,
                              int listenerPort,
                              TextWriter report)
    {
        _listeners = listeners;
        _upstreams = upstreams.ToDictionary(upstream => upstream.Id);
        _sources = new IRedirectContextSource[]
        {
            new ForwardedRedirectContextSource(device),
            new HostRedirectContextSource(),
        };
        _listenerPort = listenerPort;
        _report = report;
        _receiveLoops = listeners.Select(socket => Task.Run(() => ReceiveLoopAsync(socket, _stop.Token))).ToArray();
        _sweepLoop = Task.Run(() => SweepLoopAsync(_stop.Token));
    }

    public static ForwardedUdpRelay Start(IReadOnlyList<Socket> listeners,
                                          WfpDeviceClient device,
                                          IReadOnlyList<ForwardedUpstream> upstreams,
                                          int listenerPort,
                                          TextWriter report)
    {
        ArgumentNullException.ThrowIfNull(listeners);
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(upstreams);
        ArgumentNullException.ThrowIfNull(report);
        if (listeners.Count == 0)
        {
            throw new ArgumentException("UDP listener 不能为空", nameof(listeners));
        }

        if (listenerPort is <= 0 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(listenerPort), listenerPort, "listener 端口越界");
        }

        ForwardedUdpRelay relay = new(listeners.ToArray(), device, upstreams, listenerPort, report);
        relay.Report($"udp-relay start port={listenerPort} listeners={listeners.Count} " +
                     $"upstreams={upstreams.Count}");
        return relay;
    }

    public long DatagramsIn => Interlocked.Read(ref _datagramsIn);

    public long DatagramsRelayed => Interlocked.Read(ref _datagramsRelayed);

    public long DatagramsBack => Interlocked.Read(ref _datagramsBack);

    public long AssociationsCreated => Interlocked.Read(ref _associationsCreated);

    public long AssociationFailures => Interlocked.Read(ref _associationFailures);

    public long Drops => Interlocked.Read(ref _drops);

    private async Task ReceiveLoopAsync(Socket listener, CancellationToken token)
    {
        byte[] buffer = new byte[65535];
        EndPoint remote = listener.AddressFamily == AddressFamily.InterNetwork
                              ? new IPEndPoint(IPAddress.Any, 0)
                              : new IPEndPoint(IPAddress.IPv6Any, 0);

        while (!token.IsCancellationRequested)
        {
            SocketReceiveFromResult result;
            try
            {
                result = await listener.ReceiveFromAsync(buffer, SocketFlags.None, remote, token);
            }
            catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException)
            {
                return;
            }
            catch (SocketException exception)
            {
                if (token.IsCancellationRequested)
                {
                    return;
                }

                Report($"udp-relay receive-error {exception.SocketErrorCode}: {exception.Message}");
                continue;
            }

            Interlocked.Increment(ref _datagramsIn);
            if (result.RemoteEndPoint is not IPEndPoint client || result.ReceivedBytes <= 0)
            {
                Drop("bad-client-endpoint");
                continue;
            }

            if (!TryResolve(client, out RedirectDestination? destination, out string error) || destination is null)
            {
                Drop($"no-redirect {error}");
                continue;
            }

            if (destination.UpstreamId is not uint upstreamId)
            {
                Drop("missing-upstream-id");
                continue;
            }

            if (!_upstreams.TryGetValue(upstreamId, out ForwardedUpstream? upstream))
            {
                Interlocked.Increment(ref _associationFailures);
                Drop($"upstream-missing id={upstreamId}");
                continue;
            }

            AssociationKey key = new(client.Address, client.Port, destination.EndPoint.Address,
                                     destination.EndPoint.Port, upstreamId);
            if (_slots.Count >= MaxAssociations && !_slots.ContainsKey(key))
            {
                Drop($"association-capacity active={_slots.Count}");
                continue;
            }

            Lazy<AssociationSlot> lazy = _slots.GetOrAdd(
                key,
                _ => new Lazy<AssociationSlot>(
                    () => CreateSlot(key, client, destination.EndPoint, upstream, listener, token),
                    LazyThreadSafetyMode.ExecutionAndPublication));
            AssociationSlot slot = lazy.Value;
            slot.Touch();
            if (!slot.Queue.Writer.TryWrite(buffer.AsSpan(0, result.ReceivedBytes).ToArray()))
            {
                Drop($"queue-full client={client}");
            }
        }
    }

    private AssociationSlot CreateSlot(AssociationKey key,
                                       IPEndPoint client,
                                       IPEndPoint server,
                                       ForwardedUpstream upstream,
                                       Socket listener,
                                       CancellationToken token)
    {
        AssociationSlot slot = new(key, client, server, upstream, listener, token);
        Interlocked.Increment(ref _associationsCreated);
        Report($"udp-relay association client={client} server={server} upstream_id={upstream.Id} via={upstream.Endpoint}");
        slot.Worker = Task.Run(() => RunSlotAsync(slot));
        return slot;
    }

    private async Task RunSlotAsync(AssociationSlot slot)
    {
        CancellationToken token = slot.Token;
        try
        {
            using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(EstablishTimeout);
            Socks5UdpAssociation association =
                await Socks5UdpAssociation.EstablishAsync(slot.Upstream.Endpoint, EstablishTimeout, deadline.Token);
            slot.Association = association;
            slot.Touch();
            Report($"udp-relay associated client={slot.Client} relay={association.RelayEndPoint} " +
                   $"upstream_id={slot.Upstream.Id}");

            Task send = SendLoopAsync(slot, association, token);
            Task receive = ReceiveLoopAsync(slot, association, token);
            await Task.WhenAny(send, receive).ConfigureAwait(false);
            await slot.CancelAsync().ConfigureAwait(false);
            await association.DisposeAsync().ConfigureAwait(false);
            await Task.WhenAll(send, receive).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 空闲回收或进程退出。
        }
        catch (Exception exception) when (exception is SocketException or Socks5Exception or IOException
                                       or InvalidOperationException or ObjectDisposedException)
        {
            Interlocked.Increment(ref _associationFailures);
            Report($"udp-relay association-failed client={slot.Client} server={slot.Server} " +
                   $"{exception.GetType().Name}: {exception.Message}");
        }
        finally
        {
            slot.Queue.Writer.TryComplete();
            slot.DisposeAssociation();
            if (_slots.TryGetValue(slot.Key, out Lazy<AssociationSlot>? current) &&
                ReferenceEquals(current.Value, slot))
            {
                _slots.TryRemove(slot.Key, out _);
            }
    }
}

    private async Task SendLoopAsync(AssociationSlot slot, Socks5UdpAssociation association, CancellationToken token)
    {
        await foreach (byte[] payload in slot.Queue.Reader.ReadAllAsync(token).ConfigureAwait(false))
        {
            try
            {
                await association.SendAsync(slot.Server, payload, token).ConfigureAwait(false);
                slot.Touch();
                Interlocked.Increment(ref _datagramsRelayed);
            }
            catch (Exception exception) when (exception is SocketException or ObjectDisposedException
                                           or OperationCanceledException)
            {
                if (token.IsCancellationRequested)
                {
                    return;
                }

                Interlocked.Increment(ref _associationFailures);
                Report($"udp-relay send-failed client={slot.Client} server={slot.Server} " +
                       $"{exception.GetType().Name}: {exception.Message}");
                await slot.CancelAsync().ConfigureAwait(false);
                return;
            }
        }
    }

    private async Task ReceiveLoopAsync(AssociationSlot slot, Socks5UdpAssociation association, CancellationToken token)
    {
        byte[] buffer = new byte[65535];
        while (!token.IsCancellationRequested)
        {
            int received;
            try
            {
                received = await association.ReceiveAsync(buffer, token).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException)
            {
                return;
            }
            catch (SocketException exception)
            {
                if (token.IsCancellationRequested)
                {
                    return;
                }

                Interlocked.Increment(ref _associationFailures);
                Report($"udp-relay receive-upstream-failed client={slot.Client} {exception.SocketErrorCode}: {exception.Message}");
                await slot.CancelAsync().ConfigureAwait(false);
                return;
            }

            if (!Socks5UdpCodec.TryDecode(buffer.AsSpan(0, received), out IPEndPoint? source, out int payloadOffset,
                                          out string error))
            {
                Drop($"decode {error}");
                continue;
            }

            if (source is null || !source.Equals(slot.Server))
            {
                Drop($"response-source {source} != {slot.Server}");
                continue;
            }

            int payloadLength = received - payloadOffset;
            if (payloadLength <= 0)
            {
                continue;
            }

            try
            {
                await slot.Listener.SendToAsync(buffer.AsMemory(payloadOffset, payloadLength), SocketFlags.None,
                                                slot.Client, token).ConfigureAwait(false);
                slot.Touch();
                Interlocked.Increment(ref _datagramsBack);
            }
            catch (Exception exception) when (exception is SocketException or ObjectDisposedException)
            {
                if (token.IsCancellationRequested)
                {
                    return;
                }

                Interlocked.Increment(ref _associationFailures);
                Report($"udp-relay send-client-failed client={slot.Client} {exception.GetType().Name}: {exception.Message}");
                await slot.CancelAsync().ConfigureAwait(false);
                return;
            }
        }
    }

    private bool TryResolve(IPEndPoint client, out RedirectDestination? destination, out string error)
    {
        destination = null;
        error = string.Empty;
        foreach (IRedirectContextSource source in _sources)
        {
            if (source.TryReadUdp(client, _listenerPort, out destination, out string readError))
            {
                return true;
            }

            error = $"{source.Name}: {readError}";
            if (source.Name == "forwarded")
            {
                // forwarded 未命中时继续尝试 host；本仓库没有 host UDP listener，最终仍会丢包。
                continue;
            }
        }

        destination = null;
        error = "no-source";
        return false;
    }

    private async Task SweepLoopAsync(CancellationToken token)
    {
        using PeriodicTimer timer = new(SweepInterval);
        while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
        {
            foreach (KeyValuePair<AssociationKey, Lazy<AssociationSlot>> pair in _slots)
            {
                AssociationSlot slot = pair.Value.Value;
                if (slot.IdleFor <= AssociationIdleTimeout)
                {
                    continue;
                }

                if (_slots.TryGetValue(pair.Key, out Lazy<AssociationSlot>? current) &&
                    ReferenceEquals(current, pair.Value) && _slots.TryRemove(pair.Key, out _))
                {
                    Report($"udp-relay idle-close client={slot.Client} server={slot.Server} " +
                           $"idle={slot.IdleFor.TotalSeconds:0.###}s");
                    await slot.CancelAsync().ConfigureAwait(false);
                }
            }
        }
    }

    private void Drop(string reason)
    {
        Interlocked.Increment(ref _drops);
        int reported = Interlocked.Increment(ref _dropReports);
        if (reported <= MaximumDropsToReport)
        {
            Report($"udp-relay drop reason={reason}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        foreach (KeyValuePair<AssociationKey, Lazy<AssociationSlot>> pair in _slots)
        {
            await pair.Value.Value.CancelAsync().ConfigureAwait(false);
        }

        try
        {
            await Task.WhenAll(_receiveLoops.Append(_sweepLoop)).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException)
        {
            // 关闭路径。
        }

        Task[] workers = _slots.Values
                              .Select(pair => pair.Value.Worker)
                              .Where(task => task is not null)
                              .Select(task => task!)
                              .ToArray();
        if (workers.Length > 0)
        {
            try
            {
                await Task.WhenAll(workers).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is TimeoutException or OperationCanceledException
                                           or ObjectDisposedException)
            {
                // 关闭路径：残留 worker 不能再阻塞控制器退出。
            }
        }

        foreach (KeyValuePair<AssociationKey, Lazy<AssociationSlot>> pair in _slots)
        {
            pair.Value.Value.DisposeAssociation();
        }

        _slots.Clear();
        Report($"udp-relay stop in={DatagramsIn} relayed={DatagramsRelayed} back={DatagramsBack} " +
               $"associations={AssociationsCreated} failures={AssociationFailures} drops={Drops}");
        _stop.Dispose();
    }

    private void Report(string line)
    {
        lock (_reportGate)
        {
            _report.WriteLine(line);
            _report.Flush();
        }
    }

    private readonly record struct AssociationKey(IPAddress ClientAddress,
                                                 int ClientPort,
                                                 IPAddress ServerAddress,
                                                 int ServerPort,
                                                 uint UpstreamId);

    private sealed class AssociationSlot
    {
        private readonly CancellationTokenSource _cts;
        private Socks5UdpAssociation? _association;

        public AssociationSlot(AssociationKey key,
                               IPEndPoint client,
                               IPEndPoint server,
                               ForwardedUpstream upstream,
                               Socket listener,
                               CancellationToken token)
        {
            Key = key;
            Client = client;
            Server = server;
            Upstream = upstream;
            Listener = listener;
            _cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            Queue = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(QueueCapacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false,
            });
            Touch();
        }

        public AssociationKey Key { get; }

        public IPEndPoint Client { get; }

        public IPEndPoint Server { get; }

        public ForwardedUpstream Upstream { get; }

        public Socket Listener { get; }

        public Channel<byte[]> Queue { get; }

        public CancellationToken Token => _cts.Token;

        public Task? Worker { get; set; }

        public Socks5UdpAssociation? Association
        {
            get => Volatile.Read(ref _association);
            set => Volatile.Write(ref _association, value);
        }

        private long _lastActivity;

        public TimeSpan IdleFor => Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastActivity));

        public void Touch() => Interlocked.Exchange(ref _lastActivity, Stopwatch.GetTimestamp());

        public Task CancelAsync()
        {
            if (!_cts.IsCancellationRequested)
            {
                _cts.Cancel();
            }

            return Task.CompletedTask;
        }

        public void DisposeAssociation()
        {
            Socks5UdpAssociation? association = Interlocked.Exchange(ref _association, null);
            association?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }
}
