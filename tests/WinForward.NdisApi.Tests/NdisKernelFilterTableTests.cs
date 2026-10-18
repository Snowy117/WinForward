using System.Net;
using System.Runtime.Versioning;
using WinForward.TestSupport;
using Xunit;

namespace WinForward.NdisApi.Tests;

/// <summary>
/// The driver's static-filter-table seam over a scripted native fake: the exact bytes a replace
/// hands to the driver, the read-back verification against the recorded installation, the failure
/// mapping (<c>Unavailable</c> for a missing export, <c>Failed</c> plus the native error for a native
/// FALSE, <c>TableMismatch</c> for a table that is not ours), the reset, and the traffic counters.
/// </summary>
public sealed class NdisKernelFilterTableTests
{
    private static readonly IPAddress s_client = IPAddress.Parse("192.0.2.10");
    private static readonly IPAddress s_server = IPAddress.Parse("198.51.100.7");
    private static readonly NdisTcpFlowRule s_request = new(s_client, 53000, s_server, 443);
    private static readonly NdisTcpFlowRule s_response = new(s_server, 8080, s_client, 61000);

    [Fact]
    [SupportedOSPlatform("windows")]
    public void ReplaceEncodesOneTableWithTwoOrientedRecordsPerRule()
    {
        var calls = new EchoStaticFilterCalls();
        using var driver = NdisApiDriver.CreateForTests(new NoReadCalls(), calls);

        var status = driver.Replace([s_request], out var nativeError);

        Assert.Equal(NdisKernelFilterStatus.Success, status);
        Assert.Equal(0, nativeError);
        Assert.Equal(1, calls.SetCalls);
        var table = Assert.IsType<byte[]>(calls.InstalledTable);
        Assert.Equal(new byte[] { 2, 0, 0, 0, 0, 0, 0, 0 }, table[..8]);
        Assert.Equal(s_client.GetAddressBytes(), StaticFilterTableOracle.ReadEntry(table, 0).Source);
        Assert.Equal(s_server.GetAddressBytes(), StaticFilterTableOracle.ReadEntry(table, 1).Source);
        Assert.Equal(53000, StaticFilterTableOracle.ReadEntry(table, 0).SourcePort);
        Assert.Equal(443, StaticFilterTableOracle.ReadEntry(table, 1).SourcePort);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void ReplaceRefusesAnEmptyRuleSetWithoutCallingTheNativeApi()
    {
        var calls = new EchoStaticFilterCalls();
        using var driver = NdisApiDriver.CreateForTests(new NoReadCalls(), calls);

        Assert.Throws<ArgumentException>(() => driver.Replace([], out _));

        Assert.Equal(0, calls.SetCalls);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void ReplaceRefusesAnUnencodableRuleWithoutCallingTheNativeApi()
    {
        var calls = new EchoStaticFilterCalls();
        using var driver = NdisApiDriver.CreateForTests(new NoReadCalls(), calls);
        var linkLocal = new NdisTcpFlowRule(IPAddress.Parse("fe80::1"), 53000, IPAddress.Parse("2001:db8::7"), 443);

        Assert.Throws<ArgumentException>(() => driver.Replace([linkLocal], out _));

        Assert.Equal(0, calls.SetCalls);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void ReadBackSumsPacketsInAndOutPerRuleInInstalledOrder()
    {
        var calls = new EchoStaticFilterCalls();
        using var driver = NdisApiDriver.CreateForTests(new NoReadCalls(), calls);
        NdisTcpFlowRule[] rules = [s_request, s_response];
        Assert.Equal(NdisKernelFilterStatus.Success, driver.Replace(rules, out _));
        var installed = calls.InstalledTable!;
        var readBack = StaticFilterTableOracle.WithCounters(installed, 0, packetsIn: 3, packetsOut: 4);
        readBack = StaticFilterTableOracle.WithCounters(readBack, 1, packetsIn: 0, packetsOut: 0);
        readBack = StaticFilterTableOracle.WithCounters(readBack, 2, packetsIn: 7, packetsOut: 1);
        readBack = StaticFilterTableOracle.WithCounters(readBack, 3, packetsIn: 2, packetsOut: 3);
        calls.ReadBackTable = readBack;
        var counts = new long[rules.Length];

        var status = driver.ReadRulePacketCounts(rules, counts, out var nativeError);

        Assert.Equal(NdisKernelFilterStatus.Success, status);
        Assert.Equal(0, nativeError);
        Assert.Equal([7L, 13L], counts);
        Assert.Equal(1, calls.ReadCalls);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void ReplaceReadReplaceWithAChangedSetReadAndReset()
    {
        var calls = new EchoStaticFilterCalls();
        using var driver = NdisApiDriver.CreateForTests(new NoReadCalls(), calls);

        Assert.Equal(NdisKernelFilterStatus.Success, driver.Replace([s_request], out _));
        Assert.Equal(NdisKernelFilterStatus.Success, driver.ReadRulePacketCounts([s_request], new long[1], out _));

        // A changed set replaces the whole table, and the next read-back must describe the new order
        // rather than the previous installation.
        Assert.Equal(NdisKernelFilterStatus.Success, driver.Replace([s_request, s_response], out _));
        Assert.Equal(2, calls.SetCalls);
        Assert.Equal(NdisKernelFilterStatus.Success, driver.ReadRulePacketCounts([s_request, s_response], new long[2], out _));

        Assert.Equal(NdisKernelFilterStatus.Success, driver.Reset(out _));
        Assert.Null(calls.InstalledTable);
        Assert.Equal(NdisKernelFilterStatus.TableMismatch, driver.ReadRulePacketCounts([s_request], new long[1], out _));
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void ReadBackWritesTheRecordCountIntoTheHeaderBeforeTheCall()
    {
        var calls = new EchoStaticFilterCalls();
        using var driver = NdisApiDriver.CreateForTests(new NoReadCalls(), calls);
        Assert.Equal(NdisKernelFilterStatus.Success, driver.Replace([s_request, s_response], out _));

        Assert.Equal(NdisKernelFilterStatus.Success, driver.ReadRulePacketCounts([s_request, s_response], new long[2], out _));

        Assert.Equal(4u, calls.LastRequestedTableSize);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void ReadBackRejectsATableWhoseRecordCountDiffers()
    {
        var calls = new EchoStaticFilterCalls();
        using var driver = NdisApiDriver.CreateForTests(new NoReadCalls(), calls);
        Assert.Equal(NdisKernelFilterStatus.Success, driver.Replace([s_request], out _));
        calls.SizeOverride = 3;

        Assert.Equal(NdisKernelFilterStatus.TableMismatch, driver.ReadRulePacketCounts([s_request], new long[1], out var nativeError));
        Assert.Equal(0, nativeError);
        Assert.Equal(0, calls.ReadCalls);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void ReadBackRejectsAChangedFieldEvenWhenTheSizeMatches()
    {
        var calls = new EchoStaticFilterCalls();
        using var driver = NdisApiDriver.CreateForTests(new NoReadCalls(), calls);
        Assert.Equal(NdisKernelFilterStatus.Success, driver.Replace([s_request], out _));
        // Offset 12 is the filter action: a record that is no longer PASS cannot be ours.
        calls.ReadBackTable = StaticFilterTableOracle.WithByte(calls.InstalledTable!, 0, 12, 0x02);

        Assert.Equal(NdisKernelFilterStatus.TableMismatch, driver.ReadRulePacketCounts([s_request], new long[1], out _));
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void ReadBackAcceptsAChangedStatisticsBlockAndLastReset()
    {
        var calls = new EchoStaticFilterCalls();
        using var driver = NdisApiDriver.CreateForTests(new NoReadCalls(), calls);
        Assert.Equal(NdisKernelFilterStatus.Success, driver.Replace([s_request], out _));
        var readBack = StaticFilterTableOracle.WithCounters(calls.InstalledTable!, 0, packetsIn: 9, packetsOut: 1);
        readBack = StaticFilterTableOracle.WithByte(readBack, 0, 20, 0x7F); // LastReset is driver-owned
        calls.ReadBackTable = readBack;

        Assert.Equal(NdisKernelFilterStatus.Success, driver.ReadRulePacketCounts([s_request], new long[1], out _));
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void ReadBackRejectsRulesThisDriverDidNotInstall()
    {
        var calls = new EchoStaticFilterCalls();
        using var driver = NdisApiDriver.CreateForTests(new NoReadCalls(), calls);

        Assert.Equal(NdisKernelFilterStatus.TableMismatch, driver.ReadRulePacketCounts([s_request], new long[1], out _));

        Assert.Equal(NdisKernelFilterStatus.Success, driver.Replace([s_request], out _));
        Assert.Equal(NdisKernelFilterStatus.TableMismatch, driver.ReadRulePacketCounts([s_response], new long[1], out _));
        Assert.Equal(NdisKernelFilterStatus.TableMismatch, driver.ReadRulePacketCounts([s_request, s_response], new long[2], out _));
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void ResetClearsTheTableAndTheRecordedInstallation()
    {
        var calls = new EchoStaticFilterCalls();
        using var driver = NdisApiDriver.CreateForTests(new NoReadCalls(), calls);
        Assert.Equal(NdisKernelFilterStatus.Success, driver.Replace([s_request], out _));

        var status = driver.Reset(out var nativeError);

        Assert.Equal(NdisKernelFilterStatus.Success, status);
        Assert.Equal(0, nativeError);
        Assert.Equal(1, calls.ResetCalls);
        Assert.Null(calls.InstalledTable);
        Assert.Equal(NdisKernelFilterStatus.TableMismatch, driver.ReadRulePacketCounts([s_request], new long[1], out _));
        Assert.Equal(NdisKernelFilterStatus.Success, driver.Replace([s_request], out _));
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void AMissingExportIsUnavailableOnEveryOperation()
    {
        var calls = new EchoStaticFilterCalls { MissingExport = true };
        using var driver = NdisApiDriver.CreateForTests(new NoReadCalls(), calls);

        Assert.Equal(NdisKernelFilterStatus.Unavailable, driver.Replace([s_request], out var replaceError));
        Assert.Equal(0, replaceError);
        Assert.Equal(NdisKernelFilterStatus.Unavailable, driver.Reset(out var resetError));
        Assert.Equal(0, resetError);
        // A read-back requires a table this driver installed; with no reachable export there is none,
        // so the read is a table-ownership mismatch rather than a second availability probe.
        Assert.Equal(NdisKernelFilterStatus.TableMismatch, driver.ReadRulePacketCounts([s_request], new long[1], out _));
    }


    [Fact]
    [SupportedOSPlatform("windows")]
    public void ANativeFalseIsFailedWithTheNativeError()
    {
        var calls = new EchoStaticFilterCalls { SetSucceeds = false, SetError = 87, ResetSucceeds = false, ResetError = 5 };
        using var driver = NdisApiDriver.CreateForTests(new NoReadCalls(), calls);
        Assert.Equal(NdisKernelFilterStatus.Failed, driver.Replace([s_request], out var setError));
        Assert.Equal(87, setError);
        Assert.Equal(NdisKernelFilterStatus.Failed, driver.Reset(out var resetError));
        Assert.Equal(5, resetError);

        var sizeCalls = new EchoStaticFilterCalls();
        using var sizeDriver = NdisApiDriver.CreateForTests(new NoReadCalls(), sizeCalls);
        Assert.Equal(NdisKernelFilterStatus.Success, sizeDriver.Replace([s_request], out _));
        Assert.Equal(NdisKernelFilterStatus.Success, sizeDriver.ReadRulePacketCounts([s_request], new long[1], out _));

        var failingRead = new EchoStaticFilterCalls { ReadSucceeds = false, ReadError = 31 };
        using var readDriver = NdisApiDriver.CreateForTests(new NoReadCalls(), failingRead);
        Assert.Equal(NdisKernelFilterStatus.Success, readDriver.Replace([s_request], out _));
        Assert.Equal(NdisKernelFilterStatus.Failed, readDriver.ReadRulePacketCounts([s_request], new long[1], out var readError));
        Assert.Equal(31, readError);

        var failingSize = new EchoStaticFilterCalls { SizeSucceeds = false, SizeError = 1 };
        using var sizeFailingDriver = NdisApiDriver.CreateForTests(new NoReadCalls(), failingSize);
        Assert.Equal(NdisKernelFilterStatus.Success, sizeFailingDriver.Replace([s_request], out _));
        Assert.Equal(NdisKernelFilterStatus.Failed, sizeFailingDriver.ReadRulePacketCounts([s_request], new long[1], out var sizeError));
        Assert.Equal(1, sizeError);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void ADriverFabricatedWithoutAFilterSeamReportsUnavailable()
    {
        using var driver = NdisApiDriver.CreateForTests(new NoReadCalls());

        Assert.Equal(NdisKernelFilterStatus.Unavailable, driver.Replace([s_request], out _));
        Assert.Equal(NdisKernelFilterStatus.Unavailable, driver.Reset(out _));
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void TrafficCountersExposeTheReadPathAndStartTheReinjectionHalfAtZero()
    {
        var reads = new ScriptedReadCalls(4, 3);
        using var batch = new PacketBatch(4);
        using var driver = NdisApiDriver.CreateForTests(reads);

        Assert.Equal(4, driver.TryReadPackets(0x2B, batch.Buffers));
        Assert.Equal(3, driver.TryReadPackets(0x2B, batch.Buffers));

        var counters = driver.GetTrafficCounters();
        Assert.Equal(2, counters.ReadCalls);
        Assert.Equal(7, counters.PacketsRead);
        // The reinjection half needs ndisapi.dll; it is exercised on Windows (the cache summary
        // reads it through the same snapshot).
        Assert.Equal(0, counters.SendCalls);
        Assert.Equal(0, counters.PacketsReinjected);
    }


    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task ConcurrentReplaceCallsStaySerializedOnTheControlGate()
    {
        var calls = new EchoStaticFilterCalls { NativeCallSpinMilliseconds = 2 };
        var driver = NdisApiDriver.CreateForTests(new NoReadCalls(), calls);
        var release = new ManualResetEventSlim();
        try
        {
            var outcomes = await ReplaceTogetherAsync(driver, release).ConfigureAwait(false);
            Assert.All(outcomes, outcome => Assert.Equal(NdisKernelFilterStatus.Success, outcome));
            Assert.Equal(8, calls.SetCalls);
            Assert.Equal(1, driver.ControlGateMaxConcurrentCalls);
        }
        finally
        {
            release.Dispose();
            driver.Dispose();
        }

    }

    [SupportedOSPlatform("windows")]
    private static async Task<NdisKernelFilterStatus[]> ReplaceTogetherAsync(NdisApiDriver driver, ManualResetEventSlim release)
    {
        var tasks = new Task<NdisKernelFilterStatus>[8];
        for (var index = 0; index < tasks.Length; index++)
        {
            tasks[index] = Task.Run(() =>
            {
                release.Wait();
                return driver.Replace([s_request], out _);
            });
        }

        release.Set();
        return await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    /// <summary>
    /// An echo driver: it records the table a replace handed it, reports that table's record count,
    /// and copies the table (or a test-supplied variant, with counters or a corrupted field) back on
    /// read. Every failure path is scripted.
    /// </summary>
    private sealed class EchoStaticFilterCalls : INdisStaticFilterCalls
    {
        public int SetCalls { get; private set; }

        public int ResetCalls { get; private set; }

        public int ReadCalls { get; private set; }

        public byte[]? InstalledTable { get; private set; }

        public byte[]? ReadBackTable { get; set; }

        public uint? SizeOverride { get; set; }

        public bool MissingExport { get; init; }

        public bool SetSucceeds { get; init; } = true;

        public int SetError { get; init; }

        public bool ResetSucceeds { get; init; } = true;

        public int ResetError { get; init; }

        public bool SizeSucceeds { get; init; } = true;

        public int SizeError { get; init; }

        public bool ReadSucceeds { get; init; } = true;

        public int ReadError { get; init; }

        public int NativeCallSpinMilliseconds { get; init; }

        public uint LastRequestedTableSize { get; private set; }

        public bool SetPacketFilterTable(ReadOnlySpan<byte> table, out int nativeError)
        {
            ThrowIfMissingExport();
            SpinNativeCall();
            SetCalls++;
            if (!SetSucceeds)
            {
                nativeError = SetError;
                return false;
            }

            InstalledTable = table.ToArray();
            nativeError = 0;
            return true;
        }

        public bool ResetPacketFilterTable(out int nativeError)
        {
            ThrowIfMissingExport();
            ResetCalls++;
            if (!ResetSucceeds)
            {
                nativeError = ResetError;
                return false;
            }

            InstalledTable = null;
            nativeError = 0;
            return true;
        }

        public bool GetPacketFilterTableSize(out uint tableSize, out int nativeError)
        {
            ThrowIfMissingExport();
            if (!SizeSucceeds)
            {
                tableSize = 0;
                nativeError = SizeError;
                return false;
            }

            tableSize = SizeOverride ?? (InstalledTable is { } table ? BitConverter.ToUInt32(table, 0) : 0u);
            nativeError = 0;
            return true;
        }

        public bool GetPacketFilterTable(Span<byte> table, out int nativeError)
        {
            ThrowIfMissingExport();
            ReadCalls++;
            LastRequestedTableSize = BitConverter.ToUInt32(table);
            if (!ReadSucceeds)
            {
                nativeError = ReadError;
                return false;
            }

            (ReadBackTable ?? InstalledTable)!.CopyTo(table);
            nativeError = 0;
            return true;
        }

        private void ThrowIfMissingExport()
        {
            if (MissingExport) throw new EntryPointNotFoundException("SetPacketFilterTable");
        }

        private void SpinNativeCall()
        {
            if (NativeCallSpinMilliseconds > 0) _ = SpinWait.SpinUntil(static () => false, NativeCallSpinMilliseconds);
        }
    }

    /// <summary>A read seam that reports an always-empty queue; the filter-table facts never read.</summary>
    private sealed class NoReadCalls : INdisReadPacketCalls
    {
        public bool GetAdapterPacketQueueSize(nint adapterHandle, out uint queuedPacketCount, out int nativeError)
        {
            queuedPacketCount = 0;
            nativeError = 0;
            return true;
        }

        public bool ReadPackets(nint adapterHandle, NdisPacketBuffer[] buffers, int count, out uint packetsSuccess, out int nativeError)
        {
            packetsSuccess = 0;
            nativeError = 0;
            return true;
        }
    }

    /// <summary>A read seam that succeeds with a scripted packet count per drain.</summary>
    private sealed class ScriptedReadCalls(params uint[] packetsPerDrain) : INdisReadPacketCalls
    {
        private int _drain;

        public bool GetAdapterPacketQueueSize(nint adapterHandle, out uint queuedPacketCount, out int nativeError)
        {
            queuedPacketCount = 0;
            nativeError = 0;
            return true;
        }

        public bool ReadPackets(nint adapterHandle, NdisPacketBuffer[] buffers, int count, out uint packetsSuccess, out int nativeError)
        {
            packetsSuccess = Math.Min(packetsPerDrain[_drain++ % packetsPerDrain.Length], (uint)count);
            nativeError = 0;
            return true;
        }
    }

    private sealed class PacketBatch(int count) : IDisposable
    {
        public NdisPacketBuffer[] Buffers { get; } = Create(count);

        public void Dispose()
        {
            foreach (var buffer in Buffers) buffer.Dispose();
        }

        private static NdisPacketBuffer[] Create(int count)
        {
            var buffers = new NdisPacketBuffer[count];
            for (var index = 0; index < count; index++) buffers[index] = new NdisPacketBuffer();
            return buffers;
        }
    }
}
