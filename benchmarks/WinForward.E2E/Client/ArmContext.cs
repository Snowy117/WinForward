using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using WinForward.E2E.Cli;
using WinForward.E2E.Contracts;
using WinForward.E2E.Contracts.Json;

namespace WinForward.E2E.Client;

internal static class Clock
{
    internal static long Now => Stopwatch.GetTimestamp();

    internal static double ToSeconds(long ticks) => ticks / (double)Stopwatch.Frequency;

    internal static long FromSeconds(double seconds) => (long)(seconds * Stopwatch.Frequency);

    internal static long ToNanoseconds(long ticks) => (long)(ticks * (1_000_000_000.0 / Stopwatch.Frequency));

    internal static long ToMicroseconds(long ticks) => (long)(ticks * (1_000_000.0 / Stopwatch.Frequency));
}

[StructLayout(LayoutKind.Auto)]
internal readonly struct Pacer
{
    private readonly double _ticksPerRequest;
    private readonly long _startTicks;

    internal Pacer(double ratePerSecond, long startTicks)
    {
        _startTicks = startTicks;
        _ticksPerRequest = ratePerSecond > 0 ? Stopwatch.Frequency / ratePerSecond : 0;
    }

    internal long IntendedTicks(long index) => _startTicks + (long)(index * _ticksPerRequest);

    internal static void WaitUntil(long deadlineTicks, CancellationToken cancellationToken)
    {
        while (true)
        {
            var remainingTicks = deadlineTicks - Stopwatch.GetTimestamp();
            if (remainingTicks <= 0)
            {
                return;
            }

            var remainingMilliseconds = remainingTicks * 1000.0 / Stopwatch.Frequency;
            switch (remainingMilliseconds)
            {
                case > 8:
                    Thread.Sleep((int)remainingMilliseconds - 6);
                    break;
                case > 0.2:
                    Thread.Sleep(0);
                    break;
                default:
                    Thread.SpinWait(32);
                    break;
            }

            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    internal static async ValueTask WaitUntilAsync(long deadlineTicks, CancellationToken cancellationToken)
    {
        var remainingTicks = deadlineTicks - Stopwatch.GetTimestamp();
        var remainingMilliseconds = remainingTicks * 1000.0 / Stopwatch.Frequency;
        if (remainingMilliseconds > 20)
        {
            await Task.Delay((int)remainingMilliseconds - 15, cancellationToken).ConfigureAwait(false);
        }

#pragma warning disable S6966, VSTHRD103, MA0042 // Sub-millisecond open-loop pacing; Task.Delay cannot hold these instants on Windows.
        WaitUntil(deadlineTicks, cancellationToken);
#pragma warning restore S6966, VSTHRD103, MA0042
    }
}

/// <summary>
/// Starts an async body on a thread of its own, so the caller keeps running even when the body's
/// first awaits complete synchronously: without that property a lane can run to completion inside
/// the loop that starts the lanes, and the lanes after it never start at all.
/// </summary>
internal static class Dedicated
{
    internal static Task RunOnOwnThreadAsync(Func<Task> body) =>
        Task.Factory.StartNew(body, CancellationToken.None, TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach, TaskScheduler.Default).Unwrap();

    internal static Task<T> RunOnOwnThreadAsync<T>(Func<Task<T>> body) =>
        Task.Factory.StartNew(body, CancellationToken.None, TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach, TaskScheduler.Default).Unwrap();
}

internal sealed class LatencySet
{
    internal LogHistogram TcpConnect { get; } = new();

    internal LogHistogram TcpRtt { get; } = new();

    internal LogHistogram UdpRtt { get; } = new();

    internal LogHistogram DnsRtt { get; } = new();

    internal void WriteTo(Utf8JsonWriter writer)
    {
        writer.WriteStartObject(ArmKeys.Common.Record.Latency);
        Write(writer, ArmKeys.Common.LatencyRecord.TcpConnect, TcpConnect);
        Write(writer, ArmKeys.Common.LatencyRecord.TcpRtt, TcpRtt);
        Write(writer, ArmKeys.Common.LatencyRecord.UdpRtt, UdpRtt);
        Write(writer, ArmKeys.Common.LatencyRecord.DnsRtt, DnsRtt);
        writer.WriteEndObject();
    }

    private static void Write(Utf8JsonWriter writer, string name, LogHistogram histogram)
    {
        if (histogram.Count > 0)
        {
            histogram.Snapshot().WriteTo(writer, name);
        }
    }
}

internal sealed class ArmOutcome
{
    /// <summary>
    /// What the arm ran with, after its own defaults were applied. The value is typed rather than a
    /// name/value bag, so an arm constructs the members it published and every other member stays
    /// absent from the record (D14.21).
    /// </summary>
    internal required ArmParameters Parameters { get; init; }

    /// <summary>
    /// The arm's <c>metrics</c> object. It is carried as an <see cref="IJsonWritable"/> rather than a
    /// dictionary so the record writer has one call for every arm, and it is required so an arm
    /// cannot reach the record with no metrics at all.
    /// </summary>
    internal required IJsonWritable Metrics { get; init; }

    /// <summary>
    /// The arm's <c>gates</c> object: a name/value map whose keys are the
    /// <see cref="ArmKeys.Common.Gates"/> constants. The ceiling on directly measurable latency is a
    /// fraction of a millisecond rather than a count, so the values are doubles; every counter is a
    /// whole number far below 2^53 and therefore exact.
    /// </summary>
    internal Dictionary<string, double> Gates { get; } = new(StringComparer.Ordinal);

    internal List<string> Notes { get; } = [];
}

/// <summary>
/// The metrics of an arm that failed before it built any: the record still publishes a
/// <c>metrics</c> object, the same empty one a run that reached the writer with nothing measured has
/// always had.
/// </summary>
internal sealed class EmptyMetrics : IJsonWritable
{
    /// <inheritdoc/>
    public void WriteTo(Utf8JsonWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.WriteStartObject();
        writer.WriteEndObject();
    }
}

internal sealed class ArmContext
{
    internal required ArmSpec Spec { get; init; }

    internal required ClientOptions Options { get; init; }

    internal required IPAddress TargetAddress { get; init; }

    internal required JsonlSink Sink { get; init; }

    internal required LatencySet Latency { get; init; }

    internal required CancellationToken CancellationToken { get; init; }

    internal IPEndPoint TcpEndPoint => new(TargetAddress, Options.TcpPort);

    internal IPEndPoint UdpEndPoint => new(TargetAddress, Options.UdpPort);

    internal IPEndPoint DnsEndPoint => new(TargetAddress, Options.DnsPort);

    internal Socket CreateTcpSocket() => new(TargetAddress.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };

    internal Socket CreateUdpSocket() => new(TargetAddress.AddressFamily, SocketType.Dgram, ProtocolType.Udp);

    internal long DeadlineTicks(long startTicks) => startTicks + Clock.FromSeconds(Spec.Seconds);

    internal CancellationTokenSource CreateLinkedTokenSource() => CancellationTokenSource.CreateLinkedTokenSource(CancellationToken);

    internal ArmContext WithSpec(ArmSpec spec) => new()
    {
        Spec = spec,
        Options = Options,
        TargetAddress = TargetAddress,
        Sink = Sink,
        Latency = Latency,
        CancellationToken = CancellationToken,
    };
}
