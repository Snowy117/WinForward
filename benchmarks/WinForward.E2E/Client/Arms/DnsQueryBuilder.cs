using WinForward.E2E.Wire;

namespace WinForward.E2E.Client.Arms;

/// <summary>
/// The queries a dns arm puts on the wire: the type mix, the name a query asks for, and the wire type
/// name the published <c>queryTypes</c> counts use.
/// </summary>
internal static class DnsQueryBuilder
{
    internal static string TypeName(ushort type) => type switch
    {
        DnsWire.TypeA => "A",
        DnsWire.TypeAaaa => "AAAA",
        DnsWire.TypeCname => "CNAME",
        DnsWire.TypeHttps => "HTTPS",
        DnsWire.TypeTxt => "TXT",

        // One key per unlisted type: a shared "other" bucket would let one type's count overwrite another's.
        _ => $"type{type}",
    };

    internal static ushort QueryTypeFor(long index, int cnameEvery)
    {
        if (cnameEvery > 0 && index > 0 && index % cnameEvery == 0)
        {
            return DnsWire.TypeCname;
        }

        return (index % 100) switch
        {
            < 54 => DnsWire.TypeA,
            < 78 => DnsWire.TypeAaaa,
            < 98 => DnsWire.TypeHttps,
            _ => DnsWire.TypeTxt,
        };
    }

    internal static int BuildQuery(Span<byte> destination, ushort transactionId, long index, int cnameEvery) =>
        DnsWire.BuildQuery(destination, transactionId, string.Create(System.Globalization.CultureInfo.InvariantCulture, $"q{index}.bench.local"), QueryTypeFor(index, cnameEvery));
}
