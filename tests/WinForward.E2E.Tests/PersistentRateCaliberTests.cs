using System.Net;
using System.Net.Sockets;
using WinForward.E2E.Client;
using WinForward.E2E.Client.Arms;
using WinForward.E2E.Contracts.Json;
using WinForward.E2E.Contracts.Metrics;
using WinForward.E2E.Target;
using Xunit;

namespace WinForward.E2E.Tests;

/// <summary>
/// PERSIST's two rate calibers (D19.2 ⑧): <c>achievedRate</c> counts the requests whose send completed
/// per elapsed second and <c>completionRate</c> counts the responses per elapsed second -- the population
/// <c>achievedRate</c> carried before the harness unified that name. Both facts drive a real arm and read
/// the published pair beside the counters it is built from, so the rename is pinned on one run's own
/// numbers rather than against another run's.
/// </summary>
public sealed class PersistentRateCaliberTests
{
    /// <summary>
    /// Every request answered: the two calibers carry the same value, which is the value the old
    /// expression (<c>responses</c> over the elapsed span) published under the old name, so the rename
    /// moved no number.
    /// </summary>
    [Fact]
    public async Task EveryRequestAnsweredPublishesBothCalibersWithTheSameValue()
    {
        var tcpPort = ArmRunFixture.FreePort(SocketType.Stream, ProtocolType.Tcp);
        using var ledgerStream = new MemoryStream();
        await using var ledger = new JsonlSink(ledgerStream, JsonlPolicy.SwallowAndCount, envelope: null, flushInterval: TimeSpan.FromMinutes(1));
        await using var target = new TcpTargetServer(new IPEndPoint(IPAddress.Loopback, tcpPort), ledger);
        using var serving = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var accepting = target.RunAsync(serving.Token);
        await using var fixture = ArmRunFixture.Create(
            tcpPort,
            ArmRunFixture.FreePort(SocketType.Dgram, ProtocolType.Udp),
            ArmRunFixture.FreePort(SocketType.Dgram, ProtocolType.Udp));

        var metrics = await RunPersistentAsync(fixture, seconds: 3, intervalMs: 500);

        await serving.CancelAsync();
        await accepting;

        Assert.True(metrics.Requests > 0, "the arm offered no request");
        Assert.Equal(metrics.Requests, metrics.Responses);
        Assert.True(metrics.CompletionRate > 0, "an answered run published no completion rate");
        Assert.Equal(metrics.CompletionRate, metrics.AchievedRate);
    }

    /// <summary>
    /// Requests sent and never answered: the send caliber moves and the completion caliber stays at the
    /// zero the old expression yields for an empty response population, which is what makes the two
    /// names two populations rather than one written twice.
    /// </summary>
    [Fact]
    public async Task RequestsSentButNeverAnsweredSeparateTheTwoCalibers()
    {
        using var listener = Sockets.BindTcpListener(new IPEndPoint(IPAddress.Loopback, 0));
        var tcpPort = ((IPEndPoint)listener.LocalEndPoint!).Port;
        using var serving = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var draining = DrainWithoutAnsweringAsync(listener, serving.Token);
        await using var fixture = ArmRunFixture.Create(
            tcpPort,
            ArmRunFixture.FreePort(SocketType.Dgram, ProtocolType.Udp),
            ArmRunFixture.FreePort(SocketType.Dgram, ProtocolType.Udp));

        var metrics = await RunPersistentAsync(fixture, seconds: 5, intervalMs: 2_000);

        await serving.CancelAsync();
        await draining;

        Assert.True(metrics.Requests > 0, "the arm offered no request");
        Assert.Equal(0, metrics.Responses);
        Assert.True(metrics.Timeouts > 0, "a silent peer produced no timeout");
        Assert.Equal(0, metrics.CompletionRate);
        Assert.True(
            metrics.AchievedRate > metrics.CompletionRate,
            $"a sent request lifted the completion caliber: achievedRate {metrics.AchievedRate} vs completionRate {metrics.CompletionRate}");
    }

    /// <summary>One PERSIST arm over the fixture's target, with the published metrics read back.</summary>
    private static async Task<PersistentMetrics> RunPersistentAsync(ArmRunFixture fixture, double seconds, int intervalMs)
    {
        var outcome = await PersistentArm.RunAsync(fixture.ContextFor(new ArmSpec
        {
            Name = "PERSIST",
            Kind = "persistent",
            Seconds = seconds,
            IntervalMs = intervalMs,
            IdleSeconds = 1,
        }));

        return Assert.IsType<PersistentMetrics>(outcome.Metrics);
    }

    /// <summary>
    /// A peer that accepts, reads every frame and answers none: the requests are sent and time out, which
    /// is the only shape in which the two calibers can differ.
    /// </summary>
    private static async Task DrainWithoutAnsweringAsync(Socket listener, CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                using var peer = await listener.AcceptAsync(cancellationToken).ConfigureAwait(false);
                while (await peer.ReceiveAsync(buffer, SocketFlags.None, cancellationToken).ConfigureAwait(false) > 0)
                {
                    /* read and drop: answering is the one thing this peer must not do */
                }
            }
        }
        catch (OperationCanceledException)
        {
            /* the fact ended the peer */
        }
        catch (ObjectDisposedException)
        {
            /* the fact disposed the listener under it */
        }
    }
}
