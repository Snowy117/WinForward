using System.Buffers.Binary;
using WinForward.E2E.Wire;
using Xunit;

namespace WinForward.E2E.Tests;

public sealed class DnsWireTests
{
    private const ushort TransactionId = 0x1234;

    [Fact]
    public void AQueryRoundTripsThroughTheParser()
    {
        Span<byte> query = stackalloc byte[512];

        var length = DnsWire.BuildQuery(query, TransactionId, "example.com", DnsWire.TypeA);

        Assert.True(length > 12);
        Assert.True(DnsWire.TryParseQuery(query[..length], out var info));
        Assert.Equal(TransactionId, info.TransactionId);
        Assert.Equal(DnsWire.TypeA, info.QueryType);
        // The question ends where the built message ends: parser and builder must agree byte for
        // byte, because the response is copied from that slice.
        Assert.Equal(length, info.QuestionEnd);
    }

    [Fact]
    public void AnEmptyNameIsRejected()
    {
        Span<byte> query = stackalloc byte[64];

        // A zero-length label is the root name, which this builder does not emit: the caller either
        // has a real name or has nothing to ask about.
        Assert.Equal(-1, DnsWire.BuildQuery(query, 1, string.Empty, DnsWire.TypeA));
    }

    [Fact]
    public void ACompressionPointerQuestionIsParsedAsATwoByteName()
    {
        Span<byte> message = stackalloc byte[64];
        message[..18].Clear();
        BinaryPrimitives.WriteUInt16BigEndian(message[4..], 1);
        message[12] = 0xC0;
        message[13] = 0x0C;
        BinaryPrimitives.WriteUInt16BigEndian(message[14..], DnsWire.TypeA);
        BinaryPrimitives.WriteUInt16BigEndian(message[16..], 1);

        Assert.True(DnsWire.TryParseQuery(message[..18], out var info));
        Assert.Equal(18, info.QuestionEnd);
        Assert.Equal(DnsWire.TypeA, info.QueryType);
    }

    [Theory]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("a..b")]
    [InlineData("caf\u00e9.example")]
    public void AnUnrepresentableNameIsRejected(string name)
    {
        Span<byte> query = stackalloc byte[512];

        Assert.Equal(-1, DnsWire.BuildQuery(query, TransactionId, name, DnsWire.TypeA));
    }

    [Fact]
    public void ADestinationWithoutRoomForTheQuestionIsRejected()
    {
        Span<byte> query = stackalloc byte[17];

        Assert.Equal(-1, DnsWire.BuildQuery(query, TransactionId, "example.com", DnsWire.TypeA));
    }

    [Fact]
    public void AResponseRepeatsTheQuestionAndPointsTheAnswerAtIt()
    {
        Span<byte> query = stackalloc byte[512];
        var queryLength = DnsWire.BuildQuery(query, TransactionId, "example.com", DnsWire.TypeA);
        Assert.True(DnsWire.TryParseQuery(query[..queryLength], out var info));

        Span<byte> response = stackalloc byte[512];
        var responseLength = DnsWire.BuildResponse(query[..queryLength], in info, response, out var answerCount);

        Assert.Equal(1, answerCount);
        Assert.True(DnsWire.TryParseResponse(response[..responseLength], out var parsedId, out var rcode, out var parsedAnswers));
        Assert.Equal(TransactionId, parsedId);
        Assert.Equal(0, rcode);
        Assert.Equal(1, parsedAnswers);
        Assert.Equal(0xC0, response[queryLength]);
        Assert.Equal(0x0C, response[queryLength + 1]);
        Assert.NotEqual(0, response[2] & 0x80);
    }

    [Theory]
    [InlineData(DnsWire.TypeA, 1)]
    [InlineData(DnsWire.TypeCname, 0)]
    [InlineData(DnsWire.TypeTxt, 0)]
    [InlineData(DnsWire.TypeAaaa, 1)]
    [InlineData(DnsWire.TypeHttps, 0)]
    public void OnlyAddressQueriesCarryAnAnswer(ushort queryType, int expectedAnswers)
    {
        Span<byte> query = stackalloc byte[512];
        var queryLength = DnsWire.BuildQuery(query, TransactionId, "example.com", queryType);
        Assert.True(DnsWire.TryParseQuery(query[..queryLength], out var info));

        Span<byte> response = stackalloc byte[512];
        var responseLength = DnsWire.BuildResponse(query[..queryLength], in info, response, out var answerCount);

        Assert.Equal(expectedAnswers, answerCount);
        Assert.True(DnsWire.TryParseResponse(response[..responseLength], out _, out var rcode, out var parsedAnswers));
        Assert.Equal(0, rcode);
        Assert.Equal(expectedAnswers, parsedAnswers);
    }

    [Fact]
    public void ADestinationWithoutRoomForTheAnswerIsRejected()
    {
        Span<byte> query = stackalloc byte[512];
        var queryLength = DnsWire.BuildQuery(query, TransactionId, "example.com", DnsWire.TypeA);
        Assert.True(DnsWire.TryParseQuery(query[..queryLength], out var info));
        var destination = new byte[queryLength + 12];

        Assert.Equal(-1, DnsWire.BuildResponse(query[..queryLength], in info, destination, out var answerCount));
        Assert.Equal(1, answerCount);
    }

    [Fact]
    public void TheResponseFlagSeparatesQueriesFromResponses()
    {
        Span<byte> query = stackalloc byte[512];
        var queryLength = DnsWire.BuildQuery(query, TransactionId, "example.com", DnsWire.TypeA);

        Assert.False(DnsWire.TryParseResponse(query[..queryLength], out _, out _, out _));
        Assert.False(DnsWire.TryParseResponse(new byte[11], out _, out _, out _));
    }
}
