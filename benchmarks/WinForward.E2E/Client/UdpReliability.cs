using System.Diagnostics;
using System.Runtime.InteropServices;

namespace WinForward.E2E.Client;

internal sealed class SequenceBitmap
{
    private ulong[] _words = new ulong[1024];

    internal bool TrySet(long index)
    {
        // Sequence numbers arrive from the network and a single flipped byte can set the high bit,
        // so the index is untrusted: without this bound the word offset goes negative and the
        // resize loop below overflows to zero and spins forever.
        if (index is < 0 or > UdpReliabilityTracker.MaxSequence)
        {
            OutOfRange++;
            return false;
        }

        var word = index >> 6;
        if (word >= _words.Length)
        {
            var capacity = _words.Length;
            while (capacity <= word)
            {
                capacity *= 2;
            }

            Array.Resize(ref _words, capacity);
        }

        var mask = 1UL << (int)(index & 63);
        if ((_words[word] & mask) != 0)
        {
            return false;
        }

        _words[word] |= mask;
        return true;
    }

    /// <summary>
    /// Un-books a sequence <see cref="TrySet"/> accepted. Used by an arm that registered a datagram
    /// before handing it to the socket so a concurrent reply could still be matched, and then found
    /// the socket refused it.
    /// </summary>
    internal bool TryClear(long index)
    {
        if (index < 0 || (index >> 6) >= _words.Length)
        {
            return false;
        }

        var mask = 1UL << (int)(index & 63);
        if ((_words[index >> 6] & mask) == 0)
        {
            return false;
        }

        _words[index >> 6] &= ~mask;
        return true;
    }

    internal long OutOfRange { get; private set; }

    internal bool IsSet(long index)
    {
        if (index < 0 || (index >> 6) >= _words.Length)
        {
            return false;
        }

        return (_words[index >> 6] & (1UL << (int)(index & 63))) != 0;
    }

}

[StructLayout(LayoutKind.Auto)]
internal readonly struct LossCounts
{
    /// <summary>
    /// Sent datagrams that arrived no later than W after the instant they were scheduled for.
    /// </summary>
    internal long Arrived { get; init; }

    /// <summary>
    /// Sent datagrams that arrived, but more than W after the instant they were scheduled for.
    /// </summary>
    internal long Late { get; init; }

    /// <summary>
    /// Sent datagrams that were still missing once their whole W had elapsed: path loss.
    /// </summary>
    internal long Never { get; init; }

    /// <summary>
    /// Sent datagrams whose W had not elapsed when observation stopped, so neither arrival nor loss
    /// can be claimed for them.
    /// </summary>
    internal long Undetermined { get; init; }

    internal long Duplicate { get; init; }

    internal long Reordered { get; init; }

    /// <summary>
    /// Corrupt arrivals, including those whose sequence could not be tied to a sent datagram.
    /// </summary>
    internal long Corrupt { get; init; }

    /// <summary>
    /// Sent datagrams for which a corrupt arrival was booked. Distinct from <see cref="Corrupt"/>,
    /// which counts arrivals and therefore also counts replay of a frame already booked corrupt.
    /// </summary>
    internal long CorruptDatagrams { get; init; }
}

/// <summary>
/// Books what the client offered, what the socket accepted and what came back, so that every
/// published number is derived from one accounting: a datagram is sent once, resolved at most once
/// by an arrival or a verdict, retires from the in-flight window when its W elapses, and lands in
/// exactly one classification bucket.
/// </summary>
internal sealed class UdpReliabilityTracker
{
    // The heaviest planned arm offers 500 datagrams a second for two minutes, so a ceiling of a
    // quarter million sequences is far above any legitimate index and still bounds what a corrupt
    // one can allocate.
    internal const long MaxSequence = (1L << 18) - 1;

    private readonly SequenceBitmap _sent = new();
    private readonly SequenceBitmap _arrived = new();
    private readonly SequenceBitmap _corruptAt = new();
    private long[] _sendTicks = new long[4096];
    private int[] _arrivalMilliseconds = new int[4096];
    private long _highestSent;
    private long _highestArrived;
    private long _retireBelow = 1;

    internal long Supplied { get; private set; }

    internal long SentOk { get; private set; }

    internal long SendWouldBlock { get; private set; }

    internal long SendFailure { get; private set; }

    internal long WindowOverflow { get; private set; }

    private long Corrupt { get; set; }

    private long Duplicate { get; set; }

    private long Reordered { get; set; }

    internal long UnmatchedReplies { get; private set; }

    /// <summary>
    /// Datagrams that arrived carrying a connection id this socket never used. A reply from another
    /// flow is internally consistent -- its checksum and filler both validate against its own id --
    /// so without this check it registers as a legitimate arrival and the sequence it displaced is
    /// reported as loss. That is exactly what a proxy which mixes datagrams between flows produces.
    /// </summary>
    internal long ForeignConnection { get; private set; }

    internal long ReceivedDatagrams { get; private set; }

    internal long ReceivedBytes { get; private set; }

    /// <summary>
    /// Sequences the bitmaps refused because they fell outside <see cref="MaxSequence"/>. A non-zero
    /// value means part of the offered schedule was never tracked, so the published classification
    /// covers fewer datagrams than <see cref="SentOk"/> claims: it is a measurement caveat, not a
    /// detail, and it is published rather than left silent.
    /// </summary>
    internal long OutOfRange => _sent.OutOfRange + _arrived.OutOfRange + _corruptAt.OutOfRange;

    /// <summary>
    /// Sent datagrams whose W has not elapsed and which no arrival or verdict has resolved. A
    /// datagram that never arrives has to fall out of this window on its own: a slot held for the
    /// rest of the arm would stop the client from sending at all, and the measured population would
    /// then be selected by the product's own failure.
    /// </summary>
    internal long Outstanding { get; private set; }

    internal bool WasSent(long sequence) => _sent.IsSet(sequence);

    internal void MarkSupplied() => Supplied++;

    /// <summary>
    /// Records one datagram handed to the socket. Sequences must be handed out in increasing order
    /// with non-decreasing <paramref name="sendTicks"/>, which is what every arm's pacer produces;
    /// retirement advances a single monotone pointer and relies on that order.
    /// </summary>
    internal void MarkSent(long sequence, long sendTicks)
    {
        Ensure(sequence);
        _sent.TrySet(sequence);
        _sendTicks[sequence] = sendTicks;
        _arrivalMilliseconds[sequence] = -1;
        if (sequence > _highestSent)
        {
            _highestSent = sequence;
        }

        SentOk++;
        Outstanding++;
    }

    internal void MarkWouldBlock() => SendWouldBlock++;

    internal void MarkSendFailure() => SendFailure++;

    /// <summary>
    /// Un-books a datagram <see cref="MarkSent"/> already registered because the socket refused the
    /// send. An arm that registers before the call -- so a concurrent receive loop can match a reply
    /// to a sequence the socket has not finished accepting -- must book the refusal as client send
    /// loss here, or the datagram stays in the sent population and is later reported as path loss.
    /// </summary>
    internal void MarkSendRefused(long sequence)
    {
        SendFailure++;
        if (!_sent.TryClear(sequence))
        {
            return;
        }

        SentOk--;
        if (sequence >= _retireBelow)
        {
            Outstanding--;
        }
    }

    internal void MarkWindowOverflow() => WindowOverflow++;

    internal void MarkUnmatchedReply() => UnmatchedReplies++;

    internal void MarkForeignConnection() => ForeignConnection++;

    internal void MarkCorrupt() => Corrupt++;

    internal void MarkCorruptWithKnownSequence(long sequence)
    {
        Corrupt++;
        if (_arrived.IsSet(sequence) || !_corruptAt.TrySet(sequence))
        {
            Duplicate++;
            return;
        }

        ReceivedDatagrams++;
        if (_sent.IsSet(sequence))
        {
            ResolveSlot(sequence);
        }
    }

    /// <summary>
    /// Books an arrival for a sequence this socket sent. Replies for a sequence it never sent and
    /// replies carrying another flow's connection id are booked by the caller instead, so they
    /// cannot enter the loss or ordering accounting.
    /// </summary>
    internal void MarkArrival(long sequence, long nowTicks, int payloadBytes)
    {
        if (!_arrived.TrySet(sequence))
        {
            Duplicate++;
            return;
        }

        ReceivedDatagrams++;
        ReceivedBytes += payloadBytes;

        if (!_sent.IsSet(sequence))
        {
            return;
        }

        // RFC 4737 calls a packet reordered when it arrives after one with a higher sequence number
        // has already arrived. Arriving after a higher sequence was merely sent is not reordering.
        if (sequence < _highestArrived)
        {
            Reordered++;
        }
        else
        {
            _highestArrived = sequence;
        }

        var elapsedTicks = nowTicks - _sendTicks[sequence];
        var milliseconds = (int)(elapsedTicks * 1000.0 / Stopwatch.Frequency);
        _arrivalMilliseconds[sequence] = Math.Clamp(milliseconds, 0, 65535);
        ResolveSlot(sequence);
    }

    /// <summary>
    /// Releases every send slot whose W has elapsed. This is what keeps <see cref="Outstanding"/>
    /// meaning "sent within the last W and not yet resolved" rather than "sent and not yet seen".
    /// </summary>
    internal void Retire(long nowTicks, long windowTicks)
    {
        while (_retireBelow <= _highestSent)
        {
            var sequence = _retireBelow;
            // Send instants rise with the sequence number, so the first sequence still inside its
            // window bounds every later one and the scan can stop here.
            if (_sent.IsSet(sequence) && _sendTicks[sequence] + windowTicks > nowTicks)
            {
                return;
            }

            _retireBelow++;
            if (_sent.IsSet(sequence) && !IsResolved(sequence))
            {
                Outstanding--;
            }
        }
    }

    /// <summary>
    /// Classifies the datagrams actually sent against <paramref name="windowTicks"/>, given the
    /// instant observation stopped. Every sent datagram lands in exactly one bucket, so
    /// arrived + late + never + undetermined + corruptDatagrams == sent by construction; the
    /// published corrupt counter counts arrivals and is deliberately not part of that sum. A
    /// schedule index skipped by a window overflow was never handed to the socket, so it is not
    /// classified at all and cannot be reported as lost.
    /// </summary>
    internal LossCounts Classify(long windowTicks, long observationEndTicks)
    {
        Retire(observationEndTicks, windowTicks);

        long arrived = 0;
        long late = 0;
        long never = 0;
        long undetermined = 0;
        long corruptDatagrams = 0;
        var windowMilliseconds = windowTicks * 1000.0 / Stopwatch.Frequency;

        for (var sequence = 1L; sequence <= _highestSent; sequence++)
        {
            if (!_sent.IsSet(sequence))
            {
                continue;
            }

            if (IsResolved(sequence))
            {
                if (_corruptAt.IsSet(sequence))
                {
                    corruptDatagrams++;
                }
                else if (_arrivalMilliseconds[sequence] > windowMilliseconds)
                {
                    late++;
                }
                else
                {
                    arrived++;
                }

                continue;
            }

            if (sequence < _retireBelow)
            {
                never++;
            }
            else
            {
                undetermined++;
            }
        }

        return new LossCounts
        {
            Arrived = arrived,
            Late = late,
            Never = never,
            Undetermined = undetermined,
            Duplicate = Duplicate,
            Reordered = Reordered,
            Corrupt = Corrupt,
            CorruptDatagrams = corruptDatagrams,
        };
    }

    private bool IsResolved(long sequence) => _arrived.IsSet(sequence) || _corruptAt.IsSet(sequence);

    private void ResolveSlot(long sequence)
    {
        // A slot already retired when its W elapsed was released then, and releasing it twice would
        // let the window hold more datagrams than it declares.
        if (sequence >= _retireBelow)
        {
            Outstanding--;
        }
    }

    private void Ensure(long sequence)
    {
        if (sequence is < 0 or > MaxSequence)
        {
            return;
        }

        var required = (int)sequence + 1;
        if (required <= _sendTicks.Length)
        {
            return;
        }

        var capacity = _sendTicks.Length;
        while (capacity < required)
        {
            capacity *= 2;
        }

        Array.Resize(ref _sendTicks, capacity);
        Array.Resize(ref _arrivalMilliseconds, capacity);
    }
}

internal static class UdpLossMath
{
    /// <summary>
    /// The loss threshold used when a plan does not declare one. W is a declared, published
    /// parameter (RFC 2680 style) and is never derived from a latency histogram: a derived W
    /// silently collapsed to this floor in every arm that recorded no UDP RTT of its own.
    /// </summary>
    internal const int DefaultWindowMilliseconds = 200;

    internal static long WindowTicks(int windowMilliseconds) =>
        (long)(windowMilliseconds / 1000.0 * Stopwatch.Frequency);
}
