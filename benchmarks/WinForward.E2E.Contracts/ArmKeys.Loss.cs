namespace WinForward.E2E.Contracts;

/// <summary>
/// The <c>metrics</c> object of a <c>loss</c> result record: the UDP classification of one paced
/// datagram stream, its byte and rate derivatives, and the declared loss window.
/// </summary>
/// <remarks>
/// <para><b>One shard, two records.</b> The loss arm publishes these keys directly under
/// <c>metrics</c>, and the control's loss phase publishes the very same keys one level down under
/// <c>metrics/loss</c>: the phase value is the shared <see cref="Metrics.LossMetrics"/> record,
/// whose writer references this class, so a phase key is the arm's own key and not a copy of it.
/// <see cref="ArmKeys.Control.Loss"/> declares only the container.</para>
/// <para><b>Conditional fields: none.</b> Every key here is published by every run. A rate whose
/// population or duration was empty is written as JSON <see langword="null"/> and keeps its key, so
/// a missing key never has to be read as an unknown value.</para>
/// <para>The record's <c>gates</c> keys are shared and live in <see cref="ArmKeys.Common.Gates"/>; its
/// <c>parameters</c> keys in <see cref="ArmKeys.Common.Parameters"/>.</para>
/// </remarks>
public static partial class ArmKeys
{
    /// <summary>Keys under <c>metrics</c> for the <c>loss</c> kind; also the members of the control's loss phase.</summary>
    public static class Loss
    {
        /// <summary>Datagrams sent.</summary>
        public const string Sent = "sent";

        /// <summary>Datagrams the pacer offered.</summary>
        public const string Supplied = "supplied";

        /// <summary>Datagrams classified as arrived inside their window.</summary>
        public const string Arrived = "arrived";

        /// <summary>Datagrams that arrived after their window closed.</summary>
        public const string Late = "late";

        /// <summary>Datagrams that never arrived.</summary>
        public const string Never = "never";

        /// <summary>Corrupt arrivals booked against a known sequence.</summary>
        public const string Corrupt = "corrupt";

        /// <summary>Sent datagrams booked corrupt, so they are not path loss.</summary>
        public const string CorruptDatagrams = "corruptDatagrams";

        /// <summary>Arrivals of a sequence already seen.</summary>
        public const string Duplicate = "duplicate";

        /// <summary>Arrivals that followed the arrival of a higher sequence (RFC 4737).</summary>
        public const string Reordered = "reordered";

        /// <summary>Valid replies that matched no outstanding datagram.</summary>
        public const string UnmatchedReplies = "unmatchedReplies";

        /// <summary>Replies carrying a connection id this socket never used.</summary>
        public const string ForeignConnection = "foreignConnection";

        /// <summary>Datagrams the receive loop saw at all.</summary>
        public const string ReceivedDatagrams = "receivedDatagrams";

        /// <summary>Datagram bytes the receive loop saw at all.</summary>
        public const string ReceivedBytes = "receivedBytes";

        /// <summary>Sent datagrams that were refused, deferred or abandoned.</summary>
        public const string ClientSendLoss = "clientSendLoss";

        /// <summary>Sends the kernel did not accept synchronously; those sends still complete.</summary>
        public const string SendWouldBlock = "sendWouldBlock";

        /// <summary>Individual sends that threw; each costs one datagram, not the schedule.</summary>
        public const string SendFailures = "sendFailures";

        /// <summary>Datagrams offered at a full in-flight window and therefore deferred.</summary>
        public const string WindowOverflow = "windowOverflow";

        /// <summary>Datagrams still inside their window when the drain was cut short.</summary>
        public const string AbandonedAtTeardown = "abandonedAtTeardown";

        /// <summary>The declared loss window W, in milliseconds.</summary>
        public const string Window = "window";

        /// <summary>Late plus never over sent; null when nothing was sent.</summary>
        public const string LossRate = "lossRate";

        /// <summary>Late plus never plus corrupt over sent; null when nothing was sent.</summary>
        public const string StrictLossRate = "strictLossRate";

        /// <summary>Late over sent; null when nothing was sent.</summary>
        public const string LateRate = "lateRate";

        /// <summary>Corrupt arrivals over sent; null when nothing was sent.</summary>
        public const string CorruptRate = "corruptRate";

        /// <summary>Duplicate arrivals over sent; null when nothing was sent.</summary>
        public const string DuplicateRate = "duplicateRate";

        /// <summary>Reordered arrivals over sent; null when nothing was sent.</summary>
        public const string ReorderRate = "reorderRate";

        /// <summary>Client send loss over supplied; null when nothing was offered.</summary>
        public const string ClientSendLossRate = "clientSendLossRate";

        /// <summary>Sequences a received datagram named that the tracker refused as outside its bounded space.</summary>
        public const string OutOfRangeSequences = "outOfRangeSequences";

        /// <summary>Offered slots the tracker refused to send as outside its bounded space.</summary>
        public const string SentOutOfRangeSequences = "sentOutOfRangeSequences";

        /// <summary>Sent datagrams per elapsed second; null when no time passed.</summary>
        public const string AchievedRate = "achievedRate";
    }
}
