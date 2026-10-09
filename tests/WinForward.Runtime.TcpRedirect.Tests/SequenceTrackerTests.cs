using System.Net;
using System.Reflection;
using WinForward.Core;
using Xunit;
using static WinForward.TestSupport.TcpCoordinatorFakes;

namespace WinForward.Runtime.TcpRedirect.Tests;

/// <summary>
/// The atomic sequence-tracker contract: two <see cref="long"/> words per association
/// (<c>-1</c> unobserved) written by a CAS-max loop and read with <c>Volatile.Read</c>.
/// Retransmissions and pure ACKs never move a tracker backwards.
/// </summary>
public sealed class SequenceTrackerTests
{
    [Fact]
    public void TcpRedirectAssociationHoldsNoLockField()
    {
        var fields = typeof(TcpRedirectAssociation).GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        Assert.DoesNotContain(fields, field => !field.FieldType.IsValueType);
    }

    [Fact]
    public void UnobservedTrackerReadsNullAndObservedZeroReadsZero()
    {
        var association = CreateHostAssociation(Endpoint.From(IPAddress.Loopback, 40_000));

        Assert.Null(association.ClientNextSeq);
        Assert.Null(association.ServerNextSeq);

        association.ObserveClientSequence(0);
        Assert.Equal(0u, association.ClientNextSeq);

        // 0xFFFFFFFF is a legal sequence: the sentinel must not collide with it.
        association.ObserveServerSequence(uint.MaxValue);
        Assert.Equal(uint.MaxValue, association.ServerNextSeq);

        // A lower candidate never moves a tracker backwards (retransmissions and pure ACKs).
        association.ObserveClientSequence(1);
        Assert.Equal(1u, association.ClientNextSeq);
        association.ObserveClientSequence(0);
        Assert.Equal(1u, association.ClientNextSeq);
    }

    [Fact]
    public void ConcurrentSequenceObservationsKeepTheLargerValue()
    {
        var association = CreateHostAssociation(Endpoint.From(IPAddress.Loopback, 40_000));
        const uint baseSequence = 1_000_000;
        const int perThread = 20_000;
        var barrier = new Barrier(3);
        var writersDone = new CountdownEvent(2);
        var first = new Thread(() => ObserveBurst(baseSequence));
        var second = new Thread(() => ObserveBurst(baseSequence + perThread));
        first.Start();
        second.Start();

        var sawValue = false;
        var decreases = 0;
        uint last = 0;
        try
        {
            barrier.SignalAndWait();
            // `CountdownEvent.IsSet` is the thread-safe completion flag, so the observation loop
            // overlaps the writers instead of polling a shared mutable counter.
            while (!writersDone.IsSet)
            {
                Observe();
            }
        }
        finally
        {
            // Join before disposing: every thread that captured the barrier and the countdown has
            // finished before either handle is released.
            first.Join();
            second.Join();
            barrier.Dispose();
            writersDone.Dispose();
        }

        // One observation after the join: under suite load the reader may be descheduled across the
        // whole burst, so the loop alone does not guarantee it ever read a tracked value.
        Observe();

        Assert.True(sawValue, "the reader never observed a tracked sequence");
        Assert.Equal(0, decreases);
        Assert.Equal(baseSequence + (2 * perThread) - 1, association.ClientNextSeq);

        // ReSharper disable AccessToDisposedClosure -- the finally above joins both writer threads before
        // the barrier or the countdown is disposed, so nothing that captured them outlives its handle.
        // ReSharper disable once SeparateLocalFunctionsWithJumpStatement -- these are the method's last
        // members and are blank-line separated; moving them further buys no readability.
        void Observe()
        {
            if (association.ClientNextSeq is not { } current) return;
            sawValue = true;
            if (current < last) decreases++;
            if (current > last) last = current;
        }

        void ObserveBurst(uint start)
        {
            barrier.SignalAndWait();
            for (var index = 0; index < perThread; index++) association.ObserveClientSequence(start + (uint)index);
            writersDone.Signal();
        }
        // ReSharper restore AccessToDisposedClosure
    }
}
