using System.Net;
using System.Net.Sockets;
using WinForward.E2E.Cli;
using WinForward.E2E.Client;
using WinForward.E2E.Contracts.Json;
using WinForward.E2E.Target;
using Xunit;

namespace WinForward.E2E.Tests;

/// <summary>
/// A real <see cref="ArmContext"/> for a fact that runs one arm end to end: the verb's own option
/// parser, a sink on a temporary file and a loopback target, so the fact asserts what the arm
/// published rather than a re-derivation of it.
/// </summary>
/// <remarks>
/// The ports are handed in rather than probed: an arm reads them from the options it was started with,
/// so a fact that wants a target with nothing behind it picks the port and binds the black hole itself.
/// </remarks>
internal sealed class ArmRunFixture : IAsyncDisposable
{
    /// <summary>The ports this process has handed out, so two facts never probe the same number.</summary>
    private static readonly HashSet<int> s_handedOut = [];

    private readonly string _directory;
    private readonly CancellationTokenSource _cancellation = new();

    private ArmRunFixture(string directory, ClientOptions options, JsonlSink sink)
    {
        _directory = directory;
        Options = options;
        Sink = sink;
    }

    private ClientOptions Options { get; }

    private JsonlSink Sink { get; }

    internal static ArmRunFixture Create(int tcpPort, int udpPort, int dnsPort)
    {
        var directory = Path.Combine(Path.GetTempPath(), "wf-e3b1", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var options = Parse(tcpPort, udpPort, dnsPort, directory);

        // No envelope: these facts read the arm's own outcome value, and the record's envelope belongs
        // to the run writer the record shape facts already cover.
        return new ArmRunFixture(directory, options, new JsonlSink(Path.Combine(directory, "arm.jsonl"), JsonlPolicy.Propagate, envelope: null));
    }

    internal ArmContext ContextFor(ArmSpec spec) => new()
    {
        Spec = spec,
        Options = Options,
        TargetAddress = IPAddress.Loopback,
        Sink = Sink,
        Latency = new LatencySet(),
        CancellationToken = _cancellation.Token,
    };

    /// <summary>
    /// A datagram socket nothing reads: the kernel accepts the sends and no reply is ever produced, so
    /// an arm that offers more than its in-flight window can be driven into its own refusal path.
    /// </summary>
    internal static Socket BindBlackHoleUdp(int port) =>
        Sockets.BindUdp(new IPEndPoint(IPAddress.Loopback, port));

    /// <summary>
    /// A listener that accepts a connection, waits for the client's command frame to arrive, and then
    /// resets the connection: the sends that follow the reset throw, which is the one way the throughput
    /// arm loses a frame of its own. Reading before the reset is what keeps the failure inside the arm's
    /// send loop — a reset that landed before the command frame would end the stream in the connect
    /// handshake — and it is an event rather than a delay, so a loaded host cannot skip it.
    /// </summary>
    internal static Socket BindResettingTcp(int port)
    {
        var listener = Sockets.BindTcpListener(new IPEndPoint(IPAddress.Loopback, port));
        listener.Listen(8);
        _ = Task.Run(async () =>
        {
            var probe = new byte[16];
            while (true)
            {
                Socket accepted;
                try
                {
                    accepted = await listener.AcceptAsync().ConfigureAwait(false);
                    _ = await accepted.ReceiveAsync(probe, SocketFlags.None).ConfigureAwait(false);
                }
                catch (SocketException)
                {
                    return;
                }
                catch (ObjectDisposedException)
                {
                    return;
                }

                // Linger zero turns the close into an RST instead of a FIN, so the peer's sends fail
                // rather than queueing behind a connection that only half closed.
                accepted.LingerState = new LingerOption(enable: true, seconds: 0);
                accepted.Dispose();
            }
        });

        return listener;
    }

    /// <summary>
    /// A port nothing holds right now, and none this process has handed out before: a probe releases
    /// the number it picked, so two facts running in parallel can otherwise be given the same one and
    /// the second listener fails to bind (measured: one shape fact lost its udp port to another class's
    /// probe). A port stays spoken for even if its caller never binds it, which costs a number and
    /// removes the race.
    /// </summary>
    internal static int FreePort(SocketType socketType, ProtocolType protocolType)
    {
        for (var attempt = 0; attempt < 64; attempt++)
        {
            using var probe = new Socket(AddressFamily.InterNetwork, socketType, protocolType);
            probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            var port = ((IPEndPoint)probe.LocalEndPoint!).Port;
            lock (s_handedOut)
            {
                if (s_handedOut.Add(port))
                {
                    return port;
                }
            }
        }

        throw new InvalidOperationException("no port was free that this process has not already handed out");
    }

    /// <summary>
    /// A port free for both transports, which a DNS responder needs: it binds a datagram socket and a
    /// stream listener on the same number, so a port free for one of them is not enough.
    /// </summary>
    internal static int FreeDualPort()
    {
        for (var attempt = 0; attempt < 64; attempt++)
        {
            var port = FreePort(SocketType.Dgram, ProtocolType.Udp);
            using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                listener.Bind(new IPEndPoint(IPAddress.Loopback, port));
                return port;
            }
            catch (SocketException)
            {
                /* another process holds the stream half of this number; the next probe picks another */
            }
        }

        throw new InvalidOperationException("no port was free for both a datagram socket and a stream listener");
    }

    /// <summary>
    /// One datagram through a real receive loop: a valid frame is echoed back, which is what a fact
    /// driving a listener needs to know it served.
    /// </summary>
    internal static async Task EchoOneDatagramAsync(int port, CancellationToken cancellationToken)
    {
        const int payloadBytes = 32;
        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        await client.ConnectAsync(new IPEndPoint(IPAddress.Loopback, port), cancellationToken).ConfigureAwait(false);

        var frame = new FrameBuffer(payloadBytes);
        var length = frame.Build(connectionId: 0x5348_0001u, sequence: 1, sendTicks: 0);
        var sent = frame.Memory[..length].ToArray();
        await client.SendAsync(sent, SocketFlags.None, cancellationToken).ConfigureAwait(false);

        var echoed = new byte[sent.Length];
        var received = await client.ReceiveAsync(echoed, SocketFlags.None, cancellationToken).ConfigureAwait(false);
        Assert.Equal(sent.Length, received);
        Assert.Equal(sent, echoed);
    }

    public async ValueTask DisposeAsync()
    {
        await _cancellation.CancelAsync().ConfigureAwait(false);
        await Sink.DisposeAsync().ConfigureAwait(false);
        _cancellation.Dispose();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            /* the fact's own verdict is what matters; a temp directory left behind is not a failure */
        }
    }

    private static ClientOptions Parse(int tcpPort, int udpPort, int dnsPort, string directory)
    {
        var arguments = new[]
        {
            "--target", "127.0.0.1",
            "--out", directory,
            "--label", "arm-run",
            "--tcp-port", tcpPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--udp-port", udpPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--dns-port", dnsPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };

        Assert.True(ClientOptions.TryCreate(arguments, out var options, out var error), error);
        return options;
    }
}
