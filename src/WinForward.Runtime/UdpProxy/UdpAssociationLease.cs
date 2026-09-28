using System.Net;
using System.Net.Sockets;

namespace WinForward.Runtime.UdpProxy;

/// <summary>
/// The handle a transport borrows from <see cref="UdpAssociationPool"/>: a thin view over one
/// association (the association reference plus one flow lease) so an in-place re-association is
/// visible through it without any per-lease update — every read goes through to the association's
/// current state. Releasing the reference exactly once is the lease's only mutation, and the
/// association object outlives its leases, so reads stay safe after release.
/// </summary>
internal sealed class UdpAssociationLease : IAsyncDisposable
{
    private readonly UdpControlAssociation _association;
    private readonly UdpAssociationEvidence _evidence;

    /// <summary>The pool-scope lease: it is what makes the pool's drain join this flow's holder.</summary>
    private WorkLease _poolLease;
    private int _released;

    internal UdpAssociationLease(UdpControlAssociation association, UdpAssociationEvidence evidence, WorkLease poolLease)
    {
        _association = association;
        _evidence = evidence;
        _poolLease = poolLease;
    }

    /// <summary>The relay the flow must currently send to and accept responses from.</summary>
    internal IPEndPoint RelayEndpoint => _association.RelayEndpoint;

    /// <summary>
    /// The association's current relay publication, pre-serialized for the allocation-free send
    /// path. Comparing this one reference is how a transport detects an in-place re-association and
    /// how it reads the endpoint belonging to that same re-association.
    /// </summary>
    internal UdpRelayTarget RelayTarget => _association.RelayTarget;

    /// <summary>The relay's address family; fixed for the lease's lifetime because a family change faults the association.</summary>
    internal AddressFamily RelayAddressFamily => _association.RelayAddressFamily;

    /// <summary>True when the association died without recovering in place.</summary>
    internal bool IsFaulted => _association.IsFaulted;

    /// <summary>The association's stored fault, or null while it is healthy.</summary>
    internal Exception? Fault => _association.Fault;

    /// <summary>
    /// Records one datagram this lease sent successfully: the capability sampler's send-side
    /// evidence. One interlocked increment, no allocation — this is the whole hot-path addition (I3).
    /// </summary>
    internal void RecordDatagramSent() => _evidence.RecordDatagramSent();

    /// <summary>
    /// Records the first relay datagram this lease decoded successfully: the capability sampler's
    /// receive-side evidence. A skipped (never decoded) relay datagram must not reach this call.
    /// </summary>
    internal void RecordResponseReceived() => _evidence.RecordResponseReceived();

    /// <summary>Releases the association reference and the pool-scope lease, exactly once.</summary>
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _released, 1) != 0) return ValueTask.CompletedTask;
        _poolLease.Dispose();
        return _association.ReleaseLeaseAsync(_evidence);
    }
}
