namespace WinForward.E2E.Contracts;

/// <summary>
/// The <c>metrics</c> object of a <c>mix</c> result record: the four flow-class blocks of
/// <c>classes</c>, the arm-wide counters folded from them, and one lane witness per desktop.
/// </summary>
/// <remarks>
/// <para><b>The nesting is real, so every level declares its own names.</b> <c>classes</c> is an
/// object with one member per flow class, and each of those is an object of its own, so a leaf such
/// as <c>bytes</c> or <c>sent</c> appears at several levels: <see cref="Mix.PageBytes"/> is
/// <c>metrics/pageBytes</c>, <see cref="Mix.PageClass.Bytes"/> is <c>metrics/classes/page/bytes</c> and
/// <see cref="Mix.UdpClass.Bytes"/> is <c>metrics/classes/udp/bytes</c>. Each of them is a separate
/// constant, because a shared constant is how "which level was this written to" stops being
/// checkable (D14.17).</para>
/// <para><b>Two keys contain a dot.</b> <see cref="Mix.UdpSent"/> (<c>metrics/udp.sent</c>) and
/// <see cref="Mix.UdpLossRate"/> are one member name each, not an object named <c>udp</c>; the same
/// holds for the three respelled members of a desktop lane (<see cref="Mix.DesktopLane.UdpSent"/>,
/// <see cref="Mix.DesktopLane.UdpArrived"/>, <see cref="Mix.DesktopLane.UdpForeignConnection"/>).</para>
/// <para><b>Conditional fields: none.</b> Every key here is published by every <c>mix</c> run: a flow
/// class that measured nothing publishes its zeros rather than leaving its block out, and an unknown
/// reading keeps its key and publishes JSON <see langword="null"/>. The only shape that follows the
/// plan is an array's length: <see cref="Mix.UdpClass.SentPerDesktop"/> and <see cref="Mix.Desktops"/>
/// carry one element per desktop, in desktop order, so a lane that never ran is visible as its own
/// zero rather than averaged away.</para>
/// <para>The record's <c>gates</c> keys are shared and live in <see cref="ArmKeys.Common.Gates"/>; its
/// <c>parameters</c> keys in <see cref="ArmKeys.Common.Parameters"/>.</para>
/// </remarks>
public static partial class ArmKeys
{
    /// <summary>Keys under <c>metrics</c> for the <c>mix</c> kind.</summary>
    public static class Mix
    {
        // S3218 / MemberHidesStaticFromOuterClass: every constant below is named after the member it
        // publishes, and a member of a nested block legitimately repeats a name declared one level up
        // (`DesktopLane.DnsSent` under `Mix.DnsSent`, `PageClass.Pages` under `Mix.Pages`,
        // `ClassNames.Dns` under the `ArmKeys.Dns` shard, `UdpClass.ClientSendLoss` under
        // `Mix.ClientSendLoss`, `DesktopLane.UdpSent`/`PageConnections` under the `Mix.*` constants of
        // the same names). D14.17 makes each level its own constant precisely so this repetition stays
        // visible, so the shadowing is the declaration rule rather than an accident. Each of the six
        // members that trigger the two rules carries its own one-member suppression below.

        /// <summary>The <c>classes</c> object: one member per flow class.</summary>
        public const string Classes = "classes";

        /// <summary>Page fetches the desktops completed.</summary>
        public const string Pages = "pages";

        /// <summary>Page connections the desktops established.</summary>
        public const string PageConnections = "pageConnections";

        /// <summary>Request bytes the page class wrote.</summary>
        public const string PageBytes = "pageBytes";

        /// <summary>Frame bytes the bulk class read back.</summary>
        public const string BulkBytes = "bulkBytes";

        /// <summary>DNS queries the desktops wrote.</summary>
        public const string DnsSent = "dnsSent";

        /// <summary>Datagrams the desktops sent; one dotted member name.</summary>
        public const string UdpSent = "udp.sent";

        /// <summary>Path loss over sent datagrams; null when nothing was sent.</summary>
        public const string UdpLossRate = "udp.lossRate";

        /// <summary>Datagrams the socket refused, the window deferred or a drain abandoned.</summary>
        public const string ClientSendLoss = "clientSendLoss";

        /// <summary>The per-desktop lane witnesses: one array element per desktop.</summary>
        public const string Desktops = "desktops";

        /// <summary>
        /// The member names of <c>classes</c>, one per flow class, in write order. This class is named
        /// after the member it declares because <see cref="Mix.Classes"/> is the container key.
        /// </summary>
        public static class ClassNames
        {
            /// <summary>The page class.</summary>
            public const string Page = "page";

            /// <summary>The bulk-transfer class.</summary>
            public const string Bulk = "bulk";

            /// <summary>The DNS class.</summary>
            /// <remarks>Named after the member it publishes, which shadows the <see cref="ArmKeys.Dns"/> shard.</remarks>
#pragma warning disable S3218
            // ReSharper disable once MemberHidesStaticFromOuterClass
            public const string Dns = "dns";
#pragma warning restore S3218

            /// <summary>The UDP reliability class.</summary>
            public const string Udp = "udp";
        }

        /// <summary>The members of the <c>classes.page</c> block, in write order.</summary>
        public static class PageClass
        {
            /// <summary>Page fetches the desktops completed.</summary>
            /// <remarks>Named after the member it publishes, which shadows <see cref="Mix.Pages"/>.</remarks>
#pragma warning disable S3218
            // ReSharper disable once MemberHidesStaticFromOuterClass
            public const string Pages = "pages";
#pragma warning restore S3218

            /// <summary>Page connections established.</summary>
            public const string Connections = "connections";

            /// <summary>Page responses read back.</summary>
            public const string Messages = "messages";

            /// <summary>Request bytes written.</summary>
            public const string Bytes = "bytes";

            /// <summary>Connections that failed, and reads that ended before their response.</summary>
            public const string Errors = "errors";

            /// <summary>Bytes over pages; null when no page completed.</summary>
            public const string BytesPerPage = "bytesPerPage";
        }

        /// <summary>The members of the <c>classes.bulk</c> block, in write order.</summary>
        public static class BulkClass
        {
            /// <summary>Frame bytes read back.</summary>
            public const string Bytes = "bytes";

            /// <summary>Frame bytes written.</summary>
            public const string BytesSent = "bytesSent";

            /// <summary>Frames round-tripped.</summary>
            public const string Frames = "frames";

            /// <summary>Connects and reads that failed.</summary>
            public const string Errors = "errors";

            /// <summary>Read-back bytes per elapsed second.</summary>
            public const string GoodputBps = "goodputBps";
        }

        /// <summary>The members of the <c>classes.dns</c> block, in write order.</summary>
        public static class DnsClass
        {
            /// <summary>Queries written.</summary>
            public const string Sent = "sent";

            /// <summary>Queries answered with rcode 0.</summary>
            public const string Answered = "answered";

            /// <summary>Queries answered with a non-zero rcode.</summary>
            public const string Servfail = "servfail";

            /// <summary>Queries with no response inside the wait window.</summary>
            public const string Timeout = "timeout";

            /// <summary>Responses that did not match their query, and socket errors.</summary>
            public const string Other = "other";
        }

        /// <summary>The members of the <c>classes.udp</c> block, in write order.</summary>
        public static class UdpClass
        {
            /// <summary>Datagrams sent.</summary>
            public const string Sent = "sent";

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

            /// <summary>Datagrams still inside their window when the drain was cut short.</summary>
            public const string AbandonedAtTeardown = "abandonedAtTeardown";

            /// <summary>Datagrams the socket refused.</summary>
            public const string SendFailures = "sendFailures";

            /// <summary>Datagrams deferred at a full in-flight window; structurally zero for this class.</summary>
            public const string WindowOverflow = "windowOverflow";

            /// <summary>Sequences a received datagram named that the tracker refused as outside its bounded space.</summary>
            public const string OutOfRangeSequences = "outOfRangeSequences";

            /// <summary>Offered slots the tracker refused to send as outside its bounded space.</summary>
            public const string SentOutOfRangeSequences = "sentOutOfRangeSequences";

            /// <summary>Datagram bytes written.</summary>
            public const string Bytes = "bytes";

            /// <summary>Sent datagrams that were refused, deferred or abandoned.</summary>
            /// <remarks>Shadows <see cref="Mix.ClientSendLoss"/>; see the shard's shadowing note.</remarks>
#pragma warning disable S3218
            // ReSharper disable once MemberHidesStaticFromOuterClass
            public const string ClientSendLoss = "clientSendLoss";
#pragma warning restore S3218

            /// <summary>Late plus never over sent; null when nothing was sent.</summary>
            public const string LossRate = "lossRate";

            /// <summary>The declared loss window W, in milliseconds.</summary>
            public const string Window = "window";

            /// <summary>Sent datagrams per desktop, in desktop order; arity is the desktop count.</summary>
            public const string SentPerDesktop = "sentPerDesktop";
        }

        /// <summary>The members of one <c>desktops[]</c> element, in write order.</summary>
        public static class DesktopLane
        {
            /// <summary>The desktop's index.</summary>
            public const string Desktop = "desktop";

            /// <summary>Datagrams this desktop sent; one dotted member name.</summary>
            /// <remarks>Shadows <see cref="Mix.UdpSent"/>; see the shard's shadowing note.</remarks>
#pragma warning disable S3218
            // ReSharper disable once MemberHidesStaticFromOuterClass
            public const string UdpSent = "udp.sent";
#pragma warning restore S3218

            /// <summary>Datagrams this desktop saw arrive inside their window.</summary>
            public const string UdpArrived = "udp.arrived";

            /// <summary>Datagrams this desktop never saw arrive.</summary>
            public const string UdpNever = "udpNever";

            /// <summary>Replies this desktop saw for another flow's connection id.</summary>
            public const string UdpForeignConnection = "udp.foreignConnection";

            /// <summary>Page connections this desktop established.</summary>
            /// <remarks>Shadows <see cref="Mix.PageConnections"/>; see the shard's shadowing note.</remarks>
#pragma warning disable S3218
            // ReSharper disable once MemberHidesStaticFromOuterClass
            public const string PageConnections = "pageConnections";
#pragma warning restore S3218

            /// <summary>Bulk frames this desktop round-tripped.</summary>
            public const string BulkFrames = "bulkFrames";

            /// <summary>DNS queries this desktop wrote.</summary>
            /// <remarks>Shadows <see cref="Mix.DnsSent"/>; see the shard's shadowing note.</remarks>
#pragma warning disable S3218
            // ReSharper disable once MemberHidesStaticFromOuterClass
            public const string DnsSent = "dnsSent";
#pragma warning restore S3218
        }
    }
}
