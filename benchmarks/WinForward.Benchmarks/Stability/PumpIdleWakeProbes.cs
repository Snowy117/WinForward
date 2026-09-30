using System.Diagnostics;
using WinForward.NdisApi;

namespace WinForward.Benchmarks.Stability;

/// <summary>
/// The probe types behind the <c>pump</c> stability scenario's rows: the readers, counters and
/// arrival-signal decorators that make each row's counts exact. The scenario file keeps the row
/// orchestration and the reading conventions.
/// </summary>
internal sealed record IdleSample(long Polls, long ReadCalls, double Seconds, double CpuSeconds, long AllocatedBytes)
{
    public double PollsPerSecond => Polls / Seconds;

    public double CpuSecondsPerIdleSecond => CpuSeconds / Seconds;

    public double ReadCallsPerPoll => (double)ReadCalls / Math.Max(1, Polls);
}

internal sealed record WakeResult(int Wakes, long FrameReadCalls, long Packets, LatencyDistribution Latency)
{
    public double ReadsPerWake => (double)FrameReadCalls / Wakes;

    public double ReadCallsPerPoll => (double)FrameReadCalls / Wakes;

    public double ReadCallsPerPacket => (double)FrameReadCalls / Math.Max(1, Packets);
}

/// <summary>Batch-completed callback counter: one increment per loop iteration, plus one on run exit.</summary>
internal sealed class IterationCounter
{
    private long _count;

    public long Count => Interlocked.Read(ref _count);

    public void Increment() => Interlocked.Increment(ref _count);

    public void Reset() => Interlocked.Exchange(ref _count, 0);
}

/// <summary>
/// An always-empty reader that counts its calls. Reads pass straight through during the warmup; the
/// first call after <see cref="ArmGate"/> parks on the harness gate and signals entry, so the
/// harness can reset the counters and start its clocks with the pump parked inside a read and no
/// partly-counted poll in the window. <see cref="Release"/> clears the arming before releasing, so
/// the poll loop pays the gate only once.
/// </summary>
internal sealed class GatedEmptyCaptureReader : INdisPacketReader
{
    private readonly ManualResetEventSlim _entered = new(initialState: false);
    private readonly ManualResetEventSlim _release = new(initialState: false);
    private long _reads;
    private int _armed;

    public long Reads => Interlocked.Read(ref _reads);

    public int TryReadPackets(nint adapterHandle, NdisPacketBuffer[] buffers)
    {
        if (Volatile.Read(ref _armed) != 0)
        {
            _entered.Set();
            _release.Wait();
        }

        // Counted after the gate so the parked call is not wiped by the harness's reset: the read
        // that completes inside the window is the window's first poll.
        Interlocked.Increment(ref _reads);
        return 0;
    }

    public void ArmGate() => Volatile.Write(ref _armed, 1);

    public bool WaitUntilEntered(TimeSpan timeout) => _entered.Wait(timeout);

    public void Release()
    {
        Volatile.Write(ref _armed, 0);
        _release.Set();
    }

    public void ResetReads() => Interlocked.Exchange(ref _reads, 0);
}

/// <summary>
/// The F5.2 shape: <c>TryReadPackets</c> announces that it is about to block, then blocks until the
/// harness arms exactly one frame, then returns 1. The pump thread is therefore parked inside the
/// read between wakes, and the harness's arm timestamp is the arrival the handler's dispatch is
/// measured against. Only frame-delivering reads are counted in <see cref="ReadCalls"/>, which keeps
/// the per-wake accounting stable while the pump is parked in the next read. <see cref="Stop"/>
/// releases the parked read with an empty result, because a stop cannot cancel a read blocked in a
/// semaphore wait.
/// </summary>
internal sealed class SignallingCaptureReader(byte[] frame) : INdisPacketReader, IDisposable
{
    private readonly SemaphoreSlim _armed = new(initialCount: 0);
    private readonly AutoResetEvent _parked = new(initialState: false);
    private long _readCalls;
    private long _emptyReads;
    private long _packets;
    private int _stopped;

    public long ReadCalls => Interlocked.Read(ref _readCalls);

    public long EmptyReads => Interlocked.Read(ref _emptyReads);

    public long Packets => Interlocked.Read(ref _packets);

    public int TryReadPackets(nint adapterHandle, NdisPacketBuffer[] buffers)
    {
        if (Volatile.Read(ref _stopped) != 0)
        {
            Interlocked.Increment(ref _emptyReads);
            return 0;
        }

        _parked.Set();
        _armed.Wait();
        if (Volatile.Read(ref _stopped) != 0)
        {
            Interlocked.Increment(ref _emptyReads);
            return 0;
        }

        buffers[0].SetFrame(frame, NdisApiAbi.PacketFlagOnReceive, adapterHandle);
        Interlocked.Increment(ref _readCalls);
        Interlocked.Increment(ref _packets);
        return 1;
    }

    /// <summary>Blocks until the reader has announced that it is entering its blocking wait.</summary>
    public bool WaitUntilParked(TimeSpan timeout) => _parked.WaitOne(timeout);

    public void Arm() => _armed.Release();

    public void Stop()
    {
        Volatile.Write(ref _stopped, 1);
        _armed.Release();
    }

    public void Dispose()
    {
        _armed.Dispose();
        _parked.Dispose();
    }
}

/// <summary>
/// Dispatch timestamps taken in the handler the pump calls: timestamp first, then a release-fenced
/// dispatch count, so the harness only reads a slot after the count proves it was written. The
/// handler runs on the pump thread alone, so the slot cursor needs no interlocked update.
/// </summary>
internal sealed class WakeRecorder(int capacity)
{
    private readonly long[] _timestamps = new long[capacity];
    private long _dispatched;
    private int _cursor;

    public long Dispatched => Volatile.Read(ref _dispatched);

    public ValueTask RecordDispatchAsync()
    {
        var index = _cursor;
        _cursor = index + 1;
        _timestamps[index] = Stopwatch.GetTimestamp();
        Volatile.Write(ref _dispatched, index + 1);
        return ValueTask.CompletedTask;
    }

    public long TimestampAt(long index) => Volatile.Read(ref _timestamps[index]);
}

/// <summary>
/// The <c>pump.idleWakeEvent</c> row's park confirmation: a thin decorator over the production
/// signal that records entry into <see cref="Wait"/> and delegates, so the production wait is still
/// what blocks. Auto-reset makes the entry count load-bearing — an arm-and-set issued before the
/// pump parks would be retained and return immediately — so the harness waits for one more entry
/// than the previous wake before it arms, and a missing entry fails the row instead of reporting a
/// hot handoff as a wake.
/// </summary>
internal sealed class ParkConfirmingArrivalSignal(INdisPacketArrivalSignal inner) : INdisPacketArrivalSignal
{
    private long _entered;
    private long _returnedBeforeTimeout;
    private long _lastTimeoutTicks;

    public long Entered => Interlocked.Read(ref _entered);

    public long ReturnedBeforeTimeout => Interlocked.Read(ref _returnedBeforeTimeout);

    public TimeSpan LastTimeout => TimeSpan.FromTicks(Interlocked.Read(ref _lastTimeoutTicks));

    public bool Wait(TimeSpan timeout)
    {
        Interlocked.Increment(ref _entered);
        Interlocked.Exchange(ref _lastTimeoutTicks, timeout.Ticks);
        var returned = inner.Wait(timeout);
        if (returned) Interlocked.Increment(ref _returnedBeforeTimeout);
        return returned;
    }

    /// <summary>Zeroes the counters, so a window can report exactly what happened inside it.</summary>
    public void ResetCounters()
    {
        Interlocked.Exchange(ref _entered, 0);
        Interlocked.Exchange(ref _returnedBeforeTimeout, 0);
    }

    public void Dispose() => inner.Dispose();
}

/// <summary>
/// The <c>pump.idleWakeEvent</c> row's reader: never blocks. It returns one armed frame when the
/// harness has armed one and an empty queue otherwise, so the pump parks in the arrival wait — not
/// in the read — between wakes. Only frame-delivering reads are counted in <see cref="FrameReads"/>,
/// which keeps the per-wake accounting stable while the pump sits at the top of its next iteration.
/// </summary>
internal sealed class ArmedFrameCaptureReader(byte[] frame) : INdisPacketReader
{
    private int _armed;
    private int _stopped;
    private long _frameReads;
    private long _emptyReads;
    private long _packets;

    public long FrameReads => Interlocked.Read(ref _frameReads);

    public long EmptyReads => Interlocked.Read(ref _emptyReads);

    public long Packets => Interlocked.Read(ref _packets);

    public int TryReadPackets(nint adapterHandle, NdisPacketBuffer[] buffers)
    {
        if (Volatile.Read(ref _stopped) != 0 || Interlocked.Exchange(ref _armed, 0) == 0)
        {
            Interlocked.Increment(ref _emptyReads);
            return 0;
        }

        buffers[0].SetFrame(frame, NdisApiAbi.PacketFlagOnReceive, adapterHandle);
        Interlocked.Increment(ref _frameReads);
        Interlocked.Increment(ref _packets);
        return 1;
    }

    public void Arm() => Volatile.Write(ref _armed, 1);

    public void Stop() => Volatile.Write(ref _stopped, 1);
}
