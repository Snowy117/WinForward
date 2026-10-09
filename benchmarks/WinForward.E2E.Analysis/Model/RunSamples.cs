using System.Globalization;
using System.Text.Json;
using WinForward.E2E.Contracts;

namespace WinForward.E2E.Analysis.Model;

/// <summary>
/// The views a run's samples are read through: the product's own resource samples, the sampler's own,
/// the sampler's failures, and whether one sample can be used at all.
/// </summary>
/// <remarks>
/// <para><b>Every sample of the run, arm by arm, in load order.</b> The harness is one process sampling
/// another, so each 1 Hz tick writes a sample marked <c>self</c> for the sampler and a sample without
/// the mark for the product; the two series answer different questions and are never mixed. A sample
/// that matched no process is kept as well — its absence is a reading (the product had not started),
/// which is why the reference keeps those records instead of dropping them.</para>
/// <para><b>A partially read sample is rejected rather than averaged.</b> The harness writes
/// <c>readError</c> when it could not read a process's counters and leaves the counters null;
/// averaging that in would report a measurement that did not happen. The rejected count is published
/// beside the sample count precisely so the reader can see how much of a series was discarded.</para>
/// </remarks>
internal static class RunSamples
{
    /// <summary>The unit the memory sections report sample counters in.</summary>
    internal const double Mebibyte = 1024.0 * 1024.0;

    /// <summary>The product's samples: every sample of the run that is not the sampler's own.</summary>
    internal static List<JsonElement> Product(ClientRun run)
    {
        ArgumentNullException.ThrowIfNull(run);

        var samples = new List<JsonElement>();
        foreach (var arm in run.Arms.All)
        {
            foreach (var sample in arm.Samples)
            {
                if (JsonValue.IsTrue(sample, ArmKeys.Sample.Self))
                {
                    continue;
                }

                samples.Add(sample);
            }
        }

        return samples;
    }

    /// <summary>The sampler's own samples: the ones marked <c>self</c>.</summary>
    internal static List<JsonElement> Self(ClientRun run)
    {
        ArgumentNullException.ThrowIfNull(run);

        var samples = new List<JsonElement>();
        foreach (var arm in run.Arms.All)
        {
            foreach (var sample in arm.Samples)
            {
                if (!JsonValue.IsTrue(sample, ArmKeys.Sample.Self))
                {
                    continue;
                }

                samples.Add(sample);
            }
        }

        return samples;
    }

    /// <summary>Every <c>samplerError</c> record of the run, arm by arm.</summary>
    internal static List<JsonElement> SamplerErrors(ClientRun run)
    {
        ArgumentNullException.ThrowIfNull(run);

        var errors = new List<JsonElement>();
        foreach (var arm in run.Arms.All)
        {
            errors.AddRange(arm.SamplerErrors);
        }

        return errors;
    }

    /// <summary>Whether a sample's counters were read completely enough to be used.</summary>
    internal static bool IsReadable(JsonElement sample) => !JsonValue.IsTrue(sample, ArmKeys.Sample.ReadError);

    /// <summary>One arm's samples, in file order, or an empty list when the run has no such arm.</summary>
    /// <param name="run">The run to read.</param>
    /// <param name="armName">The arm, e.g. <c>LAT</c>.</param>
    /// <param name="selfOnly">Whether to keep the sampler's own samples instead of the product's.</param>
    internal static List<JsonElement> Arm(ClientRun run, string armName, bool selfOnly = false)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(armName);

        var arm = run.Arms.Find(armName);
        if (arm is null)
        {
            return [];
        }

        return [.. arm.Samples.Where(sample => JsonValue.IsTrue(sample, ArmKeys.Sample.Self) == selfOnly)];
    }

    /// <summary>
    /// The sampled process the campaign actually measured: the product process name with the most
    /// records, ties broken by the name.
    /// </summary>
    /// <remarks>
    /// The campaign samples one product, but a run's records can also carry samples that matched no
    /// process at all (the product had not started). Picking the name with the most records rather than
    /// the first one seen is what keeps a startup gap from becoming the row's whole series.
    /// </remarks>
    /// <param name="run">The run to read.</param>
    /// <returns>The process name, or null when no product sample matched anything.</returns>
    internal static string? PrimaryProductProcess(ClientRun run)
    {
        ArgumentNullException.ThrowIfNull(run);

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var sample in Product(run))
        {
            var name = ProcessName(sample);
            if (!counts.TryAdd(name, 1))
            {
                counts[name]++;
            }
            else
            {
                order.Add(name);
            }
        }

        if (order.Count == 0)
        {
            return null;
        }

        order.Sort((left, right) =>
        {
            var byCount = counts[right].CompareTo(counts[left]);
            return byCount != 0 ? byCount : string.CompareOrdinal(left, right);
        });
        return order[0];
    }

    /// <summary>
    /// A sample's private bytes: the sum over its per-process block, or the record's own total when the
    /// block carries none.
    /// </summary>
    /// <param name="sample">The sample to read.</param>
    /// <returns>The bytes, or null when nothing in the sample carried the counter.</returns>
    internal static double? ProcessPrivateBytes(JsonElement sample)
    {
        if (JsonValue.Array(sample, ArmKeys.Sample.Processes) is { Count: > 0 } processes)
        {
            var total = 0.0;
            var found = false;
            foreach (var process in processes)
            {
                if (JsonValue.Number(process, ArmKeys.Sample.ProcessEntry.PrivateBytes) is { } value)
                {
                    total += value;
                    found = true;
                }
            }

            if (found)
            {
                return total;
            }
        }

        return JsonValue.Number(sample, ArmKeys.Sample.Counters.PrivateBytes);
    }

    /// <summary>
    /// The readable samples of the primary product process that actually carry counters — the ones a
    /// CPU or memory cell may be computed from.
    /// </summary>
    /// <param name="run">The run to read.</param>
    /// <param name="armName">The arm whose samples are read.</param>
    /// <param name="primary">The primary process name, or null when the run matched none.</param>
    internal static List<JsonElement> PresentProductSamples(ClientRun run, string armName, string? primary)
    {
        var present = new List<JsonElement>();
        foreach (var sample in Arm(run, armName))
        {
            if (primary is not null && !IsProcess(sample, primary))
            {
                continue;
            }

            if (!IsReadable(sample)
                || JsonValue.Number(sample, ArmKeys.Sample.Matched) is not > 0
                || (JsonValue.Number(sample, ArmKeys.Sample.Counters.PrivateBytes) is null
                    && JsonValue.Number(sample, ArmKeys.Sample.Counters.CpuSeconds) is null))
            {
                continue;
            }

            present.Add(sample);
        }

        return present;
    }

    /// <summary>
    /// One arm's steady-state samples: the readable product samples after the arm's own warmup window,
    /// or the whole series when the run cannot place the window in ticks.
    /// </summary>
    /// <remarks>
    /// The window is per arm, not per run: each arm has its own start tick and the campaign's warmup is
    /// measured from it, so a long first arm does not shift what counts as steady state for the second.
    /// </remarks>
    /// <param name="run">The run to read.</param>
    /// <param name="armName">The arm whose samples are read.</param>
    /// <param name="primary">The primary process name, or null when the run matched none.</param>
    /// <param name="warmupSeconds">How many seconds of the arm to discard.</param>
    internal static List<JsonElement> SteadySamples(ClientRun run, string armName, string? primary, double warmupSeconds)
    {
        var samples = PresentProductSamples(run, armName, primary);
        if (RunClocks.ArmStartTicks(run, armName) is not { } started
            || RunClocks.TickFrequency(run) is not { } frequency)
        {
            return samples;
        }

        var cut = started + (warmupSeconds * frequency);
        var kept = new List<JsonElement>(samples.Count);
        foreach (var sample in samples)
        {
            if (JsonValue.Number(sample, ArmKeys.Sample.Ticks) is { } ticks && ticks >= cut)
            {
                kept.Add(sample);
            }
        }

        return kept.Count > 0 ? kept : samples;
    }

    /// <summary>
    /// One sample's per-process block as the counters a CPU cell reads: the identity that owns the
    /// counter, the cumulative CPU seconds it reported, and whether that read succeeded.
    /// </summary>
    /// <remarks>
    /// The identity is <c>(pid, startUtc)</c> rather than the process id: Windows recycles ids, so the
    /// id alone cannot tell a restart from the process that was already there, and summing first/last
    /// differences over a name would silently subtract one process's counter from another's.
    /// </remarks>
    /// <param name="sample">The sample to read.</param>
    internal static List<(ProcessIdentity Identity, double? CpuSeconds, bool Read)> SampleIdentities(JsonElement sample)
    {
        var identities = new List<(ProcessIdentity, double?, bool)>();
        foreach (var process in JsonValue.Array(sample, ArmKeys.Sample.Processes) ?? [])
        {
            if (process.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            identities.Add((
                new ProcessIdentity(
                    IdentityPart(JsonValue.Member(process, ArmKeys.Sample.ProcessEntry.Pid)),
                    IdentityPart(JsonValue.Member(process, ArmKeys.Sample.ProcessEntry.StartUtc))),
                JsonValue.Number(process, ArmKeys.Sample.ProcessEntry.CpuSeconds),
                JsonValue.IsTrue(process, ArmKeys.Sample.ProcessEntry.CountersRead)));
        }

        return identities;
    }

    /// <summary>
    /// Whether a sample belongs to the named process, which is an equality against the sample's own
    /// <c>process</c> member: a sample that matched nothing has none and belongs to no process.
    /// </summary>
    /// <param name="sample">The sample to test.</param>
    /// <param name="primary">The process name the run's primary process was ranked as.</param>
    internal static bool IsProcess(JsonElement sample, string primary)
    {
        ArgumentNullException.ThrowIfNull(primary);

        return JsonValue.Member(sample, ArmKeys.Sample.Process) is { ValueKind: JsonValueKind.String } name
            && string.Equals(name.GetString(), primary, StringComparison.Ordinal);
    }

    /// <summary>One sample's <c>process</c> member as the name the ranking keys on.</summary>
    private static string ProcessName(JsonElement sample)
    {
        var member = JsonValue.Member(sample, ArmKeys.Sample.Process);
        return member is { ValueKind: JsonValueKind.String } name ? name.GetString()! : string.Empty;
    }

    /// <summary>
    /// One identity component as a value two samples can be compared on: the JSON kind is part of the
    /// value, because the reference keys its identities on the decoded value and <c>"1"</c>, <c>1</c> and
    /// <see langword="true"/> are three different keys there.
    /// </summary>
    private static string IdentityPart(JsonElement? member) => member switch
    {
        null => "<absent>",
        { ValueKind: JsonValueKind.Null } => "<null>",
        { ValueKind: JsonValueKind.String } text => $"s:{text.GetString()}",
        { ValueKind: JsonValueKind.Number } number => $"n:{NumberText(number)}",
        { } other => $"j:{other.GetRawText()}",
    };

    /// <summary>One number component in the round-trip form, in which <c>1203</c> and <c>1203.0</c> agree.</summary>
    private static string NumberText(JsonElement number) =>
        number.GetDouble().ToString("R", CultureInfo.InvariantCulture);
}

/// <summary>
/// The identity a summed CPU counter belongs to: the process id and the instant that process started,
/// which together survive id recycling.
/// </summary>
/// <param name="Pid">The process id as the sampler wrote it.</param>
/// <param name="StartUtc">The process start instant as the sampler wrote it.</param>
internal readonly record struct ProcessIdentity(string Pid, string StartUtc);
