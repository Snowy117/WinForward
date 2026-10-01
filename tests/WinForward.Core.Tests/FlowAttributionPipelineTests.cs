using System.Globalization;
using System.Net;
using WinForward.Configuration;
using WinForward.NdisApi;
using WinForward.Runtime;
using WinForward.TestSupport;
using Xunit;

namespace WinForward.Core.Tests;

/// <summary>
/// The deferred-attribution pipeline's exact facts, driven through the real
/// <see cref="FlowDispatcher"/>: admission, the setup worker, and the pump-thread delivery. The
/// test thread plays the pump, so "no attribution on the pump thread" is the attributor's own
/// thread record, not a product counter.
/// </summary>
public sealed class FlowAttributionPipelineTests
{
    private const nint AdapterHandle = 7;
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task NoAttributionRunsOnThePumpThread()
    {
        await using var harness = new Harness();
        var pumpThread = Environment.CurrentManagedThreadId;

        for (ushort port = 53100; port < 53106; port++) await DispatchAsync(harness, port);
        await WaitUntilAsync(() => harness.Attributor.Calls == 6, "the attribution workers never finished");

        Assert.Equal(6, harness.Attributor.Calls);
        Assert.Equal(0, harness.Attributor.CallsOnThread(pumpThread));
        Assert.Equal(6, harness.Pipeline.AttributionsOnSetupWorker);
        Assert.Equal(6, harness.Pipeline.Admissions);
    }

    [Fact]
    public async Task ThePumpIterationReturnsWhileAttributionIsStillParked()
    {
        await using var harness = new Harness(parks: true);
        var packet = Packet(53110);

        var pending = harness.Dispatcher.DispatchAsync(packet, CancellationToken.None);

        Assert.True(pending.IsCompletedSuccessfully, "the pump iteration must return while attribution is still parked");
        Assert.Equal(PacketDisposition.Deferred, packet.Lease.Disposition);
        Assert.Equal(1, harness.Pipeline.PendingCount);
        harness.Attributor.Release();
        await pending;
    }

    [Fact]
    public async Task OneAttributionPerAdmittedEntry()
    {
        await using var harness = new Harness();
        for (byte marker = 1; marker <= 4; marker++) await DispatchAsync(harness, 53120, marker);

        // The worker runs off the admitting thread, so its two counts are awaited rather than sampled:
        // the fact is "exactly one attribution per admitted entry", not "the worker had already run
        // when the asserting thread got here" (the sampled form failed once in a full-suite run).
        await WaitUntilAsync(() => harness.Attributor.Calls == 1, "the attribution worker never ran for the admitted entry");
        Assert.Equal(1, harness.Attributor.Calls);
        Assert.Equal(1, harness.Pipeline.Admissions);
        Assert.Equal(4, harness.Pipeline.RetainedPacketCount);
        Assert.Equal(1, harness.Pipeline.AttributionsOnSetupWorker);
    }

    [Fact]
    public async Task PacketsAreDeliveredInArrivalOrderAfterTheDecision()
    {
        await using var harness = new Harness();
        for (byte marker = 1; marker <= 3; marker++) await DispatchAsync(harness, 53130, marker);

        await WaitForDecidedAsync(harness);
        harness.Pipeline.DeliverDecided(AdapterHandle);

        Assert.Equal([1, 2, 3], harness.Executor.Events.Select(entry => entry.Marker));
        Assert.Equal(3, harness.Executor.PassCount);
        Assert.Equal(1, harness.Dispatcher.FlowCount);
    }

    [Fact]
    public async Task NoPacketIsDeliveredTwice()
    {
        await using var harness = new Harness();
        for (byte marker = 1; marker <= 3; marker++) await DispatchAsync(harness, 53140, marker);

        await WaitForDecidedAsync(harness);
        harness.Pipeline.DeliverDecided(AdapterHandle);
        harness.Pipeline.DeliverDecided(AdapterHandle);

        Assert.Equal(3, harness.Executor.PassCount);
        Assert.Equal([1, 2, 3], harness.Executor.Events.Select(entry => entry.Marker));
    }

    [Fact]
    public async Task AClaimIsPublishedOnlyAfterTheLastPendingPacket()
    {
        await using var harness = new Harness();
        for (byte marker = 1; marker <= 3; marker++) await DispatchAsync(harness, 53150, marker);

        await WaitForDecidedAsync(harness);
        // The decision landing must not claim the flow: its pending packets would then be
        // overtaken by any packet that resolves warm.
        Assert.Equal(0, harness.Dispatcher.FlowCount);

        harness.Pipeline.DeliverDecided(AdapterHandle);

        Assert.Equal(1, harness.Dispatcher.FlowCount);
        Assert.Equal(0, harness.Pipeline.PendingCount);
    }

    [Fact]
    public async Task AFailingSetupBlocksEveryPendingPacketAndNothingIsStranded()
    {
        await using var harness = new Harness(failure: new InvalidOperationException("attribution exploded"));
        for (byte marker = 1; marker <= 3; marker++) await DispatchAsync(harness, 53160, marker);

        await WaitForDecidedAsync(harness);
        harness.Pipeline.DeliverDecided(AdapterHandle);

        Assert.Equal(3, harness.Executor.BlockCount);
        Assert.Equal(0, harness.Executor.PassCount);
        Assert.Equal(0, harness.Pipeline.PendingCount);
        Assert.Equal(1, harness.Pipeline.CooldownCount);
        Assert.Equal(0, harness.Dispatcher.FlowCount);
    }

    [Fact]
    public async Task AThrowingExecutorBlocksTheBatchRemainderAndNothingIsDetached()
    {
        await using var harness = new Harness();
        for (byte marker = 1; marker <= 3; marker++) await DispatchAsync(harness, 53170, marker);
        await WaitForDecidedAsync(harness);
        harness.Executor.ThrowOn = _ => true;

        Assert.Throws<InvalidOperationException>(() => harness.Pipeline.DeliverDecided(AdapterHandle));

        // The batch is entry-owned, so the throw leaves the unexecuted remainder blocked rather
        // than detached, and the next drain finds an empty ring and claims the flow normally.
        Assert.Equal(2, harness.Executor.BlockCount);
        harness.Executor.ThrowOn = null;
        harness.Pipeline.DeliverDecided(AdapterHandle);
        Assert.Equal(1, harness.Dispatcher.FlowCount);
        Assert.Equal(0, harness.Pipeline.PendingCount);
    }

    [Fact]
    public async Task AnEnqueueDuringADrainDoesNotDisturbIt()
    {
        await using var harness = new Harness();
        for (byte marker = 1; marker <= 3; marker++) await DispatchAsync(harness, 53180, marker);
        await WaitForDecidedAsync(harness);

        var fired = 0;
        harness.Executor.OnExecute = _ =>
        {
            if (Interlocked.Increment(ref fired) != 1) return;
            var nested = Packet(53180, marker: 9);
            // Re-entrant admission on the pump thread: the result must already be there, so the
            // packet is in the ring when this drain's next take runs.
            // ReSharper disable once AccessToDisposedClosure // This hook runs synchronously inside the DeliverDecided call on this thread, which returns before the harness is disposed.
            Assert.True(harness.Dispatcher.DispatchAsync(nested, CancellationToken.None).AsTask().IsCompletedSuccessfully, "the nested admission must complete on the pump thread");
        };

        harness.Pipeline.DeliverDecided(AdapterHandle);

        Assert.Equal([1, 2, 3, 9], harness.Executor.Events.Select(entry => entry.Marker));
    }

    [Fact]
    public async Task ASaSecondKeyOfOneTupleIsCountedAsAReAdmission()
    {
        await using var harness = new Harness();
        var first = Packet(53190, adapterId: "id-a");
        var second = Packet(53190, adapterId: "id-b");

        await harness.Dispatcher.DispatchAsync(first, CancellationToken.None);
        await harness.Dispatcher.DispatchAsync(second, CancellationToken.None);
        await WaitForDecidedAsync(harness, 2);
        harness.Pipeline.DeliverDecided(AdapterHandle);

        // One transport tuple observed on two adapters is two keys and two attributions; the
        // second claim returns the first decision, so the observable outcome is unchanged.
        Assert.Equal(2, harness.Attributor.Calls);
        Assert.Equal(1, harness.Pipeline.ReAdmissionCount);
        Assert.Equal(2, harness.Executor.PassCount);
        Assert.Equal(1, harness.Dispatcher.FlowCount);
    }

    [Fact]
    public async Task AFailedClaimIsCountedAndDoesNotReAttributePerPacket()
    {
        await using var harness = new Harness(flowCapacity: 1);
        await DispatchAsync(harness, 53200);
        await WaitForDecidedAsync(harness);
        harness.Pipeline.DeliverDecided(AdapterHandle);
        Assert.Equal(1, harness.Dispatcher.FlowCount);

        await DispatchAsync(harness, 53201, marker: 1);
        await WaitForDecidedAsync(harness);
        harness.Pipeline.DeliverDecided(AdapterHandle);
        // The claim-failed entry is re-queued by this arrival, so a decision is waiting again.
        await DispatchAsync(harness, 53201, marker: 2);
        await WaitForDecidedAsync(harness);
        harness.Pipeline.DeliverDecided(AdapterHandle);

        Assert.Equal(2, harness.Attributor.Calls);
        Assert.True(harness.Pipeline.ClaimFailedCount >= 1, "the capacity refusal must be counted");
        Assert.Equal(1, harness.Executor.BlockCount);
        // The refused claim keeps the entry so its later packets keep failing closed.
        Assert.Equal(1, harness.Pipeline.PendingCount);
    }

    [Fact]
    public async Task AForwardedMissNeverCreatesAPendingEntry()
    {
        await using var harness = new Harness();
        var packet = Packet(53210, origin: FlowOriginKind.Forwarded);

        await harness.Dispatcher.DispatchAsync(packet, CancellationToken.None);

        Assert.Equal(0, harness.Attributor.Calls);
        Assert.Equal(0, harness.Pipeline.PendingCount);
        Assert.Equal(1, harness.Executor.PassCount);
    }

    [Fact]
    public async Task ANoProcessRuleMissNeverCreatesAPendingEntry()
    {
        await using var harness = new Harness(processRule: false);

        await DispatchAsync(harness, 53220);

        // No process selector means no pipeline at all: the shape keeps today's inline path.
        Assert.Null(harness.Dispatcher.Attribution);
        Assert.Equal(0, harness.Attributor.Calls);
        Assert.Equal(1, harness.Executor.PassCount);
    }

    [Fact]
    public async Task ADisposedPipelineBlocksInsteadOfAttributingInline()
    {
        var harness = new Harness();
        await using (harness)
        {
            await harness.Pipeline.DisposeAsync();
            await DispatchAsync(harness, 53230);

            Assert.Equal(0, harness.Attributor.Calls);
            Assert.Equal(1, harness.Executor.BlockCount);
        }
    }

    [Fact]
    public async Task AShutdownCancellationArmsNoCooldown()
    {
        var harness = new Harness(parks: true);
        await using (harness)
        {
            var pending = harness.Dispatcher.DispatchAsync(Packet(53240), CancellationToken.None);
            await WaitUntilAsync(() => harness.Attributor.Calls == 1, "the worker never reached the attributor");

            await harness.Pipeline.DisposeAsync();
            await pending;

            // The shutdown arm fails the entry closed and the seal blocks what it still held —
            // never a silent drop, and never a cooldown.
            Assert.Equal(0, harness.Pipeline.CooldownCount);
            Assert.Equal(1, harness.Pipeline.BlockedPacketCount);
            await Task.CompletedTask;
        }
    }

    [Fact]
    public async Task PendingAdmitAllocatesOnlyTheDocumentedColdBudget()
    {
        await using var harness = new Harness();
        // A closed flow, so the measured admits are steady-state appends onto a live entry.
        await DispatchAsync(harness, 53250);
        await WaitForDecidedAsync(harness);
        harness.Pipeline.DeliverDecided(AdapterHandle);

        // Pre-fill the retention pool: a rent that misses the free list allocates its native
        // manager, which is pool sizing, not the per-packet path.
        var warm = new NativeLease[48];
        for (var index = 0; index < warm.Length; index++) warm[index] = harness.Pool.Rent();
        foreach (var lease in warm) lease.Dispose();

        // One admitted entry pays the documented cold budget: the entry object, its fixed ring and
        // the first retained slot.
        var threadId = Environment.CurrentManagedThreadId;
        var coldBefore = GC.GetAllocatedBytesForCurrentThread();
        var firstOutcome = harness.Pipeline.Admit(Packet(53251));
        var cold = GC.GetAllocatedBytesForCurrentThread() - coldBefore;
        Assert.Equal(AttributionAdmission.Deferred, firstOutcome);
        Assert.InRange(cold, 1, 8192);

        // Every later packet of that pending flow is a ring append under an existing lease. The
        // packets are built before the window: a packet is the harness's own allocation.
        var followers = new CapturedFlowPacket[7];
        for (var index = 0; index < followers.Length; index++) followers[index] = Packet(53251, (byte)(index + 2));
        var allDeferred = true;
        var steadyBefore = GC.GetAllocatedBytesForCurrentThread();
        // The boolean form only: Assert.Equal allocates ~250 B per call, which would land in the
        // very window this gate measures.
        foreach (var follower in followers) allDeferred &= harness.Pipeline.Admit(follower) == AttributionAdmission.Deferred;
        var steady = GC.GetAllocatedBytesForCurrentThread() - steadyBefore;

        Assert.True(allDeferred, "every later packet of a pending flow must be retained");
        Assert.Equal(threadId, Environment.CurrentManagedThreadId);
        Assert.True(steady == 0, string.Create(CultureInfo.InvariantCulture, $"an append onto an already-pending flow allocated {steady} B on the admitting thread"));
        Assert.True(cold > 0, "the cold budget must be visible, or the gate measures nothing");
    }

    [Fact]
    public async Task AnEntryIsDrainedOnlyByItsOwnAdaptersPump()
    {
        await using var harness = new Harness();
        for (byte marker = 1; marker <= 2; marker++) await DispatchAsync(harness, 53320, marker);
        await WaitForDecidedAsync(harness);

        // Another adapter's drain never touches this entry: the decided queues are per adapter
        // handle, which is what keeps a delivery on the pump that owns the entry's lanes.
        harness.Pipeline.DeliverDecided(AdapterHandle + 1);
        Assert.Equal(0, harness.Executor.PassCount);
        Assert.Equal(1, harness.Pipeline.DecidedDepth);

        harness.Pipeline.DeliverDecided(AdapterHandle);
        Assert.Equal(2, harness.Executor.PassCount);
        Assert.Equal(0, harness.Pipeline.DecidedDepth);
    }

    [Fact]
    public async Task ThePipelineSignalsOnlyItsOwnAdaptersEvent()
    {
        await using var harness = new Harness();
        using var registry = new FlowAttributionWakeRegistry();
        using var ownDriverEvent = new EventWaitHandle(initialState: false, EventResetMode.AutoReset);
        using var ownDriver = new NdisPacketArrivalSignal(ownDriverEvent);
        var own = registry.Register(AdapterHandle, ownDriver);
        using var otherDriverEvent = new EventWaitHandle(initialState: false, EventResetMode.AutoReset);
        using var otherDriver = new NdisPacketArrivalSignal(otherDriverEvent);
        var other = registry.Register(AdapterHandle + 1, otherDriver);
        harness.Pipeline.Wake = registry;

        await DispatchAsync(harness, 53300);
        await WaitUntilAsync(() => own.Wait(TimeSpan.Zero), "the decided entry never woke the pump that owns its adapter");

        Assert.False(other.Wait(TimeSpan.Zero), "the pipeline woke an adapter that owns no entry of its own");
    }

    /// <summary>
    /// The lock-order fact: the pipeline's gate is held across the flow-table gate it waits for
    /// (pending → flow), and the flow-table gate's own holder never waits on the pipeline's gate —
    /// which is what makes the nesting acyclic. The discriminator is the second admission: it can
    /// only complete if it can take the pipeline gate, so a flow-gate-first admission would let it
    /// through while the first admission is still parked.
    /// </summary>
    [Fact]
    public async Task AdmissionHoldsThePipelineGateWhileItWaitsForTheFlowGate()
    {
        await using var harness = new Harness();
        var flows = ((IFlowAttributionHost)harness.Dispatcher).Flows;

        // One dedicated thread parks inside the flow table's gate; a pool thread could inline the
        // admission onto the parked holder's thread and the fact would pass vacuously.
        var parked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        flows.GateHoldProbe = () =>
        {
            parked.TrySetResult();
            _ = release.Task.Wait(TimeSpan.FromSeconds(10));
        };

        var holderClaimed = false;
        var holder = new Thread(() => holderClaimed = flows.TryClaimResolved(Key(53310), new FlowDecision(FlowAction.Pass, 0, ProxyServerName: null), out _))
        {
            IsBackground = true,
            Name = "flow-gate-holder",
        };

        var admitted = new TaskCompletionSource<AttributionAdmission>[2];
        var admitters = new Thread[2];
        for (var index = 0; index < admitters.Length; index++)
        {
            var port = (ushort)(53311 + index);
            admitted[index] = new TaskCompletionSource<AttributionAdmission>(TaskCreationOptions.RunContinuationsAsynchronously);
            var outcome = admitted[index];
            // ReSharper disable once AccessToDisposedClosure // Every exit path joins this thread in the finally below, before the harness is disposed.
            admitters[index] = new Thread(() => outcome.TrySetResult(harness.Pipeline.Admit(Packet(port))))
            {
                IsBackground = true,
                Name = $"attribution-admitter-{port}",
            };
        }

        holder.Start();
        try
        {
            Assert.True(await CompletesWithinAsync(parked.Task, TimeSpan.FromSeconds(10)), "no thread parked inside the flow-table gate");

            admitters[0].Start();
            Assert.False(await CompletesWithinAsync(admitted[0].Task, TimeSpan.FromMilliseconds(200)), "the admission did not wait for the flow gate at all");

            // The second admission can only be blocked by the pipeline's own gate, so a completed
            // task here means admission took the flow gate before its own.
            admitters[1].Start();
            Assert.False(await CompletesWithinAsync(admitted[1].Task, TimeSpan.FromMilliseconds(200)), "the pipeline's gate is not held across the flow-gate wait");
        }
        finally
        {
            // Release before asserting anything else: on the failure this fact exists to detect the
            // holder is still parked inside the gate, and an assertion would leave it there. Every
            // capturing thread is then joined on every exit path, so no closure it holds can outlive
            // the harness this method's scope disposes.
            release.TrySetResult();
            holder.Join(TimeSpan.FromSeconds(10));
            foreach (var admitter in admitters) admitter.Join(TimeSpan.FromSeconds(10));
        }

        Assert.True(holderClaimed);
        Assert.True(await CompletesWithinAsync(admitted[0].Task, TimeSpan.FromSeconds(10)), "the first admission never settled after the gate was released");
        Assert.True(await CompletesWithinAsync(admitted[1].Task, TimeSpan.FromSeconds(10)), "the second admission never settled after the gate was released");
        Assert.Equal(AttributionAdmission.Deferred, await admitted[0].Task);
        Assert.Equal(AttributionAdmission.Deferred, await admitted[1].Task);
    }

    /// <summary>Whether <paramref name="task"/> settles within <paramref name="timeout"/>; the awaited form of a boolean bounded wait.</summary>
    private static async Task<bool> CompletesWithinAsync(Task task, TimeSpan timeout)
    {
        try
        {
            await task.WaitAsync(timeout, TimeProvider.System).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    private static async Task DispatchAsync(Harness harness, ushort localPort, byte marker = 1, FlowOriginKind origin = FlowOriginKind.Host, string adapterId = "id-a")
    {
        var packet = Packet(localPort, marker, origin, adapterId);
        await harness.Dispatcher.DispatchAsync(packet, CancellationToken.None);
    }

    private static CapturedFlowPacket Packet(ushort localPort, byte marker = 1, FlowOriginKind origin = FlowOriginKind.Host, string adapterId = "id-a")
    {
        var frame = new byte[256];
        frame[0] = marker;
        return new CapturedFlowPacket(new PacketLease(frame), FlowBuilders.Context(Key(localPort, origin, adapterId), adapterId: adapterId), new PacketCaptureMetadata(0, AdapterHandle));
    }

    private static FlowKey Key(ushort localPort, FlowOriginKind origin = FlowOriginKind.Host, string adapterId = "id-a") => FlowKey.Create(
        Endpoint.From(IPAddress.Parse("192.0.2.10"), localPort),
        Endpoint.From(IPAddress.Parse("192.0.2.53"), 443),
        TransportProtocol.Tcp,
        origin,
        FlowBuilders.SlotOf(adapterId, 1),
        1);

    private static async Task WaitForDecidedAsync(Harness harness, int expected = 1) =>
        await WaitUntilAsync(() => harness.Pipeline.DecidedDepth >= expected, "the attribution worker never published a decision");

    private static async Task WaitUntilAsync(Func<bool> condition, string message)
    {
        var deadline = DateTime.UtcNow + s_timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException(message);
            await Task.Delay(1);
        }
    }

    private sealed class Harness : IAsyncDisposable
    {
        public Harness(bool parks = false, Exception? failure = null, int flowCapacity = 65_536, bool processRule = true)
        {
            Pool = new NativeBufferPool(1514, 64);
            Setup = new SetupExecutor(workerCount: 2, ringCapacity: 64);
            Attributor = new GatedAttributor { Parks = parks, Failure = failure };
            Executor = new RecordingExecutor();
            var matcher = processRule
                ? new RuleMatcher(Processes: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "dns.exe" })
                : new RuleMatcher();
            var rules = new[] { new PolicyRule(matcher, new FlowDecision(FlowAction.Pass, 0, ProxyServerName: null)) };
            var configuration = new ValidatedConfiguration(
                new Dictionary<string, Socks5Server>(StringComparer.OrdinalIgnoreCase),
                new PolicySnapshot(rules, FlowAction.Pass));
            Dispatcher = new FlowDispatcher(configuration, new FakeGuard(), Executor, Attributor, flowCapacity: flowCapacity, attributionPool: Pool, setupExecutor: Setup);
        }

        public NativeBufferPool Pool { get; }

        private SetupExecutor Setup { get; }

        public GatedAttributor Attributor { get; }

        public RecordingExecutor Executor { get; }

        public FlowDispatcher Dispatcher { get; }

        public FlowAttributionPipeline Pipeline => Dispatcher.Attribution ?? throw new InvalidOperationException("The harness dispatcher has no attribution pipeline.");

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (Dispatcher.Attribution is { } pipeline) await pipeline.DisposeAsync();
            }
            finally
            {
                Setup.Dispose();
                Pool.Dispose();
            }
        }
    }
}
