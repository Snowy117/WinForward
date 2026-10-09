namespace WinForward.E2E.Contracts;

/// <summary>
/// The keys of the target's <c>ledger.jsonl</c>: the envelope every record carries, the four summary
/// families the target publishes (<c>tcp</c> per connection, <c>udpSummary</c>, <c>dnsSummary</c> and
/// <c>targetSummary</c>) and the <c>error</c> record a failure that could not reach a summary leaves.
/// The ledger is the target half of the cross-binary contract -- the client writes the arm files, the
/// target writes this one, and the analyzer reads both -- so its keys are declared here rather than
/// spelled at the four writers that publish them.
/// </summary>
/// <remarks>
/// <para><b>The envelope.</b> <see cref="Ledger.Envelope.Utc"/> and <see cref="Ledger.Envelope.Label"/>
/// are written by the sink's envelope callback ahead of a record's own body, so they open every record
/// of every family. The member that names the family itself, <c>type</c>, is the record's own first
/// member and is declared once for every record this harness writes, in
/// <see cref="ArmKeys.Common.Record"/>.</para>
/// <para><b>Same leaf name at a different level is a different constant.</b> The tcp, udp and dns
/// totals reach the ledger at two levels: <see cref="Ledger.TcpSummary"/> declares the members of a
/// <c>tcpSummary</c> record at its own root, and <see cref="Ledger.TargetSummary.TcpTotals"/> declares
/// the same members one level down, under <c>targetSummary/tcp</c>; <c>udp</c> and <c>dns</c> are
/// declared the same way, and the udp counters also sit at the root of the record they summarise
/// (<see cref="Ledger.UdpSummary"/>). Two spellings that are equal today -- the same counters are
/// published at both levels -- are still two constants, because the constant is what fixes the level;
/// the writers take the level's key set rather than spelling a name at the call site. The
/// one shared name set is a data-driven container's members: <see cref="Ledger.VerdictNames"/> carries
/// the eight verdict names every <c>verdicts</c> object is written with, the way
/// <see cref="ArmKeys.Reliability.OutcomeNames"/> carries the leaves of three distributions at three
/// levels.</para>
/// <para><b>One leaf name may also be published by two families for two mechanisms.</b>
/// <c>truncatedFrames</c> sits at the root of both <c>tcpSummary</c> and <c>dnsSummary</c>: the first
/// counts a connection the frame reader found cut in half by the peer's close, the second counts the
/// DNS listener's own short length-prefixed read. They are two constants, and each family's
/// value is only ever read against its own mechanism. <c>acceptErrors</c> is the shared name of the
/// other pair: one listener's accept call refused a connection, which the TCP listener counts in its
/// own family and each DNS listener counts in its own, beside the connections it did accept. The two
/// are the same mechanism on different listeners, so a reader comparing them is comparing two
/// listeners rather than two definitions.</para>
/// <para><b>The <c>error</c> family names itself.</b> A record whose <c>type</c> is <c>error</c> is the
/// ledger's account of a failure that could not be written as the record it belonged to -- a summary
/// or a connection the sink refused. Its family is the record kind itself, so the only key it declares
/// is the innermost cause, <see cref="Ledger.ErrorRecord.Detail"/>; the client's arm files carry the same
/// leaf name under <see cref="ArmKeys.Common.ErrorRecord"/> beside the outer exception's type, and the
/// two are two constants for two levels.</para>
/// <para><b>Conditional keys.</b> One container is written only when the run asked for it:
/// <see cref="Ledger.TargetSummary.DnsAlt"/> and the whole block under it appear only when the target
/// was started with a second DNS port, and the listener it names publishes a second
/// <c>dnsSummary</c> record of its own. One array's members appear only when the traffic behind them
/// did: <see cref="Ledger.UdpSummary.Sources"/> itself is always written (an interval that saw no
/// datagram publishes <c>[]</c>), while its elements' keys,
/// <see cref="Ledger.UdpSummary.SourceEntry"/>, are written once per source that interval saw. Every
/// other declared key of every family is written by every record of that family, whatever the traffic
/// was.</para>
/// <para><b>No ledger key is ever null, and no key is ever omitted because a value is unknown.</b> A
/// counter with nothing behind it is published as <c>0</c> and keeps its key, which is what makes a
/// missing key mean "this record never carries it" rather than "nothing happened"; the shape test
/// asserts the zero and the absence separately for that reason.</para>
/// <para>The record's verdict distribution is keyed by the names the wire's <c>TcpCommand</c>
/// publishes for <c>TcpVerdict</c>, and the writer loops the enum rather than a list here, so
/// <see cref="Ledger.VerdictNames"/> is the declaration the tie test holds against that table.</para>
/// </remarks>
public static partial class ArmKeys
{
    /// <summary>The keys of the target's ledger, family by family.</summary>
    public static class Ledger
    {
        /// <summary>The two members the sink's envelope writes before a record's own body, in write order.</summary>
        public static class Envelope
        {
            /// <summary>The record's absolute UTC instant, in round-trip form.</summary>
            public const string Utc = "utc";

            /// <summary>The label the target was started with; empty when none was given.</summary>
            public const string Label = "label";
        }

        /// <summary>
        /// The members of one <c>tcp</c> record, in write order: the connection's command, its echo and
        /// its verdict, which is one ledger line per accepted connection.
        /// </summary>
        public static class TcpRecord
        {
            /// <summary>The connection id from the client's first frame; zero when it never sent one.</summary>
            public const string ConnectionId = "connectionId";

            /// <summary>The mode the client asked for, or <c>unknown</c> when it asked for none.</summary>
            public const string Mode = "mode";

            /// <summary>The echo size the mode asked for.</summary>
            public const string ExpectedBytes = "expectedBytes";

            /// <summary>Payload bytes this connection echoed back.</summary>
            public const string BytesEchoed = "bytesEchoed";

            /// <summary>The verdict the connection ended with.</summary>
            public const string Verdict = "verdict";

            /// <summary>The peer's address and port, or <c>unknown</c> when the socket could not say.</summary>
            public const string Peer = "peer";

            /// <summary>Stopwatch tick the connection was accepted at.</summary>
            public const string StartedTicks = "startedTicks";

            /// <summary>Stopwatch tick the connection ended at.</summary>
            public const string EndedTicks = "endedTicks";
        }

        /// <summary>
        /// The members of a <c>tcpSummary</c> record, in write order: what every connection of the run
        /// added up to. The same block is published again under <c>targetSummary/tcp</c> from
        /// <see cref="TargetSummary.TcpTotals"/>.
        /// </summary>
        public static class TcpSummary
        {
            /// <summary>Connections accepted.</summary>
            public const string Connections = "connections";

            /// <summary>Accepts this listener's accept loop refused and then retried.</summary>
            public const string AcceptErrors = "acceptErrors";

            /// <summary>Payload bytes echoed across every connection.</summary>
            public const string BytesEchoed = "bytesEchoed";

            /// <summary>Frames the protocol could not read.</summary>
            public const string ProtocolErrors = "protocolErrors";

            /// <summary>Connections the peer's close cut off in the middle of a frame.</summary>
            public const string TruncatedFrames = "truncatedFrames";

            /// <summary>The verdict distribution: one member per name in <see cref="VerdictNames"/>.</summary>
            public const string Verdicts = "verdicts";
        }

        /// <summary>
        /// The members of a <c>udpSummary</c> record, in write order: the interval's totals, the tick
        /// they were taken at, and the census of the sources they arrived from. Three of these counters
        /// are published again, beside <c>sendErrors</c>, under <c>targetSummary/udp</c> from
        /// <see cref="TargetSummary.UdpTotals"/>.
        /// </summary>
        public static class UdpSummary
        {
            /// <summary>Datagrams received in the interval.</summary>
            public const string Received = "received";

            /// <summary>Datagrams that were not a readable frame.</summary>
            public const string Undecodable = "undecodable";

            /// <summary>Payload bytes received in the interval.</summary>
            public const string Bytes = "bytes";

            /// <summary>Stopwatch tick this summary was taken at.</summary>
            public const string Ticks = "ticks";

            /// <summary>The sources the interval's datagrams arrived from; one element each, in address order.</summary>
            public const string Sources = "sources";

            /// <summary>Datagrams a full census table could not place.</summary>
            public const string SourceOverflow = "sourceOverflow";

            /// <summary>
            /// The members of one <see cref="Sources"/> element, in write order. The class name is not a
            /// JSON member: its constants are the members of each array element, one source's identity
            /// and the datagrams that interval saw from it.
            /// </summary>
            public static class SourceEntry
            {
                /// <summary>The source address, IPv4-mapped form resolved.</summary>
                public const string Address = "address";

                /// <summary>The source's port; the client's ephemeral port, so it differs per run.</summary>
                public const string Port = "port";

                /// <summary>Datagrams this interval saw from that source.</summary>
                public const string Datagrams = "datagrams";
            }
        }

        /// <summary>
        /// The members of a <c>dnsSummary</c> record, in write order, one record per DNS listener: what
        /// the responder answered over its datagram socket and its stream listener. The same block is
        /// published again under <c>targetSummary/dns</c> and <c>targetSummary/dnsAlt</c> from
        /// <see cref="TargetSummary.DnsTotals"/>.
        /// </summary>
        public static class DnsSummary
        {
            /// <summary>The port this responder was bound to.</summary>
            public const string Port = "port";

            /// <summary>Queries read from the datagram socket.</summary>
            public const string UdpQueries = "udpQueries";

            /// <summary>Queries answered with at least one record.</summary>
            public const string UdpAnswers = "udpAnswers";

            /// <summary>Queries answered with no records, which is a valid answer.</summary>
            public const string UdpEmptyAnswers = "udpEmptyAnswers";

            /// <summary>Datagrams that carried no readable query.</summary>
            public const string UdpMalformed = "udpMalformed";

            /// <summary>Answers the datagram socket refused.</summary>
            public const string UdpSendErrors = "udpSendErrors";

            /// <summary>Queries read from the stream listener.</summary>
            public const string TcpQueries = "tcpQueries";

            /// <summary>Stream queries answered with at least one record.</summary>
            public const string TcpAnswers = "tcpAnswers";

            /// <summary>Stream queries answered with no records.</summary>
            public const string TcpEmptyAnswers = "tcpEmptyAnswers";

            /// <summary>Stream queries that carried no readable query.</summary>
            public const string TcpMalformed = "tcpMalformed";

            /// <summary>Stream messages the peer stopped writing in the middle of.</summary>
            /// <remarks>
            /// This is the responder's own definition and not the frame reader's truncated status:
            /// the stream listener reads a two-byte length prefix and then that many bytes,
            /// so a message is cut in half when either read ends short. It shares its name with
            /// <see cref="TcpSummary.TruncatedFrames"/>, which counts a different mechanism on a
            /// different listener; the two are compared only against themselves.
            /// </remarks>
            public const string TruncatedFrames = "truncatedFrames";

            /// <summary>Stream connections accepted.</summary>
            public const string TcpConnections = "tcpConnections";

            /// <summary>Accepts this listener's accept loop refused and then retried.</summary>
            /// <remarks>
            /// The same mechanism <see cref="TcpSummary.AcceptErrors"/> counts, on this responder's own
            /// stream listener: each listener owns its accept loop, so each publishes its own count.
            /// </remarks>
            public const string AcceptErrors = "acceptErrors";

            /// <summary>Stream connections that ended on a socket fault or a shutdown.</summary>
            public const string TcpAborted = "tcpAborted";
        }

        /// <summary>
        /// The members of the <c>targetSummary</c> record, in write order: the run's own clock, the
        /// ledger's own error count, and one block per listener family.
        /// </summary>
        public static class TargetSummary
        {
            /// <summary>Stopwatch tick the target started listening at.</summary>
            public const string StartedTicks = "startedTicks";

            /// <summary>Stopwatch tick the target stopped at.</summary>
            public const string EndedTicks = "endedTicks";

            /// <summary>Records the ledger could not write, whichever policy swallowed them.</summary>
            public const string LedgerWriteErrors = "ledgerWriteErrors";

            /// <summary>The <c>tcpSummary</c> block, one level down.</summary>
            public const string Tcp = "tcp";

            /// <summary>The udp totals block, one level down.</summary>
            public const string Udp = "udp";

            /// <summary>The first DNS listener's summary block, one level down.</summary>
            /// <remarks>Named after the member it publishes, which shadows the <see cref="ArmKeys.Dns"/> shard.</remarks>
#pragma warning disable S3218
            // ReSharper disable once MemberHidesStaticFromOuterClass
            public const string Dns = "dns";
#pragma warning restore S3218

            /// <summary>
            /// The second DNS listener's summary block, one level down. It is the only conditional
            /// container of the ledger: a target started without a second DNS port publishes no key of
            /// it, and the block under it is <see cref="DnsTotals"/> written at
            /// <c>targetSummary/dnsAlt</c>.
            /// </summary>
            public const string DnsAlt = "dnsAlt";

            /// <summary>
            /// The members of the tcp totals block under <c>targetSummary/tcp</c>, in write order: the
            /// same quantities <see cref="TcpSummary"/> declares at the <c>tcpSummary</c> record's own
            /// root, declared again here because a leaf written at another level is another constant.
            /// </summary>
            public static class TcpTotals
            {
                /// <summary>Connections accepted.</summary>
                public const string Connections = "connections";

                /// <summary>Accepts the listener's accept loop refused and then retried.</summary>
                public const string AcceptErrors = "acceptErrors";

                /// <summary>Payload bytes echoed across every connection.</summary>
                public const string BytesEchoed = "bytesEchoed";

                /// <summary>Frames the protocol could not read.</summary>
                public const string ProtocolErrors = "protocolErrors";

                /// <summary>Connections the peer's close cut off in the middle of a frame.</summary>
                public const string TruncatedFrames = "truncatedFrames";

                /// <summary>The verdict distribution: one member per name in <see cref="VerdictNames"/>.</summary>
                public const string Verdicts = "verdicts";
            }

            /// <summary>
            /// The members of the udp totals block under <c>targetSummary/udp</c>, in write order. It
            /// carries the three counters <see cref="UdpSummary"/> also publishes, plus
            /// <see cref="SendErrors"/>, which only this level has: the interval records leave the
            /// refused-send count to the run's final block.
            /// </summary>
            public static class UdpTotals
            {
                /// <summary>Receive loops the echo listener actually started.</summary>
                /// <remarks>
                /// The loops that reached their receive call, not the number the command line asked for:
                /// a listener whose loop never started serves nothing, and a count of what was requested
                /// could not show that. The DNS listeners take the same configured count for their own
                /// datagram loops, which are not part of this block.
                /// </remarks>
                public const string UdpReceivers = "udpReceivers";

                /// <summary>Datagrams received across the run.</summary>
                public const string Received = "received";

                /// <summary>Datagrams that were not a readable frame.</summary>
                public const string Undecodable = "undecodable";

                /// <summary>Payload bytes received across the run.</summary>
                public const string Bytes = "bytes";

                /// <summary>Datagrams the echo socket refused.</summary>
                public const string SendErrors = "sendErrors";
            }

            /// <summary>
            /// The members of a dns totals block, in write order: the same quantities
            /// <see cref="DnsSummary"/> declares at a <c>dnsSummary</c> record's own root, declared
            /// again here because a leaf written at another level is another constant. One block
            /// serves both containers, <c>targetSummary/dns</c> and <c>targetSummary/dnsAlt</c>: they
            /// are the same depth and the same writer, so the container is the caller's business and
            /// the block is declared once.
            /// </summary>
            public static class DnsTotals
            {
                /// <summary>The port this responder was bound to.</summary>
                public const string Port = "port";

                /// <summary>Queries read from the datagram socket.</summary>
                public const string UdpQueries = "udpQueries";

                /// <summary>Queries answered with at least one record.</summary>
                public const string UdpAnswers = "udpAnswers";

                /// <summary>Queries answered with no records, which is a valid answer.</summary>
                public const string UdpEmptyAnswers = "udpEmptyAnswers";

                /// <summary>Datagrams that carried no readable query.</summary>
                public const string UdpMalformed = "udpMalformed";

                /// <summary>Answers the datagram socket refused.</summary>
                public const string UdpSendErrors = "udpSendErrors";

                /// <summary>Queries read from the stream listener.</summary>
                public const string TcpQueries = "tcpQueries";

                /// <summary>Stream queries answered with at least one record.</summary>
                public const string TcpAnswers = "tcpAnswers";

                /// <summary>Stream queries answered with no records.</summary>
                public const string TcpEmptyAnswers = "tcpEmptyAnswers";

                /// <summary>Stream queries that carried no readable query.</summary>
                public const string TcpMalformed = "tcpMalformed";

                /// <summary>
                /// Stream messages a peer stopped writing in the middle of: this listener's own
                /// short length-prefixed read, not the frame reader's truncated status (the meaning is
                /// stated in full on <see cref="DnsSummary.TruncatedFrames"/>).
                /// </summary>
                public const string TruncatedFrames = "truncatedFrames";

                /// <summary>Stream connections accepted.</summary>
                public const string TcpConnections = "tcpConnections";

                /// <summary>Accepts this responder's accept loop refused and then retried.</summary>
                public const string AcceptErrors = "acceptErrors";

                /// <summary>Stream connections that ended on a socket fault or a shutdown.</summary>
                public const string TcpAborted = "tcpAborted";
            }
        }

        /// <summary>
        /// The members of an <c>error</c> record, in write order: the ledger's own account of a failure
        /// that could not be written as the record it belonged to. The class is named after the record
        /// it declares the members of rather than after a container, because the family is the record's
        /// <c>type</c> and never a JSON member of its own.
        /// </summary>
        public static class ErrorRecord
        {
            /// <summary>The innermost exception of the failure, by type name.</summary>
            /// <remarks>
            /// The same derivation the client's arm files use for
            /// <see cref="ArmKeys.Common.ErrorRecord.Detail"/> -- the base exception's type name, so a
            /// wrapped failure names its cause rather than its wrapper -- declared again here because a
            /// leaf at another level is another constant. The wrapper's own type name is what
            /// the family already says: the record is an error.
            /// </remarks>
            public const string Detail = "detail";
        }

        /// <summary>
        /// The members of every <c>verdicts</c> object, in write order. The class is named after the
        /// members it declares rather than after a container, because it is never a JSON member of its
        /// own: <see cref="TcpSummary.Verdicts"/> and <see cref="TargetSummary.TcpTotals"/>'s
        /// <c>verdicts</c> each write these leaves inside their own object, the way
        /// <see cref="ArmKeys.Common.LatencyRecord.Histogram"/> carries the leaves of four histograms.
        /// The writer loops <c>TcpVerdict</c> and spells each name through
        /// <c>TcpCommand.Name</c>, so a tie test holds this list against that table.
        /// </summary>
        public static class VerdictNames
        {
            /// <summary>The exchange completed as its mode called for.</summary>
            public const string Clean = "clean";

            /// <summary>The peer reset the connection.</summary>
            public const string Reset = "reset";

            /// <summary>The peer shut its send side down after the expected echo.</summary>
            public const string PartialFin = "partialFin";

            /// <summary>The peer shut its send side down and a trailer followed.</summary>
            public const string HalfClose = "halfClose";

            /// <summary>The echo stopped for longer than the stall window.</summary>
            public const string Stall = "stall";

            /// <summary>The peer closed before its mode was satisfied.</summary>
            public const string ClientClosedEarly = "clientClosedEarly";

            /// <summary>A frame could not be read.</summary>
            public const string ProtocolError = "protocolError";

            /// <summary>The connection ended on a socket fault or a shutdown.</summary>
            public const string Error = "error";
        }
    }
}
