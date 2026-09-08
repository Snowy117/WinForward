#pragma warning disable CA1416 // The pump and processor are Windows-attributed; the benchmark drives their managed-only pipeline through a fake reader, so it runs on any OS.
using System.Buffers.Binary;
using BenchmarkDotNet.Attributes;
using WinForward.Configuration;
using WinForward.Core;
using WinForward.NdisApi;
using WinForward.Runtime;
using WinForward.Runtime.Capture;
using WinForward.Runtime.Socks5;
using WinForward.Windows;

namespace WinForward.Benchmarks.Perf;

[MemoryDiagnoser]
public class CapturePumpBenchmarks
{
    private const int PacketsPerRound = 200_000;
    private const int DistinctFlows = 1_024;
    private static long s_sink;

    [Params(128, 1400)]
    public int FrameBytes { get; set; }

    [Params(32, 1)]
    public int BatchCapacity { get; set; }

    [Benchmark]
    public async Task EndToEndAsync()
    {
        // Fresh pipeline state per invocation so the measured pass count matches the packet count.
        var passConfiguration = new ValidatedConfiguration(
            new Dictionary<string, Socks5Server>(StringComparer.OrdinalIgnoreCase),
            new PolicySnapshot([], FlowAction.Pass));
        var executor = new CountingExecutor();
        var logger = new ThresholdOnlyLogger(RuntimeLogLevel.Info);
        var dispatcher = new FlowDispatcher(passConfiguration, new NeverOwnedGuard(), executor, logger: logger);
        var processor = new CapturePacketProcessor(dispatcher, logger);
        var adapter = new WindowsAdapter("bench-adapter", "Benchmark Adapter", @"\DEVICE\{00000000-B3NCH-4ARK-0000-000000000000}", (nint)0x55, 1);
        var frame = BenchmarkShared.CreateIpv4TcpFrame(FrameBytes);

        using var completion = new CancellationTokenSource();
        var reader = new FiniteCaptureReader(frame, PacketsPerRound, DistinctFlows, completion, NdisApiAbi.PacketFlagOnSend);
        await using var pump = new NdisCapturePump(reader, adapter.RuntimeHandle, (packet, cancellationToken) => processor.ProcessAsync(packet, adapter, cancellationToken), new NdisCapturePumpOptions { PollDelay = TimeSpan.FromMilliseconds(1), BatchCapacity = BatchCapacity });

        try
        {
            await pump.RunAsync(completion.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (completion.IsCancellationRequested)
        {
            // Exhaustion cancellation is the pump's normal shutdown path (NdisCapturePumpTests uses the same termination).
        }

        Volatile.Write(ref s_sink, executor.PassCount);
        if (executor.PassCount != PacketsPerRound) throw new InvalidOperationException($"The capture pump benchmark processed {executor.PassCount} of {PacketsPerRound} packets.");
    }

    /// <summary>
    /// Supplies a finite stream of synthetic frames to <see cref="NdisCapturePump"/> without native
    /// hardware. Every call fills as many batch slots as requested (up to the remaining supply) so
    /// the pump never pays the empty-queue poll delay; when the supply is exhausted the reader
    /// cancels the completion token and reports an empty queue, which is the pump's normal
    /// termination path. Each frame gets a rotating TCP source port so the pipeline observes a
    /// bounded mix of first-observation flow claims and cached resolutions.
    /// </summary>
    private sealed class FiniteCaptureReader(byte[] frame, long totalPackets, int distinctFlows, CancellationTokenSource completion, uint deviceFlags) : INdisPacketReader
    {
        private long _remaining = totalPackets;
        private int _sequence;

        public int TryReadPackets(nint adapterHandle, NdisPacketBuffer[] buffers)
        {
            var remaining = _remaining;
            if (remaining <= 0)
            {
                completion.Cancel();
                return 0;
            }

            var count = (int)Math.Min(remaining, (long)buffers.Length);
            for (var index = 0; index < count; index++)
            {
                var sourcePort = (ushort)(1_024 + _sequence % distinctFlows);
                BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(34, 2), sourcePort);
                buffers[index].SetFrame(frame, deviceFlags, adapterHandle);
                _sequence++;
            }

            _remaining -= count;
            return count;
        }
    }
}
