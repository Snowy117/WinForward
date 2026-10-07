using System.Text.Json;
using WinForward.E2E.Contracts.Json;

namespace WinForward.E2E.Contracts.Metrics;

/// <summary>
/// The <c>metrics</c> object of a <c>mix</c> result record: the four flow-class blocks, the arm-wide
/// counters folded from them, and one lane witness per desktop. Every property is
/// <see langword="required"/> so a new key cannot be added without the factory in the shape test
/// failing to compile, and the write order below is the declaration order of <see cref="ArmKeys.Mix"/>.
/// </summary>
/// <remarks>
/// <see cref="Classes"/> and <see cref="Desktops"/> are the two members that arrive as an object and
/// an array of their own rather than as dotted members: the class blocks keep the same numbers the
/// per-desktop lanes are classified into, so a class total and its witnesses are two views of one
/// classification pass. Nothing here is conditional, and the only shape that follows the plan is the
/// arity of <see cref="Desktops"/> and of <see cref="MixUdpClassMetrics.SentPerDesktop"/>: one element
/// per desktop, in desktop order.
/// </remarks>
public sealed record MixMetrics : IJsonWritable
{
    /// <summary>The <c>classes</c> object: one block per flow class.</summary>
    public required MixClassesMetrics Classes { get; init; }

    /// <summary>Page fetches the desktops completed.</summary>
    public required long Pages { get; init; }

    /// <summary>Page connections the desktops established.</summary>
    public required long PageConnections { get; init; }

    /// <summary>Request bytes the page class wrote.</summary>
    public required long PageBytes { get; init; }

    /// <summary>Frame bytes the bulk class read back.</summary>
    public required long BulkBytes { get; init; }

    /// <summary>DNS queries the desktops wrote.</summary>
    public required long DnsSent { get; init; }

    /// <summary>Datagrams the desktops sent.</summary>
    public required long UdpSent { get; init; }

    /// <summary>Path loss over sent datagrams; <see langword="null"/> when nothing was sent.</summary>
    public required double? UdpLossRate { get; init; }

    /// <summary>Sent datagrams that were refused, deferred or abandoned.</summary>
    public required long ClientSendLoss { get; init; }

    /// <summary>The per-desktop lane witnesses, in desktop order; arity is the desktop count.</summary>
    public required IReadOnlyList<MixDesktopMetrics> Desktops { get; init; }

    /// <inheritdoc/>
    public void WriteTo(Utf8JsonWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.WriteStartObject();
        writer.WritePropertyName(ArmKeys.Mix.Classes);
        Classes.WriteTo(writer);
        writer.WriteNumber(ArmKeys.Mix.Pages, Pages);
        writer.WriteNumber(ArmKeys.Mix.PageConnections, PageConnections);
        writer.WriteNumber(ArmKeys.Mix.PageBytes, PageBytes);
        writer.WriteNumber(ArmKeys.Mix.BulkBytes, BulkBytes);
        writer.WriteNumber(ArmKeys.Mix.DnsSent, DnsSent);
        writer.WriteNumber(ArmKeys.Mix.UdpSent, UdpSent);
        Reading.Write(writer, ArmKeys.Mix.UdpLossRate, UdpLossRate);
        writer.WriteNumber(ArmKeys.Mix.ClientSendLoss, ClientSendLoss);
        writer.WriteStartArray(ArmKeys.Mix.Desktops);
        foreach (var desktop in Desktops)
        {
            desktop.WriteTo(writer);
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }
}

/// <summary>
/// The <c>classes</c> object: one block per flow class the arm drives, in write order. Each member is
/// a JSON object of its own, unlike the latency kind's dotted protocol families.
/// </summary>
/// <remarks>
/// The four blocks are always present, so a class that measured nothing publishes its zeros instead of
/// disappearing. <see cref="MixPageClassMetrics.BytesPerPage"/> is the only nullable reading in this
/// object: it is null when no page completed, which is an empty population rather than a page of zero
/// bytes.
/// </remarks>
public sealed record MixClassesMetrics : IJsonWritable
{
    /// <summary>The page class.</summary>
    public required MixPageClassMetrics Page { get; init; }

    /// <summary>The bulk-transfer class.</summary>
    public required MixBulkClassMetrics Bulk { get; init; }

    /// <summary>The DNS class.</summary>
    public required MixDnsClassMetrics Dns { get; init; }

    /// <summary>The UDP reliability class.</summary>
    public required MixUdpClassMetrics Udp { get; init; }

    /// <inheritdoc/>
    public void WriteTo(Utf8JsonWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.WriteStartObject();
        writer.WritePropertyName(ArmKeys.Mix.ClassNames.Page);
        Page.WriteTo(writer);
        writer.WritePropertyName(ArmKeys.Mix.ClassNames.Bulk);
        Bulk.WriteTo(writer);
        writer.WritePropertyName(ArmKeys.Mix.ClassNames.Dns);
        Dns.WriteTo(writer);
        writer.WritePropertyName(ArmKeys.Mix.ClassNames.Udp);
        Udp.WriteTo(writer);
        writer.WriteEndObject();
    }
}

/// <summary>The <c>classes.page</c> block: the page-fetch class, in write order.</summary>
public sealed record MixPageClassMetrics : IJsonWritable
{
    /// <summary>Page fetches the desktops completed.</summary>
    public required long Pages { get; init; }

    /// <summary>Page connections established.</summary>
    public required long Connections { get; init; }

    /// <summary>Page responses read back.</summary>
    public required long Messages { get; init; }

    /// <summary>Request bytes written.</summary>
    public required long Bytes { get; init; }

    /// <summary>Connections that failed, and reads that ended before their response.</summary>
    public required long Errors { get; init; }

    /// <summary>Bytes over pages; <see langword="null"/> when no page completed.</summary>
    public required long? BytesPerPage { get; init; }

    /// <inheritdoc/>
    public void WriteTo(Utf8JsonWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.WriteStartObject();
        writer.WriteNumber(ArmKeys.Mix.PageClass.Pages, Pages);
        writer.WriteNumber(ArmKeys.Mix.PageClass.Connections, Connections);
        writer.WriteNumber(ArmKeys.Mix.PageClass.Messages, Messages);
        writer.WriteNumber(ArmKeys.Mix.PageClass.Bytes, Bytes);
        writer.WriteNumber(ArmKeys.Mix.PageClass.Errors, Errors);
        Reading.Write(writer, ArmKeys.Mix.PageClass.BytesPerPage, BytesPerPage);
        writer.WriteEndObject();
    }
}

/// <summary>The <c>classes.bulk</c> block: the bulk-transfer class, in write order.</summary>
/// <remarks>
/// <see cref="GoodputBps"/> is the one reading here without a measurement it can be null for: it is
/// written through <see cref="Reading"/> so a quotient that is not finite -- no wall time passed at
/// all -- publishes JSON <see langword="null"/> exactly as the pre-typed writer did, rather than
/// throwing at the record writer.
/// </remarks>
public sealed record MixBulkClassMetrics : IJsonWritable
{
    /// <summary>Frame bytes read back.</summary>
    public required long Bytes { get; init; }

    /// <summary>Frame bytes written.</summary>
    public required long BytesSent { get; init; }

    /// <summary>Frames round-tripped.</summary>
    public required long Frames { get; init; }

    /// <summary>Connects and reads that failed.</summary>
    public required long Errors { get; init; }

    /// <summary>Read-back bytes per elapsed second.</summary>
    public required double GoodputBps { get; init; }

    /// <inheritdoc/>
    public void WriteTo(Utf8JsonWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.WriteStartObject();
        writer.WriteNumber(ArmKeys.Mix.BulkClass.Bytes, Bytes);
        writer.WriteNumber(ArmKeys.Mix.BulkClass.BytesSent, BytesSent);
        writer.WriteNumber(ArmKeys.Mix.BulkClass.Frames, Frames);
        writer.WriteNumber(ArmKeys.Mix.BulkClass.Errors, Errors);
        Reading.Write(writer, ArmKeys.Mix.BulkClass.GoodputBps, GoodputBps);
        writer.WriteEndObject();
    }
}

/// <summary>The <c>classes.dns</c> block: the DNS class, in write order.</summary>
public sealed record MixDnsClassMetrics : IJsonWritable
{
    /// <summary>Queries written.</summary>
    public required long Sent { get; init; }

    /// <summary>Queries answered with rcode 0.</summary>
    public required long Answered { get; init; }

    /// <summary>Queries answered with a non-zero rcode.</summary>
    public required long Servfail { get; init; }

    /// <summary>Queries with no response inside the wait window.</summary>
    public required long Timeout { get; init; }

    /// <summary>Responses that did not match their query, and socket errors.</summary>
    public required long Other { get; init; }

    /// <inheritdoc/>
    public void WriteTo(Utf8JsonWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.WriteStartObject();
        writer.WriteNumber(ArmKeys.Mix.DnsClass.Sent, Sent);
        writer.WriteNumber(ArmKeys.Mix.DnsClass.Answered, Answered);
        writer.WriteNumber(ArmKeys.Mix.DnsClass.Servfail, Servfail);
        writer.WriteNumber(ArmKeys.Mix.DnsClass.Timeout, Timeout);
        writer.WriteNumber(ArmKeys.Mix.DnsClass.Other, Other);
        writer.WriteEndObject();
    }
}

/// <summary>
/// The <c>classes.udp</c> block: the UDP reliability class, in write order. Its counters are folded
/// from the per-desktop trackers, so this block and <see cref="MixMetrics.Desktops"/> are two views of
/// one classification pass.
/// </summary>
/// <remarks>
/// The classification identity holds by construction: <see cref="Arrived"/>, <see cref="Late"/>,
/// <see cref="Never"/>, <see cref="AbandonedAtTeardown"/> and <see cref="CorruptDatagrams"/> sum to
/// <see cref="Sent"/>, and <see cref="ClientSendLoss"/> is <see cref="AbandonedAtTeardown"/> plus
/// <see cref="SendFailures"/> plus <see cref="WindowOverflow"/> plus
/// <see cref="SentOutOfRangeSequences"/>, with all four terms published beside it. <see cref="LossRate"/>
/// is <see langword="null"/> when nothing was sent, and <see cref="SentPerDesktop"/> holds one element per
/// desktop in desktop order.
/// </remarks>
public sealed record MixUdpClassMetrics : IJsonWritable
{
    /// <summary>Datagrams sent.</summary>
    public required long Sent { get; init; }

    /// <summary>Datagrams classified as arrived inside their window.</summary>
    public required long Arrived { get; init; }

    /// <summary>Datagrams that arrived after their window closed.</summary>
    public required long Late { get; init; }

    /// <summary>Datagrams that never arrived.</summary>
    public required long Never { get; init; }

    /// <summary>Corrupt arrivals booked against a known sequence.</summary>
    public required long Corrupt { get; init; }

    /// <summary>Sent datagrams booked corrupt, so they are not path loss.</summary>
    public required long CorruptDatagrams { get; init; }

    /// <summary>Arrivals of a sequence already seen.</summary>
    public required long Duplicate { get; init; }

    /// <summary>Arrivals that followed the arrival of a higher sequence (RFC 4737).</summary>
    public required long Reordered { get; init; }

    /// <summary>Valid replies that matched no outstanding datagram.</summary>
    public required long UnmatchedReplies { get; init; }

    /// <summary>Replies carrying a connection id this socket never used.</summary>
    public required long ForeignConnection { get; init; }

    /// <summary>Datagrams still inside their window when the drain was cut short.</summary>
    public required long AbandonedAtTeardown { get; init; }

    /// <summary>Datagrams the socket refused.</summary>
    public required long SendFailures { get; init; }

    /// <summary>Datagrams deferred at a full in-flight window; structurally zero for this class.</summary>
    public required long WindowOverflow { get; init; }

    /// <summary>Sequences a received datagram named that the tracker refused as outside its bounded space.</summary>
    public required long OutOfRangeSequences { get; init; }

    /// <summary>Offered slots the tracker refused to send as outside its bounded space, folded into <see cref="ClientSendLoss"/>.</summary>
    public required long SentOutOfRangeSequences { get; init; }

    /// <summary>Datagram bytes written.</summary>
    public required long Bytes { get; init; }

    /// <summary>Sent datagrams that were refused, deferred or abandoned.</summary>
    public required long ClientSendLoss { get; init; }

    /// <summary>Late plus never over sent; <see langword="null"/> when nothing was sent.</summary>
    public required double? LossRate { get; init; }

    /// <summary>The declared loss window W, in milliseconds.</summary>
    public required double Window { get; init; }

    /// <summary>Sent datagrams per desktop, in desktop order; arity is the desktop count.</summary>
    public required IReadOnlyList<long> SentPerDesktop { get; init; }

    /// <inheritdoc/>
    public void WriteTo(Utf8JsonWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.WriteStartObject();
        writer.WriteNumber(ArmKeys.Mix.UdpClass.Sent, Sent);
        writer.WriteNumber(ArmKeys.Mix.UdpClass.Arrived, Arrived);
        writer.WriteNumber(ArmKeys.Mix.UdpClass.Late, Late);
        writer.WriteNumber(ArmKeys.Mix.UdpClass.Never, Never);
        writer.WriteNumber(ArmKeys.Mix.UdpClass.Corrupt, Corrupt);
        writer.WriteNumber(ArmKeys.Mix.UdpClass.CorruptDatagrams, CorruptDatagrams);
        writer.WriteNumber(ArmKeys.Mix.UdpClass.Duplicate, Duplicate);
        writer.WriteNumber(ArmKeys.Mix.UdpClass.Reordered, Reordered);
        writer.WriteNumber(ArmKeys.Mix.UdpClass.UnmatchedReplies, UnmatchedReplies);
        writer.WriteNumber(ArmKeys.Mix.UdpClass.ForeignConnection, ForeignConnection);
        writer.WriteNumber(ArmKeys.Mix.UdpClass.AbandonedAtTeardown, AbandonedAtTeardown);
        writer.WriteNumber(ArmKeys.Mix.UdpClass.SendFailures, SendFailures);
        writer.WriteNumber(ArmKeys.Mix.UdpClass.WindowOverflow, WindowOverflow);
        writer.WriteNumber(ArmKeys.Mix.UdpClass.OutOfRangeSequences, OutOfRangeSequences);
        writer.WriteNumber(ArmKeys.Mix.UdpClass.SentOutOfRangeSequences, SentOutOfRangeSequences);
        writer.WriteNumber(ArmKeys.Mix.UdpClass.Bytes, Bytes);
        writer.WriteNumber(ArmKeys.Mix.UdpClass.ClientSendLoss, ClientSendLoss);
        Reading.Write(writer, ArmKeys.Mix.UdpClass.LossRate, LossRate);
        writer.WriteNumber(ArmKeys.Mix.UdpClass.Window, Window);
        writer.WriteStartArray(ArmKeys.Mix.UdpClass.SentPerDesktop);
        foreach (var sent in SentPerDesktop)
        {
            writer.WriteNumberValue(sent);
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }
}

/// <summary>
/// One element of the <c>desktops</c> array: the witness counters of a single desktop, in write order.
/// A lane that never ran is published as its own zero rather than averaged into a total that still
/// looks plausible, which is what <c>gates.idleLanes</c> counts.
/// </summary>
/// <remarks>
/// Every element carries the same members, so the array's shape does not depend on what the desktops
/// managed to do; only its length depends on the plan.
/// </remarks>
public sealed record MixDesktopMetrics : IJsonWritable
{
    /// <summary>The desktop's index.</summary>
    public required long Desktop { get; init; }

    /// <summary>Datagrams this desktop sent.</summary>
    public required long UdpSent { get; init; }

    /// <summary>Datagrams this desktop saw arrive inside their window.</summary>
    public required long UdpArrived { get; init; }

    /// <summary>Datagrams this desktop never saw arrive.</summary>
    public required long UdpNever { get; init; }

    /// <summary>Replies this desktop saw for another flow's connection id.</summary>
    public required long UdpForeignConnection { get; init; }

    /// <summary>Page connections this desktop established.</summary>
    public required long PageConnections { get; init; }

    /// <summary>Bulk frames this desktop round-tripped.</summary>
    public required long BulkFrames { get; init; }

    /// <summary>DNS queries this desktop wrote.</summary>
    public required long DnsSent { get; init; }

    /// <inheritdoc/>
    public void WriteTo(Utf8JsonWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.WriteStartObject();
        writer.WriteNumber(ArmKeys.Mix.DesktopLane.Desktop, Desktop);
        writer.WriteNumber(ArmKeys.Mix.DesktopLane.UdpSent, UdpSent);
        writer.WriteNumber(ArmKeys.Mix.DesktopLane.UdpArrived, UdpArrived);
        writer.WriteNumber(ArmKeys.Mix.DesktopLane.UdpNever, UdpNever);
        writer.WriteNumber(ArmKeys.Mix.DesktopLane.UdpForeignConnection, UdpForeignConnection);
        writer.WriteNumber(ArmKeys.Mix.DesktopLane.PageConnections, PageConnections);
        writer.WriteNumber(ArmKeys.Mix.DesktopLane.BulkFrames, BulkFrames);
        writer.WriteNumber(ArmKeys.Mix.DesktopLane.DnsSent, DnsSent);
        writer.WriteEndObject();
    }
}
