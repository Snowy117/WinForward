using System.Net;
using System.Net.Sockets;
using WinForward.E2E.Client;
using WinForward.E2E.Client.Arms;
using WinForward.E2E.Target;
using WinForward.E2E.Wire;
using Xunit;

namespace WinForward.E2E.Tests;

/// <summary>
/// The reliability attempt's side of the truncation contract: a peer that stops writing inside a frame
/// leaves a stream the attempt cannot read a boundary from, so the attempt is a protocol error rather
/// than the clean end of stream the mode asked it to complete. Both halves are pinned here -- what the
/// attempt publishes, and that the status is named in the reader's switch instead of falling through
/// the arm that absorbs anything unclassified.
/// </summary>
public sealed class ReliabilityTruncationTests
{
    private const uint ConnectionId = 0x5452_0002u;

    private const int ExpectedBytes = 4096;

    [Fact]
    public async Task AnAttemptWhoseStreamEndsInsideAFrameIsAProtocolErrorAndNotACleanEof()
    {
        var port = ArmRunFixture.FreePort(SocketType.Stream, ProtocolType.Tcp);
        using var listener = Sockets.BindTcpListener(new IPEndPoint(IPAddress.Loopback, port));
        listener.Listen(1);

        await using var fixture = ArmRunFixture.Create(
            port,
            ArmRunFixture.FreePort(SocketType.Dgram, ProtocolType.Udp),
            ArmRunFixture.FreePort(SocketType.Dgram, ProtocolType.Udp));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var peer = ServeOneAttemptAsync(listener, cancellation.Token);
        var attempt = await ReliabilityExchange.RunAttemptAsync(
            fixture.ContextFor(new ArmSpec { Name = "REL", Kind = "reliability" }),
            TcpMode.Clean,
            ExpectedBytes,
            ConnectionId,
            intendedTicks: 0,
            cancellation.Token);
        await peer;

        Assert.Equal(ReliabilityOutcome.OtherError, attempt.Observed);
        Assert.True(attempt.ProtocolError, "the truncated frame was not booked as a protocol error");
        Assert.False(attempt.Eof, "a stream cut inside a frame is not the clean end of stream the mode asks for");
        Assert.Equal(0, attempt.Echoed);
    }

    [Fact]
    public void TheAttemptNamesTheTruncatedStatusRatherThanLettingTheDefaultAbsorbIt()
    {
        var source = File.ReadAllText(Path.Combine(
            RepoPaths.Root, "benchmarks", "WinForward.E2E", "Client", "Arms", "ReliabilityExchange.cs"));

        Assert.Contains(
            $"case {nameof(FrameReadStatus)}.{nameof(FrameReadStatus.Truncated)}:",
            source,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// One attempt's peer: reads the command frame, answers with the first ten bytes of a frame and
    /// closes its send side, then keeps reading. Draining matters -- a close that leaves unread bytes
    /// behind resets the connection, which would turn the truncation into a socket error before the
    /// attempt ever read it.
    /// </summary>
    private static async Task ServeOneAttemptAsync(Socket listener, CancellationToken cancellationToken)
    {
        using var peer = await listener.AcceptAsync(cancellationToken).ConfigureAwait(false);
        var command = new byte[FrameCodec.HeaderSize + TcpCommand.PayloadLength + FrameCodec.TrailerSize];
        await ReadExactlyAsync(peer, command, cancellationToken).ConfigureAwait(false);

        await peer.SendAsync(command.AsMemory(0, 10), SocketFlags.None, cancellationToken).ConfigureAwait(false);
        peer.Shutdown(SocketShutdown.Send);

        var scratch = new byte[ReliabilityArm.FramePayloadBytes];
        while (await peer.ReceiveAsync(scratch, SocketFlags.None, cancellationToken).ConfigureAwait(false) > 0)
        {
            /* the attempt's own frames, read so the close above does not reset the connection */
        }
    }

    private static async Task ReadExactlyAsync(Socket socket, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var received = await socket.ReceiveAsync(buffer[read..], SocketFlags.None, cancellationToken).ConfigureAwait(false);
            Assert.NotEqual(0, received);
            read += received;
        }
    }
}
