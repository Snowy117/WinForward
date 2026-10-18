using System.Net;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using WinForward.Core;
using WinForward.NdisApi;
using WinForward.TestSupport;
using Xunit;

namespace WinForward.Runtime.Capture.Tests;

/// <summary>
/// The kernel pass cache's lease lifecycle over a fake driver table and a hand-driven clock: install
/// delay, one coalesced apply per tick, counter-driven renewal, idle removal and re-arm, the flow cap,
/// ring overflow, pause/resume, the start and dispose resets, the fail-safe disable on persistent
/// faults, and the aggregate summary. The worker is driven by calling the tick directly, so every fact
/// is deterministic rather than timer-paced.
/// </summary>
public sealed class NdisKernelPassCacheTests
{
    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task StartClearsAPreviousRunsTableAndDoesNotInstallOnThePumpPath()
    {
        await using var harness = new Harness();

        harness.Cache.Start();
        harness.Cache.OnPassPacket(Key(0));

        Assert.Equal(1, harness.Table.ResetCalls);
        // The observation only queues a candidate: the driver is never called from this thread.
        Assert.Equal(["reset"], harness.Table.Operations);
        Assert.Equal(1, harness.Cache.PendingCandidates);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task AFlowIsInstalledOnlyAfterTheObservationDelay()
    {
        await using var harness = new Harness();
        harness.Cache.Start();
        harness.Cache.OnPassPacket(Key(0));

        harness.Advance(TimeSpan.FromMilliseconds(900));
        harness.Tick();
        Assert.Equal(0, harness.Table.ReplaceCalls);
        Assert.Equal(0, harness.Cache.RuleCount);

        harness.Advance(TimeSpan.FromMilliseconds(200));
        harness.Tick();
        Assert.Equal(1, harness.Table.ReplaceCalls);
        var installed = Assert.Single(harness.Table.InstalledRules);
        Assert.Equal(0, harness.Cache.PendingCandidates);
        Assert.Equal(Key(0).Local.Address, installed.AddressA);
        Assert.Equal(Key(0).Local.Port, installed.PortA);
        Assert.Equal(Key(0).Remote.Address, installed.AddressB);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task ABurstOfObservationsCostsOneApply()
    {
        await using var harness = new Harness();
        harness.Cache.Start();
        for (var index = 0; index < 3; index++) harness.Cache.OnPassPacket(Key(index));
        harness.Advance(TimeSpan.FromSeconds(1));
        harness.Tick();

        Assert.Equal(1, harness.Table.ReplaceCalls);
        Assert.Equal(3, harness.Table.InstalledRules.Count);
        Assert.Equal(1, harness.Cache.Applies);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task AdvancingCountersRenewTheLease()
    {
        await using var harness = new Harness();
        harness.Cache.Start();
        Install(harness, Key(0));

        harness.Table.AdvanceCounters(5);
        harness.Advance(TimeSpan.FromSeconds(1.5));
        harness.Tick();
        Assert.Equal(5, harness.Cache.BypassedPackets);

        harness.Advance(TimeSpan.FromSeconds(1.9));
        harness.Tick();
        Assert.Equal(1, harness.Cache.RuleCount);

        harness.Advance(TimeSpan.FromMilliseconds(200));
        harness.Tick();
        Assert.Equal(0, harness.Cache.RuleCount);
        Assert.Empty(harness.Table.InstalledRules);
        Assert.Equal(1, harness.Cache.Evictions);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task ARemovedRulesNextObservationInstallsItAgain()
    {
        await using var harness = new Harness();
        harness.Cache.Start();
        Install(harness, Key(0));
        Expire(harness);
        Assert.Equal(0, harness.Cache.RuleCount);

        harness.Cache.OnPassPacket(Key(0));
        Assert.Equal(1, harness.Cache.PendingCandidates);
        harness.Advance(TimeSpan.FromSeconds(1));
        harness.Tick();

        Assert.Equal(1, harness.Cache.RuleCount);
        Assert.Single(harness.Table.InstalledRules);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task TheFlowCapEvictsTheLeastRecentlyActiveRule()
    {
        await using var harness = new Harness();
        harness.Cache.Start();
        for (var index = 0; index < NdisKernelPassCache.MaxFlows + 1; index++) harness.Cache.OnPassPacket(Key(index));
        harness.Advance(TimeSpan.FromSeconds(1));
        harness.Tick();

        Assert.Equal(NdisKernelPassCache.MaxFlows, harness.Cache.RuleCount);
        Assert.Equal(NdisKernelPassCache.MaxFlows, harness.Table.InstalledRules.Count);
        Assert.Equal(1, harness.Cache.Evictions);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task AFullCandidateRingDropsTheObservationAndClearsItsMarker()
    {
        await using var harness = new Harness();
        harness.Cache.Start();
        for (var index = 0; index < NdisKernelPassCache.CandidateCapacity; index++) harness.Cache.OnPassPacket(Key(index));

        harness.Cache.OnPassPacket(Key(NdisKernelPassCache.CandidateCapacity));

        Assert.Equal(1, harness.Cache.RejectedObservations);
        Assert.Equal(NdisKernelPassCache.CandidateCapacity, harness.Cache.PendingCandidates);
        // The dropped tuple's marker was cleared, so the next observation of it is accepted again.
        harness.Cache.OnPassPacket(Key(NdisKernelPassCache.CandidateCapacity));
        Assert.Equal(2, harness.Cache.RejectedObservations);
        Assert.Equal(NdisKernelPassCache.CandidateCapacity, harness.Cache.PendingCandidates);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task PauseClearsTheTableAndIgnoredObservationsResumeAfterwards()
    {
        await using var harness = new Harness();
        harness.Cache.Start();
        Install(harness, Key(0));

        harness.Cache.Pause();
        Assert.Equal(0, harness.Cache.RuleCount);
        Assert.Empty(harness.Table.InstalledRules);
        var resets = harness.Table.ResetCalls;

        harness.Cache.OnPassPacket(Key(1));
        Assert.Equal(0, harness.Cache.PendingCandidates);
        Assert.Equal(resets, harness.Table.ResetCalls);

        harness.Cache.Resume();
        harness.Tick();
        harness.Cache.OnPassPacket(Key(1));
        harness.Advance(TimeSpan.FromSeconds(1));
        harness.Tick();
        Assert.Equal(1, harness.Cache.RuleCount);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task DisposeClearsTheTableOnceMoreAndEmitsTheFinalSummary()
    {
        var harness = new Harness();
        harness.Cache.Start();
        Install(harness, Key(0));
        harness.Table.AdvanceCounters(4);
        harness.Advance(TimeSpan.FromMilliseconds(300));
        harness.Tick();
        var resets = harness.Table.ResetCalls;

        await harness.Cache.DisposeAsync();

        Assert.True(harness.Table.ResetCalls > resets);
        Assert.Empty(harness.Table.InstalledRules);
        Assert.True(harness.Cache.IsDisabled);
        Assert.Contains(harness.Logger.Events, recorded => string.Equals(recorded.Name, "kernel.passCache", StringComparison.Ordinal));
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task APersistentlyRefusedReplaceEndsInTheFailSafeDisable()
    {
        await using var harness = new Harness();
        harness.Cache.Start();
        harness.Table.ReplaceStatus = NdisKernelFilterStatus.Failed;
        harness.Table.NativeError = 87;
        harness.Cache.OnPassPacket(Key(0));
        harness.Advance(TimeSpan.FromSeconds(1));

        for (var tick = 0; tick < NdisKernelPassCache.ApplyFailureLimit; tick++)
        {
            Assert.False(harness.Cache.IsDisabled);
            harness.Tick();
        }

        Assert.True(harness.Cache.IsDisabled);
        Assert.Equal(NdisKernelPassCache.ApplyFailureLimit, harness.Cache.ApplyFailures);
        Assert.Equal(0, harness.Cache.RuleCount);
        Assert.Contains(harness.Logger.Lines, line => line.Level == LogLevel.Error && line.Message.Contains("disabled itself", StringComparison.Ordinal));
        // The fail-safe path leaves no rule behind: capture keeps the user-mode path from here on.
        Assert.Empty(harness.Table.InstalledRules);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task AnUnreadableTableSkipsExpiryAndEventuallyDisables()
    {
        await using var harness = new Harness();
        harness.Cache.Start();
        Install(harness, Key(0));
        harness.Table.ReadStatus = NdisKernelFilterStatus.Failed;
        harness.Table.NativeError = 31;

        // The lease has long expired, but a table we cannot read must not be trusted enough to remove
        // rules from: the rule stays until the fault escalates.
        harness.Advance(TimeSpan.FromSeconds(5));
        for (var tick = 0; tick < 5; tick++) harness.Tick();
        Assert.Equal(1, harness.Cache.RuleCount);

        for (var tick = 0; tick < NdisKernelPassCache.ReadFailureLimit - 5; tick++) harness.Tick();

        Assert.True(harness.Cache.IsDisabled);
        Assert.Equal(NdisKernelPassCache.ReadFailureLimit, harness.Cache.ReadFailures);
        Assert.Equal(0, harness.Cache.RuleCount);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task ACounterThatMovedBackwardsIsOnlyABaseline()
    {
        await using var harness = new Harness();
        harness.Cache.Start();
        Install(harness, Key(0));
        harness.Table.AdvanceCounters(10);
        harness.Advance(TimeSpan.FromSeconds(1.5));
        harness.Tick();
        Assert.Equal(10, harness.Cache.BypassedPackets);

        // A driver reset makes the counter smaller: the cache rebaselines instead of claiming activity.
        harness.Table.SetCounter(0, 0);
        harness.Advance(TimeSpan.FromSeconds(1.5));
        harness.Tick();
        Assert.Equal(10, harness.Cache.BypassedPackets);

        harness.Advance(TimeSpan.FromMilliseconds(500));
        harness.Tick();
        Assert.Equal(0, harness.Cache.RuleCount);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task AnUnavailableExportDisablesTheCacheWithOneWarning()
    {
        await using var harness = new Harness();
        harness.Table.ResetStatus = NdisKernelFilterStatus.Unavailable;

        harness.Cache.Start();

        Assert.True(harness.Cache.IsDisabled);
        Assert.Equal(0, harness.Table.ReplaceCalls);
        var warning = Assert.Single(harness.Logger.Events, recorded => string.Equals(recorded.Name, "kernel.passCache.unavailable", StringComparison.Ordinal));
        Assert.Equal(LogLevel.Warning, warning.Level);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task AFailedStartupResetIsNeverRetriedAtDispose()
    {
        await using var harness = new Harness();
        var key = Key(0);
        Assert.Equal(
            NdisKernelFilterStatus.Success,
            harness.Table.Replace([new NdisTcpFlowRule(key.Local.Address, key.Local.Port, key.Remote.Address, key.Remote.Port)], out _));
        harness.Table.ResetStatus = NdisKernelFilterStatus.Failed;

        harness.Cache.Start();
        Assert.True(harness.Cache.IsDisabled);

        // The startup reset failed, so the cache never claimed the table: dispose must not clear a
        // table it could not own.
        var resets = harness.Table.ResetCalls;
        await harness.Cache.DisposeAsync();
        Assert.Equal(resets, harness.Table.ResetCalls);
        Assert.Single(harness.Table.InstalledRules);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task TheSummaryOmitsTheRatiosWhenNothingWasAccountedFor()
    {
        await using var harness = new Harness();
        harness.Cache.Start();

        harness.Advance(NdisKernelPassCache.s_summaryInterval);
        harness.Tick();

        var summary = Assert.Single(harness.Logger.Events, recorded => string.Equals(recorded.Name, "kernel.passCache", StringComparison.Ordinal));
        Assert.Null(summary.Field("CrossingsPerMille"));
        Assert.Null(summary.Field("CallsPerMille"));
        Assert.Equal(0L, summary.Field("Bypassed"));
        Assert.Equal(0L, summary.Field("Captured"));
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task TheSummaryReportsTheDriversDeltasAndMirrorsTheEventCounters()
    {
        await using var harness = new Harness();
        harness.Cache.Start();
        Install(harness, Key(0));
        harness.Table.AdvanceCounters(30);
        harness.Table.Traffic = new NdisTrafficCounters(ReadCalls: 4, PacketsRead: 50, SendCalls: 3, PacketsReinjected: 40);
        harness.Advance(TimeSpan.FromMilliseconds(300));
        harness.Tick();
        harness.Advance(NdisKernelPassCache.s_summaryInterval);
        // Renew the lease at the summary tick: the flow is still passing traffic, which is exactly what
        // the counter-driven lease is for.
        harness.Table.AdvanceCounters(20);
        harness.Tick();

        var summary = Assert.Single(harness.Logger.Events, recorded => string.Equals(recorded.Name, "kernel.passCache", StringComparison.Ordinal));
        Assert.Equal(50L, summary.Field("Bypassed"));
        Assert.Equal(50L, summary.Field("Captured"));
        Assert.Equal(40L, summary.Field("Reinjected"));
        // 1000 x (50 read + 40 reinjected) / (50 read + 50 bypassed) = 900.
        Assert.Equal(900L, summary.Field("CrossingsPerMille"));
        // 1000 x (4 read calls + 3 send calls) / 100 = 70.
        Assert.Equal(70L, summary.Field("CallsPerMille"));
        Assert.Equal(1L, harness.Counters.Get(RuntimeCounters.KernelPassCacheApplies));
        Assert.Equal(50L, harness.Counters.Get(RuntimeCounters.KernelPassCacheBypassed));
        Assert.Equal(1, harness.Cache.RuleCount);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task TheDriverIsCalledFromTheWorkersThreadNotTheObserver()
    {
        await using var harness = new Harness();
        harness.Cache.Start();
        var cache = harness.Cache;
        var observer = new Thread(() => cache.OnPassPacket(Key(0)));
        observer.Start();
        observer.Join();

        // The observation ran on its own thread and touched only the marker and ring: the driver saw
        // just the startup reset. The tick then drives the replace from this thread, so the caller
        // identity is asserted rather than raced against a pool thread.
        Assert.Equal(["reset"], harness.Table.Operations);
        harness.Advance(TimeSpan.FromSeconds(1));
        var workerThread = Environment.CurrentManagedThreadId;
        harness.Tick();

        Assert.Equal(1, harness.Table.ReplaceCalls);
        Assert.Equal(workerThread, harness.Table.LastOperationThreadId);
    }

    [SupportedOSPlatform("windows")]
    private static void Install(Harness harness, FlowKey key)
    {
        harness.Cache.OnPassPacket(key);
        harness.Advance(TimeSpan.FromSeconds(1));
        harness.Tick();
        Assert.Equal(1, harness.Cache.RuleCount);
    }

    [SupportedOSPlatform("windows")]
    private static void Expire(Harness harness)
    {
        harness.Advance(TimeSpan.FromSeconds(2));
        harness.Tick();
        Assert.Equal(0, harness.Cache.RuleCount);
    }

    private static FlowKey Key(int index) => FlowKey.Create(
        Endpoint.From(IPAddress.Parse("192.0.2.10"), (ushort)(30_000 + index)),
        Endpoint.From(IPAddress.Parse("198.51.100.7"), 443),
        TransportProtocol.Tcp,
        FlowOriginKind.Host);

    [SupportedOSPlatform("windows")]
    private sealed class Harness
    {
        public Harness()
        {
            Table = new RecordingKernelFilterTable();
            Logger = new RecordingLogger();
            Counters = new RuntimeCounters();
            Cache = new NdisKernelPassCache(Table, Logger, Clock, Counters);
        }

        private ManualTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 10, 18, 0, 0, 0, TimeSpan.Zero));

        public RecordingKernelFilterTable Table { get; }

        public RecordingLogger Logger { get; }

        public RuntimeCounters Counters { get; }

        public NdisKernelPassCache Cache { get; }

        public void Tick() => Cache.Tick();

        public void Advance(TimeSpan duration) => Clock.Advance(duration);

        public async ValueTask DisposeAsync() => await Cache.DisposeAsync().ConfigureAwait(false);

    }
}
