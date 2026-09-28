namespace WinForward.Runtime.UdpProxy;

/// <summary>
/// What one SOCKS5 server has been observed to accept, per run (design §5). The verdict is sticky:
/// once a server is known to pin one client source port per association it serves per-flow
/// associations for the rest of the run, and once a second attached flow is confirmed to have
/// received a response the server stops being sampled.
/// </summary>
internal enum UdpServerCapability
{
    /// <summary>No evidence either way yet: share, and keep sampling.</summary>
    Unknown,

    /// <summary>A second attached flow received a response: the server multiplexes flows on one association.</summary>
    SharedOk,

    /// <summary>One attached flow's replies never arrive while a sibling's do: the server pins the association's source port.</summary>
    PerFlowOnly,
}

/// <summary>
/// The per-lease evidence the capability sampler reads, owned by the association that handed the
/// lease out. It is the only state the datagram path writes (I3): one <see cref="Interlocked"/>
/// increment per successfully sent datagram, and a write-once flag for the first successfully
/// decoded relay datagram. Both are allocation-free on the calling thread.
/// <para>
/// The record lives exactly as long as its lease is attached: the association holds the attached
/// records as a live set, so a sample describes the flows multiplexed on the association at that
/// instant and a released flow's unanswered sends are no longer part of it.
/// </para>
/// </summary>
internal sealed class UdpAssociationEvidence
{
    private int _datagramsSent;
    private int _sawResponse;

    /// <summary>The number of datagrams this lease sent successfully.</summary>
    internal int DatagramsSent => Volatile.Read(ref _datagramsSent);

    /// <summary>Whether this lease ever decoded a relay response.</summary>
    internal bool SawResponse => Volatile.Read(ref _sawResponse) != 0;

    /// <summary>Records one successfully sent datagram.</summary>
    internal void RecordDatagramSent() => Interlocked.Increment(ref _datagramsSent);

    /// <summary>
    /// Records the first successfully decoded relay datagram. A plain volatile read short-circuits
    /// every later response, so the flag costs one interlocked operation per lease, not per datagram.
    /// </summary>
    internal void RecordResponseReceived()
    {
        if (Volatile.Read(ref _sawResponse) != 0) return;
        _ = Interlocked.Exchange(ref _sawResponse, 1);
    }
}

/// <summary>
/// The passive capability sampler (design §5, R2). It runs on the pool's existing maintenance tick
/// and reads the per-lease evidence of an association's attached flows — never the datagram path.
/// </summary>
internal static class UdpAssociationCapabilitySampler
{
    /// <summary>The unanswered sends that make an attached flow pinning-suspicious.</summary>
    internal const int PinningSuspicionThreshold = 3;

    /// <summary>
    /// The verdict for one association from its attached leases' evidence, or null when the sample
    /// carries no verdict (fewer than two attached flows: no evidence either way).
    /// <para>
    /// The positive case is checked first: a second attached flow with a response proves the server
    /// multiplexes flows, which is the safe direction to be wrong in — a false
    /// <see cref="UdpServerCapability.PerFlowOnly"/> costs only the sharing win, while a false
    /// <see cref="UdpServerCapability.SharedOk"/> costs datagrams.
    /// </para>
    /// </summary>
    internal static UdpServerCapability? Evaluate(UdpAssociationEvidence[] evidence, int attached)
    {
        if (attached < 2) return null;
        return (CountResponders(evidence, attached), HasPinningSuspicion(evidence, attached)) switch
        {
            ( >= 2, _) => UdpServerCapability.SharedOk,
            (1, true) => UdpServerCapability.PerFlowOnly,
            _ => null,
        };
    }

    private static int CountResponders(UdpAssociationEvidence[] evidence, int attached)
    {
        var responders = 0;
        for (var index = 0; index < attached; index++)
        {
            if (evidence[index].SawResponse) responders++;
        }

        return responders;
    }

    private static bool HasPinningSuspicion(UdpAssociationEvidence[] evidence, int attached)
    {
        for (var index = 0; index < attached; index++)
        {
            var lease = evidence[index];
            if (lease is { DatagramsSent: >= PinningSuspicionThreshold, SawResponse: false }) return true;
        }

        return false;
    }
}
