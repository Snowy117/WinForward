using System.Globalization;
using System.Text.Json;
using WinForward.E2E.Analysis.Loading;
using WinForward.E2E.Analysis.Model;

namespace WinForward.E2E.Analysis.Release;

/// <summary>Which side of a pass a probe was taken on.</summary>
internal enum ClockPhase
{
    /// <summary>Before the pass's measurements.</summary>
    Pre,

    /// <summary>After the pass's measurements.</summary>
    Post,
}

/// <summary>One <c>clock.jsonl</c> line: a phase median's own sample, in both clocks.</summary>
/// <param name="Pass">The pass number the line declared.</param>
/// <param name="Phase">Which side of the pass the probe was taken on.</param>
/// <param name="OffsetMs">The probe's offset, signed <c>vm - host</c>.</param>
/// <param name="RttMs">The probe's round trip, as the writer measured it.</param>
/// <param name="VmUtc">The instant the VM sent the probe, on the VM's clock.</param>
/// <param name="HostUtc">The instant the host received the probe, on the host's clock.</param>
internal sealed record ClockProbe(
    int Pass,
    ClockPhase Phase,
    double OffsetMs,
    double RttMs,
    DateTimeOffset VmUtc,
    DateTimeOffset HostUtc);

/// <summary>What one <c>clock.jsonl</c> file holds, including whether it was there at all.</summary>
/// <param name="Probes">Every line that was a valid probe, in file order.</param>
/// <param name="BadLines">Lines that were not a probe: not JSON, or JSON that does not carry the six members with their types.</param>
/// <param name="Present">Whether the file existed.</param>
/// <param name="Unreadable">The reason the file could not be read, when it could not.</param>
internal sealed record ClockLog(
    IReadOnlyList<ClockProbe> Probes,
    int BadLines,
    bool Present,
    string? Unreadable);

/// <summary>
/// Reads the campaign root's <c>clock.jsonl</c>: the orchestrator's per-pass pre/post probe samples.
/// </summary>
/// <remarks>
/// <para><b>A bad line is counted, never thrown on.</b> The file is written by the orchestrator while it
/// runs a campaign, so a truncated last line or a hand-edited field is a fact about the campaign rather
/// than a reason to lose the samples beside it. A line is a probe only when every member is present with
/// its declared type: the pass is a positive integer, the phase is exactly <c>pre</c> or <c>post</c>,
/// both millisecond values are finite and inside a day, and both stamps parse as UTC instants.</para>
/// <para><b>Unknown members are ignored.</b> The probe's six members are what the correction reads; a
/// later writer adding a field must not turn every sample into a bad line, and no reader may invent a
/// meaning for a member this analysis does not know.</para>
/// </remarks>
internal static class ClockLogReader
{
    /// <summary>The file's name at the campaign root.</summary>
    internal const string FileName = "clock.jsonl";

    /// <summary>How large a millisecond value may be before it is not a clock offset any campaign measured.</summary>
    /// <remarks>
    /// The orchestrator's own sanity bound aborts a campaign beyond five seconds; a day is therefore a
    /// type check rather than a policy, and it keeps the window arithmetic away from a fabricated value
    /// that would overflow <see cref="DateTimeOffset"/>.
    /// </remarks>
    private const double MaxMilliseconds = 86_400_000;

    private const string PassName = "pass";
    private const string PhaseName = "phase";
    private const string OffsetName = "offsetMs";
    private const string RttName = "rttMs";
    private const string VmName = "vmUtc";
    private const string HostName = "hostUtc";

    private const string PrePhase = "pre";
    private const string PostPhase = "post";

    /// <summary>Reads the file at <paramref name="path"/>, reporting absence and unreadability as values.</summary>
    internal static ClockLog Read(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        if (!File.Exists(path))
        {
            return new ClockLog([], 0, Present: false, Unreadable: null);
        }

        JsonLines lines;
        try
        {
            lines = JsonReader.ReadLines(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new ClockLog([], 0, Present: true, Unreadable: exception.Message);
        }

        var probes = new List<ClockProbe>(lines.Records.Count);
        var badLines = lines.BadLines;
        foreach (var record in lines.Records)
        {
            if (TryProbe(record, out var probe))
            {
                probes.Add(probe);
            }
            else
            {
                badLines++;
            }
        }

        return new ClockLog(probes, badLines, Present: true, Unreadable: null);
    }

    private static bool TryProbe(JsonElement record, out ClockProbe probe)
    {
        probe = null!;
        if (!TryPass(record, out var pass)
            || !TryPhase(record, out var phase)
            || !TryMilliseconds(record, OffsetName, out var offset)
            || !TryMilliseconds(record, RttName, out var rtt)
            || !TryStamp(record, VmName, out var vmUtc)
            || !TryStamp(record, HostName, out var hostUtc))
        {
            return false;
        }

        probe = new ClockProbe(pass, phase, offset, rtt, vmUtc, hostUtc);
        return true;
    }

    private static bool TryPass(JsonElement record, out int pass)
    {
        pass = 0;
        return JsonValue.Member(record, PassName) is { ValueKind: JsonValueKind.Number } value
            && value.TryGetInt32(out pass)
            && pass >= 1;
    }

    private static bool TryPhase(JsonElement record, out ClockPhase phase)
    {
        phase = ClockPhase.Pre;
        var value = JsonValue.String(record, PhaseName);
        if (string.Equals(value, PrePhase, StringComparison.Ordinal))
        {
            return true;
        }

        if (!string.Equals(value, PostPhase, StringComparison.Ordinal))
        {
            return false;
        }

        phase = ClockPhase.Post;
        return true;
    }

    private static bool TryMilliseconds(JsonElement record, string name, out double value)
    {
        value = 0.0;
        var number = JsonValue.Number(record, name);
        if (number is not { } found || !double.IsFinite(found) || Math.Abs(found) > MaxMilliseconds)
        {
            return false;
        }

        value = found;
        return true;
    }

    private static bool TryStamp(JsonElement record, string name, out DateTimeOffset stamp)
    {
        stamp = default;
        var text = JsonValue.String(record, name);
        if (string.IsNullOrEmpty(text)
            || !DateTimeOffset.TryParse(
                text,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind | DateTimeStyles.AssumeUniversal,
                out var parsed))
        {
            return false;
        }

        stamp = parsed;
        return true;
    }
}
