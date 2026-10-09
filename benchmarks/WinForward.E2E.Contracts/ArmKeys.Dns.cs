namespace WinForward.E2E.Contracts;

/// <summary>
/// The <c>metrics</c> object of a <c>dns</c> result record: the keys the arm writes, in write order. A
/// constant here is the member name under <c>metrics</c>, so <see cref="Dns.Sent"/> declares the path
/// <c>metrics/sent</c>; <see cref="Dns.UdpSent"/> is one dotted member name, not an object named
/// <c>udp</c> (the canonical path walker keeps a dot inside a key whole).
/// </summary>
/// <remarks>
/// <para><b>Nothing here is conditional.</b> Every key is published by every DNS run: a run that
/// answered nothing says so through zeros and through empty <see cref="Dns.Rcodes"/> and
/// <see cref="Dns.QueryTypes"/> objects rather than by leaving the key out. Unknown is therefore always a
/// written JSON <see langword="null"/>, which is what <see cref="Dns.AnswerRate"/> and
/// <see cref="Dns.AchievedRate"/> publish when their population or duration was empty.</para>
/// <para><b>Two members are data-driven.</b> <see cref="Dns.Rcodes"/> and <see cref="Dns.QueryTypes"/> are
/// objects with one member per observed rcode (decimal, <c>0</c>..<c>15</c>) and per observed query
/// type (<c>A</c>, <c>AAAA</c>, <c>CNAME</c>, <c>HTTPS</c>, <c>TXT</c>, or <c>typeN</c> for a type
/// outside that list). The objects are always present and may be empty; their member names are not
/// constants here because they are data, and the shape test checks them by shape.</para>
/// <para><see cref="Dns.UdpSent"/> and <see cref="Dns.UdpUnsent"/> are not two spellings of one statistic:
/// the first is what the UDP half sent, the second what pacing left unsent.</para>
/// <para>The record's <c>gates</c> keys are shared and live in <see cref="ArmKeys.Common.Gates"/>; its
/// <c>parameters</c> keys in <see cref="ArmKeys.Common.Parameters"/>.</para>
/// </remarks>
public static partial class ArmKeys
{
    /// <summary>Keys under <c>metrics</c> for the <c>dns</c> kind.</summary>
    public static class Dns
    {
        /// <summary>Queries written to a socket by both halves.</summary>
        public const string Sent = "sent";

        /// <summary>Queries the UDP half wrote.</summary>
        public const string UdpSent = "udp.sent";

        /// <summary>Queries the TCP half wrote.</summary>
        public const string TcpSent = "tcpSent";

        /// <summary>Pacing slots skipped because the in-flight window was full, both halves.</summary>
        public const string Unsent = "unsent";

        /// <summary>UDP pacing slots skipped because the in-flight window was full.</summary>
        public const string UdpUnsent = "udpUnsent";

        /// <summary>TCP pacing slots skipped because the in-flight window was full.</summary>
        public const string TcpUnsent = "tcpUnsent";

        /// <summary>Queries the arm offered: <c>sent + unsent</c>.</summary>
        public const string Offered = "offered";

        /// <summary>Queries answered with rcode 0.</summary>
        public const string Answered = "answered";

        /// <summary>Queries answered with a non-zero rcode.</summary>
        public const string Servfail = "servfail";

        /// <summary>Queries still pending when the drain window closed.</summary>
        public const string Timeout = "timeout";

        /// <summary>TCP only: responses that consumed a queued query under a different transaction id.</summary>
        public const string Other = "other";

        /// <summary>Queries with no answer of their own: <c>timeout + other</c>.</summary>
        public const string Unanswered = "unanswered";

        /// <summary>Socket-level failures (connect, send, receive) that belong to no single query.</summary>
        public const string SocketErrors = "socketErrors";

        /// <summary>Answers with rcode 0 that carried no record.</summary>
        public const string EmptyAnswers = "emptyAnswers";

        /// <summary>Responses that did not parse, and queries that could not be built.</summary>
        public const string Malformed = "malformed";

        /// <summary>Valid responses that matched no outstanding query.</summary>
        public const string Unmatched = "unmatched";

        /// <summary>Answered over sent; null when nothing was sent.</summary>
        public const string AnswerRate = "answerRate";

        /// <summary>Sent per elapsed second; null when no time passed.</summary>
        public const string AchievedRate = "achievedRate";

        /// <summary>One member per observed rcode, named by its decimal value; may be empty.</summary>
        public const string Rcodes = "rcodes";

        /// <summary>One member per observed query type; may be empty.</summary>
        public const string QueryTypes = "queryTypes";
    }
}
