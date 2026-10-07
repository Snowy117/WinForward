using System.Text.Json;
using WinForward.E2E.Contracts.Json;

namespace WinForward.E2E.Contracts;

/// <summary>
/// The <c>parameters</c> object of a result record, and of the armSummary record written beside it:
/// what the arm actually ran with, after its own defaults were applied.
/// </summary>
/// <remarks>
/// <para><b>Every property is nullable, and a null one is not written at all.</b> This is the one
/// shape rule the object has, and it is the opposite of the metrics rule: metrics publish a key for
/// every reading and put JSON <see langword="null"/> in an unknown one, while a parameter that does
/// not apply to the kind is simply absent. The members a kind publishes are therefore exactly the
/// members the arm set, and the shape test asserts that set against the same declaration it uses for
/// the arm's metrics.</para>
/// <para><b>Nesting is real, not dotted.</b> <see cref="Latency"/> and <see cref="Loss"/> are objects
/// of their own, and their members are the phase arms' own parameters one level down: the control
/// writes the child outcome's object rather than a copy of its keys, so the phase and the standalone
/// arm can never disagree about what they ran with.</para>
/// <para><b>Write order</b> is the declaration order of <see cref="ArmKeys.Common.Parameters"/>, so
/// the object reads the same whatever order an arm happened to fill its fields in.</para>
/// </remarks>
public sealed record ArmParameters : IJsonWritable
{
    /// <summary>Wall time the arm was asked to run for.</summary>
    public double? Seconds { get; init; }

    /// <summary>Requests per second the arm paced to.</summary>
    public int? RatePerSecond { get; init; }

    /// <summary>Payload bytes per request.</summary>
    public int? PayloadBytes { get; init; }

    /// <summary>The protocol the latency arm ran: tcp, udp or tcp+udp.</summary>
    public string? Protocol { get; init; }

    /// <summary>TCP lanes the latency arm ran.</summary>
    public int? Lanes { get; init; }

    /// <summary>Requests per lane the latency arm keeps in flight; not the loss arm's window.</summary>
    public int? InFlightWindow { get; init; }

    /// <summary>The traffic an idle arm generated, as a word rather than a number.</summary>
    public string? Traffic { get; init; }

    /// <summary>Streams the throughput arm ran.</summary>
    public int? Streams { get; init; }

    /// <summary>Aggregate send rate the throughput arm paced to.</summary>
    public long? TargetBytesPerSecond { get; init; }

    /// <summary>Frame payload the throughput and reliability arms used.</summary>
    public int? FramePayloadBytes { get; init; }

    /// <summary>The declared loss window W, in milliseconds.</summary>
    public int? LossWindowMs { get; init; }

    /// <summary>Desktops the mix arm ran.</summary>
    public int? Desktops { get; init; }

    /// <summary>Seconds between page fetches in the mix arm.</summary>
    public double? PageIntervalSeconds { get; init; }

    /// <summary>Connections one page fetch opens.</summary>
    public int? PageConnections { get; init; }

    /// <summary>Requests one page fetch issues.</summary>
    public int? PageRequestsTotal { get; init; }

    /// <summary>Response bytes one page fetch yields.</summary>
    public int? PageMessageBytes { get; init; }

    /// <summary>Bulk bits per second per desktop.</summary>
    public long? BulkBitsPerSecondPerDesktop { get; init; }

    /// <summary>DNS queries one page fetch issues.</summary>
    public int? DnsQueriesPerPage { get; init; }

    /// <summary>UDP datagrams per second per desktop.</summary>
    public int? UdpPacketsPerSecondPerDesktop { get; init; }

    /// <summary>Payload bytes per UDP datagram.</summary>
    public int? UdpPayloadBytes { get; init; }

    /// <summary>Share of DNS queries the arm sent over TCP.</summary>
    public int? TcpPercent { get; init; }

    /// <summary>Every n-th query is a CNAME query; zero means none were.</summary>
    public int? CnameEvery { get; init; }

    /// <summary>The DNS port the arm used.</summary>
    public int? DnsPort { get; init; }

    /// <summary>How long the DNS arm kept reading after its send phase ended.</summary>
    public int? DrainWindowMs { get; init; }

    /// <summary>Connections per second the reliability arm paced to.</summary>
    public int? ConnectionsPerSecond { get; init; }

    /// <summary>The mode mix the reliability arm ran.</summary>
    public string? ModeMix { get; init; }

    /// <summary>Echo bytes the reliability and persistent arms requested.</summary>
    public int? ExpectedBytes { get; init; }

    /// <summary>Pacing interval of the persistent arm.</summary>
    public int? IntervalMs { get; init; }

    /// <summary>Length of the idle window the persistent arm opened.</summary>
    public int? IdleSeconds { get; init; }

    /// <summary>How long the persistent arm waited for an echo.</summary>
    public int? ResponseTimeoutMs { get; init; }

    /// <summary>Wall time one phase of the control ran for.</summary>
    public double? PhaseSeconds { get; init; }

    /// <summary>The phases the control ran, in run order.</summary>
    public IReadOnlyList<string>? Phases { get; init; }

    /// <summary>The latency phase's own parameters, one level down.</summary>
    public ArmParameters? Latency { get; init; }

    /// <summary>The loss phase's own parameters, one level down.</summary>
    public ArmParameters? Loss { get; init; }

    /// <inheritdoc/>
    public void WriteTo(Utf8JsonWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.WriteStartObject();
        WriteNumber(writer, ArmKeys.Common.Parameters.Seconds, Seconds);
        WriteNumber(writer, ArmKeys.Common.Parameters.RatePerSecond, RatePerSecond);
        WriteNumber(writer, ArmKeys.Common.Parameters.PayloadBytes, PayloadBytes);
        WriteText(writer, ArmKeys.Common.Parameters.Protocol, Protocol);
        WriteNumber(writer, ArmKeys.Common.Parameters.Lanes, Lanes);
        WriteNumber(writer, ArmKeys.Common.Parameters.InFlightWindow, InFlightWindow);
        WriteText(writer, ArmKeys.Common.Parameters.Traffic, Traffic);
        WriteNumber(writer, ArmKeys.Common.Parameters.Streams, Streams);
        WriteNumber(writer, ArmKeys.Common.Parameters.TargetBytesPerSecond, TargetBytesPerSecond);
        WriteNumber(writer, ArmKeys.Common.Parameters.FramePayloadBytes, FramePayloadBytes);
        WriteNumber(writer, ArmKeys.Common.Parameters.LossWindowMs, LossWindowMs);
        WriteNumber(writer, ArmKeys.Common.Parameters.Desktops, Desktops);
        WriteNumber(writer, ArmKeys.Common.Parameters.PageIntervalSeconds, PageIntervalSeconds);
        WriteNumber(writer, ArmKeys.Common.Parameters.PageConnections, PageConnections);
        WriteNumber(writer, ArmKeys.Common.Parameters.PageRequestsTotal, PageRequestsTotal);
        WriteNumber(writer, ArmKeys.Common.Parameters.PageMessageBytes, PageMessageBytes);
        WriteNumber(writer, ArmKeys.Common.Parameters.BulkBitsPerSecondPerDesktop, BulkBitsPerSecondPerDesktop);
        WriteNumber(writer, ArmKeys.Common.Parameters.DnsQueriesPerPage, DnsQueriesPerPage);
        WriteNumber(writer, ArmKeys.Common.Parameters.UdpPacketsPerSecondPerDesktop, UdpPacketsPerSecondPerDesktop);
        WriteNumber(writer, ArmKeys.Common.Parameters.UdpPayloadBytes, UdpPayloadBytes);
        WriteNumber(writer, ArmKeys.Common.Parameters.TcpPercent, TcpPercent);
        WriteNumber(writer, ArmKeys.Common.Parameters.CnameEvery, CnameEvery);
        WriteNumber(writer, ArmKeys.Common.Parameters.DnsPort, DnsPort);
        WriteNumber(writer, ArmKeys.Common.Parameters.DrainWindowMs, DrainWindowMs);
        WriteNumber(writer, ArmKeys.Common.Parameters.ConnectionsPerSecond, ConnectionsPerSecond);
        WriteText(writer, ArmKeys.Common.Parameters.ModeMix, ModeMix);
        WriteNumber(writer, ArmKeys.Common.Parameters.ExpectedBytes, ExpectedBytes);
        WriteNumber(writer, ArmKeys.Common.Parameters.IntervalMs, IntervalMs);
        WriteNumber(writer, ArmKeys.Common.Parameters.IdleSeconds, IdleSeconds);
        WriteNumber(writer, ArmKeys.Common.Parameters.ResponseTimeoutMs, ResponseTimeoutMs);
        WriteNumber(writer, ArmKeys.Common.Parameters.PhaseSeconds, PhaseSeconds);
        WriteList(writer, ArmKeys.Common.Parameters.Phases, Phases);
        WriteObject(writer, ArmKeys.Common.Parameters.Latency, Latency);
        WriteObject(writer, ArmKeys.Common.Parameters.Loss, Loss);
        writer.WriteEndObject();
    }

    private static void WriteNumber(Utf8JsonWriter writer, string name, double? value)
    {
        if (value is { } number)
        {
            writer.WriteNumber(name, number);
        }
    }

    private static void WriteNumber(Utf8JsonWriter writer, string name, int? value)
    {
        if (value is { } number)
        {
            writer.WriteNumber(name, number);
        }
    }

    private static void WriteNumber(Utf8JsonWriter writer, string name, long? value)
    {
        if (value is { } number)
        {
            writer.WriteNumber(name, number);
        }
    }

    private static void WriteText(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is not null)
        {
            writer.WriteString(name, value);
        }
    }

    private static void WriteList(Utf8JsonWriter writer, string name, IReadOnlyList<string>? value)
    {
        if (value is null)
        {
            return;
        }

        writer.WriteStartArray(name);
        foreach (var item in value)
        {
            writer.WriteStringValue(item);
        }

        writer.WriteEndArray();
    }

    private static void WriteObject(Utf8JsonWriter writer, string name, ArmParameters? value)
    {
        if (value is null)
        {
            return;
        }

        writer.WritePropertyName(name);
        value.WriteTo(writer);
    }
}
