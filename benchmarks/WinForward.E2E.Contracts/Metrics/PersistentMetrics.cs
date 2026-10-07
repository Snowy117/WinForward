using System.Text.Json;
using WinForward.E2E.Contracts.Json;

namespace WinForward.E2E.Contracts.Metrics;

/// <summary>
/// The <c>metrics</c> object of a <c>persistent</c> result record: one long-lived connection's paced
/// exchanges, the reconnect and idle-window evidence around them, and the error counters that explain
/// a request the arm could not complete. Every property is <see langword="required"/> so a new key
/// cannot be added without the factory in the shape test failing to compile, and the write order below
/// is the declaration order of <see cref="ArmKeys.Persistent"/>.
/// </summary>
/// <remarks>
/// Nothing here is conditional: a run that never reconnected publishes its zero, and a reading with no
/// measurement behind it publishes JSON <see langword="null"/> while keeping its key.
/// </remarks>
public sealed record PersistentMetrics : IJsonWritable
{
    /// <summary>Paced exchanges the arm attempted.</summary>
    public required long Requests { get; init; }

    /// <summary>Exchanges an echo completed.</summary>
    public required long Responses { get; init; }

    /// <summary>Requests that had to open a replacement connection first.</summary>
    public required long Reconnects { get; init; }

    /// <summary>Whether the connection live when the idle window opened completed the first request after it.</summary>
    public required bool SurvivedIdle { get; init; }

    /// <summary>Whole pacing intervals the requested idle period was rounded to.</summary>
    public required double IdleSecondsScheduled { get; init; }

    /// <summary>Silence observed between the last request before the window and the first after it.</summary>
    public required double IdleSecondsObserved { get; init; }

    /// <summary>Sends the kernel did not accept synchronously; those sends still complete.</summary>
    public required long SendWouldBlock { get; init; }

    /// <summary>Sends that threw.</summary>
    public required long SendFailures { get; init; }

    /// <summary>Rounds whose echo did not arrive inside the response timeout.</summary>
    public required long Timeouts { get; init; }

    /// <summary>Rounds whose connection was closed or faulted while the arm waited.</summary>
    public required long RemoteClosed { get; init; }

    /// <summary>Framing errors, and frames that failed their checksum or their filler.</summary>
    public required long ProtocolErrors { get; init; }

    /// <summary>Frames whose payload did not match its filler.</summary>
    public required long Corrupt { get; init; }

    /// <summary>Valid frames that matched no outstanding request.</summary>
    public required long UnmatchedReplies { get; init; }

    /// <summary>Connects the arm started.</summary>
    public required long ConnectAttempts { get; init; }

    /// <summary>Connects in that same population that failed.</summary>
    public required long ConnectFailures { get; init; }

    /// <summary>Mean connect duration in milliseconds; <see langword="null"/> when none succeeded.</summary>
    public required double? MeanConnectMs { get; init; }

    /// <summary>Responses over requests; <see langword="null"/> when nothing was offered.</summary>
    public required double? ResponseRate { get; init; }

    /// <summary>Responses per elapsed second; <see langword="null"/> when no time passed.</summary>
    public required double? AchievedRate { get; init; }

    /// <inheritdoc/>
    public void WriteTo(Utf8JsonWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.WriteStartObject();
        writer.WriteNumber(ArmKeys.Persistent.Requests, Requests);
        writer.WriteNumber(ArmKeys.Persistent.Responses, Responses);
        writer.WriteNumber(ArmKeys.Persistent.Reconnects, Reconnects);
        writer.WriteBoolean(ArmKeys.Persistent.SurvivedIdle, SurvivedIdle);
        writer.WriteNumber(ArmKeys.Persistent.IdleSecondsScheduled, IdleSecondsScheduled);
        writer.WriteNumber(ArmKeys.Persistent.IdleSecondsObserved, IdleSecondsObserved);
        writer.WriteNumber(ArmKeys.Persistent.SendWouldBlock, SendWouldBlock);
        writer.WriteNumber(ArmKeys.Persistent.SendFailures, SendFailures);
        writer.WriteNumber(ArmKeys.Persistent.Timeouts, Timeouts);
        writer.WriteNumber(ArmKeys.Persistent.RemoteClosed, RemoteClosed);
        writer.WriteNumber(ArmKeys.Persistent.ProtocolErrors, ProtocolErrors);
        writer.WriteNumber(ArmKeys.Persistent.Corrupt, Corrupt);
        writer.WriteNumber(ArmKeys.Persistent.UnmatchedReplies, UnmatchedReplies);
        writer.WriteNumber(ArmKeys.Persistent.ConnectAttempts, ConnectAttempts);
        writer.WriteNumber(ArmKeys.Persistent.ConnectFailures, ConnectFailures);
        Reading.Write(writer, ArmKeys.Persistent.MeanConnectMs, MeanConnectMs);
        Reading.Write(writer, ArmKeys.Persistent.ResponseRate, ResponseRate);
        Reading.Write(writer, ArmKeys.Persistent.AchievedRate, AchievedRate);
        writer.WriteEndObject();
    }
}
