using System.Text.Json;
using WinForward.E2E.Contracts.Json;

namespace WinForward.E2E.Contracts.Metrics;

/// <summary>
/// The <c>metrics</c> object of a <c>loss</c> result record: one datagram stream's classification,
/// its byte and rate derivatives, and the declared loss window the classification used. Every property
/// is <see langword="required"/> so a new key cannot be added without the factory in the shape test
/// failing to compile, and the write order below is the declaration order of <see cref="ArmKeys.Loss"/>.
/// </summary>
/// <remarks>
/// The same record is the control's loss phase, one level down under <c>metrics/loss</c>: the arm and
/// the phase are one value with one writer, so a key cannot exist in one of them and not the other.
/// Nothing here is conditional, and a rate whose population or duration was empty publishes JSON
/// <see langword="null"/> while keeping its key.
/// </remarks>
public sealed record LossMetrics : IJsonWritable
{
    /// <summary>Datagrams sent.</summary>
    public required long Sent { get; init; }

    /// <summary>Datagrams the pacer offered.</summary>
    public required long Supplied { get; init; }

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

    /// <summary>Datagrams the receive loop saw at all.</summary>
    public required long ReceivedDatagrams { get; init; }

    /// <summary>Datagram bytes the receive loop saw at all.</summary>
    public required long ReceivedBytes { get; init; }

    /// <summary>Sent datagrams that were refused, deferred or abandoned.</summary>
    public required long ClientSendLoss { get; init; }

    /// <summary>Sends the kernel did not accept synchronously; those sends still complete.</summary>
    public required long SendWouldBlock { get; init; }

    /// <summary>Individual sends that threw; each costs one datagram, not the schedule.</summary>
    public required long SendFailures { get; init; }

    /// <summary>Datagrams offered at a full in-flight window and therefore deferred.</summary>
    public required long WindowOverflow { get; init; }

    /// <summary>Datagrams still inside their window when the drain was cut short.</summary>
    public required long AbandonedAtTeardown { get; init; }

    /// <summary>The declared loss window W, in milliseconds.</summary>
    public required double Window { get; init; }

    /// <summary>Late plus never over sent; <see langword="null"/> when nothing was sent.</summary>
    public required double? LossRate { get; init; }

    /// <summary>Late plus never plus corrupt over sent; <see langword="null"/> when nothing was sent.</summary>
    public required double? StrictLossRate { get; init; }

    /// <summary>Late over sent; <see langword="null"/> when nothing was sent.</summary>
    public required double? LateRate { get; init; }

    /// <summary>Corrupt arrivals over sent; <see langword="null"/> when nothing was sent.</summary>
    public required double? CorruptRate { get; init; }

    /// <summary>Duplicate arrivals over sent; <see langword="null"/> when nothing was sent.</summary>
    public required double? DuplicateRate { get; init; }

    /// <summary>Reordered arrivals over sent; <see langword="null"/> when nothing was sent.</summary>
    public required double? ReorderRate { get; init; }

    /// <summary>Client send loss over supplied; <see langword="null"/> when nothing was offered.</summary>
    public required double? ClientSendLossRate { get; init; }

    /// <summary>Sequences the tracker refused as outside its bounded space.</summary>
    public required long OutOfRangeSequences { get; init; }

    /// <summary>Sent datagrams per elapsed second; <see langword="null"/> when no time passed.</summary>
    public required double? AchievedRate { get; init; }

    /// <inheritdoc/>
    public void WriteTo(Utf8JsonWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.WriteStartObject();
        writer.WriteNumber(ArmKeys.Loss.Sent, Sent);
        writer.WriteNumber(ArmKeys.Loss.Supplied, Supplied);
        writer.WriteNumber(ArmKeys.Loss.Arrived, Arrived);
        writer.WriteNumber(ArmKeys.Loss.Late, Late);
        writer.WriteNumber(ArmKeys.Loss.Never, Never);
        writer.WriteNumber(ArmKeys.Loss.Corrupt, Corrupt);
        writer.WriteNumber(ArmKeys.Loss.CorruptDatagrams, CorruptDatagrams);
        writer.WriteNumber(ArmKeys.Loss.Duplicate, Duplicate);
        writer.WriteNumber(ArmKeys.Loss.Reordered, Reordered);
        writer.WriteNumber(ArmKeys.Loss.UnmatchedReplies, UnmatchedReplies);
        writer.WriteNumber(ArmKeys.Loss.ForeignConnection, ForeignConnection);
        writer.WriteNumber(ArmKeys.Loss.ReceivedDatagrams, ReceivedDatagrams);
        writer.WriteNumber(ArmKeys.Loss.ReceivedBytes, ReceivedBytes);
        writer.WriteNumber(ArmKeys.Loss.ClientSendLoss, ClientSendLoss);
        writer.WriteNumber(ArmKeys.Loss.SendWouldBlock, SendWouldBlock);
        writer.WriteNumber(ArmKeys.Loss.SendFailures, SendFailures);
        writer.WriteNumber(ArmKeys.Loss.WindowOverflow, WindowOverflow);
        writer.WriteNumber(ArmKeys.Loss.AbandonedAtTeardown, AbandonedAtTeardown);
        writer.WriteNumber(ArmKeys.Loss.Window, Window);
        Reading.Write(writer, ArmKeys.Loss.LossRate, LossRate);
        Reading.Write(writer, ArmKeys.Loss.StrictLossRate, StrictLossRate);
        Reading.Write(writer, ArmKeys.Loss.LateRate, LateRate);
        Reading.Write(writer, ArmKeys.Loss.CorruptRate, CorruptRate);
        Reading.Write(writer, ArmKeys.Loss.DuplicateRate, DuplicateRate);
        Reading.Write(writer, ArmKeys.Loss.ReorderRate, ReorderRate);
        Reading.Write(writer, ArmKeys.Loss.ClientSendLossRate, ClientSendLossRate);
        writer.WriteNumber(ArmKeys.Loss.OutOfRangeSequences, OutOfRangeSequences);
        Reading.Write(writer, ArmKeys.Loss.AchievedRate, AchievedRate);
        writer.WriteEndObject();
    }
}
