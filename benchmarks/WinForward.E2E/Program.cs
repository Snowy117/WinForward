using System.Net.Sockets;
using System.Runtime.InteropServices;
using WinForward.E2E.Cli;
using WinForward.E2E.Client;
using WinForward.E2E.Target;

namespace WinForward.E2E;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 0)
        {
            PrintUsage();
            return ExitCodes.UsageError;
        }

        var role = args[0];
        var rest = args[1..];

        if (IsHelp(role))
        {
            PrintUsage();
            return ExitCodes.Success;
        }

        if (rest.Length == 1 && IsHelp(rest[0]))
        {
            if (string.Equals(role, "target", StringComparison.Ordinal))
            {
                TargetRunner.PrintHelp();
                return ExitCodes.Success;
            }

            if (string.Equals(role, "client", StringComparison.Ordinal))
            {
                ClientRunner.PrintHelp();
                return ExitCodes.Success;
            }
        }

        using var shutdown = new CancellationTokenSource();
        using var registration = RegisterShutdownSignals(shutdown);
        void CancelHandler(object? _, ConsoleCancelEventArgs eventArgs)
        {
            eventArgs.Cancel = true;
            // ReSharper disable once AccessToDisposedClosure // The handler is unregistered in the finally below before the using disposes the source, and Cancel swallows the ObjectDisposedException a dispatch already in flight would see.
            Cancel(shutdown);
        }

        Console.CancelKeyPress += CancelHandler;
        try
        {
            return role switch
            {
                "target" => await RunTargetAsync(rest, shutdown.Token).ConfigureAwait(false),
                "client" => await RunClientAsync(rest, shutdown.Token).ConfigureAwait(false),
                _ => UnknownRole(role),
            };
        }
        finally
        {
            Console.CancelKeyPress -= CancelHandler;
        }
    }

    private static async Task<int> RunTargetAsync(string[] args, CancellationToken cancellationToken)
    {
        if (!TargetRunner.TryCreate(args, out var options, out var error))
        {
            await Console.Error.WriteLineAsync($"e2e target: {error}").ConfigureAwait(false);
            TargetRunner.PrintHelp();
            return ExitCodes.UsageError;
        }

        try
        {
            return await TargetRunner.RunAsync(options, cancellationToken).ConfigureAwait(false);
        }
        catch (SocketException exception)
        {
            await Console.Error.WriteLineAsync($"e2e target: {exception.Message}").ConfigureAwait(false);
            return ExitCodes.RuntimeError;
        }
        catch (OperationCanceledException)
        {
            return ExitCodes.Success;
        }
        catch (Exception exception)
        {
            // Last resort only: a target-side throw that reaches here is still reported as a runtime
            // error with a named exception instead of an unhandled crash.
            await Console.Error.WriteLineAsync($"e2e target: {exception.GetType().Name}: {exception.Message}").ConfigureAwait(false);
            return ExitCodes.RuntimeError;
        }
    }

    private static async Task<int> RunClientAsync(string[] args, CancellationToken cancellationToken)
    {
        if (!ClientRunner.TryCreate(args, out var options, out var error))
        {
            await Console.Error.WriteLineAsync($"e2e client: {error}").ConfigureAwait(false);
            ClientRunner.PrintHelp();
            return ExitCodes.UsageError;
        }

        try
        {
            return await ClientRunner.RunAsync(options, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await Console.Error.WriteLineAsync("e2e client: interrupted before every arm completed.").ConfigureAwait(false);
            return ExitCodes.RuntimeError;
        }
        catch (Exception exception)
        {
            // Last resort only: an arm failure is handled inside the arm loop, where the error record
            // and run.json are still written. What reaches here is a failure of the run itself
            // (the output directory, the plan reader, the sampler), and it must not be a crash.
            await Console.Error.WriteLineAsync($"e2e client: {exception.GetType().Name}: {exception.Message}").ConfigureAwait(false);
            return ExitCodes.RuntimeError;
        }
    }

    private static int UnknownRole(string role)
    {
        Console.Error.WriteLine($"e2e: unknown role '{role}'.");
        PrintUsage();
        return ExitCodes.UsageError;
    }

    private static bool IsHelp(string value) =>
        string.Equals(value, "--help", StringComparison.Ordinal) || string.Equals(value, "-h", StringComparison.Ordinal);

    private static void Cancel(CancellationTokenSource shutdown)
    {
        try
        {
            shutdown.Cancel();
        }
        catch (ObjectDisposedException)
        {
            /* shutdown already ran */
        }
        catch (OperationCanceledException)
        {
            /* a cancelled callback must not take the process down while it is stopping */
        }
        catch (AggregateException)
        {
            /* a cancelled callback must not take the process down while it is stopping */
        }
    }

    private static PosixSignalRegistration? RegisterShutdownSignals(CancellationTokenSource shutdown)
    {
        try
        {
            return PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
            {
                context.Cancel = true;
                Cancel(shutdown);
            });
        }
        catch (PlatformNotSupportedException)
        {
            return null;
        }
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
            WinForward.E2E - cross-platform end-to-end benchmark harness for the WinForward comparison.

            Usage:
              WinForward.E2E target [--bind <ip>] [--tcp-port <n>] [--udp-port <n>] [--dns-port <n>] [--dns-alt-port <n>] [--label <name>] [--ledger <path>]
              WinForward.E2E client --target <ip> --plan <path> --out <dir> [--label <name>]
                                    [--tcp-port <n>] [--udp-port <n>] [--dns-port <n>] [--sampler-process <name>]...
                                    [--inject-corrupt-every <n>] [--inject-rewrite-every <n>]

              WinForward.E2E target --help
              WinForward.E2E client --help
            """);
    }
}
