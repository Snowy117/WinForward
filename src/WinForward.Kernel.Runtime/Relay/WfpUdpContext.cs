using System.Buffers.Binary;
using WinForward.Userland.Device;

namespace WinForward.Userland.Relay;

/// <summary>
/// Walks the <c>WSACMSGHDR</c> chain of a datagram received on a WFP redirect listener and extracts
/// the redirect-context payload the driver attached to the association.
/// </summary>
/// <remarks>
/// <para>
/// The option is a Winsock vendor extension: <c>IP_WFP_REDIRECT_CONTEXT</c> /
/// <c>IPV6_WFP_REDIRECT_CONTEXT</c>, both type <see cref="IpWfpRedirectContext"/>, carried at either
/// protocol level. The level is a property of the layer that delivered the datagram, not of the
/// context it carries: the driver normalizes an IPv4-mapped destination to family 4 before it builds
/// the context, while a v4-mapped <em>connected</em> association is still delivered by the IPv6 layer,
/// so a level-41 control message can legitimately hold a family-4 context. The level is therefore only
/// diagnostic here; the family is read from the context itself.
/// </para>
/// <para>
/// Only the chain walk lives here. The payload is handed to
/// <see cref="RedirectDestination.TryParse"/>, which owns the magic/version/family/protocol/port
/// validation, so there is one ABI decoder rather than a second one beside it.
/// </para>
/// </remarks>
public static class WfpUdpContext
{
    /// <summary><c>IP_WFP_REDIRECT_CONTEXT</c> / <c>IPV6_WFP_REDIRECT_CONTEXT</c>: the cmsg type.</summary>
    public const int IpWfpRedirectContext = 70;

    /// <summary><c>IPPROTO_IP</c>: the level an IPv4-layer datagram's cmsg is tagged with.</summary>
    public const int IpProtocolIp = 0;

    /// <summary><c>IPPROTO_IPV6</c>: the level an IPv6-layer datagram's cmsg is tagged with.</summary>
    public const int IpProtocolIpv6 = 41;

    /// <summary>
    /// <c>WSACMSGHDR</c>: <c>SIZE_T cmsg_len</c>, <c>INT cmsg_level</c>, <c>INT cmsg_type</c>. The
    /// chain is laid out at the pointer size of the receiving process, which is the driver's target.
    /// </summary>
    private static readonly int s_headerSize = IntPtr.Size + (2 * sizeof(int));

    /// <summary>
    /// Extracts the single redirect-context payload from a received control buffer. Returns
    /// <see langword="false"/> when no context is present, when the chain is malformed, or when more
    /// than one context datagram is attached — an ambiguous association is refused rather than guessed.
    /// </summary>
    /// <param name="control">The control buffer as reported by <c>WSARecvMsg</c>.</param>
    /// <param name="contextPayload">The context bytes, still in the caller's buffer; empty on failure.</param>
    /// <param name="level">The cmsg level carrying the context; <c>-1</c> on failure.</param>
    public static bool TryParseControl(ReadOnlySpan<byte> control, out ReadOnlySpan<byte> contextPayload, out int level)
    {
        contextPayload = default;
        level = -1;
        var found = false;
        var offset = 0;
        while (offset + s_headerSize <= control.Length)
        {
            var header = control[offset..];
            // Windows is little-endian on every supported target; cmsg_len is a SIZE_T of that width.
            var declaredLength = IntPtr.Size == sizeof(long)
                ? BinaryPrimitives.ReadUInt64LittleEndian(header)
                : BinaryPrimitives.ReadUInt32LittleEndian(header);
            if (declaredLength < (ulong)s_headerSize || declaredLength > (ulong)control.Length) return false;

            var cmsgLength = (int)declaredLength;
            if (offset + cmsgLength > control.Length) return false;
            var cmsgLevel = BinaryPrimitives.ReadInt32LittleEndian(header[IntPtr.Size..]);
            var cmsgType = BinaryPrimitives.ReadInt32LittleEndian(header[(IntPtr.Size + sizeof(int))..]);

            if (cmsgType == IpWfpRedirectContext && cmsgLevel is IpProtocolIp or IpProtocolIpv6)
            {
                if (found)
                {
                    // A second context means the association is ambiguous; refuse it rather than let
                    // the first one win by position.
                    contextPayload = default;
                    level = -1;
                    return false;
                }

                var dataLength = cmsgLength - s_headerSize;
                if (dataLength < DeviceLayout.RedirectContextSize)
                {
                    contextPayload = default;
                    return false;
                }

                contextPayload = header.Slice(s_headerSize, dataLength);
                level = cmsgLevel;
                found = true;
            }

            var advance = ((long)cmsgLength + IntPtr.Size - 1) & ~((long)IntPtr.Size - 1);
            if (advance is <= 0 or > int.MaxValue) return false;
            offset += (int)advance;
        }

        if (!found) contextPayload = default;
        return found;
    }
}
