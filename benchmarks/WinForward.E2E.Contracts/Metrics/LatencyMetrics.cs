using System.Text.Json;
using WinForward.E2E.Contracts.Json;

namespace WinForward.E2E.Contracts.Metrics;

/// <summary>
/// The <c>metrics</c> object of a <c>latency</c> result record: one block per protocol the arm ran.
/// Every property is <see langword="required"/> so a new key cannot be added without the factory in
/// the shape test failing to compile, and the write order below is the declaration order of
/// <see cref="ArmKeys.Latency"/>.
/// </summary>
/// <remarks>
/// The two blocks are flat, dotted members of this object rather than nested objects, so a block
/// writes its own members into the object this value opens instead of opening one of its own. A block
/// the arm did not run is <see langword="null"/> and publishes no key at all: the whole
/// <c>tcp.*</c>/<c>udp.*</c> family is conditional on the arm's protocol, while a reading inside a
/// published block is never omitted (it publishes JSON <see langword="null"/> when unknown).
/// </remarks>
public sealed record LatencyMetrics : IJsonWritable
{
    /// <summary>The <c>tcp.*</c> block, or <see langword="null"/> when the arm ran no TCP.</summary>
    public required LatencyTcpMetrics? Tcp { get; init; }

    /// <summary>The <c>udp.*</c> block, or <see langword="null"/> when the arm ran no UDP.</summary>
    public required LatencyUdpMetrics? Udp { get; init; }

    /// <inheritdoc/>
    public void WriteTo(Utf8JsonWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.WriteStartObject();
        Tcp?.WriteMembers(writer);
        Udp?.WriteMembers(writer);
        writer.WriteEndObject();
    }
}

/// <summary>
/// The <c>tcp.*</c> members of a <c>latency</c> record, in write order: the counters of every TCP lane,
/// the connect probe, and the totals the arm derived from them after the lanes joined.
/// </summary>
/// <remarks>
/// The two per-lane arrays hold one element per lane in lane order, so their length is the arm's lane
/// count; a lane that never ran still has an element, which is what tells "offered nothing" apart from
/// "did not run". <see cref="AchievedRate"/> and <see cref="MeanConnectMs"/> publish JSON
/// <see langword="null"/> when their population was empty.
/// </remarks>
public sealed record LatencyTcpMetrics
{
    /// <summary>0 or 1: whether the lane body itself ran, summed across lanes.</summary>
    public required long LaneStarted { get; init; }

    /// <summary>Requests offered per lane, in lane order; one element per lane.</summary>
    public required IReadOnlyList<long> LaneSupplied { get; init; }

    /// <summary>Requests the kernel accepted per lane, in lane order; one element per lane.</summary>
    public required IReadOnlyList<long> LaneSentOk { get; init; }

    /// <summary>Requests the lanes offered.</summary>
    public required long Supplied { get; init; }

    /// <summary>Requests the lanes sent.</summary>
    public required long Sent { get; init; }

    /// <summary>Sends the kernel did not accept synchronously; those sends still complete.</summary>
    public required long SendWouldBlock { get; init; }

    /// <summary>Requests offered at a full in-flight window and therefore deferred.</summary>
    public required long WindowOverflow { get; init; }

    /// <summary>Deferred requests the bounded queue discarded.</summary>
    public required long BacklogDrops { get; init; }

    /// <summary>Individual sends that threw; each costs one sample, not the schedule.</summary>
    public required long SendFailures { get; init; }

    /// <summary>Requests sent but neither answered, failed nor dropped when the lanes stopped.</summary>
    public required long AbandonedAtTeardown { get; init; }

    /// <summary>Requests that reached no histogram: supplied minus sent.</summary>
    public required long ClientSendLoss { get; init; }

    /// <summary>Valid frames received, duplicates included.</summary>
    public required long Received { get; init; }

    /// <summary>Requests still unanswered, plus deferred requests that never got a slot.</summary>
    public required long OutstandingAtTeardown { get; init; }

    /// <summary>Frames that failed their checksum.</summary>
    public required long Corrupt { get; init; }

    /// <summary>Frames that decoded to something other than a valid frame, or a fatal socket error.</summary>
    public required long ProtocolErrors { get; init; }

    /// <summary>Lanes whose peer closed the stream first.</summary>
    public required long RemoteClosed { get; init; }

    /// <summary>Valid frames that matched no outstanding request.</summary>
    public required long UnmatchedReplies { get; init; }

    /// <summary>
    /// Ceiling on directly measurable latency in milliseconds, or 0 when it is undefined: this is a
    /// floor on a measurable latency rather than a rate, so it does not take the null convention.
    /// </summary>
    public required double WindowCeilingMs { get; init; }

    /// <summary>1 when a lane never connected or its offer loop ended before its deadline.</summary>
    public required long ScheduleTruncated { get; init; }

    /// <summary>Achieved requests per second; <see langword="null"/> when no time passed.</summary>
    public required double? AchievedRate { get; init; }

    /// <summary>Connects attempted: the per-lane connects plus the 1 Hz probe.</summary>
    public required long ConnectAttempts { get; init; }

    /// <summary>Connects in that same population that failed.</summary>
    public required long ConnectFailures { get; init; }

    /// <summary>Mean connect duration in milliseconds; <see langword="null"/> when none succeeded.</summary>
    public required double? MeanConnectMs { get; init; }

    /// <summary>
    /// Writes the block's members into the object the caller has open: dotted members under
    /// <c>metrics</c>, so the block never writes braces of its own.
    /// </summary>
    internal void WriteMembers(Utf8JsonWriter writer)
    {
        writer.WriteNumber(ArmKeys.Latency.TcpLaneStarted, LaneStarted);
        writer.WriteStartArray(ArmKeys.Latency.TcpLaneSupplied);
        foreach (var count in LaneSupplied)
        {
            writer.WriteNumberValue(count);
        }

        writer.WriteEndArray();
        writer.WriteStartArray(ArmKeys.Latency.TcpLaneSentOk);
        foreach (var count in LaneSentOk)
        {
            writer.WriteNumberValue(count);
        }

        writer.WriteEndArray();
        writer.WriteNumber(ArmKeys.Latency.TcpSupplied, Supplied);
        writer.WriteNumber(ArmKeys.Latency.TcpSent, Sent);
        writer.WriteNumber(ArmKeys.Latency.TcpSendWouldBlock, SendWouldBlock);
        writer.WriteNumber(ArmKeys.Latency.TcpWindowOverflow, WindowOverflow);
        writer.WriteNumber(ArmKeys.Latency.TcpBacklogDrops, BacklogDrops);
        writer.WriteNumber(ArmKeys.Latency.TcpSendFailures, SendFailures);
        writer.WriteNumber(ArmKeys.Latency.TcpAbandonedAtTeardown, AbandonedAtTeardown);
        writer.WriteNumber(ArmKeys.Latency.TcpClientSendLoss, ClientSendLoss);
        writer.WriteNumber(ArmKeys.Latency.TcpReceived, Received);
        writer.WriteNumber(ArmKeys.Latency.TcpOutstandingAtTeardown, OutstandingAtTeardown);
        writer.WriteNumber(ArmKeys.Latency.TcpCorrupt, Corrupt);
        writer.WriteNumber(ArmKeys.Latency.TcpProtocolErrors, ProtocolErrors);
        writer.WriteNumber(ArmKeys.Latency.TcpRemoteClosed, RemoteClosed);
        writer.WriteNumber(ArmKeys.Latency.TcpUnmatchedReplies, UnmatchedReplies);
        Reading.Write(writer, ArmKeys.Latency.TcpWindowCeilingMs, WindowCeilingMs);
        writer.WriteNumber(ArmKeys.Latency.TcpScheduleTruncated, ScheduleTruncated);
        Reading.Write(writer, ArmKeys.Latency.TcpAchievedRate, AchievedRate);
        writer.WriteNumber(ArmKeys.Latency.TcpConnectAttempts, ConnectAttempts);
        writer.WriteNumber(ArmKeys.Latency.TcpConnectFailures, ConnectFailures);
        Reading.Write(writer, ArmKeys.Latency.TcpMeanConnectMs, MeanConnectMs);
    }
}

/// <summary>
/// The <c>udp.*</c> members of a <c>latency</c> record, in write order. The UDP half is a single lane,
/// so where the TCP block publishes a per-lane array this one publishes the scalar
/// <see cref="LaneStarted"/>.
/// </summary>
/// <remarks>
/// <see cref="LossRate"/> is <see langword="null"/> when nothing was sent, which is an empty
/// population rather than a loss of zero, and <see cref="AchievedRate"/> is <see langword="null"/>
/// when no time passed.
/// </remarks>
public sealed record LatencyUdpMetrics
{
    /// <summary>0 or 1: whether the lane body itself ran.</summary>
    public required long LaneStarted { get; init; }

    /// <summary>Requests offered.</summary>
    public required long Supplied { get; init; }

    /// <summary>Requests sent.</summary>
    public required long Sent { get; init; }

    /// <summary>Sends the kernel did not accept synchronously; those sends still complete.</summary>
    public required long SendWouldBlock { get; init; }

    /// <summary>Requests offered at a full in-flight window and therefore deferred.</summary>
    public required long WindowOverflow { get; init; }

    /// <summary>Deferred requests the bounded queue discarded.</summary>
    public required long BacklogDrops { get; init; }

    /// <summary>Individual sends that threw; each costs one sample, not the schedule.</summary>
    public required long SendFailures { get; init; }

    /// <summary>Requests sent but neither answered, failed nor dropped when the lane stopped.</summary>
    public required long AbandonedAtTeardown { get; init; }

    /// <summary>Requests that reached no histogram: supplied minus sent.</summary>
    public required long ClientSendLoss { get; init; }

    /// <summary>Valid frames received, duplicates included.</summary>
    public required long Received { get; init; }

    /// <summary>Frames that decoded to something other than a valid frame, or failed their filler.</summary>
    public required long Corrupt { get; init; }

    /// <summary>Receive-loop socket errors.</summary>
    public required long ProtocolErrors { get; init; }

    /// <summary>Valid frames that matched no outstanding request.</summary>
    public required long UnmatchedReplies { get; init; }

    /// <summary>Replies bearing a connection id this socket never used.</summary>
    public required long ForeignConnection { get; init; }

    /// <summary>Requests still unanswered, plus deferred requests that never got a slot.</summary>
    public required long OutstandingAtTeardown { get; init; }

    /// <summary>
    /// Ceiling on directly measurable latency in milliseconds, or 0 when it is undefined: this is a
    /// floor on a measurable latency rather than a rate, so it does not take the null convention.
    /// </summary>
    public required double WindowCeilingMs { get; init; }

    /// <summary>1 when the offer loop ended before its deadline.</summary>
    public required long ScheduleTruncated { get; init; }

    /// <summary>Sent minus matched arrivals over sent; <see langword="null"/> when nothing was sent.</summary>
    public required double? LossRate { get; init; }

    /// <summary>Achieved requests per second; <see langword="null"/> when no time passed.</summary>
    public required double? AchievedRate { get; init; }

    /// <summary>
    /// Writes the block's members into the object the caller has open: dotted members under
    /// <c>metrics</c>, so the block never writes braces of its own.
    /// </summary>
    internal void WriteMembers(Utf8JsonWriter writer)
    {
        writer.WriteNumber(ArmKeys.Latency.UdpLaneStarted, LaneStarted);
        writer.WriteNumber(ArmKeys.Latency.UdpSupplied, Supplied);
        writer.WriteNumber(ArmKeys.Latency.UdpSent, Sent);
        writer.WriteNumber(ArmKeys.Latency.UdpSendWouldBlock, SendWouldBlock);
        writer.WriteNumber(ArmKeys.Latency.UdpWindowOverflow, WindowOverflow);
        writer.WriteNumber(ArmKeys.Latency.UdpBacklogDrops, BacklogDrops);
        writer.WriteNumber(ArmKeys.Latency.UdpSendFailures, SendFailures);
        writer.WriteNumber(ArmKeys.Latency.UdpAbandonedAtTeardown, AbandonedAtTeardown);
        writer.WriteNumber(ArmKeys.Latency.UdpClientSendLoss, ClientSendLoss);
        writer.WriteNumber(ArmKeys.Latency.UdpReceived, Received);
        writer.WriteNumber(ArmKeys.Latency.UdpCorrupt, Corrupt);
        writer.WriteNumber(ArmKeys.Latency.UdpProtocolErrors, ProtocolErrors);
        writer.WriteNumber(ArmKeys.Latency.UdpUnmatchedReplies, UnmatchedReplies);
        writer.WriteNumber(ArmKeys.Latency.UdpForeignConnection, ForeignConnection);
        writer.WriteNumber(ArmKeys.Latency.UdpOutstandingAtTeardown, OutstandingAtTeardown);
        Reading.Write(writer, ArmKeys.Latency.UdpWindowCeilingMs, WindowCeilingMs);
        writer.WriteNumber(ArmKeys.Latency.UdpScheduleTruncated, ScheduleTruncated);
        Reading.Write(writer, ArmKeys.Latency.UdpLossRate, LossRate);
        Reading.Write(writer, ArmKeys.Latency.UdpAchievedRate, AchievedRate);
    }
}
