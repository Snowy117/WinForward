namespace WinForward.E2E.Contracts;

/// <summary>
/// The <c>metrics</c> object of a <c>"base"</c> result record: one elapsed time, then the metrics of
/// each phase the control ran, back to back inside one record.
/// </summary>
/// <remarks>
/// <para><b>Two nested blocks, one level of real nesting.</b> <c>latency</c> and <c>loss</c> are
/// objects of their own, so the latency phase's keys are the latency kind's own member names one
/// level down (<c>metrics/latency/tcp.sent</c>): the phase value is the shared
/// <see cref="Metrics.LatencyMetrics"/> record, whose writer references <see cref="ArmKeys.Latency"/>.
/// The loss phase's members are declared here instead, because no typed record owns them yet: this
/// phase-level copy is what <c>ArmKeys.Loss</c> replaces in B2c, when the loss arm's metrics become a
/// typed record and both records compose the same shard.</para>
/// <para><b>Conditional fields.</b> The loss phase publishes every key of
/// <see cref="Control.LossPhase"/> on every run. The latency phase publishes no <c>tcp.*</c> key at all when
/// the control's plan declares a UDP-only protocol, and no <c>udp.*</c> key when it declares
/// TCP-only: the omission is a whole protocol block and only ever means "the phase did not run that
/// protocol", never "the value is unknown" (unknown is a written JSON <see langword="null"/>). The
/// two container keys <see cref="Control.Latency"/> and <see cref="Control.Loss"/> are always present, even when a
/// block inside them is not.</para>
/// <para>The record's <c>gates</c> keys are shared and live in <see cref="ArmKeys.Common.Gates"/>; its
/// <c>parameters</c> keys in <see cref="ArmKeys.Common.Parameters"/>, where
/// <see cref="ArmKeys.Common.Parameters.Latency"/> and <see cref="ArmKeys.Common.Parameters.Loss"/>
/// declare the two phase objects' names under <c>parameters</c>.</para>
/// </remarks>
public static partial class ArmKeys
{
    /// <summary>Keys under <c>metrics</c> for the <c>"base"</c> kind.</summary>
    public static class Control
    {
        /// <summary>Wall time the two phases took together.</summary>
        public const string ElapsedSeconds = "elapsedSeconds";

        // S3218 / MemberHidesStaticFromOuterClass: the published member name is `latency`, and
        // `ArmKeys.Latency` is the latency arm's own shard. The rule this file follows -- a member's
        // name is its key's name -- makes the collision the contract rather than an accident; the
        // same shadowing is already declared and justified on ArmKeys.Common.Parameters.Latency.
#pragma warning disable S3218
        // ReSharper disable once MemberHidesStaticFromOuterClass
        public const string Latency = "latency";
#pragma warning restore S3218

        // The loss phase's container key. ArmKeys.Loss (B2c) will shadow it for the same reason
        // ArmKeys.Latency shadows the constant above, and will need the same pair of suppressions.
        /// <summary>The loss phase's container key.</summary>
        public const string Loss = "loss";

        /// <summary>
        /// The members of the <c>loss</c> phase, in write order: the loss arm's own metrics, one level
        /// down, which is why they are declared here rather than beside the phase that publishes them.
        /// </summary>
        /// <remarks>
        /// Nothing here is conditional. The eight nullable readings (<see cref="LossRate"/>,
        /// <see cref="StrictLossRate"/>, <see cref="LateRate"/>, <see cref="CorruptRate"/>,
        /// <see cref="DuplicateRate"/>, <see cref="ReorderRate"/>, <see cref="ClientSendLossRate"/>
        /// and <see cref="AchievedRate"/>) publish JSON <see langword="null"/> when their population
        /// or duration was empty, and keep their key.
        /// </remarks>
        public static class LossPhase
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

            /// <summary>Sequences the tracker refused as outside its bounded space.</summary>
            public const string OutOfRangeSequences = "outOfRangeSequences";

            /// <summary>Sent datagrams per elapsed second; null when no time passed.</summary>
            public const string AchievedRate = "achievedRate";
        }
    }
}
