using System.Buffers.Binary;
using System.Net;

namespace WinForward.Protocols;

public enum Socks5Command : byte
{
    Connect = 1,
    UdpAssociate = 3
}

/// <summary>
/// The discriminated outcome of parsing a SOCKS5 reply (RFC 1928 section 6). <see cref="Invalid"/>
/// is a malformed or truncated reply, <see cref="Failure"/> is a well-formed reply carrying a
/// definite failure REP status (1-8), and <see cref="Success"/> is a well-formed success reply
/// (REP 0) with a valid bound address. Callers distinguish these so an undersized failure reply is
/// never mistaken for a successful parse.
/// </summary>
public enum Socks5ReplyKind
{
    Invalid,
    Failure,
    Success,
}

public static class Socks5Messages
{
    /// <summary>
    /// The process-lifetime constant greeting frames: <c>[5,1,0]</c> (no authentication offered)
    /// and <c>[5,2,0,2]</c> (username/password). Built once; callers never copy or mutate them.
    /// </summary>
    public static ReadOnlyMemory<byte> GreetingNoCredentials { get; } = new byte[] { 5, 1, 0 };

    public static ReadOnlyMemory<byte> GreetingWithCredentials { get; } = new byte[] { 5, 2, 0, 2 };

    public static ReadOnlyMemory<byte> Greeting(bool credentials) => credentials ? GreetingWithCredentials : GreetingNoCredentials;

    /// <summary>The byte length of the RFC 1929 username/password message for a credential pair.</summary>
    public static int UsernamePasswordLength(string username, string password)
    {
        var userLength = System.Text.Encoding.UTF8.GetByteCount(username);
        var secretLength = System.Text.Encoding.UTF8.GetByteCount(password);
        // RFC 1929 permits a zero-length password; only the username must be 1..255 bytes.
        if (userLength is 0 or > 255 || secretLength > 255) throw new ArgumentOutOfRangeException(nameof(username));
        return 3 + userLength + secretLength;
    }

    /// <summary>
    /// Writes the RFC 1929 username/password message into <paramref name="destination"/> and
    /// returns its length.
    /// </summary>
    public static int WriteUsernamePassword(string username, string password, Span<byte> destination)
    {
        var length = UsernamePasswordLength(username, password);
        if (destination.Length < length) throw new ArgumentException("The destination span is too small for the username/password message.", nameof(destination));
        var userLength = System.Text.Encoding.UTF8.GetByteCount(username);
        destination[0] = 1;
        destination[1] = (byte)userLength;
        System.Text.Encoding.UTF8.GetBytes(username.AsSpan(), destination.Slice(2, userLength));
        destination[2 + userLength] = (byte)(length - 3 - userLength);
        System.Text.Encoding.UTF8.GetBytes(password.AsSpan(), destination.Slice(3 + userLength));
        return length;
    }

    /// <summary>The byte length of the SOCKS5 request for <paramref name="address"/>.</summary>
    public static int RequestLength(IPAddress address) =>
        4 + (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 4 : 16) + 2;

    /// <summary>
    /// Writes a SOCKS5 request (RFC 1928 section 4) into <paramref name="destination"/> and
    /// returns its length; the reserved byte is written explicitly because the caller's scratch
    /// span is reused.
    /// </summary>
    public static int WriteRequest(Socks5Command command, IPAddress address, ushort port, Span<byte> destination)
    {
        var addressLength = address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 4 : 16;
        var length = 4 + addressLength + 2;
        if (destination.Length < length) throw new ArgumentException("The destination span is too small for the request.", nameof(destination));
        destination[0] = 5;
        destination[1] = (byte)command;
        destination[2] = 0;
        destination[3] = addressLength == 4 ? (byte)1 : (byte)4;
        if (!address.TryWriteBytes(destination.Slice(4, addressLength), out var written) || written != addressLength)
        {
            throw new ArgumentException("The address does not have a writable network-order form.", nameof(address));
        }
        BinaryPrimitives.WriteUInt16BigEndian(destination.Slice(4 + addressLength, 2), port);
        return length;
    }

    /// <summary>
    /// Parses a full SOCKS5 reply into a discriminated <see cref="Socks5ReplyKind"/> plus the REP
    /// status and, for a success reply, the bound address type and port. A failure reply (REP 1-8)
    /// carries no meaningful bound address per RFC 1928 and is reported as <see cref="Socks5ReplyKind.Failure"/>
    /// without requiring a bound address; only a success reply is <see cref="Socks5ReplyKind.Success"/>.
    /// A truncated success reply (fewer than 8 bytes) is <see cref="Socks5ReplyKind.Invalid"/>, never
    /// "parsed" into garbage (L2).
    /// </summary>
    public static Socks5ReplyKind TryParseReply(ReadOnlySpan<byte> reply, out byte status, out byte addressType, out ushort port)
    {
        status = 0;
        addressType = 0;
        port = 0;
        if (reply.Length < 2 || reply[0] != 5) return Socks5ReplyKind.Invalid;
        status = reply[1];
        if (status > 8) return Socks5ReplyKind.Invalid;
        // A failure reply (REP 1-8) is a definite, well-formed failure; the status code itself is
        // the diagnostic and no bound address is required.
        if (status != 0) return Socks5ReplyKind.Failure;
        if (reply.Length < 5 || reply[2] != 0) return Socks5ReplyKind.Invalid;
        addressType = reply[3];
        var addressLength = addressType switch { 1 => 4, 4 => 16, _ => 0 };
        if (addressType is not (1 or 3 or 4)) return Socks5ReplyKind.Invalid;
        var portOffset = 4 + addressLength + (addressType == 3 ? 1 : 0);
        if (addressType is 3)
        {
            if (reply.Length < 5) return Socks5ReplyKind.Invalid;
            addressLength = reply[4];
            portOffset = 5 + addressLength;
        }
        if (addressType is 3 && addressLength == 0) return Socks5ReplyKind.Invalid;
        if (reply.Length < portOffset + 2) return Socks5ReplyKind.Invalid;
        port = BinaryPrimitives.ReadUInt16BigEndian(reply.Slice(portOffset, 2));
        return Socks5ReplyKind.Success;
    }

    /// <summary>
    /// Normalizes a SOCKS5 reply bound address for the given <paramref name="command"/> so the caller
    /// can construct a routable relay endpoint. An IPv4-mapped <c>::ffff:a.b.c.d</c> reply is mapped
    /// to its IPv4 form before any decision. Only a <see cref="Socks5Command.UdpAssociate"/> reply
    /// substitutes an unspecified wildcard (<c>0.0.0.0</c>/<c>::</c>) with the TCP control peer's
    /// address (the RFC-endorsed fallback); a <see cref="Socks5Command.Connect"/> reply is never
    /// routed so no substitution is applied (M1). The server-provided BND port is never modified
    /// here — the caller preserves it. A genuine IPv6 reply that lost its scope during raw-byte
    /// reconstruction inherits the control peer's non-zero <see cref="IPAddress.ScopeId"/> so a
    /// link-local relay resolves on the correct interface (M2).
    /// </summary>
    public static IPAddress NormalizeBndAddress(IPAddress replyAddress, IPAddress controlPeer, Socks5Command command)
    {
        ArgumentNullException.ThrowIfNull(replyAddress);
        ArgumentNullException.ThrowIfNull(controlPeer);

        // Normalize an IPv4-mapped form before checking for an unspecified wildcard, so a mapped
        // ::ffff:0.0.0.0 is recognized as Any and later routing targets use the plain IPv4 address.
        if (replyAddress.IsIPv4MappedToIPv6) replyAddress = replyAddress.MapToIPv4();

        if (command == Socks5Command.UdpAssociate && (replyAddress.Equals(IPAddress.Any) || replyAddress.Equals(IPAddress.IPv6Any)))
        {
            return controlPeer;
        }

        // A genuine IPv6 address built from raw reply bytes (new IPAddress(byte[])) has ScopeId 0.
        // Carry the control peer's interface scope so a link-local relay endpoint routes correctly.
        if (replyAddress.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 &&
            controlPeer.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 &&
            controlPeer.ScopeId != 0)
        {
            return new IPAddress(replyAddress.GetAddressBytes(), controlPeer.ScopeId);
        }

        return replyAddress;
    }

    /// <summary>
    /// Maps a SOCKS5 reply status code (RFC 1928 section 6) to its human-readable description for
    /// diagnostics. Credentials are never included; only the protocol status is surfaced.
    /// </summary>
    public static string DescribeReplyStatus(byte status) => status switch
    {
        0 => "succeeded",
        1 => "general SOCKS server failure",
        2 => "connection not allowed by ruleset",
        3 => "network unreachable",
        4 => "host unreachable",
        5 => "connection refused",
        6 => "TTL expired",
        7 => "command not supported",
        8 => "address type not supported",
        _ => $"unknown status {status}"
    };

    /// <summary>
    /// Validates a 5-byte reply prefix (VER REP RSV ATYP) and returns the REP status code. A
    /// prefix is well-formed when VER==5; the status tells success (0) from a failure (1-8). This
    /// does NOT require a full reply, because a success prefix is only 5 bytes and the bound
    /// address arrives later. Callers must read the full reply (see <see cref="TryGetReplyLength"/>
    /// and <see cref="TryParseReply"/>) to obtain the bound endpoint.
    /// </summary>
    public static bool TryParseReplyPrefix(ReadOnlySpan<byte> prefix, out byte status)
    {
        status = 0;
        if (prefix.Length < 5 || prefix[0] != 5 || prefix[1] > 8 || prefix[2] != 0 || prefix[3] is not (1 or 3 or 4)) return false;
        status = prefix[1];
        return true;
    }

    public static bool TryGetReplyLength(ReadOnlySpan<byte> prefix, out int length)
    {
        length = 0;
        if (!TryParseReplyPrefix(prefix, out _)) return false;
        length = prefix[3] switch
        {
            1 => 10,
            3 => 7 + prefix[4],
            4 => 22,
            _ => 0
        };
        return length > 0;
    }
}
