namespace WinForward.E2E.Contracts;

/// <summary>
/// Every field name a record carries, one constant per key, split by family. The write side and the
/// read side both reference these constants, so a rename is one edit in one place rather than a
/// literal that silently stops being found.
/// </summary>
/// <remarks>
/// <para><b>Two forms, and what the value means.</b> A key whose value contains a dot is one whole
/// key: <c>ArmKeys.Common.Parameters.RatePerSecond = "ratePerSecond"</c> is the member
/// <c>ratePerSecond</c>, while a form-A key such as a future <c>ArmKeys.Latency.TcpSent = "tcp.sent"</c>
/// is the member literally named <c>tcp.sent</c> (the canonical path walker never splits a dot). A
/// nested class expresses real nesting, and then a leaf constant holds the leaf name alone:
/// <c>ArmKeys.Common.Gates.ClientSendLoss = "clientSendLoss"</c> is <c>gates/clientSendLoss</c>,
/// and the class name is the JSON member of the level above it, written in lower camel case
/// (<c>Gates</c> is <c>gates</c>). Two groupings state their member name instead, because a class
/// name would collide with another shard's: <see cref="Common.Record"/> is the record root itself,
/// and <see cref="Common.LatencyRecord"/> is the <c>latency</c> member (<c>Latency</c> belongs to the
/// latency arm's own shard).</para>
/// <para><b>Same leaf name at a different level is a different constant.</b> Two paths that happen
/// to share a leaf name get one declaration each, because a shared constant is how "which level was
/// this written to" stops being checkable.</para>
/// <para><b>Conditional keys, by family.</b> A key is omitted only when the arm does not publish that
/// quantity at all, never because its value is unknown (unknown is JSON <see langword="null"/>):
/// <list type="bullet">
/// <item><description><c>idle</c>: nothing in <c>metrics</c> is conditional; <c>elapsedSeconds</c> is
/// always present.</description></item>
/// <item><description><c>throughput</c>: nothing in <c>metrics</c> is conditional; the two goodput
/// readings are nullable (<see langword="null"/> when no time passed), never absent.</description></item>
/// <item><description>the record's <c>latency</c> object: a histogram is written only when it holds
/// at least one sample, so <c>latency</c> itself is always present and may be empty.</description></item>
/// </list>
/// A kind whose own metrics are conditional registers the condition in its own shard, next to the
/// constants it applies to.</para>
/// </remarks>
public static partial class ArmKeys
{
    /// <summary>Keys shared by every record, whatever the arm's kind.</summary>
    public static class Common
    {
        /// <summary>
        /// The members of a record's own object, in the order the writers emit them. This is the one
        /// group whose members are not nested under a member of their own: <c>type</c>, <c>arm</c>,
        /// <c>kind</c>, <c>label</c>, <c>parameters</c>, <c>metrics</c>, <c>latency</c>, <c>gates</c>,
        /// <c>notes</c>, <c>startedTicks</c>, <c>endedTicks</c>.
        /// </summary>
        public static class Record
        {
            public const string Type = "type";

            public const string Arm = "arm";

            public const string Kind = "kind";

            public const string Label = "label";

            // S3218 / MemberHidesStaticFromOuterClass: `parameters`, `gates` and `latency` are the
            // published member names, and each of them is also the name of a key set one level down
            // (the `parameters` and `gates` families here, and the latency arm's own shard for
            // `latency`). Renaming either side would break the rule this file documents -- a member's
            // name is its key's name -- so the shadowing is the contract, not an accident.
#pragma warning disable S3218
            // ReSharper disable MemberHidesStaticFromOuterClass
            public const string Parameters = "parameters";

            public const string Metrics = "metrics";

            public const string Gates = "gates";

            public const string Latency = "latency";
            // ReSharper restore MemberHidesStaticFromOuterClass
#pragma warning restore S3218

            public const string Notes = "notes";

            public const string StartedTicks = "startedTicks";

            public const string EndedTicks = "endedTicks";
        }

        /// <summary>
        /// The <c>gates</c> object: the per-arm verdicts a consumer reads without recomputing them.
        /// The object itself is a plain name/count map, unlike <c>metrics</c>; these constants are
        /// what keeps its keys from being spelled out at each write site.
        /// </summary>
        public static class Gates
        {
            public const string ClientSendLoss = "clientSendLoss";

            public const string WindowMs = "windowMs";

            public const string WindowOverflow = "windowOverflow";

            public const string BacklogDrops = "backlogDrops";

            public const string SendFailures = "sendFailures";

            public const string ScheduleTruncated = "scheduleTruncated";

            public const string LanesPlanned = "lanesPlanned";

            public const string LanesStarted = "lanesStarted";

            public const string LaneShortfall = "laneShortfall";

            public const string InFlightCeilingMs = "inFlightCeilingMs";

            public const string IdleLanes = "idleLanes";
        }

        /// <summary>
        /// The <c>parameters</c> object: what the arm actually ran with, after its own defaults were
        /// applied. <see cref="Latency"/> and <see cref="Loss"/> are themselves nested objects whose
        /// inner keys belong to the arms that produce those phases, not to this family.
        /// </summary>
        public static class Parameters
        {
            public const string Seconds = "seconds";

            public const string RatePerSecond = "ratePerSecond";

            public const string PayloadBytes = "payloadBytes";

            public const string Protocol = "protocol";

            public const string Lanes = "lanes";

            public const string InFlightWindow = "inFlightWindow";

            public const string Traffic = "traffic";

            public const string Streams = "streams";

            public const string TargetBytesPerSecond = "targetBytesPerSecond";

            public const string FramePayloadBytes = "framePayloadBytes";

            public const string LossWindowMs = "lossWindowMs";

            public const string Desktops = "desktops";

            public const string PageIntervalSeconds = "pageIntervalSeconds";

            public const string PageConnections = "pageConnections";

            public const string PageRequestsTotal = "pageRequestsTotal";

            public const string PageMessageBytes = "pageMessageBytes";

            public const string BulkBitsPerSecondPerDesktop = "bulkBitsPerSecondPerDesktop";

            public const string DnsQueriesPerPage = "dnsQueriesPerPage";

            public const string UdpPacketsPerSecondPerDesktop = "udpPacketsPerSecondPerDesktop";

            public const string UdpPayloadBytes = "udpPayloadBytes";

            public const string TcpPercent = "tcpPercent";

            public const string CnameEvery = "cnameEvery";

            public const string DnsPort = "dnsPort";

            public const string DrainWindowMs = "drainWindowMs";

            public const string ConnectionsPerSecond = "connectionsPerSecond";

            public const string ModeMix = "modeMix";

            public const string ExpectedBytes = "expectedBytes";

            public const string IntervalMs = "intervalMs";

            public const string IdleSeconds = "idleSeconds";

            public const string ResponseTimeoutMs = "responseTimeoutMs";

            public const string PhaseSeconds = "phaseSeconds";

            public const string Phases = "phases";

            // S3218: the published member name is `latency`, and `ArmKeys.Latency` is the latency
            // arm's own key shard. The rule this file documents -- a member's name is its key's
            // name -- makes the collision the contract rather than an accident (D14.17).
#pragma warning disable S3218
            // ReSharper disable once MemberHidesStaticFromOuterClass
            public const string Latency = "latency";
#pragma warning restore S3218

            public const string Loss = "loss";
        }

        /// <summary>
        /// The record's <c>latency</c> member: one histogram per measurement class, each written only
        /// when it holds at least one sample. The object is always present, so an arm that measures
        /// nothing publishes <c>latency: {}</c> rather than no reading at all. This class is named
        /// after the member it declares because <c>Latency</c> is the latency arm's own shard.
        /// </summary>
        public static class LatencyRecord
        {
            public const string TcpConnect = "tcp-connect";

            public const string TcpRtt = "tcp-rtt";

            public const string UdpRtt = "udp-rtt";

            public const string DnsRtt = "dns-rtt";

            /// <summary>
            /// The members every one of the histograms above carries, in write order. A histogram's
            /// own path is its name under <c>latency</c> plus one of these leaf names, e.g.
            /// <c>latency/tcp-connect/p99Us</c>; the histogram name itself is written by the record
            /// writer, so this class is never a JSON member of its own.
            /// </summary>
            public static class Histogram
            {
                public const string Count = "count";

                public const string MinUs = "minUs";

                public const string MaxUs = "maxUs";

                public const string MeanUs = "meanUs";

                public const string P50Us = "p50Us";

                public const string P90Us = "p90Us";

                public const string P99Us = "p99Us";

                public const string P999Us = "p999Us";
            }
        }
    }
}
