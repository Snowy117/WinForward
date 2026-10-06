using System.Text.Json;
using WinForward.E2E.Contracts.Json;

namespace WinForward.E2E.Contracts.Metrics;

/// <summary>
/// The <c>metrics</c> object of a <c>dns</c> result record. Every property is <see langword="required"/>
/// so a new key cannot be added without the factory in the shape test failing to compile, and the write
/// order below is the declaration order of <see cref="ArmKeys.Dns"/>.
/// </summary>
/// <remarks>
/// Nothing here is conditional: a DNS arm publishes every one of these keys whatever the run managed to
/// do, and a reading with no measurement behind it is written as JSON <see langword="null"/> rather
/// than left out. <see cref="Rcodes"/> and <see cref="QueryTypes"/> are the two data-driven members --
/// one entry per observed rcode and per observed query type, both possibly empty -- so their member
/// names are data, and their key alone is declared.
/// </remarks>
public sealed record DnsMetrics : IJsonWritable
{
    /// <summary>Queries written to a socket by both halves.</summary>
    public required long Sent { get; init; }

    /// <summary>Queries the UDP half wrote.</summary>
    public required long UdpSent { get; init; }

    /// <summary>Queries the TCP half wrote.</summary>
    public required long TcpSent { get; init; }

    /// <summary>Pacing slots skipped because the in-flight window was full, both halves.</summary>
    public required long Unsent { get; init; }

    /// <summary>UDP pacing slots skipped because the in-flight window was full.</summary>
    public required long UdpUnsent { get; init; }

    /// <summary>TCP pacing slots skipped because the in-flight window was full.</summary>
    public required long TcpUnsent { get; init; }

    /// <summary>Queries the arm offered: sent plus unsent.</summary>
    public required long Offered { get; init; }

    /// <summary>Queries answered with rcode 0.</summary>
    public required long Answered { get; init; }

    /// <summary>Queries answered with a non-zero rcode.</summary>
    public required long Servfail { get; init; }

    /// <summary>Queries still pending when the drain window closed.</summary>
    public required long Timeout { get; init; }

    /// <summary>TCP only: responses that consumed a queued query under a different transaction id.</summary>
    public required long Other { get; init; }

    /// <summary>Queries with no answer of their own: timeout plus other.</summary>
    public required long Unanswered { get; init; }

    /// <summary>Socket-level failures (connect, send, receive) that belong to no single query.</summary>
    public required long SocketErrors { get; init; }

    /// <summary>Answers with rcode 0 that carried no record.</summary>
    public required long EmptyAnswers { get; init; }

    /// <summary>Responses that did not parse, and queries that could not be built.</summary>
    public required long Malformed { get; init; }

    /// <summary>Valid responses that matched no outstanding query.</summary>
    public required long Unmatched { get; init; }

    /// <summary>Answered over sent; <see langword="null"/> when nothing was sent.</summary>
    public required double? AnswerRate { get; init; }

    /// <summary>Sent per elapsed second; <see langword="null"/> when no time passed.</summary>
    public required double? AchievedRate { get; init; }

    /// <summary>One entry per observed rcode, named by its decimal value; may be empty.</summary>
    public required IReadOnlyDictionary<string, long> Rcodes { get; init; }

    /// <summary>One entry per observed query type; may be empty.</summary>
    public required IReadOnlyDictionary<string, long> QueryTypes { get; init; }

    /// <inheritdoc/>
    public void WriteTo(Utf8JsonWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.WriteStartObject();
        writer.WriteNumber(ArmKeys.Dns.Sent, Sent);
        writer.WriteNumber(ArmKeys.Dns.UdpSent, UdpSent);
        writer.WriteNumber(ArmKeys.Dns.TcpSent, TcpSent);
        writer.WriteNumber(ArmKeys.Dns.Unsent, Unsent);
        writer.WriteNumber(ArmKeys.Dns.UdpUnsent, UdpUnsent);
        writer.WriteNumber(ArmKeys.Dns.TcpUnsent, TcpUnsent);
        writer.WriteNumber(ArmKeys.Dns.Offered, Offered);
        writer.WriteNumber(ArmKeys.Dns.Answered, Answered);
        writer.WriteNumber(ArmKeys.Dns.Servfail, Servfail);
        writer.WriteNumber(ArmKeys.Dns.Timeout, Timeout);
        writer.WriteNumber(ArmKeys.Dns.Other, Other);
        writer.WriteNumber(ArmKeys.Dns.Unanswered, Unanswered);
        writer.WriteNumber(ArmKeys.Dns.SocketErrors, SocketErrors);
        writer.WriteNumber(ArmKeys.Dns.EmptyAnswers, EmptyAnswers);
        writer.WriteNumber(ArmKeys.Dns.Malformed, Malformed);
        writer.WriteNumber(ArmKeys.Dns.Unmatched, Unmatched);
        Reading.Write(writer, ArmKeys.Dns.AnswerRate, AnswerRate);
        Reading.Write(writer, ArmKeys.Dns.AchievedRate, AchievedRate);
        WriteCounts(writer, ArmKeys.Dns.Rcodes, Rcodes);
        WriteCounts(writer, ArmKeys.Dns.QueryTypes, QueryTypes);
        writer.WriteEndObject();
    }

    /// <summary>
    /// Writes a data-driven object: its member names come from the run, so the object is written in the
    /// order its producer observed them and an empty one still publishes its braces.
    /// </summary>
    private static void WriteCounts(Utf8JsonWriter writer, string name, IReadOnlyDictionary<string, long> counts)
    {
        writer.WriteStartObject(name);
        foreach (var pair in counts)
        {
            writer.WriteNumber(pair.Key, pair.Value);
        }

        writer.WriteEndObject();
    }
}
