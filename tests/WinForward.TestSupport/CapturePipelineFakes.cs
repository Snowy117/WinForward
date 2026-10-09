using System.Collections.Concurrent;
using WinForward.Configuration;
using WinForward.Core;
using WinForward.NdisApi;
using WinForward.Runtime;
using WinForward.Runtime.TcpRedirect;
using WinForward.Windows;

namespace WinForward.TestSupport;

/// <summary>
/// A self-traffic guard that reports nothing owned by default; flip <see cref="Owned"/> to script
/// the dispatcher's self-traffic early-pass path.
/// </summary>
internal sealed class FakeGuard : ISelfTrafficGuard
{
    public bool Owned { get; init; }

    public bool IsOwned(FlowContext context) => Owned;

    public bool IsWildcardOwned(FlowContext context) => Owned;
}

internal sealed class FakeAttributor(string? name) : IProcessAttributor
{
    public int Calls { get; private set; }

    public ValueTask<ProcessIdentity?> FindAsync(FlowKey key, CancellationToken cancellationToken)
    {
        Calls++;
        return ValueTask.FromResult<ProcessIdentity?>(name is null ? null : new ProcessIdentity(name, FullPath: null));
    }
}

internal sealed class FakeExecutor : IPacketActionExecutor
{
    public int PassCount { get; private set; }
    public int BlockCount { get; private set; }
    public int ProxyCount { get; private set; }
    public ValueTask PassAsync(CapturedFlowPacket packet) { PassCount++; return ValueTask.CompletedTask; }
    public ValueTask BlockAsync(CapturedFlowPacket packet) { BlockCount++; return ValueTask.CompletedTask; }
    public ValueTask ProxyAsync(CapturedFlowPacket packet, ProxyTarget target, CancellationToken cancellationToken) { ProxyCount++; return ValueTask.CompletedTask; }
}

internal sealed class ThrowingPassExecutor : IPacketActionExecutor
{
    public ValueTask PassAsync(CapturedFlowPacket packet) => throw new InvalidOperationException("injection failed");
    public ValueTask BlockAsync(CapturedFlowPacket packet) => ValueTask.CompletedTask;
    public ValueTask ProxyAsync(CapturedFlowPacket packet, ProxyTarget target, CancellationToken cancellationToken) => ValueTask.CompletedTask;
}

internal sealed class ThrowingRedirectListenerFactory : ITcpRedirectListenerFactory
{
    public ValueTask<ITcpRedirectListener> CreateAsync(AddressFamilyKind addressFamily, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("listener allocation is not expected on this path");
}

internal sealed class ThrowingRelayFactory : ITcpProxyRelayFactory
{
    public ValueTask<ITcpRelay> EstablishAsync(Endpoint originalDestination, ITcpAcceptedConnection acceptedConnection, Socks5Server server, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("relay setup is not expected on this path");
}

internal sealed class ThrowingRedirectInjector : ITcpRedirectInjector
{
    public ValueTask InjectAsync(ReadOnlyMemory<byte> rewrittenFrame, bool towardMstcp, nint adapterHandle, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("injection is not expected on this path");

    public void Inject(NdisPacketBuffer stagedFrame, bool towardMstcp, nint adapterHandle, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("injection is not expected on this path");

    public void InjectBatch(NdisPacketBuffer[] frames, int count, bool towardMstcp, nint adapterHandle) =>
        throw new InvalidOperationException("injection is not expected on this path");
}

/// <summary>
/// A process attributor that records the managed thread every call ran on and can park until the
/// test releases it. The thread record is the instrument behind "no attribution runs on the pump
/// thread": no product counter can observe it, because the pipeline never knows which thread the
/// pump owns.
/// </summary>
internal sealed class GatedAttributor : IProcessAttributor
{
    private readonly ConcurrentQueue<int> _threadIds = new();
    private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>The identity a hit returns.</summary>
    private const string OwnerName = "dns.exe";

    /// <summary>When true the call parks until <see cref="Release"/> is called.</summary>
    public bool Parks { get; init; }

    /// <summary>When set, the call throws it after the park.</summary>
    public Exception? Failure { get; init; }

    public int Calls => _threadIds.Count;

    public async ValueTask<ProcessIdentity?> FindAsync(FlowKey key, CancellationToken cancellationToken)
    {
        _threadIds.Enqueue(Environment.CurrentManagedThreadId);
        if (Parks) await _gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return Failure is null ? new ProcessIdentity(OwnerName, FullPath: null) : throw Failure;
    }

    public int CallsOnThread(int threadId) => _threadIds.Count(id => id == threadId);

    public void Release() => _gate.TrySetResult();
}

/// <summary>
/// A scripted owner-table provider: one table per kind, replaced by
/// <see cref="Script(OwnerTableKind, UdpOwnerRow[], TcpOwnerRow[])"/>, with a per-kind read count.
/// </summary>
internal sealed class ScriptedOwnerTableReader : IProcessOwnerTableReader
{
    private readonly Lock _gate = new();
    private readonly Dictionary<OwnerTableKind, OwnerTable> _tables = [];
    private readonly Dictionary<OwnerTableKind, int> _reads = [];

    /// <summary>An in-read delay that models a real system-wide enumeration's cost.</summary>
    public TimeSpan ReadDelay { get; init; }

    public long ReadCount
    {
        get { lock (_gate) return _reads.Values.Sum(); }
    }

    public void Script(OwnerTableKind kind, UdpOwnerRow[] udpRows, TcpOwnerRow[] tcpRows)
    {
        lock (_gate) _tables[kind] = new OwnerTable(kind, udpRows, tcpRows);
    }

    public OwnerTable Read(OwnerTableKind kind)
    {
        OwnerTable table;
        lock (_gate)
        {
            _reads[kind] = _reads.GetValueOrDefault(kind) + 1;
            table = _tables.GetValueOrDefault(kind) ?? new OwnerTable(kind, [], []);
        }

        if (ReadDelay > TimeSpan.Zero) Thread.Sleep(ReadDelay);
        return table;
    }
}

/// <summary>A mutable clock for the snapshot-window facts.</summary>
internal sealed class TestClock(DateTimeOffset now)
{
    public DateTimeOffset Now { get; private set; } = now;

    public void Advance(TimeSpan delta) => Now += delta;

    public DateTimeOffset Read() => Now;
}

/// <summary>
/// An executor that records every packet it saw, in order, with the flow generation and the frame's
/// first byte, so ordering and exactly-once can be asserted without inspecting product internals.
/// </summary>
internal sealed class RecordingExecutor : IPacketActionExecutor
{
    private readonly Lock _gate = new();
    private readonly List<(string Action, long Generation, byte Marker)> _events = [];

    /// <summary>Runs before each recorded action; a test can dispatch another packet from here.</summary>
    public Action<CapturedFlowPacket>? OnExecute { get; set; }

    /// <summary>When set, a pass whose packet matches throws after being recorded.</summary>
    public Func<CapturedFlowPacket, bool>? ThrowOn { get; set; }

    public IReadOnlyList<(string Action, long Generation, byte Marker)> Events
    {
        get { lock (_gate) return [.. _events]; }
    }

    public int PassCount => CountOf("pass");

    public int BlockCount => CountOf("block");

    public ValueTask PassAsync(CapturedFlowPacket packet) => Record("pass", packet);

    public ValueTask BlockAsync(CapturedFlowPacket packet) => Record("block", packet);

    public ValueTask ProxyAsync(CapturedFlowPacket packet, ProxyTarget target, CancellationToken cancellationToken) => Record("proxy", packet);

    private ValueTask Record(string action, CapturedFlowPacket packet)
    {
        var bytes = packet.Lease.Frame.Span;
        lock (_gate)
        {
            _events.Add((action, packet.FlowGeneration, bytes.Length == 0 ? (byte)0 : bytes[0]));
        }

        OnExecute?.Invoke(packet);
        if (string.Equals(action, "pass", StringComparison.Ordinal) && ThrowOn?.Invoke(packet) == true) throw new InvalidOperationException("injection failed");
        return ValueTask.CompletedTask;
    }

    private int CountOf(string action)
    {
        lock (_gate) return _events.Count(entry => string.Equals(entry.Action, action, StringComparison.Ordinal));
    }
}
