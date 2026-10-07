using System.Net;
using System.Net.Sockets;
using WinForward.E2E.Target;
using Xunit;

namespace WinForward.E2E.Tests;

/// <summary>
/// The bind policy the target's three servers share. The property under test is not "the options are
/// set" but "a port a live instance still holds fails the second bind": that is what turns a leftover
/// target from a silent port thief (it binds, receives a fraction of the traffic and writes a ledger
/// that reads as a total product failure) into a startup error.
/// </summary>
public sealed class SocketsTests
{
    // The kernel's own numbers, read back by the test rather than by the production constant, so the
    // assertion cannot pass by agreeing with a value the test also defines: SOL_SOCKET = 1,
    // SO_REUSEADDR = 2 and SO_REUSEPORT = 15 on Linux.
    private const int SocketLevel = 1;
    private const int ReuseAddressOption = 2;
    private const int ReusePortOption = 15;

    [Fact]
    public void ADatagramSocketIsBoundWithNoReuseOptionAtAll()
    {
        // SO_REUSEADDR alone is enough for a second datagram socket to bind the same port on Linux, so
        // the datagram half of the policy is the absence of the option, not the clearing below.
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var socket = Sockets.BindUdp(new IPEndPoint(IPAddress.Loopback, FreePort(SocketType.Dgram, ProtocolType.Udp)));

        Assert.Equal(0, ReadRaw(socket, ReuseAddressOption));
        Assert.Equal(0, ReadRaw(socket, ReusePortOption));
    }

    [Fact]
    public void AListenerKeepsReuseAddressAndClearsReusePort()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var listener = Sockets.BindTcpListener(new IPEndPoint(IPAddress.Loopback, FreePort(SocketType.Stream, ProtocolType.Tcp)));

        // ReuseAddress stays because a restart has to rebind a port whose earlier connections are in
        // TIME_WAIT; SO_REUSEPORT goes because it is what lets a second listener share a live port.
        Assert.Equal(1, ReadRaw(listener, ReuseAddressOption));
        Assert.Equal(0, ReadRaw(listener, ReusePortOption));
    }

    [Fact]
    public void ASecondDatagramBindOfTheSamePortIsRefusedAndNamesTheEndpoint()
    {
        var port = FreePort(SocketType.Dgram, ProtocolType.Udp);
        using var first = Sockets.BindUdp(new IPEndPoint(IPAddress.Loopback, port));

        var exception = Assert.Throws<SocketException>(
            () => Sockets.BindUdp(new IPEndPoint(IPAddress.Loopback, port)));

        Assert.Equal(SocketError.AddressAlreadyInUse, exception.SocketErrorCode);
        Assert.Contains($"cannot bind 127.0.0.1:{port}", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ASecondListenerOnTheSamePortIsRefused()
    {
        // The loud second listener is Linux's behaviour only: on Windows SO_REUSEADDR means "take the
        // port over", and the target role itself is only ever deployed on Linux.
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var port = FreePort(SocketType.Stream, ProtocolType.Tcp);
        using var first = Sockets.BindTcpListener(new IPEndPoint(IPAddress.Loopback, port));

        var exception = Assert.Throws<SocketException>(
            () => Sockets.BindTcpListener(new IPEndPoint(IPAddress.Loopback, port)));

        Assert.Equal(SocketError.AddressAlreadyInUse, exception.SocketErrorCode);
        Assert.Contains($"cannot bind 127.0.0.1:{port}", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSourceTemplateIsAWildcardOfTheListenersFamily()
    {
        Assert.Equal(
            new IPEndPoint(IPAddress.Any, 0),
            Sockets.SourceTemplate(new IPEndPoint(IPAddress.Loopback, 31010)));
        Assert.Equal(
            new IPEndPoint(IPAddress.IPv6Any, 0),
            Sockets.SourceTemplate(new IPEndPoint(IPAddress.IPv6Loopback, 31010)));
    }

    private static int ReadRaw(Socket socket, int optionName)
    {
        Span<byte> value = stackalloc byte[sizeof(int)];
        socket.GetRawSocketOption(SocketLevel, optionName, value);
        return BitConverter.ToInt32(value);
    }

    /// <summary>
    /// A port nothing holds right now: the probe is bound to port 0 so the kernel picks one, and it is
    /// released before the caller binds it for real.
    /// </summary>
    private static int FreePort(SocketType socketType, ProtocolType protocolType)
    {
        using var probe = new Socket(AddressFamily.InterNetwork, socketType, protocolType);
        probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)probe.LocalEndPoint!).Port;
    }
}
