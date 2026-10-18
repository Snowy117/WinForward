using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using WinForward.E2E.Cli;
using WinForward.E2E.Client;
using Xunit;

namespace WinForward.E2E.Tests;

/// <summary>
/// The client's SOCKS5 CONNECT: the no-auth handshake, the request wire shape, and the failure
/// vocabulary. The proxy is a loopback fake, so every assertion is about bytes the client actually
/// wrote and a socket it actually opened; no real proxy is involved.
/// </summary>
public sealed class Socks5ClientTests
{
    [Fact]
    public async Task AConnectThroughTheProxySucceedsWithNoAuthenticationAndLeavesTheStreamOpen()
    {
        await using var proxy = new FakeSocks5Proxy(async client =>
        {
            var greeting = await ReadExactlyAsync(client, 3);
            Assert.Equal("050100", Convert.ToHexString(greeting));
            await SendAsync(client, [5, 0]);
            var request = await ReadExactlyAsync(client, 10);
            Assert.Equal(1, request[1]);
            await SendAsync(client, [5, 0, 0, 1, 127, 0, 0, 1, 0, 80]);
            await SendAsync(client, "PONG"u8.ToArray());
        });

        using var socket = NewSocket();
        await Socks5Connector.ConnectAsync(socket, proxy.Proxy, new IPEndPoint(IPAddress.Loopback, 443), CancellationToken.None);

        var banner = await ReadExactlyAsync(socket, 4);
        Assert.Equal("PONG", Encoding.ASCII.GetString(banner));
    }

    [Fact]
    public async Task TheOnlyCommandTheClientSendsIsConnect()
    {
        var captured = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var proxy = new FakeSocks5Proxy(async client =>
        {
            await ReadExactlyAsync(client, 3);
            await SendAsync(client, [5, 0]);
            var request = await ReadExactlyAsync(client, 10);
            captured.SetResult(request);
            await SendAsync(client, [5, 0, 0, 1, 127, 0, 0, 1, 0, 80]);
        });

        var target = new IPEndPoint(IPAddress.Parse("192.0.2.10"), 40010);
        using var socket = NewSocket();
        await Socks5Connector.ConnectAsync(socket, proxy.Proxy, target, CancellationToken.None);

        var request = await captured.Task;
        // version 5, CONNECT, reserved 0, IPv4, 192.0.2.10, port 40010 in network order.
        Assert.Equal("05010001C000020A9C4A", Convert.ToHexString(request));
    }

    [Fact]
    public async Task AnIpv6TargetUsesTheIpv6AddressType()
    {
        var captured = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var proxy = new FakeSocks5Proxy(async client =>
        {
            await ReadExactlyAsync(client, 3);
            await SendAsync(client, [5, 0]);
            var header = await ReadExactlyAsync(client, 4);
            var address = header[3] == 4 ? await ReadExactlyAsync(client, 18) : [];
            captured.SetResult([.. header, .. address]);
            await SendAsync(client, [5, 0, 0, 1, 127, 0, 0, 1, 0, 80]);
        });

        var target = new IPEndPoint(IPAddress.IPv6Loopback, 8443);
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await Socks5Connector.ConnectAsync(socket, proxy.Proxy, target, CancellationToken.None);

        var request = await captured.Task;
        // version 5, CONNECT, reserved 0, IPv6, ::1 (16 bytes), port 8443 in network order.
        Assert.Equal("050100040000000000000000000000000000000120FB", Convert.ToHexString(request));
    }

    [Fact]
    public async Task AProxyThatOffersNoAcceptableAuthenticationMethodFailsTheConnection()
    {
        await using var proxy = new FakeSocks5Proxy(async client =>
        {
            await ReadExactlyAsync(client, 3);
            await SendAsync(client, [5, 0xFF]);
        });

        using var socket = NewSocket();
        var failure = await Assert.ThrowsAsync<Socks5ConnectException>(async () =>
            await Socks5Connector.ConnectAsync(socket, proxy.Proxy, new IPEndPoint(IPAddress.Loopback, 443), CancellationToken.None));

        Assert.Contains("no acceptable authentication method", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARefusedConnectFailsTheConnectionWithTheProxyReason()
    {
        await using var proxy = new FakeSocks5Proxy(async client =>
        {
            await ReadExactlyAsync(client, 3);
            await SendAsync(client, [5, 0]);
            await ReadExactlyAsync(client, 10);
            await SendAsync(client, [5, 5, 0, 1, 0, 0, 0, 0, 0, 0]);
        });

        using var socket = NewSocket();
        var failure = await Assert.ThrowsAsync<Socks5ConnectException>(async () =>
            await Socks5Connector.ConnectAsync(socket, proxy.Proxy, new IPEndPoint(IPAddress.Loopback, 443), CancellationToken.None));

        Assert.Contains("refused CONNECT", failure.Message, StringComparison.Ordinal);
        Assert.Contains("connection refused", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AProxyThatIsNotListeningFailsAndTheTargetIsNeverDialled()
    {
        using var target = new TcpListener(IPAddress.Loopback, 0);
        target.Start();
        var targetEndPoint = (IPEndPoint)target.LocalEndpoint;
        using var socket = NewSocket();
        var proxy = new Socks5Proxy("127.0.0.1", FreePort());

        var failure = await Assert.ThrowsAsync<Socks5ConnectException>(async () =>
            await Socks5Connector.ConnectAsync(socket, proxy, targetEndPoint, CancellationToken.None));

        Assert.Contains("connecting to the SOCKS5 proxy", failure.Message, StringComparison.Ordinal);
        Assert.False(target.Pending(), "the client dialled the target directly after the proxy failed");
    }

    [Fact]
    public void TcpOnlyPlansAreAcceptedAndUdpBearingOnesAreRefused()
    {
        Assert.True(Socks5Plan.TryValidate(
            [
                new ArmSpec { Name = "IDLE", Kind = "idle" },
                TcpLatency(),
                new ArmSpec { Name = "REL", Kind = "reliability" },
                new ArmSpec { Name = "THRU", Kind = "throughput" },
                new ArmSpec { Name = "PERSIST", Kind = "persistent" },
            ],
            out var error), error);
        Assert.Null(error);

        foreach (var arm in new[]
                 {
                     new ArmSpec { Name = "BASE", Kind = "base", Protocol = "tcp" },
                     new ArmSpec { Name = "LOSS", Kind = "loss" },
                     new ArmSpec { Name = "MIX", Kind = "mix" },
                     new ArmSpec { Name = "DNS", Kind = "dns" },
                     new ArmSpec { Name = "LAT", Kind = "latency", Protocol = "tcp+udp" },
                 })
        {
            Assert.False(Socks5Plan.TryValidate([arm], out var reason), arm.Kind);
            Assert.Contains("--socks5", reason, StringComparison.Ordinal);
            Assert.Contains(arm.Name, reason, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task TheClientRefusesAUdpPlanBeforeItCreatesTheOutputDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), $"wf-socks5-plan-{Guid.NewGuid():N}");
        var outDirectory = Path.Combine(root, "out");
        try
        {
            var options = new ClientOptions
            {
                TargetAddress = "127.0.0.1",
                PlanPath = Path.Combine(RepoPaths.PlansDirectory, "base-plan.json"),
                OutDirectory = outDirectory,
                Socks5 = new Socks5Proxy("127.0.0.1", 1080),
            };

            var status = await ClientRunner.RunAsync(options, CancellationToken.None);

            Assert.Equal(ExitCodes.UsageError, status);
            Assert.False(Directory.Exists(outDirectory), "the refused plan created its output directory");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task AProxyFailureFailsTheRunAndTheTargetIsNeverDialled()
    {
        var root = Path.Combine(Path.GetTempPath(), $"wf-socks5-run-{Guid.NewGuid():N}");
        var outDirectory = Path.Combine(root, "out");
        Directory.CreateDirectory(root);
        using var target = new TcpListener(IPAddress.Loopback, 0);
        target.Start();
        var targetPort = ((IPEndPoint)target.LocalEndpoint).Port;
        try
        {
            var plan = Path.Combine(root, "tcp-latency.json");
            await File.WriteAllTextAsync(
                plan,
                """{"arms":[{"kind":"latency","name":"LAT","seconds":1,"ratePerSecond":2,"payloadBytes":120,"protocol":"tcp","window":64}]}""",
                CancellationToken.None);

            Assert.True(ClientOptions.TryCreate(
                [
                    "--target", "127.0.0.1",
                    "--out", outDirectory,
                    "--plan", plan,
                    "--tcp-port", targetPort.ToString(CultureInfo.InvariantCulture),
                    "--socks5", $"127.0.0.1:{FreePort().ToString(CultureInfo.InvariantCulture)}",
                ],
                out var options,
                out var optionsError), optionsError);

            var status = await ClientRunner.RunAsync(options, CancellationToken.None);

            Assert.Equal(ExitCodes.RuntimeError, status);
            using var run = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(outDirectory, "run.json"), CancellationToken.None));
            Assert.True(run.RootElement.GetProperty("failed").GetBoolean());

            var error = await File.ReadAllLinesAsync(Path.Combine(outDirectory, "LAT.jsonl"), CancellationToken.None);
            Assert.Contains(error, line => line.Contains("Socks5ConnectException", StringComparison.Ordinal));
            Assert.Contains(error, line => line.Contains("SOCKS5 proxy", StringComparison.Ordinal));
            Assert.False(target.Pending(), "the client dialled the target directly after the proxy failed");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static ArmSpec TcpLatency() => new()
    {
        Name = "LAT",
        Kind = "latency",
        Protocol = "tcp",
        Seconds = 1,
        RatePerSecond = 2,
        PayloadBytes = 120,
        Window = 64,
    };

    private static Socket NewSocket() => new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

    private static int FreePort()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.LocalEndPoint!).Port;
    }

    private static async Task<byte[]> ReadExactlyAsync(Socket socket, int count)
    {
        var buffer = new byte[count];
        var received = 0;
        while (received < count)
        {
            var read = await socket.ReceiveAsync(buffer.AsMemory(received), SocketFlags.None).ConfigureAwait(false);
            Assert.NotEqual(0, read);
            received += read;
        }

        return buffer;
    }

    private static async Task SendAsync(Socket socket, byte[] payload) =>
        Assert.NotEqual(0, await socket.SendAsync(payload, SocketFlags.None).ConfigureAwait(false));

    private sealed class FakeSocks5Proxy : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cancellation = new();
        private readonly Task _accept;
        private readonly Func<Socket, Task> _handler;

        internal FakeSocks5Proxy(Func<Socket, Task> handler)
        {
            _handler = handler;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Proxy = new Socks5Proxy("127.0.0.1", ((IPEndPoint)_listener.LocalEndpoint).Port);
            _accept = AcceptAsync();
        }

        internal Socks5Proxy Proxy { get; }

        public async ValueTask DisposeAsync()
        {
            await _cancellation.CancelAsync().ConfigureAwait(false);
            _listener.Stop();
            try
            {
                await _accept.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            _cancellation.Dispose();
            _listener.Dispose();
        }

        private async Task AcceptAsync()
        {
            try
            {
                while (!_cancellation.IsCancellationRequested)
                {
                    using var client = await _listener.AcceptSocketAsync(_cancellation.Token).ConfigureAwait(false);
                    await _handler(client).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (SocketException) when (_cancellation.IsCancellationRequested)
            {
            }
        }
    }
}
