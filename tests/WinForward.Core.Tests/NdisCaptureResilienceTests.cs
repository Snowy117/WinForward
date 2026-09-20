using System.ComponentModel;
using System.Runtime.Versioning;
using WinForward.Configuration;
using WinForward.NdisApi;
using WinForward.Runtime;
using WinForward.Runtime.Capture;
using WinForward.Windows;
using Xunit;

namespace WinForward.Core.Tests;

/// <summary>
/// R7 transient read-error resilience: the capture pump retries classified transient failures
/// with bounded backoff, degrades on exhaustion or permanent failure (no rethrow, batch buffers
/// still released, degradation callback fired exactly once), and the multi-adapter loop keeps
/// sibling pumps running while forwarding the degradation to the wiring callback.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class NdisCaptureResilienceTests
{
    private const int ErrorNotReady = 21;

    private static Win32Exception Transient(int error = ErrorNotReady) => new(error);

    private static Win32Exception Permanent(int error = 87) => new(error);

    [Fact]
    public async Task TransientReadFailureIsRetriedAndRunContinues()
    {
        var observed = new List<byte>();
        using var cts = new CancellationTokenSource();
        var reader = new ScriptedReader(
            reads:
            [
                _ => throw Transient(),
                slots =>
                {
                    Fill(slots[0], 0x10);
                    return 1;
                },
                _ =>
                {
                    // ReSharper disable once AccessToDisposedClosure // This scripted-reader cancel step ends the run on its third read; the pump run is awaited before the using scope disposes cts.
                    cts.Cancel();
                    return 0;
                },
            ]);

        await using var pump = new NdisCapturePump(reader, 0x55, CaptureHandler(observed), new NdisCapturePumpOptions { PollDelay = TimeSpan.FromMilliseconds(1), TransientRetryBaseDelay = TimeSpan.FromMilliseconds(1) });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pump.RunAsync(cts.Token).AsTask());

        Assert.Equal(new byte[] { 0x10 }, observed);
        Assert.Equal(1, pump.Diagnostics.TransientReadRetryCount);
        Assert.Equal(1, pump.Diagnostics.TransientReadIncidentCount);
        Assert.False(pump.Diagnostics.IsDegraded);
    }

    [Fact]
    public async Task HealthyReadSeparatesTransientIncidents()
    {
        using var cts = new CancellationTokenSource();
        var reader = new ScriptedReader(
            reads:
            [
                _ => throw Transient(),
                _ => 0,
                _ => throw Transient(),
                _ =>
                {
                    // ReSharper disable once AccessToDisposedClosure // This scripted-reader cancel step ends the run on its fourth read; the pump run is awaited before the using scope disposes cts.
                    cts.Cancel();
                    return 0;
                },
            ]);

        await using var pump = new NdisCapturePump(reader, 0x55, static (_, _) => ValueTask.CompletedTask, new NdisCapturePumpOptions { PollDelay = TimeSpan.FromMilliseconds(1), TransientRetryBaseDelay = TimeSpan.FromMilliseconds(1) });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pump.RunAsync(cts.Token).AsTask());

        Assert.Equal(2, pump.Diagnostics.TransientReadRetryCount);
        Assert.Equal(2, pump.Diagnostics.TransientReadIncidentCount);
    }

    [Fact]
    public async Task ExhaustedTransientRetriesDegradeWithoutThrowing()
    {
        var observed = new List<byte>();
        var degradedErrors = new List<int>();
        using var cts = new CancellationTokenSource();
        var reader = new ScriptedReader([], throwAlways: Transient());

        var pump = new NdisCapturePump(reader, 0x55, CaptureHandler(observed), new NdisCapturePumpOptions { PollDelay = TimeSpan.FromMilliseconds(1), OnDegraded = degradedErrors.Add, TransientRetryBaseDelay = TimeSpan.FromMilliseconds(1) });

        await pump.RunAsync(cts.Token);
        await pump.DisposeAsync();

        // Five retries then the degraded exit: the callback fires exactly once with the native
        // error, the run returns normally, and the pump observes its own degraded state.
        Assert.Equal(5, pump.Diagnostics.TransientReadRetryCount);
        Assert.True(pump.Diagnostics.IsDegraded);
        Assert.Equal(ErrorNotReady, pump.Diagnostics.LastDegradedNativeErrorCode);
        Assert.Equal([ErrorNotReady], degradedErrors);
        Assert.Empty(observed);
    }

    [Fact]
    public async Task PermanentReadFailureDegradesImmediately()
    {
        var degradedErrors = new List<int>();
        using var cts = new CancellationTokenSource();
        var reader = new ScriptedReader([], throwAlways: Permanent());

        var pump = new NdisCapturePump(reader, 0x55, static (_, _) => ValueTask.CompletedTask, new NdisCapturePumpOptions { PollDelay = TimeSpan.FromMilliseconds(1), OnDegraded = degradedErrors.Add });

        await pump.RunAsync(cts.Token);

        Assert.True(pump.Diagnostics.IsDegraded);
        Assert.Equal(0, pump.Diagnostics.TransientReadRetryCount);
        Assert.Equal([87], degradedErrors);
    }

    [Fact]
    public async Task RetryCallbackObservesAttempts()
    {
        var attempts = new List<(int NativeError, int Attempt)>();
        using var cts = new CancellationTokenSource();
        var reader = new ScriptedReader(
            reads:
            [
                _ => throw Transient(170),
                _ =>
                {
                    // ReSharper disable once AccessToDisposedClosure // This scripted-reader cancel step ends the run on its second read; the pump run is awaited before the using scope disposes cts.
                    cts.Cancel();
                    return 0;
                },
            ]);

        await using var pump = new NdisCapturePump(reader, 0x55, static (_, _) => ValueTask.CompletedTask, new NdisCapturePumpOptions { PollDelay = TimeSpan.FromMilliseconds(1), OnTransientRetry = (error, attempt) => attempts.Add((error, attempt)), TransientRetryBaseDelay = TimeSpan.FromMilliseconds(1) });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pump.RunAsync(cts.Token).AsTask());

        Assert.Equal([(170, 1)], attempts);
    }

    [Fact]
    public async Task CancellationDuringBackoffUnwinds()
    {
        using var cts = new CancellationTokenSource();
        var reader = new ScriptedReader([], throwAlways: Transient());

        var pump = new NdisCapturePump(reader, 0x55, static (_, _) => ValueTask.CompletedTask, new NdisCapturePumpOptions { PollDelay = TimeSpan.FromMilliseconds(1), TransientRetryBaseDelay = TimeSpan.FromSeconds(30) });

        var run = pump.RunAsync(cts.Token).AsTask();
        await Task.Delay(50, CancellationToken.None);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.False(pump.Diagnostics.IsDegraded);
        await pump.DisposeAsync();
    }

    private static Func<NdisCapturedPacket, CancellationToken, ValueTask> CaptureHandler(List<byte> observed) =>
        (packet, _) =>
        {
            observed.Add(packet.Buffer.GetFrame()[0]);
            return ValueTask.CompletedTask;
        };

    private static void Fill(NdisPacketBuffer buffer, byte marker) =>
        buffer.SetFrame([marker, 0xAA, 0xBB], NdisApiAbi.PacketFlagOnReceive, 0x55, flags: 0x40);
}

/// <summary>
/// R7 degradation plumbing at the loop and runtime layers: a degraded pump does not cancel its
/// siblings or rethrow, the loop forwards the event to the wiring callback once per adapter, and
/// <see cref="TransactionalCaptureRuntime.MarkAdapterDegradedAsync"/> restores exactly the
/// degraded adapter's mode snapshot once.
/// </summary>
public sealed class CaptureDegradationPlumbingTests
{
    private static WindowsAdapter Adapter(string id, nint handle) => new(id, id, $@"\DEVICE\{{{id}}}", handle, 1);

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task DegradedPumpKeepsSiblingsRunningAndForwardsCallback()
    {
        var degradedSignalled = new TaskCompletionSource<(string AdapterId, int NativeError)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var adapters = new[] { Adapter("a", 0x10), Adapter("b", 0x11) };
        using var cts = new CancellationTokenSource();
        var readers = new Dictionary<nint, INdisPacketReader>
        {
            [0x10] = new PermanentFailureReader(87),
            [0x11] = new CancellingReader(cts, readsBeforeCancel: 3),
        };
        var dispatcher = new FlowDispatcher(CreatePassConfiguration(), new FakeGuard(), new NoopExecutor());
        var processor = new CapturePacketProcessor(dispatcher);
        var loop = new MultiAdapterCaptureLoop(new PerHandleReader(readers), adapters, processor,
            TimeSpan.FromMilliseconds(1),
            onAdapterDegraded: (adapter, nativeError) =>
            {
                degradedSignalled.TrySetResult((adapter.StableId, nativeError));
                return ValueTask.CompletedTask;
            });

        // The sibling (b) keeps polling after a's degradation and ends the run itself by
        // cancelling the token: the degraded pump neither cancelled it nor rethrew.
        await loop.RunAsync(cts.Token);

        var degraded = await degradedSignalled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(("a", 87), degraded);
        Assert.Equal(1, loop.DegradedAdapterCount);
    }

    private static ValidatedConfiguration CreatePassConfiguration()
    {
        var servers = new Dictionary<string, Socks5Server>(StringComparer.OrdinalIgnoreCase) { ["p"] = new("p", "127.0.0.1", 1080, null, null) };
        return new ValidatedConfiguration(servers, new PolicySnapshot([], FlowAction.Pass));
    }

    private sealed class PerHandleReader(Dictionary<nint, INdisPacketReader> readers) : INdisPacketReader
    {
        public int TryReadPackets(nint adapterHandle, NdisPacketBuffer[] buffers) => readers[adapterHandle].TryReadPackets(adapterHandle, buffers);
    }

    private sealed class PermanentFailureReader(int error) : INdisPacketReader
    {
        public int TryReadPackets(nint adapterHandle, NdisPacketBuffer[] buffers) => throw new Win32Exception(error);
    }

    private sealed class CancellingReader(CancellationTokenSource cts, int readsBeforeCancel) : INdisPacketReader
    {
        private int _reads;

        public int TryReadPackets(nint adapterHandle, NdisPacketBuffer[] buffers)
        {
            if (++_reads >= readsBeforeCancel) cts.Cancel();
            return 0;
        }
    }

    [Fact]
    public async Task MarkAdapterDegradedRestoresOnlyTheDegradedAdapterOnce()
    {
        var modes = new FakeModes([new("a", 7), new("b", 9)]);
        var capture = new BlockingCapture();
        await using var runtime = new TransactionalCaptureRuntime(modes, capture);
        // ReSharper disable once AccessToDisposedClosure // start is awaited while the runtime is alive; the await using scope disposes the runtime only after the test's own Complete/Stop sequence.
        var start = Task.Run(async () => await runtime.StartAsync(CancellationToken.None));
        await capture.Started.Task;

        // The applied set exists only while the run is live (R7 degradations fire from a pump).
        await runtime.MarkAdapterDegradedAsync("a");
        await runtime.MarkAdapterDegradedAsync("a");

        Assert.Equal(["a"], modes.Restored);
        // The second call is a no-op (snapshot already removed), so the shutdown restore that
        // follows cleans up only the remaining adapter.
        capture.Complete();
        await start;
        await runtime.StopAsync();
        Assert.Equal(["a", "b"], modes.Restored);
    }

    [Fact]
    public async Task MarkAdapterDegradedSwallowsRestoreFailure()
    {
        var modes = new FailingRestoreModes();
        var capture = new BlockingCapture();
        await using var runtime = new TransactionalCaptureRuntime(modes, capture);
        // ReSharper disable once AccessToDisposedClosure // As in the previous test: start is awaited while the runtime is alive, and the await using disposal follows the test's own Complete/Stop sequence.
        var start = Task.Run(async () => await runtime.StartAsync(CancellationToken.None));
        await capture.Started.Task;

        await runtime.MarkAdapterDegradedAsync("a");

        Assert.Equal(1, modes.RestoreAttempts);
        capture.Complete();
        await start;
        await runtime.StopAsync();
    }

    [Fact]
    public async Task MarkUnknownAdapterIsANoOp()
    {
        var modes = new FakeModes([new("a", 7)]);
        await using var runtime = new TransactionalCaptureRuntime(modes, new CompletingCapture());

        await runtime.MarkAdapterDegradedAsync("missing");

        Assert.Empty(modes.Restored);
    }

    private sealed class NoopExecutor : IPacketActionExecutor
    {
        public ValueTask PassAsync(CapturedFlowPacket packet) => ValueTask.CompletedTask;
        public ValueTask BlockAsync(CapturedFlowPacket packet) => ValueTask.CompletedTask;
        public ValueTask ProxyAsync(CapturedFlowPacket packet, Socks5Server server, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    private sealed class FailingRestoreModes : IAdapterModeController
    {
        public int RestoreAttempts { get; private set; }

        public ValueTask<IReadOnlyList<AdapterModeSnapshot>> SnapshotAsync() => ValueTask.FromResult<IReadOnlyList<AdapterModeSnapshot>>([new("a", 7)]);
        public ValueTask ApplyCaptureModeAsync(AdapterModeSnapshot adapter) => ValueTask.CompletedTask;
        public ValueTask RestoreAsync(AdapterModeSnapshot adapter) { RestoreAttempts++; throw new InvalidOperationException("restore failed"); }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
