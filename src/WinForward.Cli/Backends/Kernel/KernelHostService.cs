using System.ComponentModel;
using System.Globalization;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using WinForward.Configuration;
using WinForward.Runtime;
using WinForward.Runtime.TcpRedirect;
using WinForward.Userland.Device;
using WinForward.Userland.Forwarded;
using WinForward.Userland.Policy;
using WinForward.Userland.Relay;

namespace WinForward.Cli.Backends.Kernel;

/// <summary>
/// The Kernel host lifecycle: the device publication that turns redirected connections into this
/// process's listener, the single set of bound sockets, and the TCP data path behind it; the host UDP
/// path is wired but stays disabled until the driver can inject the relay reply.
/// </summary>
/// <remarks>
/// <para>
/// Startup clears first, because a previous run may have left the driver redirecting to a listener that
/// is not up yet; it then binds and wires both data paths, publishes and verifies every readback before
/// the driver is enabled, and only then starts accepting. The UDP receive legs start only while
/// <see cref="HostUdpProxyEnabled"/> is true; shutdown reverses the visibility: the host redirect is
/// disabled before any socket is released, so the driver can never aim a connection or a datagram at a
/// port this process has given up.
/// </para>
/// <para>
/// Teardown order is load-bearing for the blocking UDP receive. Once the proxy is enabled the UDP
/// receiver parks in <c>WSARecvMsg</c> on the listener's socket, so the listener set — the single
/// socket owner — is disposed first to close that socket and unblock the receive; only then does the
/// UDP service join its receive thread and dispose the coordinator's sessions and transports, the TCP
/// service drain its relays, and the block sink be released after the driver can no longer steer
/// traffic at it.
/// </para>
/// <para>
/// The orchestrator borrows the device, the relay factory and the UDP collaborators;
/// <see cref="KernelBackend"/> owns and releases them.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class KernelHostService : IAsyncDisposable
{
    /// <summary>
    /// Whether the listener serves host UDP. The listener's own reply cannot preserve the original
    /// destination as its source, so the driver must inject the relay reply before a UDP association
    /// may be redirected here; while this is false the configuration gate refuses every UDP-capable
    /// proxy rule and the UDP service is constructed without its receive leg.
    /// </summary>
    // TODO: make this return true once the driver can inject the relay reply with the original
    // destination as its source; until then the host UDP proxy stays fail-closed. It is a property
    // rather than a const because a constant false would turn the guarded Start call into unreachable
    // code the compiler rejects. The constraint and the scaffolding are described in the Host UDP
    // section of .trellis/spec/backend/kernel/wfp-architecture.md
    private static bool HostUdpProxyEnabled => false;

    /// <summary>The empty forwarded table this milestone publishes to clear a previous run's forwarded state.</summary>
    private static readonly byte[] s_emptyForwardedBlob =
        ForwardedBlobCompiler.Compile(new ForwardedPolicyTable([], RuleAction.Permit));

    private readonly WfpDeviceClient _device;
    private readonly ValidatedConfiguration _configuration;
    private readonly TcpProxyRelayFactory _relayFactory;
    private readonly KernelHostUdpComposition _udpComposition;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;
    private readonly QuiescenceScope _scope = new();
    private HostListenerSet? _listeners;
    private BlockSink? _blockSink;
    private KernelHostUdpService? _udp;
    private KernelHostTcpService? _tcp;
    private int _disposeStarted;
    private int _shutdownFaulted;

    internal KernelHostService(
        WfpDeviceClient device,
        ValidatedConfiguration configuration,
        TcpProxyRelayFactory relayFactory,
        KernelHostUdpComposition udpComposition,
        ILoggerFactory loggerFactory,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(relayFactory);
        ArgumentNullException.ThrowIfNull(udpComposition);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        ArgumentNullException.ThrowIfNull(logger);
        _device = device;
        _configuration = configuration;
        _relayFactory = relayFactory;
        _udpComposition = udpComposition;
        _loggerFactory = loggerFactory;
        _logger = logger;
    }

    /// <summary>Whether the shutdown could not disable the driver's host redirect.</summary>
    internal bool ShutdownFaulted => Volatile.Read(ref _shutdownFaulted) != 0;

    /// <summary>
    /// Publishes the policy, wires the host data paths and starts the ones that are enabled — in the
    /// one order that keeps the driver from ever redirecting to a port that is not listening. Returns
    /// <see langword="false"/> with the compiler's diagnostics when the host policy does not compile.
    /// </summary>
    internal bool TryStart(out IReadOnlyList<ConfigDiagnostic> diagnostics)
    {
        VerifyControlAbi();
        DisableStaleState();

        if (!KernelPolicyCompiler.TryCompile(_configuration, WindowsProcessPathNormalizer.ForWindows(), out var compilation, out diagnostics))
        {
            return false;
        }

        var listeners = HostListenerSet.Bind();
        _listeners = listeners;
        _blockSink = compilation!.ContainsBlock ? BlockSink.Bind(listeners.Port) : null;
        KernelBackendLog.ListenerBound(
            _logger,
            listeners.Port,
            listeners.TcpListeners.Count,
            _blockSink?.Port ?? 0,
            compilation.ContainsBlock);

        try
        {
            StartDataPaths(listeners, compilation);
            PublishAndVerify(listeners.Port, compilation);
            _tcp!.StartAcceptLoops();
        }
        catch
        {
            // A start that cannot finish must leave no half-published listener and no enabled driver
            // behind: the driver is disabled first, then whatever started is released.
            RollbackStart();
            throw;
        }

        KernelBackendLog.Ready(
            _logger,
            ProbeIoctl.AbiVersion,
            listeners.Port,
            _blockSink?.Port ?? 0,
            compilation.RuleCount,
            _configuration.TcpFlowCapacity,
            _udp!.SessionCapacity,
            KernelHostUdpDatagram.MaximumDatagramBytes);
        return true;
    }

    /// <summary>
    /// Starts the data paths while the driver is still disabled; the TCP accept loops must not run
    /// before <see cref="PublishAndVerify"/> has made the redirect possible. The UDP service is
    /// constructed either way so the composition and its dispose path stay wired, but its receive leg
    /// starts only once <see cref="HostUdpProxyEnabled"/> is true.
    /// </summary>
    private void StartDataPaths(HostListenerSet listeners, KernelPolicyCompilation compilation)
    {
        _udp = new KernelHostUdpService(listeners.UdpSocket, compilation.TargetsByRuleIndex, _configuration, _udpComposition, _loggerFactory, _logger);
        if (HostUdpProxyEnabled) _udp.Start();
        _tcp = new KernelHostTcpService(listeners.TcpListeners, compilation.TargetsByRuleIndex, _configuration, _relayFactory, _logger, _scope.Token);
    }

    private void PublishAndVerify(int listenerPort, KernelPolicyCompilation compilation)
    {
        _device.SetRuleBlob(compilation.HostBlob);
        var info = VerifyRuleReadback(compilation);
        KernelBackendLog.RulesInstalled(_logger, info.Generation, compilation.RuleCount, compilation.FallbackAction, compilation.HostBlob.Length, compilation.ContainsBlock);

        var processId = (uint)Environment.ProcessId;
        var blockSinkPort = _blockSink?.Port ?? 0;
        _device.SetConfig(ProbeConfig.EnabledFor(processId, (ushort)listenerPort, [processId], (uint)blockSinkPort));
        VerifyConfigReadback(processId, listenerPort, blockSinkPort);
    }

    private void RollbackStart()
    {
        TryDeviceCall("disable the host redirect", () => _device.SetConfig(ProbeConfig.Disabled()));
        _scope.Cancel();
        // Closing the listener set is what stops the accept loops and unblocks the UDP receiver; the two
        // data paths themselves are released by DisposeAsync, which the caller's await using reaches.
        _blockSink?.Dispose();
        _blockSink = null;
        _listeners?.Dispose();
        _listeners = null;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
        {
            // A second caller joins the same drain instead of running the teardown again.
            await _scope.DrainAsync().ConfigureAwait(false);
            return;
        }

        KernelBackendLog.ShuttingDown(_logger);

        // Visibility first: with the host redirect disabled the driver cannot aim a connection or a
        // datagram at a listener or a block sink this method is about to release. A refused call is
        // recorded and the release continues — a torn-down data path beats a released port the driver
        // still uses.
        TryDeviceCall("disable the host redirect", () => _device.SetConfig(ProbeConfig.Disabled()));

        _scope.Cancel();
        _listeners?.Dispose();
        _listeners = null;

        await DisposeUdpAsync().ConfigureAwait(false);
        if (_tcp is { } tcp) await tcp.DisposeAsync().ConfigureAwait(false);

        // The sink may only be released after the driver is disabled: while it is enabled a block
        // decision is still steered at the sink port.
        var blockSinkReceived = _blockSink?.Received ?? 0;
        _blockSink?.Dispose();
        _blockSink = null;

        TryDeviceCall("clear the forwarded state", ClearForwardedState);
        await _scope.DrainAsync().ConfigureAwait(false);

        var tcpSnapshot = _tcp?.Snapshot() ?? default;
        var udpSnapshot = _udp?.Snapshot() ?? default;
        KernelBackendLog.Stopped(
            _logger,
            tcpSnapshot.Accepted,
            tcpSnapshot.Refused,
            tcpSnapshot.CapacityBlocked,
            tcpSnapshot.Established,
            tcpSnapshot.Stalled,
            tcpSnapshot.Faulted,
            tcpSnapshot.Active,
            udpSnapshot.Accepted,
            udpSnapshot.Dropped,
            udpSnapshot.Sessions,
            blockSinkReceived);
    }

    private async ValueTask DisposeUdpAsync()
    {
        if (_udp is { } udp) await udp.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Enforces the driver's control ABI before any device state is touched. Only the stats carry
    /// the driver's own <c>abi_version</c> — the config readback echoes the caller's — and the
    /// driver's own rejection of a mismatched config arrives as a bare Win32 status.
    /// </summary>
    private void VerifyControlAbi()
    {
        var stats = _device.GetStats();
        if (stats.AbiVersion != ProbeIoctl.AbiVersion)
        {
            throw new InvalidOperationException(
                $"The WinForward.Kernel driver reports control ABI {stats.AbiVersion} but this backend implements ABI {ProbeIoctl.AbiVersion}; upgrade the driver and the backend together.");
        }
    }

    /// <summary>
    /// Clears whatever a previous run may have left behind, before anything else happens: no
    /// connection can be redirected while the host config is disabled, and the forwarded side is
    /// taken down with an empty permit table (this backend serves no forwarded traffic).
    /// </summary>
    private void DisableStaleState()
    {
        _device.SetConfig(ProbeConfig.Disabled());
        ClearForwardedState();
        KernelBackendLog.StaleStateCleared(_logger);
    }

    private void ClearForwardedState()
    {
        _device.SetForwardedConfig(ProbeForwardedConfig.Disabled());
        _device.SetForwardedBlob(s_emptyForwardedBlob);
    }

    /// <summary>
    /// Verifies the driver's readback of the published blob field by field: a driver that rejected or
    /// truncated the table must fail startup instead of redirecting under a policy nobody published.
    /// </summary>
    private ProbeRuleInfo VerifyRuleReadback(KernelPolicyCompilation compilation)
    {
        var info = _device.GetRuleInfo();
        if (info.Installed == 1 &&
            info.RuleCount == (uint)compilation.RuleCount &&
            info.FallbackAction == (uint)compilation.FallbackAction &&
            info.TotalSize == (uint)compilation.HostBlob.Length)
        {
            return info;
        }

        throw new InvalidOperationException(string.Create(
            CultureInfo.InvariantCulture,
            $"The driver reports installed={info.Installed}, {info.RuleCount} rule(s), fallback {info.FallbackAction} and {info.TotalSize} byte(s) after the Kernel host rule blob was published; the backend published {compilation.RuleCount} rule(s), fallback {(uint)compilation.FallbackAction} and {compilation.HostBlob.Length} byte(s)."));
    }

    private void VerifyConfigReadback(uint processId, int listenerPort, int blockSinkPort)
    {
        var config = _device.GetConfig();
        // ReSharper disable once MergeIntoPattern // The readback check compares the driver's seven reported fields one by one; the pattern form can only carry the three constant comparisons (the parameter and inline-array-element comparisons are rejected by the pattern syntax), and a partial merge would split one verification into two shapes.
        if (config.AbiVersion == ProbeIoctl.AbiVersion &&
            config.Enabled == 1 &&
            config.ListenerPort == listenerPort &&
            config.ListenerProcessId == processId &&
            config.SelfProcessCount == 1 &&
            config.SelfProcessIds[0] == processId &&
            config.BlockSinkPort == (uint)blockSinkPort)
        {
            return;
        }

        throw new InvalidOperationException(string.Create(
            CultureInfo.InvariantCulture,
            $"The driver reports enabled={config.Enabled}, listener port {config.ListenerPort} for process {config.ListenerProcessId}, {config.SelfProcessCount} self process(es) and block sink port {config.BlockSinkPort} after the Kernel config was published; the backend published ABI {ProbeIoctl.AbiVersion}, listener port {listenerPort} for process {processId}, one self process and block sink port {blockSinkPort}."));
    }

    private void TryDeviceCall(string operation, Action call)
    {
        try
        {
            call();
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            // The teardown must reach the listener and session release even when the driver refuses a
            // call, so the fault is recorded rather than thrown; the run reports exit code 1 from it.
            Volatile.Write(ref _shutdownFaulted, 1);
            KernelBackendLog.DeviceFailed(_logger, operation, exception.GetType().Name, exception.Message);
        }
    }
}
