using System.Net;
using System.Net.Sockets;
using WinForward.Configuration;
using WinForward.Runtime.Socks5;
using Xunit;
using static WinForward.Core.Tests.AsyncTestExtensions;
using static WinForward.Core.Tests.Socks5TestServer;

namespace WinForward.Core.Tests;

public sealed class Socks5ControlConnectionQuiescenceTests
{
    [Fact]
    public async Task DisposeRefusesLateReadersBeforeTheyTouchTheAttemptDeadline()
    {
        // Regression this catches: without an admission gate, a late reader evaluates
        // `AttemptToken` on the already-disposed per-attempt CTS and surfaces an
        // ObjectDisposedException naming "System.Threading.CancellationTokenSource". The refusal
        // must come from the connection's own admission check, before any deadline read.
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var server = AcceptGreetingAsync(listener, CancellationToken.None);
        var endpoint = (IPEndPoint)listener.LocalEndpoint;
        var socksServer = new Socks5Server("test", endpoint.Address.ToString(), checked((ushort)endpoint.Port), Username: null, Password: null);
        var control = await Socks5ControlConnection.ConnectAsync(socksServer, CancellationToken.None);

        await control.DisposeAsync();

        var refusal = await Record.ExceptionAsync(async () => await control.UdpAssociateAsync(CancellationToken.None));
        var disposed = Assert.IsType<ObjectDisposedException>(refusal);
        Assert.EndsWith(nameof(Socks5ControlConnection), disposed.ObjectName, StringComparison.Ordinal);
        await server;
    }

    [Fact]
    public async Task DisposeJoinsAnInFlightAttemptOperation()
    {
        // Regression this catches: a disposal that releases the per-attempt deadline and closes the
        // stream while an admitted operation is still parked on the reply read. The scope lease
        // makes DisposeAsync join the operation (the stream teardown faults its read), and the
        // operation must never observe the disposed deadline.
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var stopServer = new CancellationTokenSource();
        var commandRead = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = StallAfterUdpAssociateRequestAsync(listener, commandRead, stopServer.Token);
        var endpoint = (IPEndPoint)listener.LocalEndpoint;
        var socksServer = new Socks5Server("test", endpoint.Address.ToString(), checked((ushort)endpoint.Port), Username: null, Password: null);
        var control = await Socks5ControlConnection.ConnectAsync(socksServer, CancellationToken.None);

        var associate = control.UdpAssociateAsync(CancellationToken.None);
        await commandRead.Task.WaitAsync(TimeSpan.FromSeconds(30));

        await AsTask(control.DisposeAsync()).WaitAsync(TimeSpan.FromSeconds(30));

        // The drain joined the admitted operation before DisposeAsync returned: its parked read was
        // faulted by the stream teardown and its lease released, so the task is already settled.
        Assert.True(associate.IsCompleted, "DisposeAsync must not return while an admitted operation is still outstanding");

        var fault = await Record.ExceptionAsync(async () => await associate);
        if (fault is ObjectDisposedException disposed)
        {
            Assert.DoesNotContain("CancellationTokenSource", disposed.ObjectName, StringComparison.Ordinal);
        }

        await stopServer.CancelAsync();
        await IgnoreExpectedCancellationAsync(server);
    }

    [Fact]
    public async Task SecondDisposeJoinsInsteadOfThrowing()
    {
        // Regression this catches: an unguarded DisposeAsync that tears down a second time (and
        // would surface an ObjectDisposedException from the already-disposed deadline).
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var server = AcceptGreetingAsync(listener, CancellationToken.None);
        var endpoint = (IPEndPoint)listener.LocalEndpoint;
        var socksServer = new Socks5Server("test", endpoint.Address.ToString(), checked((ushort)endpoint.Port), Username: null, Password: null);
        var control = await Socks5ControlConnection.ConnectAsync(socksServer, CancellationToken.None);

        await AsTask(control.DisposeAsync()).WaitAsync(TimeSpan.FromSeconds(30));
        await AsTask(control.DisposeAsync()).WaitAsync(TimeSpan.FromSeconds(30));

        await server;
    }

    private static Task AsTask(ValueTask value) => value.AsTask();
}
