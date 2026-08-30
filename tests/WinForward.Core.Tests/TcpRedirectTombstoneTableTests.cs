using System.Net;
using WinForward.Core;
using WinForward.Runtime.TcpRedirect;
using Xunit;

namespace WinForward.Core.Tests;

public sealed class TcpRedirectTombstoneTableTests
{
    private static readonly IPAddress s_client = IPAddress.Parse("192.0.2.10");
    private static readonly IPAddress s_dest = IPAddress.Parse("192.0.2.53");

    [Fact]
    public void BothKeysHitOnlyInsideTheGraceWindow()
    {
        var table = new TcpRedirectTombstoneTable(capacity: 4);
        var now = DateTimeOffset.UtcNow;
        Add(table, clientPort: 53000, now.AddSeconds(60));

        Assert.True(table.TryHit(Key(53000), now.AddSeconds(59)));
        Assert.True(table.TryHit(ReverseSource(), ReverseDestination(53000), now.AddSeconds(59)));
        // At and past the expiry the tombstone no longer matches: same-tuple packets fall back to
        // the pre-tombstone behavior.
        Assert.False(table.TryHit(Key(53000), now.AddSeconds(60)));
        Assert.False(table.TryHit(ReverseSource(), ReverseDestination(53000), now.AddSeconds(61)));
        Assert.False(table.TryHit(Key(53001), now.AddSeconds(30)));
        Assert.False(table.TryHit(ReverseSource(), ReverseDestination(53001), now.AddSeconds(30)));
    }

    [Fact]
    public void FullTableEvictsTheOldestEntry()
    {
        var table = new TcpRedirectTombstoneTable(capacity: 2);
        var now = DateTimeOffset.UtcNow;
        Add(table, clientPort: 53000, now.AddSeconds(60));
        Add(table, clientPort: 53001, now.AddSeconds(60));
        Add(table, clientPort: 53002, now.AddSeconds(60));

        Assert.Equal(2, table.Count);
        Assert.False(table.TryHit(Key(53000), now));
        Assert.True(table.TryHit(Key(53001), now));
        Assert.True(table.TryHit(Key(53002), now));
    }

    [Fact]
    public void RemoveExpiredReclaimsOnlyElapsedEntries()
    {
        var table = new TcpRedirectTombstoneTable(capacity: 4);
        var now = DateTimeOffset.UtcNow;
        Add(table, clientPort: 53000, now.AddSeconds(-1));
        Add(table, clientPort: 53001, now.AddSeconds(60));

        Assert.Equal(1, table.RemoveExpired(now));

        Assert.Equal(1, table.Count);
        Assert.False(table.TryHit(Key(53000), now));
        Assert.True(table.TryHit(Key(53001), now));
    }

    [Fact]
    public void ReAddingTheSameKeysRefreshesTheExpiry()
    {
        var table = new TcpRedirectTombstoneTable(capacity: 4);
        var now = DateTimeOffset.UtcNow;
        Add(table, clientPort: 53000, now.AddSeconds(10));
        Add(table, clientPort: 53000, now.AddSeconds(60));

        Assert.Equal(1, table.Count);
        Assert.True(table.TryHit(Key(53000), now.AddSeconds(59)));
        Assert.True(table.TryHit(ReverseSource(), ReverseDestination(53000), now.AddSeconds(59)));
    }

    [Fact]
    public void RemoveExpiredDrainsStaleQueueRecordsFromRefreshChurn()
    {
        // Without the head drain, every refresh leaks its superseded queue record: this churn
        // would leave 50 records for one live entry. After the drain sweep the queue holds only
        // the live entry's current record.
        var table = new TcpRedirectTombstoneTable(capacity: 64);
        var now = DateTimeOffset.UtcNow;
        const int refreshes = 50;
        for (var index = 0; index < refreshes; index++) Add(table, clientPort: 53000, now.AddSeconds(60));

        Assert.Equal(1, table.Count);
        Assert.Equal(refreshes, table.QueueCountForDiagnostics);

        // The sweep clock is inside the grace window: nothing expires from the dictionaries, the
        // head drain still reclaims every superseded record.
        table.RemoveExpired(now.AddSeconds(30));

        Assert.Equal(1, table.Count);
        Assert.Equal(1, table.QueueCountForDiagnostics);
    }

    [Fact]
    public void QueueLengthConvergesToLiveEntriesUnderRefreshAndExpiryChurn()
    {
        // Repeated teardown/refresh cycles below capacity (the churny-uptime shape): queue length
        // must track the live entry count, never the total TryAdd count.
        var table = new TcpRedirectTombstoneTable(capacity: 64);
        var now = DateTimeOffset.UtcNow;
        const int liveKeys = 4;
        const int rounds = 20;
        for (var round = 0; round < rounds; round++)
        {
            for (var clientPort = 53000; clientPort < 53000 + liveKeys; clientPort++)
            {
                Add(table, (ushort)clientPort, now.AddSeconds(60));
            }

            table.RemoveExpired(now.AddSeconds(30));
        }

        Assert.Equal(liveKeys, table.Count);
        Assert.Equal(liveKeys, table.QueueCountForDiagnostics);

        // Full expiry drains everything: dictionaries and queue both return to zero.
        Assert.Equal(liveKeys, table.RemoveExpired(now.AddSeconds(61)));
        Assert.Equal(0, table.Count);
        Assert.Equal(0, table.QueueCountForDiagnostics);
    }

    private static FlowKey Key(ushort clientPort) => FlowKey.Create(Endpoint.From(s_client, clientPort), Endpoint.From(s_dest, 443), TransportProtocol.Tcp, FlowOriginKind.Host);

    private static Endpoint ReverseSource() => Endpoint.From(s_client, 42000);

    private static Endpoint ReverseDestination(ushort clientPort) => Endpoint.From(s_dest, clientPort);

    private static void Add(TcpRedirectTombstoneTable table, ushort clientPort, DateTimeOffset expiry) =>
        table.TryAdd(Key(clientPort), ReverseSource(), ReverseDestination(clientPort), expiry);
}
