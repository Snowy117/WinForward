using System.Runtime.InteropServices;

namespace WinForward.E2E.Client;

/// <summary>
/// The identity of a summed process is the pair (id, start time): Windows recycles process ids, so the id
/// alone cannot tell a restart from the process that was already there.
/// </summary>
[StructLayout(LayoutKind.Auto)]
internal readonly struct SampledProcess
{
    internal SampledProcess(int id, DateTime? startedUtc, bool countersRead, double cpuSeconds, long privateBytes)
    {
        Id = id;
        StartedUtc = startedUtc;
        CountersRead = countersRead;
        CpuSeconds = cpuSeconds;
        PrivateBytes = privateBytes;
    }

    internal int Id { get; }

    internal DateTime? StartedUtc { get; }

    internal bool CountersRead { get; }

    internal double CpuSeconds { get; }

    internal long PrivateBytes { get; }
}

/// <summary>
/// Every process one name matched, summed counter by counter, with the per-process identities the sum
/// was taken over. An unmatched name is a totals value with no process in it, never a null: a zero
/// here is a measurement of an absent process, not a missing measurement.
/// </summary>
internal sealed class ProcessTotals
{
    internal List<SampledProcess> Processes { get; } = [];

    internal double CpuSeconds { get; set; }

    internal long PrivateBytes { get; set; }

    internal long WorkingSet { get; set; }

    internal long PeakWorkingSet { get; set; }

    internal long Threads { get; set; }

    internal long Handles { get; set; }
}
