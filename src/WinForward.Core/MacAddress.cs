using System.Runtime.InteropServices;

namespace WinForward.Core;

/// <summary>
/// A fixed-size, allocation-free Ethernet MAC address (six bytes, network order). The hot-path
/// representation for the recorded client MAC of a UDP flow: it lives inline in flow/session
/// state and travels to the response reinjector by value, so no per-datagram managed copy of the
/// captured header bytes is needed. <see cref="Invalid"/> marks "not recorded" and stays
/// fail-closed at the forwarding boundary.
/// </summary>
[StructLayout(LayoutKind.Auto)]
public readonly struct MacAddress : IEquatable<MacAddress>
{
    /// <summary>The Ethernet MAC length in bytes.</summary>
    public const int Length = 6;

    private readonly ulong _bits;

    private MacAddress(ulong bits, bool isValid)
    {
        _bits = bits;
        IsValid = isValid;
    }

    /// <summary>An unrecorded/absent MAC; the forwarding path drops fail-closed on this value.</summary>
    public static MacAddress Invalid => default;

    /// <summary>True when six bytes were recorded; false for <see cref="Invalid"/>.</summary>
    public bool IsValid { get; }

    /// <summary>Reads the six network-order bytes, or returns <see cref="Invalid"/> when the span is not exactly six bytes.</summary>
    public static MacAddress From(ReadOnlySpan<byte> source) => TryFrom(source, out var mac) ? mac : Invalid;

    /// <summary>Reads exactly six network-order bytes into the inline representation.</summary>
    public static bool TryFrom(ReadOnlySpan<byte> source, out MacAddress mac)
    {
        if (source.Length != Length)
        {
            mac = Invalid;
            return false;
        }

        var bits = 0UL;
        for (var index = 0; index < Length; index++) bits = (bits << 8) | source[index];
        mac = new MacAddress(bits, isValid: true);
        return true;
    }

    /// <summary>Writes the six network-order bytes into <paramref name="destination"/>.</summary>
    public void CopyTo(Span<byte> destination)
    {
        if (destination.Length < Length) throw new ArgumentOutOfRangeException(nameof(destination), destination.Length, "The destination must hold at least six bytes.");
        if (!IsValid) throw new InvalidOperationException("The MAC address was not recorded.");
        for (var index = 0; index < Length; index++) destination[index] = (byte)(_bits >> (8 * (Length - 1 - index)));
    }

    public bool Equals(MacAddress other) => IsValid == other.IsValid && (!IsValid || _bits == other._bits);

    public override bool Equals(object? obj) => obj is MacAddress other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(_bits, IsValid);

    public static bool operator ==(MacAddress left, MacAddress right) => left.Equals(right);

    public static bool operator !=(MacAddress left, MacAddress right) => !left.Equals(right);
}
