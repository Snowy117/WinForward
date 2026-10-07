namespace WinForward.E2E.Contracts;

/// <summary>
/// The <c>sample</c> and <c>samplerError</c> records the resource sampler writes into the arm's own
/// file: the tick's own shell, the counter block, the per-process census, and the failure record.
/// The record's <c>type</c> and <c>arm</c> are the shared root members, declared once in
/// <see cref="Common.Record"/>.
/// </summary>
/// <remarks>
/// <para><b>Three states, three key sets.</b> A self sample carries the full set; a named sample that
/// matched at least one process carries the counter block and the census; a named sample that matched
/// nothing replaces the counter block with <c>absent</c>. The counters group is written by one helper
/// at the record's own level, so its constants are the spelling of that block wherever it is written;
/// each census entry's <c>cpuSeconds</c>/<c>privateBytes</c> are the pair one level down and get their
/// own constants in <see cref="Sample.ProcessEntry"/>.</para>
/// <para><b>The failure record.</b> <c>samplerError</c> is a record of its own with its own shell, so
/// it names the shared <c>type</c>/<c>ticks</c>/<c>arm</c>/<c>process</c> members rather than
/// redeclaring them.</para>
/// </remarks>
public static partial class ArmKeys
{
    /// <summary>The resource sampler's own records.</summary>
    public static class Sample
    {
        public const string Ticks = "ticks";

        public const string Process = "process";

        public const string Self = "self";

        public const string Matched = "matched";

        public const string Absent = "absent";

        public const string Handles = "handles";

        public const string GeneratorCpuSeconds = "generatorCpuSeconds";

        public const string EnvWorkingSetBytes = "envWorkingSetBytes";

        /// <summary>The census array itself; one entry's keys are in <see cref="ProcessEntry"/>.</summary>
        public const string Processes = "processes";

        public const string ReadErrors = "readErrors";

        public const string ReadError = "readError";

        /// <summary>
        /// The counter block the sampler publishes at the record's own level: what was summed over the
        /// processes a tick observed.
        /// </summary>
        public static class Counters
        {
            public const string CpuSeconds = "cpuSeconds";

            public const string PrivateBytes = "privateBytes";

            public const string WorkingSetBytes = "workingSetBytes";

            public const string PeakWorkingSetBytes = "peakWorkingSetBytes";

            public const string Threads = "threads";
        }

        /// <summary>
        /// One entry of <see cref="Processes"/>. The class name is not a JSON member: its constants are
        /// the members of each array element, one process's identity and the two counters read from it.
        /// </summary>
        public static class ProcessEntry
        {
            public const string Pid = "pid";

            public const string StartUtc = "startUtc";

            public const string CountersRead = "countersRead";

            public const string CpuSeconds = "cpuSeconds";

            public const string PrivateBytes = "privateBytes";
        }

        /// <summary>The failure record's own members, beside the shell it shares with a sample.</summary>
        public static class SamplerError
        {
            public const string Error = "error";

            public const string Message = "message";
        }
    }
}
