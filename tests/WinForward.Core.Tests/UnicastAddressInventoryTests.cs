using System.Net;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using WinForward.Windows;
using Xunit;

namespace WinForward.Core.Tests;

/// <summary>
/// The parse/group/resolve helpers are pure managed code over fake buffers, so they run (and
/// pass) on any host; only the native fetch itself is Windows-only, which this suite never
/// invokes.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class UnicastAddressInventoryTests
{
    [Fact]
    public void ParseRowsReadsIpv4AndIpv6RowsWithLuidAndScope()
    {
        var buffer = BuildTable(
            (UnicastAddressInventory.AfInet, ToBytes(IPAddress.Parse("192.168.77.2")), 0u, 11uL),
            (UnicastAddressInventory.AfInet6, ToBytes(IPAddress.Parse("240c:c001:101::1")), 9u, 22uL));
        try
        {
            var rows = UnicastAddressInventory.ParseRows(buffer);

            Assert.Equal(2, rows.Count);
            Assert.Equal(11uL, rows[0].InterfaceLuid);
            Assert.Equal(IPAddress.Parse("192.168.77.2"), rows[0].Address);
            Assert.Equal(22uL, rows[1].InterfaceLuid);
            Assert.Equal(new IPAddress(IPAddress.Parse("240c:c001:101::1").GetAddressBytes(), 9), rows[1].Address);
            Assert.Equal(9, rows[1].Address.ScopeId);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [Fact]
    public void ParseRowsRejectsAnUnknownAddressFamily()
    {
        var buffer = BuildTable((999, ToBytes(IPAddress.Parse("192.168.77.2")), 0u, 11uL));
        try
        {
            Assert.Throws<InvalidOperationException>(() => UnicastAddressInventory.ParseRows(buffer));
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [Fact]
    public void ParseRowsRejectsACountThatCannotBelongToARealHostTable()
    {
        var buffer = Marshal.AllocHGlobal(4);
        try
        {
            Marshal.WriteInt32(buffer, -1);
            Assert.Throws<InvalidOperationException>(() => UnicastAddressInventory.ParseRows(buffer));

            Marshal.WriteInt32(buffer, UnicastAddressInventory.MaxUnicastAddressRows + 1);
            Assert.Throws<InvalidOperationException>(() => UnicastAddressInventory.ParseRows(buffer));

            Marshal.WriteInt32(buffer, 0);
            Assert.Empty(UnicastAddressInventory.ParseRows(buffer));
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [Fact]
    public void EntryCountValidationAcceptsTheBoundaryButRejectsBeyondIt()
    {
        UnicastAddressInventory.ValidateEntryCount(0);
        UnicastAddressInventory.ValidateEntryCount(UnicastAddressInventory.MaxUnicastAddressRows);
        Assert.Throws<InvalidOperationException>(() => UnicastAddressInventory.ValidateEntryCount(UnicastAddressInventory.MaxUnicastAddressRows + 1));
        Assert.Throws<InvalidOperationException>(() => UnicastAddressInventory.ValidateEntryCount(-1));
    }

    [Fact]
    public void ParseRowsRejectsANullBuffer()
    {
        Assert.Throws<ArgumentNullException>(() => UnicastAddressInventory.ParseRows(nint.Zero));
    }

    [Fact]
    public void GroupFingerprintsSortsAndJoinsEachInterfacesAddresses()
    {
        var guidA = new Guid(0xdd8cd9a1, 0xb6f, 0x4e6e, 0x9a, 0x86, 0x6, 0xf1, 0xae, 0xb0, 0xa1, 0xf0) /* dd8cd9a1-0b6f-4e6e-9a86-06f1aeb0a1f0 */;
        var guidB = new Guid(0x11223344, 0x5566, 0x7788, 0x99, 0xaa, 0xbb, 0xcc, 0xdd, 0xee, 0xff, 0x0) /* 11223344-5566-7788-99aa-bbccddeeff00 */;
        var rows = new[]
        {
            new UnicastAddressObservation(11, IPAddress.Parse("240c:c001:101::1")),
            new UnicastAddressObservation(11, IPAddress.Parse("192.168.77.2")),
            new UnicastAddressObservation(22, IPAddress.Parse("10.0.0.1")),
        };

        var fingerprints = UnicastAddressInventory.GroupFingerprints(
            rows,
            luid => luid == 11 ? guidA : guidB);

        Assert.Equal(2, fingerprints.Count);
        Assert.Equal("192.168.77.2;240c:c001:101::1", fingerprints[guidA.ToString("D")]);
        Assert.Equal("10.0.0.1", fingerprints[guidB.ToString("D")]);
    }

    [Fact]
    public void GroupFingerprintsExcludesIpv6LinkLocalAddresses()
    {
        var guid = new Guid(0xdd8cd9a1, 0xb6f, 0x4e6e, 0x9a, 0x86, 0x6, 0xf1, 0xae, 0xb0, 0xa1, 0xf0) /* dd8cd9a1-0b6f-4e6e-9a86-06f1aeb0a1f0 */;
        var rows = new[]
        {
            new UnicastAddressObservation(11, IPAddress.Parse("fe80::1%5")),
            new UnicastAddressObservation(11, IPAddress.Parse("240c:c001:101::1")),
        };

        var fingerprints = UnicastAddressInventory.GroupFingerprints(rows, _ => guid);

        var fingerprint = Assert.Single(fingerprints);
        Assert.Equal("240c:c001:101::1", fingerprint.Value);
    }

    [Fact]
    public void GroupFingerprintsSkipsInterfacesTheResolverCannotName()
    {
        var guid = new Guid(0xdd8cd9a1, 0xb6f, 0x4e6e, 0x9a, 0x86, 0x6, 0xf1, 0xae, 0xb0, 0xa1, 0xf0) /* dd8cd9a1-0b6f-4e6e-9a86-06f1aeb0a1f0 */;
        var rows = new[]
        {
            new UnicastAddressObservation(11, IPAddress.Parse("192.168.77.2")),
            new UnicastAddressObservation(22, IPAddress.Parse("10.0.0.1")),
        };

        var fingerprints = UnicastAddressInventory.GroupFingerprints(
            rows,
            luid => luid == 11 ? guid : null);

        Assert.Equal([guid.ToString("D")], [.. fingerprints.Keys]);
    }

    [Fact]
    public void ResolveFingerprintNormalizesTheStableIdThroughGuidExtraction()
    {
        var guid = new Guid(0xdd8cd9a1, 0xb6f, 0x4e6e, 0x9a, 0x86, 0x6, 0xf1, 0xae, 0xb0, 0xa1, 0xf0) /* dd8cd9a1-0b6f-4e6e-9a86-06f1aeb0a1f0 */;
        var fingerprints = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [guid.ToString("D")] = "192.168.77.2",
        };

        // WindowsAdapterInventory uses the raw NetworkInterface.Id (braced, original casing) as
        // the stable ID, so the resolver must normalize both sides to the canonical D form.
        Assert.Equal("192.168.77.2", UnicastAddressInventory.ResolveFingerprint($"{{{guid.ToString("D").ToUpperInvariant()}}}", fingerprints));
        Assert.Equal(string.Empty, UnicastAddressInventory.ResolveFingerprint("not-a-guid", fingerprints));
        Assert.Equal(string.Empty, UnicastAddressInventory.ResolveFingerprint(Guid.NewGuid().ToString("D"), fingerprints));
    }

    private static byte[] ToBytes(IPAddress address) => address.GetAddressBytes();

    /// <summary>
    /// Builds a MIB_UNICASTIPADDRESS_TABLE-shaped buffer: 4-byte count, 4 bytes of alignment
    /// padding (the row is 8-byte aligned, so the native layout starts Table[0] at offset 8 —
    /// poisoned here to prove the parser never reads it), then native rows.
    /// </summary>
    private static unsafe nint BuildTable(params (ushort Family, byte[] Address, uint ScopeId, ulong Luid)[] rows)
    {
        var buffer = Marshal.AllocHGlobal(IPHelperAbi.UnicastTableFirstRowOffset + (rows.Length * sizeof(IPHelperAbi.MibUnicastIpAddressRow)));
        Marshal.WriteInt32(buffer, rows.Length);
        Marshal.WriteInt32(buffer, 4, unchecked((int)0xDeadBeef));
        for (var index = 0; index < rows.Length; index++)
        {
            var row = (IPHelperAbi.MibUnicastIpAddressRow*)((byte*)buffer + IPHelperAbi.UnicastTableFirstRowOffset + (index * sizeof(IPHelperAbi.MibUnicastIpAddressRow)));
            var (family, address, scopeId, luid) = rows[index];
            *(ushort*)row->Address = family;
            var addressOffset = family == UnicastAddressInventory.AfInet6 ? IPHelperAbi.Ipv6AddressOffset : IPHelperAbi.Ipv4AddressOffset;
            fixed (byte* source = address)
            {
                Buffer.MemoryCopy(source, row->Address + addressOffset, IPHelperAbi.SockaddrInetSize - addressOffset, address.Length);
            }
            if (family == UnicastAddressInventory.AfInet6)
            {
                *(uint*)(row->Address + IPHelperAbi.Ipv6ScopeIdOffset) = scopeId;
            }
            row->InterfaceLuid = luid;
        }
        return buffer;
    }
}
