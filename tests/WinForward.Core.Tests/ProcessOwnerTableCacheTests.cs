using System.Net;
using WinForward.TestSupport;
using WinForward.Windows;
using Xunit;

namespace WinForward.Core.Tests;

/// <summary>
/// The owner-table epoch coalescer's exact facts. Every one is seam-level: the scripted reader
/// stands in for the system-wide enumeration, which cannot run on this host.
/// </summary>
public sealed class ProcessOwnerTableCacheTests
{
    private static readonly DateTimeOffset s_start = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task NConcurrentMissesInsideTheWindowReadTheTableExactlyOnce()
    {
        var reader = new ScriptedOwnerTableReader { ReadDelay = TimeSpan.FromMilliseconds(50) };
        var clock = new TestClock(s_start);
        var cache = new ProcessOwnerTableCache(reader, ProcessOwnerTableCache.DefaultWindowMs, clock.Read);
        var key = TcpKey(53000);
        var answers = new uint?[8];

        await Task.WhenAll(Enumerable.Range(0, answers.Length).Select(index => Task.Run(() => answers[index] = cache.Lookup(key, clock.Now))));

        Assert.Equal(1, reader.ReadCount);
        Assert.All(answers, Assert.Null);
    }

    [Fact]
    public void ARetryThatJoinsALaterEpochDoesNotReadAgain()
    {
        var reader = new ScriptedOwnerTableReader();
        var cache = new ProcessOwnerTableCache(reader, ProcessOwnerTableCache.DefaultWindowMs, static () => DateTimeOffset.UtcNow);
        var key = TcpKey(53001);
        reader.Script(OwnerTableKind.Tcp4, [], [new TcpOwnerRow(key.Local, key.Remote, 4242)]);

        // The retry's request instant is taken first, so the epoch the next lookup publishes was
        // necessarily taken at or after it: the join rule is exercised by the instant alone, with no
        // dependence on thread-pool start latency (the concurrent run this replaces failed once in
        // the full suite because the retry reached the refresh gate first and scanned).
        var retryInstant = DateTimeOffset.UtcNow;
        var first = cache.Lookup(key, DateTimeOffset.UtcNow);
        Assert.Equal(1, reader.ReadCount);
        Assert.Equal(4242u, first);

        var retry = cache.Lookup(key, retryInstant);
        Assert.Equal(1, reader.ReadCount);
        Assert.Equal(first, retry);
    }

    [Fact]
    public void ARequestWhoseSocketBoundAfterTheReadForcesExactlyOneMoreRead()
    {
        var reader = new ScriptedOwnerTableReader();
        var clock = new TestClock(s_start);
        var cache = new ProcessOwnerTableCache(reader, ProcessOwnerTableCache.DefaultWindowMs, clock.Read);
        var key = TcpKey(53002);
        reader.Script(OwnerTableKind.Tcp4, [], []);

        Assert.Null(cache.Lookup(key, clock.Now));
        Assert.Equal(1, reader.ReadCount);

        // The socket binds after that read; the flow's own row is only in the next table.
        reader.Script(OwnerTableKind.Tcp4, [], [TcpRow(53002, 4242)]);
        clock.Advance(TimeSpan.FromMilliseconds(50));

        Assert.Equal(4242u, cache.Lookup(key, clock.Now));
        Assert.Equal(2, reader.ReadCount);
    }

    [Fact]
    public void ACachedTcpHitAnswersWithoutARead()
    {
        var reader = new ScriptedOwnerTableReader();
        var clock = new TestClock(s_start);
        var cache = new ProcessOwnerTableCache(reader, ProcessOwnerTableCache.DefaultWindowMs, clock.Read);
        reader.Script(OwnerTableKind.Tcp4, [], [TcpRow(53003, 77)]);

        Assert.Equal(77u, cache.Lookup(TcpKey(53003), clock.Now));
        clock.Advance(TimeSpan.FromMilliseconds(100));
        Assert.Equal(77u, cache.Lookup(TcpKey(53003), clock.Now));

        Assert.Equal(1, reader.ReadCount);
    }

    [Fact]
    public void AUdpLookupNeverAnswersFromTheSnapshot()
    {
        var reader = new ScriptedOwnerTableReader();
        var clock = new TestClock(s_start);
        var cache = new ProcessOwnerTableCache(reader, ProcessOwnerTableCache.DefaultWindowMs, clock.Read);
        reader.Script(OwnerTableKind.Udp4, [new UdpOwnerRow(IPAddress.Any, 53004, 88)], []);

        Assert.Equal(88u, cache.Lookup(UdpKey(53004), clock.Now));
        clock.Advance(TimeSpan.FromMilliseconds(100));
        Assert.Equal(88u, cache.Lookup(UdpKey(53004), clock.Now));

        // The same shape over TCP is served from the window; UDP re-reads, because its predicate
        // matches the local port alone and a recycled port inside the window would be fail-open.
        Assert.Equal(2, reader.ReadCount);
    }

    [Fact]
    public void ASnapshotOlderThanTheWindowForcesARead()
    {
        var reader = new ScriptedOwnerTableReader();
        var clock = new TestClock(s_start);
        var cache = new ProcessOwnerTableCache(reader, ProcessOwnerTableCache.DefaultWindowMs, clock.Read);
        reader.Script(OwnerTableKind.Tcp4, [], [TcpRow(53005, 99)]);

        Assert.Equal(99u, cache.Lookup(TcpKey(53005), clock.Now));
        clock.Advance(TimeSpan.FromMilliseconds(400));
        Assert.Equal(99u, cache.Lookup(TcpKey(53005), clock.Now));

        Assert.Equal(2, reader.ReadCount);
    }

    [Fact]
    public void TheSnapshotLookupAgreesWithTheScanForEveryScriptedRow()
    {
        var reader = new ScriptedOwnerTableReader();
        var clock = new TestClock(s_start);
        var cache = new ProcessOwnerTableCache(reader, ProcessOwnerTableCache.DefaultWindowMs, clock.Read);
        reader.Script(
            OwnerTableKind.Tcp4,
            [],
            [TcpRow(53006, 11), TcpRow(53007, 11), TcpRow(53008, 12)]);
        reader.Script(OwnerTableKind.Udp4, [new UdpOwnerRow(IPAddress.Any, 53009, 21)], []);

        Assert.Equal(11u, cache.Lookup(TcpKey(53006), clock.Now));
        Assert.Equal(12u, cache.Lookup(TcpKey(53008), clock.Now));
        Assert.Equal(21u, cache.Lookup(UdpKey(53009), clock.Now));
        // No row for this tuple: the snapshot and a fresh scan both answer "no owner".
        Assert.Null(cache.Lookup(TcpKey(53010), clock.Now));
        Assert.Equal(2, reader.ReadCount);
    }

    [Fact]
    public void TheRecycledPortRowIsOnlyServedForTcp()
    {
        var reader = new ScriptedOwnerTableReader();
        var clock = new TestClock(s_start);
        var cache = new ProcessOwnerTableCache(reader, ProcessOwnerTableCache.DefaultWindowMs, clock.Read);
        reader.Script(OwnerTableKind.Tcp4, [], [TcpRow(53011, 1111)]);
        reader.Script(OwnerTableKind.Udp4, [new UdpOwnerRow(IPAddress.Any, 53011, 2222)], []);

        Assert.Equal(1111u, cache.Lookup(TcpKey(53011), clock.Now));
        Assert.Equal(2222u, cache.Lookup(UdpKey(53011), clock.Now));

        // Port 53011 changed hands: the exact TCP four-tuple is a different connection (which
        // TIME_WAIT makes impossible inside the window, so the stale answer is accepted), while the
        // UDP row is matched on the port alone and must not be served stale.
        reader.Script(OwnerTableKind.Tcp4, [], [TcpRow(53011, 3333)]);
        reader.Script(OwnerTableKind.Udp4, [new UdpOwnerRow(IPAddress.Any, 53011, 4444)], []);
        clock.Advance(TimeSpan.FromMilliseconds(100));

        Assert.Equal(1111u, cache.Lookup(TcpKey(53011), clock.Now));
        Assert.Equal(4444u, cache.Lookup(UdpKey(53011), clock.Now));
        Assert.Equal(3, reader.ReadCount);
    }

    [Fact]
    public async Task WindowZeroKeepsTheCoalescingAndDropsTheReuse()
    {
        var reader = new ScriptedOwnerTableReader { ReadDelay = TimeSpan.FromMilliseconds(50) };
        var clock = new TestClock(s_start);
        var cache = new ProcessOwnerTableCache(reader, windowMs: 0, clock.Read);
        var key = TcpKey(53012);

        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() => cache.Lookup(key, clock.Now))));
        Assert.Equal(1, reader.ReadCount);

        clock.Advance(TimeSpan.FromMilliseconds(50));
        Assert.Null(cache.Lookup(key, clock.Now));
        Assert.Equal(2, reader.ReadCount);
    }

    [Fact]
    public void ANonWindowsReaderReportsUnavailableAndCachesNothing()
    {
        var cache = new ProcessOwnerTableCache(UnavailableOwnerTableReader.Instance);
        Assert.Null(cache.Lookup(TcpKey(53013), s_start));
        Assert.Null(cache.Lookup(TcpKey(53013), s_start));
        Assert.Equal(0, cache.ReadCount);
    }

    [Fact]
    public void AnOutOfRangeWindowIsRejected()
    {
        var reader = new ScriptedOwnerTableReader();
        Assert.Throws<ArgumentOutOfRangeException>(() => new ProcessOwnerTableCache(reader, windowMs: -1));
    }

    private static FlowKey TcpKey(ushort localPort) => FlowKey.Create(
        Endpoint.From(IPAddress.Parse("192.0.2.10"), localPort),
        Endpoint.From(IPAddress.Parse("192.0.2.53"), 443),
        TransportProtocol.Tcp,
        FlowOriginKind.Host);

    private static FlowKey UdpKey(ushort localPort) => FlowKey.Create(
        Endpoint.From(IPAddress.Parse("192.0.2.10"), localPort),
        Endpoint.From(IPAddress.Parse("192.0.2.53"), 53),
        TransportProtocol.Udp,
        FlowOriginKind.Host);

    private static TcpOwnerRow TcpRow(ushort localPort, uint processId) => new(
        Endpoint.From(IPAddress.Parse("192.0.2.10"), localPort),
        Endpoint.From(IPAddress.Parse("192.0.2.53"), 443),
        processId);
}
