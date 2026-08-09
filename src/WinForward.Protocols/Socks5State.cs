using System.Buffers.Binary;
using System.Net;

namespace WinForward.Protocols;

public enum Socks5Command : byte
{
    Connect = 1,
    UdpAssociate = 3
}

public static class Socks5Messages
{
    public static byte[] Greeting(bool credentials) => credentials ? [5, 2, 0, 2] : [5, 1, 0];

    public static byte[] UsernamePassword(string username, string password)
    {
        var user = System.Text.Encoding.UTF8.GetBytes(username);
        var secret = System.Text.Encoding.UTF8.GetBytes(password);
        if (user.Length is 0 or > 255 || secret.Length is 0 or > 255) throw new ArgumentOutOfRangeException(nameof(username));
        var message = new byte[3 + user.Length + secret.Length];
        message[0] = 1;
        message[1] = (byte)user.Length;
        user.CopyTo(message.AsSpan(2));
        message[2 + user.Length] = (byte)secret.Length;
        secret.CopyTo(message.AsSpan(3 + user.Length));
        return message;
    }

    public static byte[] Request(Socks5Command command, IPAddress address, ushort port)
    {
        var addressBytes = address.GetAddressBytes();
        var addressType = address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? (byte)1 : (byte)4;
        var result = new byte[4 + addressBytes.Length + 2];
        result[0] = 5;
        result[1] = (byte)command;
        result[3] = addressType;
        addressBytes.CopyTo(result.AsSpan(4));
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(4 + addressBytes.Length, 2), port);
        return result;
    }

    public static bool TryParseReply(ReadOnlySpan<byte> reply, out byte status, out byte addressType, out ushort port)
    {
        status = 0;
        addressType = 0;
        port = 0;
        if (reply.Length < 2 || reply[0] != 5) return false;
        status = reply[1];
        // A failure reply (REP 1-8) carries no meaningful bound address per RFC 1928; the status
        // code itself is the diagnostic. Only a success reply parses the bound address.
        if (status != 0) return true;
        if (reply.Length < 8) return false;
        addressType = reply[3];
        var addressLength = addressType switch { 1 => 4, 3 => reply.Length >= 5 ? reply[4] : 0, 4 => 16, _ => 0 };
        var portOffset = 4 + addressLength + (addressType == 3 ? 1 : 0);
        if (addressLength == 0 || reply.Length < portOffset + 2) return false;
        port = BinaryPrimitives.ReadUInt16BigEndian(reply.Slice(portOffset, 2));
        return true;
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
        if (prefix.Length < 2 || prefix[0] != 5) return false;
        status = prefix[1];
        return true;
    }

    public static bool TryGetReplyLength(ReadOnlySpan<byte> prefix, out int length)
    {
        length = 0;
        if (prefix.Length < 5 || prefix[0] != 5) return false;
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
