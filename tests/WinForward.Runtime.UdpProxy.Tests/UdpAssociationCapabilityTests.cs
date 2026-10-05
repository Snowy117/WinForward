using System.Globalization;
using System.Net;
using System.Net.Sockets;
using WinForward.Configuration;
using WinForward.Core;
using WinForward.TestSupport;
using Xunit;
using static WinForward.TestSupport.UdpAssociationFakes;

namespace WinForward.Runtime.UdpProxy.Tests;

/// <summary>
/// Passive capability detection and the sticky per-server fallback (design §5, R2/I8): a server that
/// answers a second attached flow is confirmed shareable, a server whose attached flow keeps sending
/// unanswered while a sibling is answered is flipped to per-flow associations for the rest of the
/// run, and the flip never perturbs a session that is already attached.
/// <para>
/// The evidence is what the datagram path writes, so the fake relay is the discriminator: it either
/// echoes every datagram (permissive) or answers only the source port that reached it first
/// (pinning), which is exactly the shape a source-port-pinning server produces for a second flow.
/// </para>
/// </summary>
public sealed class UdpAssociationCapabilityTests
{
    private const int PayloadLength = 16;
    private static readonly TimeSpan s_observationWindow = TimeSpan.FromMilliseconds(250);

    [Fact]
    public async Task APermissiveServerIsConfirmedShareableAndKeepsSharing()
    {
        await using var echo = new EchoRelay();
        await using var server = new ScriptedSocks5UdpServer(echo.Endpoint);
        await using var pool = CreatePool(UdpAssociationReuseMode.Auto);
        var registry = new SelfTrafficRegistry();
        await using var first = CreateTransport(pool, registry, server);
        await using var second = CreateTransport(pool, registry, server);
        var destination = Endpoint.From(IPAddress.Parse("192.0.2.53"), 53);
        var payload = new byte[PayloadLength];

        await first.Transport.SendSpanAsync(destination, payload, CancellationToken.None);
        Assert.True(await ReceiveDatagramAsync(first.Transport));
        await second.Transport.SendSpanAsync(destination, payload, CancellationToken.None);
        Assert.True(await ReceiveDatagramAsync(second.Transport));

        Assert.Equal(0, pool.SampleServerCapabilities());
        Assert.Equal(UdpServerCapability.SharedOk, pool.CapabilityOf(server.Server));
        Assert.Equal(1, server.AssociateReplyCount);

        // The positive verdict is sticky and sharing continues: the next flow joins the same relay.
        Assert.Equal(0, pool.SampleServerCapabilities());
        await using var third = CreateTransport(pool, registry, server);
        await third.Transport.SendSpanAsync(destination, payload, CancellationToken.None);
        Assert.Equal(1, pool.AssociationCount);
        Assert.Equal(3, pool.LeasedFlowCount);
        Assert.Equal(echo.Endpoint.Port, third.Transport.PeerEndpoint.Port);
    }

    [Fact]
    public async Task ASecondFlowWithNoResponseWhileASiblingIsAnsweredNeverFlips()
    {
        await using var echo = new EchoRelay();
        await using var server = new ScriptedSocks5UdpServer(echo.Endpoint);
        await using var pool = CreatePool(UdpAssociationReuseMode.Auto);
        var registry = new SelfTrafficRegistry();
        await using var first = CreateTransport(pool, registry, server);
        await using var second = CreateTransport(pool, registry, server);
        var destination = Endpoint.From(IPAddress.Parse("192.0.2.53"), 53);
        var payload = new byte[PayloadLength];

        await first.Transport.SendSpanAsync(destination, payload, CancellationToken.None);
        Assert.True(await ReceiveDatagramAsync(first.Transport));
        await second.Transport.SendSpanAsync(destination, payload, CancellationToken.None);
        Assert.True(await ReceiveDatagramAsync(second.Transport));

        // Two answered flows is the positive shape; a later unanswered flow must not undo it.
        Assert.Equal(0, pool.SampleServerCapabilities());
        await using var third = CreateTransport(pool, registry, server);
        for (var index = 0; index < UdpAssociationCapabilitySampler.PinningSuspicionThreshold; index++)
        {
            await third.Transport.SendSpanAsync(destination, payload, CancellationToken.None);
        }

        Assert.Equal(0, pool.SampleServerCapabilities());
        Assert.Equal(UdpServerCapability.SharedOk, pool.CapabilityOf(server.Server));
        Assert.Equal(1, pool.AssociationCount);
    }

    [Fact]
    public async Task ASourcePortPinningServerFlipsAndKeepsExistingSessionsAlive()
    {
        await using var relay = new PinningRelay();
        await using var server = new ScriptedSocks5UdpServer(relay.Endpoint);
        var recorder = new RecordingRuntimeLogger();
        await using var pool = CreatePool(UdpAssociationReuseMode.Auto, recorder);
        var registry = new SelfTrafficRegistry();
        await using var first = CreateTransport(pool, registry, server);
        var fallbacksBefore = RuntimeCounters.Shared.Get(RuntimeCounters.UdpAssociationFallbacks);
        var destination = Endpoint.From(IPAddress.Parse("192.0.2.53"), 53);
        var payload = new byte[PayloadLength];

        // The first flow's datagram reaches the relay and pins its source port before the second
        // flow exists, so the answering flow is the first one by construction, not by race.
        await first.Transport.SendSpanAsync(destination, payload, CancellationToken.None);
        Assert.Equal(1, await relay.PumpAsync(1));
        Assert.True(await ReceiveDatagramAsync(first.Transport));
        await using var second = CreateTransport(pool, registry, server);
        Assert.Equal(first.Transport.PeerEndpoint.Port, second.Transport.PeerEndpoint.Port);
        for (var index = 0; index < UdpAssociationCapabilitySampler.PinningSuspicionThreshold; index++)
        {
            await second.Transport.SendSpanAsync(destination, payload, CancellationToken.None);
        }

        // The pinned flow's three datagrams reach the same relay from a second client port and are
        // never answered: the multiplexing this whole detection exists for.
        Assert.Equal(2, await relay.PumpAsync(UdpAssociationCapabilitySampler.PinningSuspicionThreshold));

        Assert.Equal(1, pool.SampleServerCapabilities());
        Assert.Equal(UdpServerCapability.PerFlowOnly, pool.CapabilityOf(server.Server));
        // The fallback counter is process-global and other flipping tests run in parallel collections,
        // so only its monotone direction is asserted here; the flip's "exactly once" property is the
        // local warn below plus the sticky verdict.
        Assert.True(RuntimeCounters.Shared.Get(RuntimeCounters.UdpAssociationFallbacks) >= fallbacksBefore + 1);
        var fallback = Assert.Single(recorder.Events, static recorded => string.Equals(recorded.Name, "udp.association.fallback", StringComparison.Ordinal));
        Assert.Equal(RuntimeLogLevel.Warn, fallback.Level);
        Assert.Equal("scripted", Field(fallback, "proxy"));
        Assert.Equal("source-port-pinned", Field(fallback, "reason"));
        Assert.Equal("2", Field(fallback, "flows"));
        Assert.Equal(UdpAssociationCapabilitySampler.PinningSuspicionThreshold.ToString(), Field(fallback, "sent"));

        // The flip retires nothing: both sessions keep their association, their sockets, and their
        // relay, and a further datagram is still answered for the answering flow. The state is read
        // through the leases rather than the process-global association-lost counter, which the
        // recovery suite increments in a parallel collection.
        Assert.Equal(1, pool.AssociationCount);
        Assert.Equal(2, pool.LeasedFlowCount);
        Assert.False(first.Lease.IsFaulted);
        Assert.False(second.Lease.IsFaulted);
        Assert.Equal(first.Transport.PeerEndpoint.Port, second.Transport.PeerEndpoint.Port);
        await first.Transport.SendSpanAsync(destination, payload, CancellationToken.None);
        Assert.Equal(2, await relay.PumpAsync(1));
        Assert.True(await ReceiveDatagramAsync(first.Transport));

        // Only placements change: a new flow is served by its own association (a second
        // authenticated control connection) instead of joining the flipped one.
        await using var third = CreateTransport(pool, registry, server);
        Assert.Equal(2, pool.AssociationCount);
        Assert.Equal(3, pool.LeasedFlowCount);
        Assert.Equal(2, server.AssociateReplyCount);

        // The verdict is recorded once: further samples do not re-emit it.
        Assert.Equal(0, pool.SampleServerCapabilities());
        Assert.Single(recorder.Events, static recorded => string.Equals(recorded.Name, "udp.association.fallback", StringComparison.Ordinal));

        // The rolled-back server never comes back on trial: its private association is left alone by
        // every later sample, so one flip is reported for the whole run.
        await third.Transport.SendSpanAsync(destination, payload, CancellationToken.None);
        Assert.Equal(0, pool.SampleServerCapabilities());
        Assert.Single(recorder.Events, static recorded => string.Equals(recorded.Name, "udp.association.fallback", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ASingleAttachedLeaseWithUnansweredSendsStaysUnknown()
    {
        using var relay = NewCapture();
        var relayEndpoint = (IPEndPoint)relay.LocalEndPoint!;
        await using var server = new ScriptedSocks5UdpServer(relayEndpoint);
        await using var pool = CreatePool(UdpAssociationReuseMode.Auto);
        var registry = new SelfTrafficRegistry();
        await using var transport = CreateTransport(pool, registry, server);
        var destination = Endpoint.From(IPAddress.Parse("192.0.2.53"), 53);
        var payload = new byte[PayloadLength];

        // One lease sends far past the suspicion threshold and decodes nothing: the datagrams reach
        // the relay, so the sampler sees the sends without a single response.
        for (var index = 0; index < UdpAssociationCapabilitySampler.PinningSuspicionThreshold + 2; index++)
        {
            await transport.Transport.SendSpanAsync(destination, payload, CancellationToken.None);
        }

        Assert.Equal(1, await ReceiveCountAsync(relay, CancellationToken.None));
        Assert.Equal(0, pool.SampleServerCapabilities());
        Assert.Equal(UdpServerCapability.Unknown, pool.CapabilityOf(server.Server));
        Assert.Equal(1, pool.AssociationCount);
    }

    [Fact]
    public async Task ExactlyThreeUnansweredSendsWithAnAnsweredSiblingFlipsButTwoDoNot()
    {
        await using var relay = new PinningRelay();
        await using var server = new ScriptedSocks5UdpServer(relay.Endpoint);
        await using var pool = CreatePool(UdpAssociationReuseMode.Auto);
        var registry = new SelfTrafficRegistry();
        await using var answering = CreateTransport(pool, registry, server);
        await using var pinned = CreateTransport(pool, registry, server);
        var destination = Endpoint.From(IPAddress.Parse("192.0.2.53"), 53);
        var payload = new byte[PayloadLength];

        await answering.Transport.SendSpanAsync(destination, payload, CancellationToken.None);
        Assert.Equal(1, await relay.PumpAsync(1));
        Assert.True(await ReceiveDatagramAsync(answering.Transport));

        // Two unanswered sends are below the threshold: the server stays on trial.
        await pinned.Transport.SendSpanAsync(destination, payload, CancellationToken.None);
        await pinned.Transport.SendSpanAsync(destination, payload, CancellationToken.None);
        Assert.Equal(0, pool.SampleServerCapabilities());
        Assert.Equal(UdpServerCapability.Unknown, pool.CapabilityOf(server.Server));

        // The third crosses it.
        await pinned.Transport.SendSpanAsync(destination, payload, CancellationToken.None);
        Assert.Equal(1, pool.SampleServerCapabilities());
        Assert.Equal(UdpServerCapability.PerFlowOnly, pool.CapabilityOf(server.Server));
    }

    [Fact]
    public async Task AlwaysModeNeverFlipsAgainstAPinningServer()
    {
        await using var relay = new PinningRelay();
        await using var server = new ScriptedSocks5UdpServer(relay.Endpoint);
        var recorder = new RecordingRuntimeLogger();
        await using var pool = CreatePool(UdpAssociationReuseMode.Always, recorder);
        var registry = new SelfTrafficRegistry();
        await using var answering = CreateTransport(pool, registry, server);
        await using var pinned = CreateTransport(pool, registry, server);
        var destination = Endpoint.From(IPAddress.Parse("192.0.2.53"), 53);
        var payload = new byte[PayloadLength];

        await answering.Transport.SendSpanAsync(destination, payload, CancellationToken.None);
        Assert.Equal(1, await relay.PumpAsync(1));
        Assert.True(await ReceiveDatagramAsync(answering.Transport));
        for (var index = 0; index < UdpAssociationCapabilitySampler.PinningSuspicionThreshold + 2; index++)
        {
            await pinned.Transport.SendSpanAsync(destination, payload, CancellationToken.None);
        }

        // Detection is off in this mode, so the pinned evidence settles nothing and this pool's
        // recorder stays silent (the process-global fallback counter is shared with parallel tests).
        Assert.Equal(0, pool.SampleServerCapabilities());
        Assert.Equal(UdpServerCapability.SharedOk, pool.CapabilityOf(server.Server));
        Assert.Empty(recorder.Events);

        await using var third = CreateTransport(pool, registry, server);
        Assert.Equal(answering.Transport.PeerEndpoint.Port, third.Transport.PeerEndpoint.Port);
    }

    [Fact]
    public async Task OffModeOpensPrivateAssociationsForEveryFlow()
    {
        await using var echo = new EchoRelay();
        await using var server = new ScriptedSocks5UdpServer(echo.Endpoint);
        await using var pool = CreatePool(UdpAssociationReuseMode.Off);
        var registry = new SelfTrafficRegistry();
        await using var first = CreateTransport(pool, registry, server);
        await using var second = CreateTransport(pool, registry, server);
        var destination = Endpoint.From(IPAddress.Parse("192.0.2.53"), 53);
        var payload = new byte[PayloadLength];

        for (var index = 0; index < 4; index++)
        {
            await first.Transport.SendSpanAsync(destination, payload, CancellationToken.None);
            await second.Transport.SendSpanAsync(destination, payload, CancellationToken.None);
        }

        // Two flows, two authenticated control connections and two associations whose relays each
        // keep answering their own flow: the per-flow shape is the placement itself, so no sampler
        // run can change it.
        Assert.Equal(0, pool.SampleServerCapabilities());
        Assert.Equal(UdpServerCapability.PerFlowOnly, pool.CapabilityOf(server.Server));
        Assert.Equal(2, pool.AssociationCount);
        Assert.Equal(2, server.AssociateReplyCount);
    }

    [Fact]
    public async Task AFlipIsStickyEvenWhenTheNextAssociationIsFullyAnswered()
    {
        await using var relay = new PinningRelay();
        await using var server = new ScriptedSocks5UdpServer(relay.Endpoint);
        await using var pool = CreatePool(UdpAssociationReuseMode.Auto);
        var registry = new SelfTrafficRegistry();
        await using var first = CreateTransport(pool, registry, server);
        var destination = Endpoint.From(IPAddress.Parse("192.0.2.53"), 53);
        var payload = new byte[PayloadLength];

        // The first flow is answered and the second is pinned away on the same association: the
        // sampler flips the server, which is the state this test then has to keep.
        await first.Transport.SendSpanAsync(destination, payload, CancellationToken.None);
        Assert.Equal(1, await relay.PumpAsync(1));
        Assert.True(await ReceiveDatagramAsync(first.Transport));
        await using var second = CreateTransport(pool, registry, server);
        for (var index = 0; index < UdpAssociationCapabilitySampler.PinningSuspicionThreshold; index++)
        {
            await second.Transport.SendSpanAsync(destination, payload, CancellationToken.None);
        }

        Assert.Equal(2, await relay.PumpAsync(UdpAssociationCapabilitySampler.PinningSuspicionThreshold));
        Assert.Equal(1, pool.SampleServerCapabilities());
        Assert.Equal(UdpServerCapability.PerFlowOnly, pool.CapabilityOf(server.Server));

        // A later association for the same server does not re-enable sharing — the verdict lasts the
        // run (I8) — and the two flows that were attached keep their association.
        await using var third = CreateTransport(pool, registry, server);
        Assert.Equal(0, pool.SampleServerCapabilities());
        Assert.Equal(UdpServerCapability.PerFlowOnly, pool.CapabilityOf(server.Server));
        Assert.Equal(2, pool.AssociationCount);
        Assert.Equal(2, server.AssociateReplyCount);
        Assert.Equal(3, pool.LeasedFlowCount);
    }

    [Fact]
    public void TheSamplerRuleIsConservativeInTheLossDirection()
    {
        Assert.Equal(UdpServerCapability.PerFlowOnly, Verdict(Answered(), Silent()));
        Assert.Equal(UdpServerCapability.SharedOk, Verdict(Answered(), Answered()));
        Assert.Null(Verdict(new UdpAssociationEvidence(), new UdpAssociationEvidence()));
        Assert.Null(Verdict(Answered()));
        Assert.Null(Verdict());

        // One responder and one silent sender that stayed below the threshold: no verdict yet.
        var belowThreshold = new UdpAssociationEvidence();
        for (var index = 0; index < UdpAssociationCapabilitySampler.PinningSuspicionThreshold - 1; index++) belowThreshold.RecordDatagramSent();
        Assert.Null(Verdict(Answered(), belowThreshold));

        // One responder and one silent sender past the threshold: pinning, because a second response
        // never arrived (that is checked first and would have won).
        var pastThreshold = new UdpAssociationEvidence();
        for (var index = 0; index < UdpAssociationCapabilitySampler.PinningSuspicionThreshold; index++) pastThreshold.RecordDatagramSent();
        Assert.Equal(UdpServerCapability.PerFlowOnly, Verdict(Answered(), pastThreshold));
    }

    /// <summary>A lease that sent one datagram and decoded the response.</summary>
    private static UdpAssociationEvidence Answered()
    {
        var evidence = new UdpAssociationEvidence();
        evidence.RecordDatagramSent();
        evidence.RecordResponseReceived();
        return evidence;
    }

    /// <summary>A lease that sent past the suspicion threshold and decoded nothing.</summary>
    private static UdpAssociationEvidence Silent()
    {
        var evidence = new UdpAssociationEvidence();
        for (var index = 0; index < UdpAssociationCapabilitySampler.PinningSuspicionThreshold; index++) evidence.RecordDatagramSent();
        return evidence;
    }

    /// <summary>
    /// Runs the sampler's rule over one association's evidence, as the pool does. The array is
    /// padded past the attached count on purpose: the sampler reads exactly the attached leases and
    /// never the untouched slots behind them.
    /// </summary>
    private static UdpServerCapability? Verdict(params UdpAssociationEvidence[] evidence)
    {
        var slots = new UdpAssociationEvidence[evidence.Length + 1];
        evidence.CopyTo(slots, 0);
        return UdpAssociationCapabilitySampler.Evaluate(slots, evidence.Length);
    }

    private static string? Field((RuntimeLogLevel Level, string Name, RuntimeLogField[] Fields) recorded, string key)
    {
        foreach (var field in recorded.Fields)
        {
            if (string.Equals(field.Key, key, StringComparison.Ordinal)) return Convert.ToString(field.Value, CultureInfo.InvariantCulture);
        }

        return null;
    }

    /// <summary>Receives one datagram; zero when nothing arrives inside the observation window.</summary>
    private static async Task<int> ReceiveCountAsync(Socket relay, CancellationToken cancellationToken)
    {
        var buffer = new byte[256];
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(s_observationWindow);
        EndPoint sender = new IPEndPoint(IPAddress.Any, 0);
        try
        {
            var result = await relay.ReceiveFromAsync(buffer, SocketFlags.None, sender, budget.Token);
            return result.ReceivedBytes > 0 ? 1 : 0;
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
    }
}
