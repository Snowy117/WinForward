using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using WinForward.Core;
using WinForward.Runtime.UdpProxy;

namespace WinForward.Benchmarks.Stability;

/// <summary>
/// The session-budget soak's measurement vocabulary: the per-sample resource record, the process
/// sampler whose descriptor series is the PRD's subject, the first-response sink, and the pure
/// arithmetic the scenario's acceptance assertions are written in. The scenario file drives the
/// phases and owns the assertions; the quantities and their thresholds live here, mirroring the
/// burst instrument's split of its measurement types from its scenario.
/// </summary>
internal static class SessionBudgetMath
{
    /// <summary>
    /// The descriptor slack the acceptance allows per live session on top of its relay socket:
    /// the proxy's per-session descriptor cost is one relay socket (plus bookkeeping that must not
    /// create a descriptor), and 25 % absorbs transient teardown/setup overlap at a sample instant.
    /// A per-flow control connection doubles the per-session cost (relay socket + control socket),
    /// which is what this bound exists to reject once the association term is charged separately.
    /// </summary>
    internal const double DescriptorSlackPerSession = 1.25;

    /// <summary>
    /// The steady-state population bound: a flow that never sends again is released after
    /// <paramref name="idleTimeout"/> and at the next sweeper tick (<paramref name="sweepInterval"/>),
    /// so the live set tracks <c>rate × (idle + 2 × sweep)</c> once saturated instead of growing with
    /// the cumulative flow count. The doubled sweep term is the honest worst case (a session can be
    /// created just after a tick and wait almost two intervals), and <paramref name="margin"/> adds
    /// arrival jitter.
    /// </summary>
    internal static int SteadyStateSessionCeiling(int rate, TimeSpan idleTimeout, TimeSpan sweepInterval, int margin)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rate);
        return checked((int)Math.Ceiling(rate * (idleTimeout + (2 * sweepInterval)).TotalSeconds)) + margin;
    }

    /// <summary>
    /// The arrival jitter a steady-state sample may exceed the retention ceiling by: two seconds of
    /// arrivals plus a fixed floor (a sample landing mid-tick sees a population up to one full sweep
    /// interval above the idle-only bound, which the doubled sweep term already covers).
    /// </summary>
    internal static int SteadyStateMargin(int rate) => Math.Max(32, 2 * rate);

    /// <summary>
    /// The descriptor budget for a live population: one relay socket per session with
    /// <see cref="DescriptorSlackPerSession"/> slack, plus one control connection per association.
    /// Charging the association term per association — not per session — is the pooling accounting:
    /// a shared association costs one descriptor for up to <c>FlowsPerAssociation</c> flows.
    /// </summary>
    internal static long FileDescriptorBudget(long sessions, long associations)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sessions);
        ArgumentOutOfRangeException.ThrowIfNegative(associations);
        return checked((long)Math.Ceiling(sessions * DescriptorSlackPerSession)) + associations;
    }

    /// <summary>
    /// The shared-association slack the pooling coverage check allows on top of the ideal fan-out
    /// <c>ceil(sessions / flowsPerAssociation)</c>. A few associations beyond the ideal placement are
    /// legitimate — a partially filled warm set, a population peak that made placement open one more
    /// association than the steady state needs, cold associations kept warm across a lull — and the
    /// honest residual is that such a shape can sit slightly above the ideal fan-out. The slack is
    /// clamped to this constant so it cannot scale with the per-server ceiling: a slack as large as
    /// the ceiling would let the acceptance load tolerate ≈1,100 private (per-flow) control
    /// connections, which is the descriptor shape this check exists to reject.
    /// </summary>
    internal const int SharedAssociationSlack = 16;

    /// <summary>
    /// The shared-association ceiling for a population inside the pool's shared budget: every
    /// <c>FlowsPerAssociation</c> flows share one association, plus at most
    /// <see cref="SharedAssociationSlack"/> associations for the legitimate cold/warm set. The cap
    /// itself still bounds the slack, so a deliberately small ceiling tightens the check.
    /// </summary>
    internal static long SharedAssociationCeiling(long sessions, int flowsPerAssociation, int maxAssociationsPerServer)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sessions);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(flowsPerAssociation);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxAssociationsPerServer);
        return ((sessions + flowsPerAssociation - 1) / flowsPerAssociation) + Math.Min(maxAssociationsPerServer, SharedAssociationSlack);
    }

    /// <summary>
    /// The smallest churn window (whole seconds) whose cumulative flow count exceeds
    /// <paramref name="ceiling"/>. Below it an implementation that never expires a session ends the
    /// churn inside the retention bound, so the ceiling assertion would claim nothing: the run must
    /// be refused rather than recorded as evidence that retention bounds the population.
    /// </summary>
    internal static int MinimumDiscriminatingChurnSeconds(int rate, int ceiling)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rate);
        ArgumentOutOfRangeException.ThrowIfNegative(ceiling);
        return (ceiling / rate) + 1;
    }

    /// <summary>
    /// The refusal sentence for a churn window that cannot discriminate retention from accumulation,
    /// or null when the window is long enough. The pre-run check and the acceptance verdict both use
    /// this one sentence, so the fast refusal and the recorded failure cannot drift apart.
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
    /// the configured per-session relay buffer. The estimate itself is the same
    /// <c>live sessions × configured buffer</c> product the product's heartbeat reports, so this
    /// budget is the byte form of the session ceiling — the assertion exists so the reported bytes
    /// are a checked acceptance term instead of a reported-only number.
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
/// own start instead of at run construction. A warm-up that took T seconds must not turn the first
/// T × rate arrivals into one catch-up burst, while a sweep that overran its slot is still caught up
/// immediately rather than shifting the realized rate.
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

/// <summary>One sampled instant: the coordinator/pool counters, the process's descriptor and memory shape, and the running churn totals.</summary>
[StructLayout(LayoutKind.Auto)]
internal readonly record struct ResourceSample(int ProxyFileDescriptors, int RawFileDescriptors, int HarnessServerConnections, long ManagedBytes, long WorkingSetBytes);

/// <summary>
/// Samples the process's descriptor and memory footprint for the session-budget series.
/// Descriptors are read from <c>/proc/self/fd</c> on Linux — the soak host, where every socket is a
/// descriptor — with <see cref="Process.HandleCount"/> as the Windows fallback. When the harness
/// SOCKS5 server runs inside this process, the two sockets it owns per accepted control connection
/// (<see cref="LoopbackSocks5UdpServer.ConnectionCount"/>) are subtracted from the raw count: the
/// acceptance is about the proxy's own sockets, and <c>--socks5-external</c> removes the harness's
/// from the process entirely.
/// </summary>
internal sealed class ProcessResourceSampler(LoopbackSocks5UdpServer? inProcessServer) : IDisposable
{
    private readonly Process _process = Process.GetCurrentProcess();

    public ResourceSample Sample()
    {
        _process.Refresh();
        var raw = OpenFileDescriptors();
        var harness = inProcessServer is null ? 0 : 2 * inProcessServer.ConnectionCount;
        return new ResourceSample(raw - harness, raw, harness, GC.GetTotalMemory(forceFullCollection: false), _process.WorkingSet64);
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
/// </summary>
internal sealed class SessionBudgetSink(int churnOffset, int flowCapacity) : IUdpResponseSink
{
    private readonly long[] _firstResponseTicks = new long[flowCapacity];
    private long _warmupFirstResponses;
    private long _churnFirstResponses;

    public long WarmupFirstResponses => Interlocked.Read(ref _warmupFirstResponses);

    public long ChurnFirstResponses => Interlocked.Read(ref _churnFirstResponses);

    /// <summary>The flow's first-response tick, or zero when no echo was observed.</summary>
    public long FirstResponseTicks(int flowId) => Volatile.Read(ref _firstResponseTicks[flowId]);

    public ValueTask InjectAsync(FlowKey originalFlow, Endpoint remoteSource, ReadOnlyMemory<byte> payload, MacAddress clientMac, CancellationToken cancellationToken)
    {
        // The three readiness preconditions are one conjunction: a header this run cannot parse, a
        // flow id outside this run's capacity, and an echo that already landed are all "not a new
        // first response", and only the first echo per flow may move a counter.
        if (DatagramHeader.TryRead(payload.Span, out _, out var flowId)
            && (uint)flowId < (uint)_firstResponseTicks.Length
            && Interlocked.CompareExchange(ref _firstResponseTicks[flowId], Stopwatch.GetTimestamp(), 0) == 0)
        {
            if (flowId < churnOffset) Interlocked.Increment(ref _warmupFirstResponses);
            else Interlocked.Increment(ref _churnFirstResponses);
        }

        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// One row of the session-budget series: the live session/association shape, the proxy's own
/// descriptor count, the estimated kernel receive buffer, the churn counters, and the derived
/// ratios. The two ratio fields are the pooling evidence — <see cref="FileDescriptorsPerSession"/>
/// near 1 means every live session costs one relay socket and the control connections are amortized
/// across the association's flows. Both per-session measurements are deltas against the run's
/// post-warm-up baseline (<see cref="BaselineFileDescriptors"/>, <see cref="BaselineManagedBytes"/>),
/// which every row carries so either ratio is recomputable from the row alone: the raw managed total
/// is a process-wide number that would otherwise read as tens of megabytes per session in the
/// in-process harness shape.
/// </summary>
internal sealed record SessionBudgetSample(
    string Phase,
    int Index,
    double ElapsedSeconds,
    int Sessions,
    int Associations,
    int LeasedFlows,
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
    long CapacityRejections,
    long SetupRejections,
    long SetupFailures)
{
    public double FileDescriptorsPerSession => SessionBudgetMath.Ratio(FileDescriptors - BaselineFileDescriptors, Sessions);

    /// <summary>This row's own sampled associations per live session; the summary quotes the steady peak's ratio instead.</summary>
    private double AssociationsPerSession => SessionBudgetMath.Ratio(Associations, Sessions);

    public double ManagedBytesPerSession => SessionBudgetMath.Ratio(ManagedBytes - BaselineManagedBytes, Sessions);

    /// <summary>The estimated kernel receive buffer per live session; equals the configured relay buffer for a non-empty population.</summary>
    public double RelayReceiveBufferBytesPerSession => SessionBudgetMath.Ratio(RelayReceiveBufferBytes, Sessions);

    public object ToRow() => new
    {
        phase = Phase,
        sample = Index,
        elapsedSeconds = ElapsedSeconds,
        sessions = Sessions,
        associations = Associations,
        leasedFlows = LeasedFlows,
        fileDescriptors = FileDescriptors,
        rawFileDescriptors = RawFileDescriptors,
        harnessServerConnections = HarnessServerConnections,
        baselineFileDescriptors = BaselineFileDescriptors,
        baselineManagedBytes = BaselineManagedBytes,
        fileDescriptorsPerSession = FileDescriptorsPerSession,
        associationsPerSession = AssociationsPerSession,
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
        datagramsLost = DatagramsLost,
        capacityRejections = CapacityRejections,
        setupRejections = SetupRejections,
        setupFailures = SetupFailures,
    };
}
