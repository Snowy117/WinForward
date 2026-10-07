namespace WinForward.E2E.Contracts;

/// <summary>
/// The keys of <c>run.json</c>: the run-level environment, the arm roster and its per-arm verdicts.
/// It is not one of the arm records, but it is the file a consumer reads first, so its keys are
/// declared here rather than spelled at the writer.
/// </summary>
/// <remarks>
/// <para><b>Same leaf name at a different level is a different constant.</b> <see cref="Run.Failed"/>,
/// <see cref="Run.StartedTicks"/> and <see cref="Run.EndedTicks"/> are declared here and again under
/// <see cref="Run.Arm"/>, because the run's own verdict and one arm's verdict are two paths (D14.17).</para>
/// <para><b>The array element.</b> <see cref="Run.Arms"/> names the array; each element's members are
/// declared by <see cref="Run.Arm"/>, which is never a JSON member of its own.</para>
/// </remarks>
public static partial class ArmKeys
{
    /// <summary>Keys of <c>run.json</c>, the one file that carries the whole run's environment.</summary>
    public static class Run
    {
        /// <summary>The record kind: <c>run</c>.</summary>
        public const string Type = "type";

        /// <summary>The run's free-form label.</summary>
        public const string Label = "label";

        /// <summary>The harness binary's assembly version.</summary>
        public const string ClientVersion = "clientVersion";

        /// <summary>The host's OS description.</summary>
        public const string OsDescription = "osDescription";

        /// <summary>The .NET runtime description.</summary>
        public const string FrameworkDescription = "frameworkDescription";

        /// <summary>The logical processor count the run saw.</summary>
        public const string LogicalProcessors = "logicalProcessors";

        /// <summary>The plan's SHA-256 prefix.</summary>
        public const string PlanHash = "planHash";

        /// <summary>The plan file's absolute path, or null when the built-in plan ran.</summary>
        public const string PlanPath = "planPath";

        /// <summary>Whether the run used a file or the built-in plan.</summary>
        public const string PlanSource = "planSource";

        /// <summary>The output directory's absolute path.</summary>
        public const string OutDirectory = "outDirectory";

        /// <summary>The target the run pointed at.</summary>
        public const string Target = "target";

        /// <summary>The process names the sampler watched.</summary>
        public const string SamplerProcesses = "samplerProcesses";

        /// <summary>UTC instant the run started.</summary>
        public const string StartedUtc = "startedUtc";

        /// <summary>UTC instant the run ended.</summary>
        public const string EndedUtc = "endedUtc";

        /// <summary>Stopwatch tick the run started at.</summary>
        public const string StartedTicks = "startedTicks";

        /// <summary>Stopwatch tick the run ended at.</summary>
        public const string EndedTicks = "endedTicks";

        /// <summary>Wall time the run took, in seconds.</summary>
        public const string WallSeconds = "wallSeconds";

        /// <summary>One element per arm, in run order.</summary>
        public const string Arms = "arms";

        /// <summary>Whether any arm of the run failed.</summary>
        public const string Failed = "failed";

        /// <summary>
        /// The members of one <c>arms</c> element, in write order. The class is named after the array
        /// it declares the elements of, because it is never a JSON member of its own.
        /// </summary>
        public static class Arm
        {
            /// <summary>The arm's plan name.</summary>
            public const string Name = "name";

            /// <summary>The arm's kind.</summary>
            public const string Kind = "kind";

            /// <summary>The result file the arm wrote.</summary>
            public const string File = "file";

            /// <summary>Stopwatch tick the arm started at.</summary>
            /// <remarks>Shadows <see cref="Run.StartedTicks"/>; see the shard's shadowing note.</remarks>
#pragma warning disable S3218
            // ReSharper disable once MemberHidesStaticFromOuterClass
            public const string StartedTicks = "startedTicks";
#pragma warning restore S3218

            /// <summary>Stopwatch tick the arm ended at.</summary>
            /// <remarks>Shadows <see cref="Run.EndedTicks"/>; see the shard's shadowing note.</remarks>
#pragma warning disable S3218
            // ReSharper disable once MemberHidesStaticFromOuterClass
            public const string EndedTicks = "endedTicks";
#pragma warning restore S3218

            /// <summary>Whether this arm failed.</summary>
            /// <remarks>Shadows <see cref="Run.Failed"/>; see the shard's shadowing note.</remarks>
#pragma warning disable S3218
            // ReSharper disable once MemberHidesStaticFromOuterClass
            public const string Failed = "failed";
#pragma warning restore S3218
        }

        /// <summary>
        /// The members of the <c>target</c> object, in write order. The class is named after the
        /// object it declares the members of, because <see cref="Run.Target"/> is the container key.
        /// </summary>
        public static class TargetObject
        {
            /// <summary>The target address the run was started with.</summary>
            public const string Address = "address";

            /// <summary>The target TCP port.</summary>
            public const string TcpPort = "tcpPort";

            /// <summary>The target UDP port.</summary>
            public const string UdpPort = "udpPort";

            /// <summary>The DNS port the run was started with.</summary>
            public const string DnsPort = "dnsPort";
        }
    }
}
