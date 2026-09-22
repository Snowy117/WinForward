using System.Runtime.InteropServices;
using System.Text.Json;

namespace WinForward.Benchmarks.Stability;

/// <summary>
/// The benchmark CLI's child-process server mode
/// (<c>--stability --serve-socks5-udp --flows N [--dial-delay-ms D]</c>): hosts the same
/// <see cref="EchoReceiver"/> + <see cref="LoopbackSocks5UdpServer"/> pair the in-process
/// scenarios build, so a real-dial instrument can dial a server whose per-connection buffers
/// (64 KiB relay loop, 4 MiB relay socket) are charged to another process's allocation counters.
/// Reports the harness endpoints as one stdout line, then serves until the parent goes away.
/// </summary>
internal static class Socks5UdpServerMode
{
    public static async Task<int> RunAsync(SoakOptions options)
    {
        await using var receiver = new EchoReceiver(options.Flows);
        await using var server = new LoopbackSocks5UdpServer(receiver.Endpoint, TimeSpan.FromMilliseconds(options.DialDelayMs));
        await Console.Out.WriteLineAsync(LoopbackServerHandshake.Serialize(server.ControlEndpoint.Port, receiver.Endpoint.Port)).ConfigureAwait(false);
        await WaitForShutdownAsync().ConfigureAwait(false);
        return 0;
    }

    /// <summary>
    /// Serves until the parent is gone. The parent holds the write end of stdin, so EOF is the
    /// normal shutdown path; SIGTERM/SIGINT cover invocations killed without an EOF wait. Both
    /// paths return normally, so the sockets and receive loops are disposed rather than abandoned
    /// to process teardown.
    /// </summary>
    private static async Task WaitForShutdownAsync()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var termination = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context => RequestShutdown(context, completion));
        using var interrupt = PosixSignalRegistration.Create(PosixSignal.SIGINT, context => RequestShutdown(context, completion));
        _ = PumpStandardInputAsync(completion);
        await completion.Task.ConfigureAwait(false);
    }

    /// <summary>Takes the signal over rather than letting the default action tear the process down mid-request.</summary>
    private static void RequestShutdown(PosixSignalContext context, TaskCompletionSource completion)
    {
        context.Cancel = true;
        completion.TrySetResult();
    }

    /// <summary>Watches stdin for EOF: the parent closes the pipe when it disposes the helper, and its death closes it too.</summary>
    private static async Task PumpStandardInputAsync(TaskCompletionSource completion)
    {
        try
        {
            var standardInput = Console.OpenStandardInput();
            var buffer = new byte[64];
            var read = 1;
            while (read > 0)
            {
                read = await standardInput.ReadAsync(buffer, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            // An unwatchable stdin leaves the signal path as the only way out; ending the wait here
            // keeps a broken pipe from stranding the server instead.
        }

        completion.TrySetResult();
    }
}

/// <summary>
/// The child server mode's stdout handshake — one JSON line naming the harness endpoints the
/// parent dials (<c>{"controlPort":P,"echoPort":E}</c>). Nothing else reaches the child's stdout,
/// so the parent reads exactly this line and treats anything else as a protocol failure.
/// </summary>
internal sealed record LoopbackServerHandshake(int ControlPort, int EchoPort)
{
    private static readonly JsonSerializerOptions s_serialization = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static string Serialize(int controlPort, int echoPort) =>
        JsonSerializer.Serialize(new LoopbackServerHandshake(controlPort, echoPort), s_serialization);

    public static LoopbackServerHandshake? TryParse(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        try
        {
            return JsonSerializer.Deserialize<LoopbackServerHandshake>(line, s_serialization);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
