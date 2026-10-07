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
/// averaging that in would report a measurement that did not happen. The rejected count is published in
/// §15 beside the sample count precisely so the reader can see how much of a series was discarded.</para>
/// </remarks>
internal static class RunSamples
{
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
    internal static bool IsReadable(JsonElement sample) => !JsonValue.Truthy(sample, ArmKeys.Sample.ReadError);
}
