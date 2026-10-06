using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.CompilerServices;
using WinForward.Configuration;
using WinForward.Core;
using WinForward.Protocols;
using WinForward.TestSupport;
using Xunit;
using static WinForward.TestSupport.AsyncTestExtensions;

namespace WinForward.Runtime.Socks5.Tests;

/// <summary>
/// The UoT transport's packet-path discipline: the warm send is a non-async entry that hands the
/// whole frame to the kernel inline with no managed allocation, concurrent sends never interleave
/// frames on the stream, and disposal is a one-shot release of the connection that refuses later
/// senders through its own guard rather than through the disposed gate.
/// </summary>
public sealed class Socks5UotTransportSendTests
{
    private static readonly TimeSpan s_budget = TimeSpan.FromSeconds(30);

    [Fact]
    public void SendSpanAsyncWarmPathRunsNoAsyncStateMachine()
    {
        var method = typeof(Socks5UotTransport).GetMethod(
            nameof(Socks5UotTransport.SendSpanAsync),
            [typeof(Endpoint), typeof(ReadOnlySpan<byte>), typeof(CancellationToken)]);
        Assert.NotNull(method);
        Assert.Null(method.GetCustomAttribute<AsyncStateMachineAttribute>());
    }

    [Fact]
    public async Task WarmSyncSendAllocatesNoManagedBytes()
    {
        // Regression gate for the stream send path: the first send writes the CONNECT request, the
        // UoT request header, and the frame into the transport's own buffer, and a later send writes
        // the prefix and payload only. Both shapes must hand the frame to the kernel inline — the
        // encoding must not materialize a managed copy and the destination must not be re-serialized.
        await using var server = new ScriptedSocks5UotServer();
        var transport = await CreateTransportAsync(server);
        var destination = Endpoint.From(IPAddress.Parse("192.0.2.53"), 53);
        var payload = "QRS"u8.ToArray();
        try
        {
            for (var warm = 0; warm < 8; warm++)
            {
                var warmSend = transport.SendSpanAsync(destination, payload, CancellationToken.None);
                Assert.True(warmSend.IsCompletedSuccessfully);
                await warmSend;
            }

            var before = GC.GetAllocatedBytesForCurrentThread();
            const int count = 64;
            for (var index = 0; index < count; index++)
            {
                var send = transport.SendSpanAsync(destination, payload, CancellationToken.None);
                Assert.True(send.IsCompletedSuccessfully, "the warm send must complete synchronously on the calling thread");
                await send;
            }
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.Equal(0, allocated);
            await WaitForAsync(() => server.FrameCount == 8 + count);
        }
        finally
        {
            await transport.DisposeAsync();
        }
    }

    [Fact]
    public async Task ConcurrentSendsNeverInterleaveFrames()
    {
        // Two datagrams written concurrently would corrupt the two-byte frame prefix, so the send
        // gate must serialize whole frames. Eight genuinely overlapping senders (released together)
        // each write one distinguishable payload; the fixture reads frames back-to-back, so any
        // interleaving desynchronizes its parse and the frame count assertion can never be met.
        const int senders = 8;
        const int perSender = 16;
        await using var server = new ScriptedSocks5UotServer();
        await using var transport = await CreateTransportAsync(server);
        var destination = Endpoint.From(IPAddress.Parse("192.0.2.53"), 53);
        var payloads = new byte[senders][];
        for (var index = 0; index < senders; index++)
        {
            payloads[index] = new byte[16];
            Array.Fill(payloads[index], (byte)('A' + index));
        }

        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sendersRunning = StartSenders(transport, destination, payloads, perSender, release.Task);
        release.TrySetResult();
        await sendersRunning.WaitAsync(s_budget);
        await WaitForAsync(() => server.FrameCount == senders * perSender);

        var frames = server.Frames;
        Assert.Equal(senders * perSender, frames.Count);
        foreach (var payload in payloads)
        {
            Assert.Equal(perSender, frames.Count(frame => frame.AsSpan().SequenceEqual(payload)));
        }

        Assert.False(server.ProtocolViolation.Task.IsCompleted);
    }

    [Fact]
    public async Task DisposalReleasesTheConnectionExactlyOnce()
    {
        await using var server = new ScriptedSocks5UotServer();
        var transport = await CreateTransportAsync(server);
        Assert.Equal(1, server.LiveConnectionCount);

        await transport.DisposeAsync();
        await transport.DisposeAsync();
        await transport.DisposeAsync();

        await WaitForAsync(() => server.LiveConnectionCount == 0);
        Assert.Equal(1, server.ConnectionCount);
    }

    [Fact]
    public async Task SendAfterDisposalIsRefusedByTheDisposalGuardNotTheDisposedGate()
    {
        // Regression this catches: the send gate is disposed last and used to be the only barrier,
        // so a sender arriving after disposal entered `_sendGate.WaitAsync` and surfaced the
        // semaphore's own ObjectDisposedException. The refusal must come from the transport's
        // disposal guard, before the gate is touched.
        await using var server = new ScriptedSocks5UotServer();
        var transport = await CreateTransportAsync(server);
        var destination = Endpoint.From(IPAddress.Parse("192.0.2.53"), 53);
        var payload = "ABC"u8.ToArray();

        await transport.DisposeAsync();

        var refusal = await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
            await transport.SendSpanAsync(destination, payload, CancellationToken.None));
        Assert.EndsWith(nameof(Socks5UotTransport), refusal.ObjectName, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReceiveAfterDisposalIsRefusedByTheConnectionRatherThanHanging()
    {
        await using var server = new ScriptedSocks5UotServer();
        var transport = await CreateTransportAsync(server);

        await transport.DisposeAsync();

        await Assert.ThrowsAnyAsync<ObjectDisposedException>(async () =>
            await transport.ReceiveAsync(new byte[64], CancellationToken.None));
    }

    [Fact]
    public async Task SendsRacingDisposalNeverObserveTheDisposedGate()
    {
        // Regression this catches: a sender that has entered SendSpanAsync but not yet reached the
        // gate must be refused by the disposal guard; it must never get the disposed semaphore's
        // ObjectDisposedException. Only the deliberate transport-level refusal is allowed.
        await using var server = new ScriptedSocks5UotServer();
        var transport = await CreateTransportAsync(server);
        var destination = Endpoint.From(IPAddress.Parse("192.0.2.53"), 53);
        var payload = "XYZ"u8.ToArray();

        var senders = new Task[4];
        for (var index = 0; index < senders.Length; index++)
        {
            senders[index] = Task.Run(async () =>
            {
                for (var attempt = 0; attempt < 50; attempt++)
                {
                    try
                    {
                        await transport.SendSpanAsync(destination, payload, CancellationToken.None);
                    }
                    catch (ObjectDisposedException exception)
                    {
                        Assert.DoesNotContain("SemaphoreSlim", exception.ObjectName, StringComparison.Ordinal);
                    }
                    catch (Exception exception) when (exception is IOException or SocketException)
                    {
                        // Disposing the connection faults an in-flight or late frame write; that is
                        // not the gate race this test pins.
                        GC.KeepAlive(exception);
                    }
                }
            });
        }

        await transport.DisposeAsync();
        await Task.WhenAll(senders).WaitAsync(s_budget);
    }

    /// <summary>
    /// One sender per payload, all released together on <paramref name="release"/> so the sends
    /// genuinely overlap instead of queueing behind each other's start.
    /// </summary>
    private static Task StartSenders(Socks5UotTransport transport, Endpoint destination, byte[][] payloads, int perSender, Task release)
    {
        var senders = new Task[payloads.Length];
        for (var index = 0; index < payloads.Length; index++)
        {
            var sender = index;
            senders[index] = Task.Run(async () =>
            {
                await release;
                for (var send = 0; send < perSender; send++)
                {
                    await transport.SendSpanAsync(destination, payloads[sender], CancellationToken.None);
                }
            });
        }

        return Task.WhenAll(senders);
    }

    private static async ValueTask<Socks5UotTransport> CreateTransportAsync(ScriptedSocks5UotServer server)
    {
        var factory = new Socks5UdpTransportFactory(new SelfTrafficRegistry(), UdpFrameBuilder.DefaultMaximumEthernetFrame);
        return (Socks5UotTransport)await factory.CreateAsync(ProxyTarget.FromServer(server.Server), CancellationToken.None);
    }
}
