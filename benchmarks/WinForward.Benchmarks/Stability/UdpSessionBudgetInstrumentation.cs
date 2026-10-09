using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using WinForward.Core;
using WinForward.Runtime.UdpProxy;

namespace WinForward.Benchmarks.Stability;

/// <summary>
/// The session-budget soak's measurement vocabulary: the per-sample resource record, the process
/// sampler, the first-response sink, and the pure arithmetic the scenario's acceptance assertions
/// are written in. The scenario drives the phases and owns the assertions; the quantities and their
/// thresholds live here, mirroring the burst instrument's split of measurement from scenario.
/// </summary>
internal static class SessionBudgetMath
{
    /// <summary>
    /// The descriptor slack the acceptance allows per live session on top of its relay socket: the
    /// per-session cost is one relay socket (bookkeeping must not create a descriptor), and 25 %
    /// absorbs transient teardown/setup overlap at a sample instant. The shipped per-flow shape adds
    /// one control connection per live session, charged separately by the control-connection term.
    /// </summary>
    internal const double DescriptorSlackPerSession = 1.25;

    /// <summary>
    /// The steady-state population bound: a flow that never sends again is released after
    /// <paramref name="idleTimeout"/> plus the next sweeper tick (<paramref name="sweepInterval"/>),
    /// so the live set tracks <c>rate × (idle + 2 × sweep)</c> once saturated instead of growing with
    /// the cumulative flow count. The doubled sweep term is the worst case (a session can be created
    /// just after a tick and wait almost two intervals); <paramref name="margin"/> adds arrival jitter.
    /// </summary>
    internal static int SteadyStateSessionCeiling(int rate, TimeSpan idleTimeout, TimeSpan sweepInterval, int margin)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rate);
        return checked((int)Math.Ceiling(rate * (idleTimeout + (2 * sweepInterval)).TotalSeconds)) + margin;
    }

    /// <summary>
    /// The arrival jitter a steady-state sample may exceed the retention ceiling by: two seconds of
    /// arrivals plus a fixed floor.
    /// </summary>
    internal static int SteadyStateMargin(int rate) => Math.Max(32, 2 * rate);

    /// <summary>
    /// The descriptor budget for a live population: one relay socket per session with
    /// <see cref="DescriptorSlackPerSession"/> slack, plus one control connection per association —
    /// charged from the server's own live count when it was observed, and from the shipped
    /// one-association-per-flow allowance when it was not.
    /// </summary>
    internal static long FileDescriptorBudget(long sessions, long associations)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sessions);
        ArgumentOutOfRangeException.ThrowIfNegative(associations);
        return checked((long)Math.Ceiling(sessions * DescriptorSlackPerSession)) + associations;
    }

    /// <summary>
    /// The smallest churn window (whole seconds) whose cumulative flow count exceeds
    /// <paramref name="ceiling"/>. Below it a run that never expires a session would still fit the
    /// retention bound, so the assertion would claim nothing and the run must be refused.
    /// </summary>
    internal static int MinimumDiscriminatingChurnSeconds(int rate, int ceiling)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rate);
        ArgumentOutOfRangeException.ThrowIfNegative(ceiling);
        return (ceiling / rate) + 1;
    }

    /// <summary>
    /// The refusal sentence for a churn window that cannot discriminate retention from accumulation,
    /// or null when it is long enough; the pre-run check and the acceptance verdict share it, so the
    /// two cannot drift apart.
    /// </summary>
    internal static string? NonDiscriminatingChurnWindow(int rate, int churnSeconds, int ceiling)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rate);
        ArgumentOutOfRangeException.ThrowIfNegative(churnSeconds);
        var minimum = MinimumDiscriminatingChurnSeconds(rate, ceiling);
        if (churnSeconds >= minimum) return null;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"A {churnSeconds} s churn window at {rate} flows/s creates {(long)rate * churnSeconds} flows, at or below the {ceiling} retention ceiling, so a run that never expires a session would still fit it and the ceiling cannot discriminate retention from accumulation: raise --churn-seconds above {minimum - 1} (at least {minimum} s at this rate), or lower --rate.");
    }

    /// <summary>
    /// The retention bound on the estimated kernel receive buffer: the session ceiling multiplied by
    /// the configured per-session relay buffer, the same <c>live sessions × configured buffer</c>
    /// product the heartbeat reports. The budget is the byte form of the session ceiling, so the
    /// reported bytes are a checked acceptance term instead of a reported-only number.
    /// </summary>
    internal static long RelayReceiveBufferBudget(int sessionCeiling, int relayReceiveBufferBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sessionCeiling);
        ArgumentOutOfRangeException.ThrowIfNegative(relayReceiveBufferBytes);
        return (long)sessionCeiling * relayReceiveBufferBytes;
    }

    /// <summary>A per-session ratio; zero for an empty population instead of an infinity that a row cannot carry.</summary>
    internal static double Ratio(long numerator, long denominator) => denominator <= 0 ? 0.0 : numerator / (double)denominator;
}

/// <summary>
/// The churn window's arrival schedule: one deadline every 1 / rate seconds, anchored at the window's
/// own start rather than at run construction — a slow warm-up must not burst the first arrivals, and a
/// sweep that overran its slot is caught up immediately instead of shifting the realized rate.
/// </summary>
internal sealed class SessionBudgetArrivals(TimeSpan start, TimeSpan tick)
{
    private TimeSpan _next = start;

    /// <summary>Advances to the next deadline and returns the wait until it; a negative value means the deadline already passed.</summary>
    internal TimeSpan Advance(TimeSpan elapsed)
    {
        _next += tick;
        return _next - elapsed;
    }
}

/// <summary>
/// One sampled instant: the process's descriptor and memory shape, plus the harness SOCKS5 server's own
/// view of the connections it serves. <see cref="HarnessControlConnections"/> is its live count, null
/// when it ran out of process (<c>--socks5-external</c>); <see cref="HarnessServerConnections"/> is the
/// two sockets it owns per live connection (the control socket and that connection's relay socket).
/// </summary>
[StructLayout(LayoutKind.Auto)]
internal readonly record struct ResourceSample(int ProxyFileDescriptors, int RawFileDescriptors, int HarnessServerConnections, int? HarnessControlConnections, long ManagedBytes, long WorkingSetBytes);

/// <summary>
/// Samples the process's descriptor and memory footprint for the session-budget series. Descriptors
/// come from <c>/proc/self/fd</c> on Linux — the soak host, where every socket is a descriptor — with
/// <see cref="Process.HandleCount"/> as the Windows fallback. When a harness server runs inside this
/// process, the two sockets it owns per live connection
/// (<see cref="LoopbackSocks5UdpServer.ConnectionCount"/>'s control socket and relay socket,
/// <see cref="LoopbackSocks5UotServer.ConnectionCount"/>'s accepted stream socket and upstream UDP
/// socket) are subtracted, because the acceptance is about the proxy's own sockets. The same read
/// supplies the rows' <c>associations</c> column, so it is taken once per sample; it is null only
/// when neither server was hosted.
/// </summary>
internal sealed class ProcessResourceSampler(LoopbackSocks5UdpServer? inProcessServer, LoopbackSocks5UotServer? inProcessUotServer = null) : IDisposable
{
    private readonly Process _process = Process.GetCurrentProcess();

    public ResourceSample Sample()
    {
        _process.Refresh();
        var raw = OpenFileDescriptors();
        var liveControls = inProcessServer is null && inProcessUotServer is null
            ? (int?)null
            : (inProcessServer?.ConnectionCount ?? 0) + (inProcessUotServer?.ConnectionCount ?? 0);
        var harness = liveControls is { } controls ? 2 * controls : 0;
        return new ResourceSample(raw - harness, raw, harness, liveControls, GC.GetTotalMemory(forceFullCollection: false), _process.WorkingSet64);
    }

    public void Dispose() => _process.Dispose();

    /// <summary>
    /// The process's open descriptor count. Linux exposes every socket, pipe, and file as a
    /// <c>/proc/self/fd</c> entry; Windows has no equivalent directory, so the handle count is the
    /// closest fallback (sockets are handles there and dominate the count in this scenario).
    /// </summary>
    internal static int OpenFileDescriptors()
    {
        if (OperatingSystem.IsLinux()) return Directory.GetFiles("/proc/self/fd").Length;
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        return process.HandleCount;
    }
}

/// <summary>
/// The soak's response sink: first-response ticks per flow id, split into the warm-up and churn
/// populations so the churn accounting can never credit a warm-up echo. One datagram per flow means
/// the first response is also the only one, so the record doubles as the loss and latency source.
/// <para>
/// A response counts only for the flow it arrived on: the payload carries its sender's flow id, the
/// <c>originalFlow</c> argument is the flow whose relay socket the reply came in on, and a server may
/// answer from a different endpoint or write to its last sender, so a foreign reply is marked against
/// its sender instead and never advances a timestamp.
/// </para>
/// </summary>
internal sealed class SessionBudgetSink(int churnOffset, int flowCapacity, FlowKey[] flowKeys) : IUdpResponseSink
{
    private readonly OwnershipFlowSet _misdeliveredFlows = new(flowCapacity);
    private readonly long[] _firstResponseTicks = new long[flowCapacity];
    private long _warmupFirstResponses;
    private long _churnFirstResponses;
    private long _warmupMisdeliveredFlows;
    private long _churnMisdeliveredFlows;

    public long WarmupFirstResponses => Interlocked.Read(ref _warmupFirstResponses);

    public long ChurnFirstResponses => Interlocked.Read(ref _churnFirstResponses);

    /// <summary>How many flows had their echo delivered to a different flow.</summary>
    public long Misdelivered => Interlocked.Read(ref _warmupMisdeliveredFlows) + Interlocked.Read(ref _churnMisdeliveredFlows);

    /// <summary>How many of the churn population's flows had their echo delivered to a different flow; never advances a timestamp.</summary>
    public long ChurnMisdeliveredFlows => Interlocked.Read(ref _churnMisdeliveredFlows);

    /// <summary>
    /// Opens the churn population: the marks already taken on churn flow ids are cleared and their
    /// counter reset, so <see cref="ChurnMisdeliveredFlows"/> counts only misdelivery the churn window
    /// observed while the warm-up population's marks stay in <see cref="Misdelivered"/>. A mark belongs
    /// to the reply's sender, so an echo that arrived elsewhere before its flow had sent is not yet
    /// evidence about the churn window.
    /// </summary>
    public void BeginChurn()
    {
        for (var flowId = churnOffset; flowId < _firstResponseTicks.Length; flowId++)
        {
            if (_misdeliveredFlows.Unmark(flowId)) Interlocked.Decrement(ref _churnMisdeliveredFlows);
        }
    }

    /// <summary>The flow's first-response tick, or zero when no echo was observed.</summary>
    public long FirstResponseTicks(int flowId) => Volatile.Read(ref _firstResponseTicks[flowId]);

    public ValueTask InjectAsync(FlowKey originalFlow, Endpoint remoteSource, ReadOnlyMemory<byte> payload, MacAddress clientMac, CancellationToken cancellationToken)
    {
        if (!DatagramHeader.TryRead(payload.Span, out _, out var flowId)) return ValueTask.CompletedTask;
        // Both guards mean "not a new first response": an out-of-capacity flow id and an echo that
        // already landed. Only the first echo per flow may move a counter.
        if ((uint)flowId >= (uint)_firstResponseTicks.Length) return ValueTask.CompletedTask;
        if (flowKeys[flowId] != originalFlow)
        {
            if (!_misdeliveredFlows.Mark(flowId)) return ValueTask.CompletedTask;
            if (flowId < churnOffset) Interlocked.Increment(ref _warmupMisdeliveredFlows);
            else Interlocked.Increment(ref _churnMisdeliveredFlows);
            return ValueTask.CompletedTask;
        }

        if (Interlocked.CompareExchange(ref _firstResponseTicks[flowId], Stopwatch.GetTimestamp(), 0) == 0)
        {
            if (flowId < churnOffset) Interlocked.Increment(ref _warmupFirstResponses);
            else Interlocked.Increment(ref _churnFirstResponses);
        }

        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// One row of the session-budget series: the live session/control-connection shape, the proxy's own
/// descriptor count, the estimated kernel receive buffer, the churn counters, and the derived ratios.
/// <see cref="Associations"/> is the harness server's own count of the connections it serves — native
/// control connections or UoT per-flow stream connections, accepted minus closed — read on the peer
/// side of the dial rather than copied from the coordinator, so a connection that outlives its session
/// is visible in the row; it is null when the server ran out of process, and the JSONL writer omits the
/// field rather than reporting an unread count. <see cref="UotHandshakes"/> carries the UoT server's own
/// counters, null for every column that did not host that fixture. Both per-session measurements are
/// deltas against the run's post-warm-up baseline (<see cref="BaselineFileDescriptors"/>,
/// <see cref="BaselineManagedBytes"/>), which every row carries so either ratio is recomputable from the
/// row alone: the raw managed total is process-wide and would otherwise read as tens of megabytes per
/// session.
/// </summary>
internal sealed record SessionBudgetSample(
    string Phase,
    int Index,
    double ElapsedSeconds,
    int Sessions,
    int? Associations,
    int FileDescriptors,
    int RawFileDescriptors,
    int HarnessServerConnections,
    long ManagedBytes,
    long WorkingSetBytes,
    long BaselineFileDescriptors,
    long BaselineManagedBytes,
    long RelayReceiveBufferBytes,
    long Accepted,
    long Rejected,
    long Expired,
    long DatagramsSent,
    long DatagramsReceived,
    long DatagramsLost,
    long Misdelivered,
    long CapacityRejections,
    long SetupRejections,
    long SetupFailures,
    UotHandshakeCounters? UotHandshakes = null)
{
    public double FileDescriptorsPerSession => SessionBudgetMath.Ratio(FileDescriptors - BaselineFileDescriptors, Sessions);

    public double ManagedBytesPerSession => SessionBudgetMath.Ratio(ManagedBytes - BaselineManagedBytes, Sessions);

    /// <summary>The estimated kernel receive buffer per live session; equals the configured relay buffer for a non-empty population.</summary>
    public double RelayReceiveBufferBytesPerSession => SessionBudgetMath.Ratio(RelayReceiveBufferBytes, Sessions);

    /// <summary>
    /// The control-connection term the descriptor budget charges for this sample: the server's own
    /// live count when it was observed, otherwise the shipped shape's one connection per live session —
    /// an allowance the budget states rather than an observation, which is all an out-of-process
    /// harness server leaves.
    /// </summary>
    public long ControlConnectionsCharged => Associations ?? Sessions;

    /// <summary>
    /// The churn population's per-flow response accounting: <see cref="DatagramsReceived"/> is the
    /// <c>own</c> term, so <c>own + Misdelivered + NoResponse</c> is this run's accepted churn flows.
    /// An echo that arrived on another flow is the <c>Misdelivered</c> term, leaving no-response for
    /// flows no echo was observed for at all.
    /// </summary>
    private long NoResponse => Math.Max(0, Accepted - DatagramsReceived - Misdelivered);

    public object ToRow() => new
    {
        phase = Phase,
        sample = Index,
        elapsedSeconds = ElapsedSeconds,
        sessions = Sessions,
        associations = Associations,
        fileDescriptors = FileDescriptors,
        rawFileDescriptors = RawFileDescriptors,
        harnessServerConnections = HarnessServerConnections,
        baselineFileDescriptors = BaselineFileDescriptors,
        baselineManagedBytes = BaselineManagedBytes,
        fileDescriptorsPerSession = FileDescriptorsPerSession,
        managedBytesPerSession = ManagedBytesPerSession,
        managedBytes = ManagedBytes,
        workingSetBytes = WorkingSetBytes,
        relayReceiveBufferBytes = RelayReceiveBufferBytes,
        relayReceiveBufferBytesPerSession = RelayReceiveBufferBytesPerSession,
        accepted = Accepted,
        rejected = Rejected,
        expired = Expired,
        datagramsSent = DatagramsSent,
        datagramsReceived = DatagramsReceived,
        misdelivered = Misdelivered,
        noResponse = NoResponse,
        datagramsLost = DatagramsLost,
        capacityRejections = CapacityRejections,
        setupRejections = SetupRejections,
        setupFailures = SetupFailures,
        uotHandshakes = UotHandshakes,
    };
}
