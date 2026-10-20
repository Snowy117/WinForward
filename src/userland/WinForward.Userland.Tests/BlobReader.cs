using System.Runtime.InteropServices;
using System.Text;
using WinForward.Userland.Policy;

namespace WinForward.Userland.Tests;

internal static class BlobReader
{
    internal static RuleBlobHeader Header(byte[] blob) => MemoryMarshal.Read<RuleBlobHeader>(blob);

    internal static RuleBlobRule Rule(byte[] blob, int index)
    {
        RuleBlobHeader header = Header(blob);
        int offset = (int)header.RulesOffset + (index * RuleBlobLayout.RuleSize);
        return MemoryMarshal.Read<RuleBlobRule>(blob.AsSpan(offset, RuleBlobLayout.RuleSize));
    }

    internal static RuleBlobNetworkPrefix NetworkPrefix(byte[] blob, int index)
    {
        RuleBlobHeader header = Header(blob);
        int offset = (int)header.NetworkPoolOffset + (index * RuleBlobLayout.NetworkPrefixSize);
        return MemoryMarshal.Read<RuleBlobNetworkPrefix>(blob.AsSpan(offset, RuleBlobLayout.NetworkPrefixSize));
    }

    internal static string ProcessString(byte[] blob, uint offset)
    {
        RuleBlobHeader header = Header(blob);
        int start = (int)header.ProcessPoolOffset + (int)offset;
        int end = start;
        while (end < blob.Length && blob[end] != 0)
        {
            end++;
        }

        return Encoding.UTF8.GetString(blob, start, end - start);
    }

    internal static byte[] NetworkAddress(RuleBlobNetworkPrefix prefix)
    {
        byte[] address = new byte[RuleBlobLayout.NetworkAddressSize];
        for (int index = 0; index < address.Length; index++)
        {
            address[index] = prefix.Network[index];
        }

        return address;
    }
}
