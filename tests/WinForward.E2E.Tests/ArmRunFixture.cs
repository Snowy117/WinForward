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

    /// <summary>A port nothing holds right now.</summary>
    internal static int FreePort(SocketType socketType, ProtocolType protocolType)
    {
        using var probe = new Socket(AddressFamily.InterNetwork, socketType, protocolType);
        probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)probe.LocalEndPoint!).Port;
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
