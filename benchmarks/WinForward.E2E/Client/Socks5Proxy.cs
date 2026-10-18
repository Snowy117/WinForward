using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace WinForward.E2E.Client;

/// <summary>
/// The SOCKS5 endpoint every measured TCP connection is relayed through. Only a host and a port are
/// needed: the client performs a no-auth CONNECT, so there is no credential to configure and no
/// configuration file to parse.
/// </summary>
/// <remarks>
/// The host may be a name or an IP literal. An IPv6 literal has to be bracketed in the command-line
/// value (<c>[::1]:1080</c>), which is why <see cref="Display"/> brackets it again when a message quotes
/// the endpoint.
/// </remarks>
internal sealed record Socks5Proxy(string Host, int Port)
{
    /// <summary>The endpoint the socket dials; a host name is resolved by the socket.</summary>
    internal EndPoint EndPoint => new DnsEndPoint(Host, Port);

    /// <summary>
    /// The address family the proxy socket is created with. A literal host answers directly; a name is
    /// dialled over IPv4, the family the shipped loopback inbound listens on.
    /// </summary>
    internal AddressFamily AddressFamily =>
        IPAddress.TryParse(Host, out var address) ? address.AddressFamily : AddressFamily.InterNetwork;

    /// <summary>The endpoint as a command-line value names it, with an IPv6 host bracketed.</summary>
    internal string Display =>
        Host.Contains(':', StringComparison.Ordinal)
            ? string.Create(CultureInfo.InvariantCulture, $"[{Host}]:{Port}")
            : string.Create(CultureInfo.InvariantCulture, $"{Host}:{Port}");

    /// <summary>
    /// Parses <c>&lt;host&gt;:&lt;port&gt;</c> or <c>[&lt;ipv6&gt;]:&lt;port&gt;</c>. A value that does
    /// not name a port is refused rather than defaulted, because a default would silently point the
    /// calibration control at a different hop than the operator configured.
    /// </summary>
    internal static bool TryParse(string value, out Socks5Proxy? proxy, out string? error)
    {
        proxy = null;
        error = null;

        if (string.IsNullOrWhiteSpace(value))
        {
            error = "--socks5 needs <host:port>";
            return false;
        }

        if (value.Any(char.IsWhiteSpace))
        {
            error = $"--socks5 value '{value}' contains whitespace";
            return false;
        }

        if (!TrySplitHostPort(value, out var host, out var portText, out error))
        {
            return false;
        }

        if (host.Length == 0)
        {
            error = $"--socks5 value '{value}' has no host before the port";
            return false;
        }

        if (!TryParsePort(portText, out var port))
        {
            error = $"--socks5 value '{value}' does not end in a port number in 1..65535";
            return false;
        }

        proxy = new Socks5Proxy(host, port);
        return true;
    }

    private static bool TrySplitHostPort(string value, out string host, out string portText, out string? error)
    {
        host = string.Empty;
        portText = string.Empty;
        error = null;

        if (value[0] == '[')
        {
            var close = value.IndexOf(']', StringComparison.Ordinal);
            if (close < 0)
            {
                error = $"--socks5 value '{value}' is missing the closing ']' of an IPv6 host";
                return false;
            }

            if (close + 1 >= value.Length || value[close + 1] != ':')
            {
                error = $"--socks5 value '{value}' is not <host:port>";
                return false;
            }

            host = value[1..close];
            portText = value[(close + 2)..];
            return true;
        }

        var separator = value.LastIndexOf(':');
        if (separator <= 0)
        {
            error = $"--socks5 value '{value}' is not <host:port>";
            return false;
        }

        host = value[..separator];
        portText = value[(separator + 1)..];
        if (host.Contains(':', StringComparison.Ordinal))
        {
            error = $"--socks5 value '{value}' has an unbracketed IPv6 host; spell it [::1]:1080";
            return false;
        }

        return true;
    }

    private static bool TryParsePort(string text, out int port) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out port) && port is >= 1 and <= 65535;
}
