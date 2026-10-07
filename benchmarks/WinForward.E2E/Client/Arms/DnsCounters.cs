namespace WinForward.E2E.Client.Arms;

/// <summary>
/// One dns lane's counters. The lane's send phase, its receive loop and its teardown share the
/// instance, and the arm reads it once both lanes have joined.
/// </summary>
internal sealed class DnsCounters
{
    internal long _sent;
    internal long _answered;
    internal long _servfail;
    internal long _timeout;
    internal long _other;
    internal long _unsent;
    internal long _socketErrors;
    internal long _emptyAnswers;
    internal long _malformed;
    internal long _unmatched;
    internal readonly long[] _rcodes = new long[16];

    /// <summary>
    /// Queries written to the socket, per DNS query type. Counted from the query the arm actually
    /// built, so the published mix stays right for a plan that interleaves CNAME queries.
    /// </summary>
    internal readonly long[] _queryTypes = new long[128];
}
