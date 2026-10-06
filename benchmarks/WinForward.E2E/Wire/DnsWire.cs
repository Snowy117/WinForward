using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace WinForward.E2E.Wire;

[StructLayout(LayoutKind.Auto)]
internal readonly struct DnsQueryInfo
{
    internal DnsQueryInfo(ushort transactionId, ushort queryType, ushort flags, int questionEnd)
    {
        TransactionId = transactionId;
        QueryType = queryType;
        Flags = flags;
        QuestionEnd = questionEnd;
    }

    internal ushort TransactionId { get; }

    internal ushort QueryType { get; }

    internal ushort Flags { get; }

    internal int QuestionEnd { get; }
}

internal static class DnsWire
{
    internal const ushort TypeA = 1;
    internal const ushort TypeCname = 5;
    internal const ushort TypeTxt = 16;
    internal const ushort TypeAaaa = 28;
    internal const ushort TypeHttps = 65;

    internal const int MaxMessageLength = 4096;

    private const ushort ClassIn = 1;
    private const int HeaderSize = 12;
    private const byte CompressionPointerToQuestion = 0x0C;

    private const ushort FlagResponse = 0x8000;
    private const ushort FlagRecursionDesired = 0x0100;
    private const ushort FlagRecursionAvailable = 0x0080;

    private const ushort OpcodeMask = 0x7800;
    private const ushort RcodeMask = 0x000F;
    private const int MaxLabelLength = 63;

    private const uint AnswerTtlSeconds = 60;

    internal static int BuildQuery(Span<byte> destination, ushort transactionId, ReadOnlySpan<char> name, ushort queryType)
    {
        if (destination.Length < HeaderSize)
        {
            return -1;
        }

        BinaryPrimitives.WriteUInt16BigEndian(destination, transactionId);
        BinaryPrimitives.WriteUInt16BigEndian(destination[2..], FlagRecursionDesired);
        BinaryPrimitives.WriteUInt16BigEndian(destination[4..], 1);
        BinaryPrimitives.WriteUInt16BigEndian(destination[6..], 0);
        BinaryPrimitives.WriteUInt16BigEndian(destination[8..], 0);
        BinaryPrimitives.WriteUInt16BigEndian(destination[10..], 0);

        var offset = HeaderSize;
        var labelStart = 0;
        for (var index = 0; index <= name.Length; index++)
        {
            if (index != name.Length && name[index] != '.')
            {
                continue;
            }

            var labelLength = index - labelStart;
            if (labelLength is 0 or > MaxLabelLength || offset + labelLength + 5 > destination.Length)
            {
                return -1;
            }

            destination[offset++] = (byte)labelLength;
            for (var character = labelStart; character < index; character++)
            {
                if (name[character] > 0x7F)
                {
                    return -1;
                }

                destination[offset++] = (byte)name[character];
            }

            labelStart = index + 1;
        }

        if (offset + 5 > destination.Length)
        {
            return -1;
        }

        destination[offset++] = 0;
        BinaryPrimitives.WriteUInt16BigEndian(destination[offset..], queryType);
        offset += 2;
        BinaryPrimitives.WriteUInt16BigEndian(destination[offset..], ClassIn);
        offset += 2;
        return offset;
    }

    internal static bool TryParseQuery(ReadOnlySpan<byte> message, out DnsQueryInfo query)
    {
        query = default;

        if (message.Length < HeaderSize || BinaryPrimitives.ReadUInt16BigEndian(message[4..]) == 0)
        {
            return false;
        }

        var offset = HeaderSize;
        while (true)
        {
            if (offset >= message.Length)
            {
                return false;
            }

            var labelLength = message[offset];
            if (labelLength == 0)
            {
                offset++;
                break;
            }

            if ((labelLength & 0xC0) != 0)
            {
                offset += 2;
                break;
            }

            offset += 1 + labelLength;
        }

        if (offset + 4 > message.Length)
        {
            return false;
        }

        query = new DnsQueryInfo(
            BinaryPrimitives.ReadUInt16BigEndian(message),
            BinaryPrimitives.ReadUInt16BigEndian(message[offset..]),
            BinaryPrimitives.ReadUInt16BigEndian(message[2..]),
            offset + 4);
        return true;
    }

    internal static int BuildResponse(ReadOnlySpan<byte> query, in DnsQueryInfo info, Span<byte> destination, out int answerCount)
    {
        var questionLength = info.QuestionEnd - HeaderSize;
        var rdataLength = info.QueryType switch
        {
            TypeA => 4,
            TypeAaaa => 16,
            _ => 0,
        };

        var answerLength = rdataLength == 0 ? 0 : 2 + 2 + 2 + 4 + 2 + rdataLength;
        var totalLength = HeaderSize + questionLength + answerLength;
        answerCount = rdataLength == 0 ? 0 : 1;

        if (destination.Length < totalLength)
        {
            return -1;
        }

        var flags = (ushort)(FlagResponse | FlagRecursionAvailable | (info.Flags & OpcodeMask) | (info.Flags & FlagRecursionDesired));
        BinaryPrimitives.WriteUInt16BigEndian(destination, info.TransactionId);
        BinaryPrimitives.WriteUInt16BigEndian(destination[2..], flags);
        BinaryPrimitives.WriteUInt16BigEndian(destination[4..], 1);
        BinaryPrimitives.WriteUInt16BigEndian(destination[6..], (ushort)answerCount);
        BinaryPrimitives.WriteUInt16BigEndian(destination[8..], 0);
        BinaryPrimitives.WriteUInt16BigEndian(destination[10..], 0);

        query.Slice(HeaderSize, questionLength).CopyTo(destination[HeaderSize..]);

        if (answerCount == 0)
        {
            return totalLength;
        }

        var answer = destination[(HeaderSize + questionLength)..];
        answer[0] = 0xC0;
        answer[1] = CompressionPointerToQuestion;
        BinaryPrimitives.WriteUInt16BigEndian(answer[2..], info.QueryType);
        BinaryPrimitives.WriteUInt16BigEndian(answer[4..], ClassIn);
        BinaryPrimitives.WriteUInt32BigEndian(answer[6..], AnswerTtlSeconds);
        BinaryPrimitives.WriteUInt16BigEndian(answer[10..], (ushort)rdataLength);
        answer.Slice(12, rdataLength).Clear();
        if (info.QueryType == TypeA)
        {
            answer[12] = 10;
            answer[13] = 0;
            answer[14] = 0;
            answer[15] = 1;
        }
        else
        {
            answer[12 + rdataLength - 1] = 1;
        }

        return totalLength;
    }

    internal static bool TryParseResponse(ReadOnlySpan<byte> message, out ushort transactionId, out int rcode, out int answerCount)
    {
        transactionId = 0;
        rcode = 0;
        answerCount = 0;

        if (message.Length < HeaderSize)
        {
            return false;
        }

        var flags = BinaryPrimitives.ReadUInt16BigEndian(message[2..]);
        if ((flags & FlagResponse) == 0)
        {
            return false;
        }

        transactionId = BinaryPrimitives.ReadUInt16BigEndian(message);
        rcode = flags & RcodeMask;
        answerCount = BinaryPrimitives.ReadUInt16BigEndian(message[6..]);
        return true;
    }
}
