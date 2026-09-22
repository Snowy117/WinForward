using System.Diagnostics;
using System.Globalization;
using System.Net;

namespace WinForward.Benchmarks.Stability;

/// <summary>
/// Parent-side owner of the out-of-process loopback SOCKS5 UDP server (the CLI's
/// <c>--stability --serve-socks5-udp</c> mode): spawns this benchmark executable in server mode,
/// reads its endpoint handshake, and guarantees the child dies with this instance. A real-dial
/// instrument then reads only the client's allocations — the harness server's per-connection
/// buffers stay in the child — and the topology matches production, where the proxy is a separate
/// process.
/// </summary>
internal sealed class ExternalLoopbackSocks5UdpServer : IAsyncDisposable
{
    /// <summary>The real-dial instruments' opt-in: run with <c>WINFORWARD_BENCH_EXTERNAL_SERVER=1</c>.</summary>
    private const string EnvironmentVariable = "WINFORWARD_BENCH_EXTERNAL_SERVER";

    private static readonly TimeSpan s_handshakeTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan s_exitTimeout = TimeSpan.FromSeconds(5);

    private readonly Process _process;
    private readonly TaskCompletionSource _exited;
    private readonly Task<string> _standardError;
    private int _stopStarted;
    private int _disposed;

    private ExternalLoopbackSocks5UdpServer(Process process, LoopbackServerHandshake handshake, TaskCompletionSource exited, Task<string> standardError)
    {
        _process = process;
        _exited = exited;
        _standardError = standardError;
        ControlEndpoint = new IPEndPoint(IPAddress.Loopback, handshake.ControlPort);
    }

    public IPEndPoint ControlEndpoint { get; }

    public static bool IsEnabled => string.Equals(Environment.GetEnvironmentVariable(EnvironmentVariable), "1", StringComparison.Ordinal);

    public static async Task<ExternalLoopbackSocks5UdpServer> StartAsync(int flows, TimeSpan associateDelay, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(flows);
        var process = Process.Start(CreateStartInfo(flows, associateDelay))
            ?? throw new InvalidOperationException("The benchmark host could not start the out-of-process loopback SOCKS5 UDP server.");
        process.EnableRaisingEvents = true;
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        process.Exited += (_, _) => exited.TrySetResult();
        // The child may have exited before the subscription above; without this the handshake wait
        // could only end through its timeout.
        if (process.HasExited) exited.TrySetResult();
        // Never cancelled: the diagnostics must survive the caller's cancellation so a failed
        // handshake can still report them.
        var standardError = process.StandardError.ReadToEndAsync(CancellationToken.None);
        var handshakeLine = process.StandardOutput.ReadLineAsync(cancellationToken).AsTask();
        try
        {
            var line = await AwaitHandshakeLineAsync(process, handshakeLine, exited.Task, cancellationToken).ConfigureAwait(false);
            var handshake = LoopbackServerHandshake.TryParse(line)
                ?? throw new InvalidOperationException($"The out-of-process loopback SOCKS5 UDP server printed an unexpected endpoint handshake line '{line}'.");

            return new ExternalLoopbackSocks5UdpServer(process, handshake, exited, standardError);
        }
        catch (Exception failure)
        {
            throw await AbandonAsync(process, handshakeLine, standardError, failure).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Fails the instrument as soon as the child is gone: the parent-side readiness waits poll
    /// counters that only the child feeds, so a dead child would otherwise stall them to their
    /// timeouts and report the stalled window as a measurement.
    /// </summary>
    public void ThrowIfExited()
    {
        if (!_exited.Task.IsCompleted) return;
        throw new InvalidOperationException(string.Create(
            CultureInfo.InvariantCulture,
            $"The out-of-process loopback SOCKS5 UDP server exited with code {_process.ExitCode} while the instrument was still dialing. {DescribeStandardError()}"));
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await StopChildAsync().ConfigureAwait(false);
        _ = await DrainAsync(_standardError).ConfigureAwait(false);
        _process.Dispose();
    }

    /// <summary>
    /// The child shutdown protocol: closing stdin reaches the child's EOF path (its normal exit);
    /// a child that does not leave within <see cref="s_exitTimeout"/> is killed with its process
    /// tree, so no server outlives its parent.
    /// </summary>
    private static async Task StopAsync(Process process)
    {
        process.StandardInput.Close();
        try
        {
            await process.WaitForExitAsync().WaitAsync(s_exitTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Ends a child whose startup failed and attaches its stderr, which is complete once the child
    /// is gone: the caller has no instance to dispose of, so a half-started server must not
    /// survive, and stderr is the child's only diagnostic channel.
    /// </summary>
    private static async Task<Exception> AbandonAsync(Process process, Task<string?> handshakeLine, Task<string> standardError, Exception failure)
    {
        await StopAsync(process).ConfigureAwait(false);
        try
        {
            _ = await handshakeLine.ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            // The line read was abandoned with the child; the startup failure is the fault to report.
        }

        var diagnostics = await DrainAsync(standardError).ConfigureAwait(false);
        process.Dispose();
        return string.IsNullOrWhiteSpace(diagnostics)
            ? failure
            : new InvalidOperationException($"{failure.Message} Child stderr:{Environment.NewLine}{diagnostics}", failure);
    }

    /// <summary>
    /// Reads the single handshake line, failing as soon as the child exits or the timeout expires
    /// instead of waiting on a pipe that will not produce one.
    /// </summary>
    private static async Task<string?> AwaitHandshakeLineAsync(Process process, Task<string?> handshakeLine, Task exited, CancellationToken cancellationToken)
    {
        var completed = await Task.WhenAny(handshakeLine, exited, Task.Delay(s_handshakeTimeout, cancellationToken)).ConfigureAwait(false);
        if (completed == handshakeLine) return await handshakeLine.ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        throw completed == exited
            ? new InvalidOperationException(string.Create(CultureInfo.InvariantCulture, $"The out-of-process loopback SOCKS5 UDP server exited with code {process.ExitCode} before reporting its endpoints."))
            : new InvalidOperationException(string.Create(CultureInfo.InvariantCulture, $"The out-of-process loopback SOCKS5 UDP server did not report its endpoints within {s_handshakeTimeout.TotalSeconds:0} s."));
    }

    /// <summary>
    /// The child's command line: this executable re-enters the benchmark CLI in server mode. BDN
    /// hosts each benchmark case as <c>dotnet &lt;assembly&gt;.dll</c>, so there the muxer is
    /// <see cref="Environment.ProcessPath"/> and takes the assembly back as its first argument;
    /// an apphost host (<c>dotnet run</c>, a published build) re-enters the CLI directly.
    /// </summary>
    private static ProcessStartInfo CreateStartInfo(int flows, TimeSpan associateDelay)
    {
        var executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("The benchmark executable path is unavailable, so the out-of-process loopback SOCKS5 UDP server cannot be started.");
        var startInfo = new ProcessStartInfo(executable)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        if (string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            startInfo.ArgumentList.Add(typeof(ExternalLoopbackSocks5UdpServer).Assembly.Location);
        }

        startInfo.ArgumentList.Add("--stability");
        startInfo.ArgumentList.Add("--serve-socks5-udp");
        startInfo.ArgumentList.Add("--flows");
        startInfo.ArgumentList.Add(flows.ToString(CultureInfo.InvariantCulture));
        if (associateDelay > TimeSpan.Zero)
        {
            startInfo.ArgumentList.Add("--dial-delay-ms");
            startInfo.ArgumentList.Add(((int)associateDelay.TotalMilliseconds).ToString(CultureInfo.InvariantCulture));
        }

        return startInfo;
    }

    private Task StopChildAsync()
    {
        // ReSharper disable once ConvertIfStatementToReturnStatement // The latch guard is the readable early exit: a ternary would fold the "already stopping" case into the return expression and hide the double-dispose path.
        if (Interlocked.Exchange(ref _stopStarted, 1) != 0) return Task.CompletedTask;
        return StopAsync(_process);
    }

    /// <summary>
    /// Returns the child's stderr — callers reach this only once the child is gone or its pipe was
    /// closed by disposal, so the drain is imminent — with a guard for a stream closed under it.
    /// </summary>
    private static async Task<string> DrainAsync(Task<string> standardError)
    {
        try
        {
            return await standardError.ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            return string.Empty;
        }
    }

    private string DescribeStandardError()
    {
        if (!_standardError.IsCompletedSuccessfully) return "Its stderr is still draining.";
#pragma warning disable VSTHRD002 // Guarded by IsCompletedSuccessfully: reading a completed task's result cannot block.
        var text = _standardError.Result;
#pragma warning restore VSTHRD002
        return string.IsNullOrWhiteSpace(text) ? "It wrote nothing to stderr." : $"Its stderr:{Environment.NewLine}{text}";
    }
}
