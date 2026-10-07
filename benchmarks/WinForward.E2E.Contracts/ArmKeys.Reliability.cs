namespace WinForward.E2E.Contracts;

/// <summary>
/// The <c>metrics</c> object of a <c>reliability</c> result record: the outcome distribution of one
/// connection schedule, the expected distribution beside it, and the joint mode x observed breakdown
/// the per-mode blocks carry. It also declares the keys of the per-attempt <c>attempt</c> records the
/// same arm writes into the same file.
/// </summary>
/// <remarks>
/// <para><b>Two outcome levels, one schema.</b> <see cref="Reliability.Outcomes"/> and
/// <see cref="Reliability.Expected"/> are
/// two distributions over the same seven names, and every mode block carries a third one under
/// <c>observed</c>. All three write <see cref="Reliability.OutcomeNames"/>, whose class is never a JSON member of
/// its own: the container key (<c>outcomes</c>, <c>expected</c>, <c>observed</c>) is written by the
/// record that holds it, exactly as <see cref="ArmKeys.Common.LatencyRecord.Histogram"/> carries the
/// leaves of four different histograms.</para>
/// <para><b>One block per scheduled mode.</b> <see cref="Reliability.ModeNames"/> names the modes a plan's
/// <c>modeMix</c> can schedule, and <c>modeSchedule</c> publishes the schedule itself. The blocks
/// under <see cref="Reliability.ByMode"/> carry the four names of the schedule the shape test models; a run whose
/// plan schedules fewer modes publishes fewer blocks, so the member set follows the plan while each
/// block's schema does not.</para>
/// <para><b>Conditional fields: none.</b> A reading with no measurement behind it is written as JSON
/// <see langword="null"/> and keeps its key; the four mode names that are data appear only because the
/// schedule named them.</para>
/// <para><b>Same leaf name at a different level is a different constant.</b>
/// <see cref="Reliability.Truncated"/> and <see cref="Reliability.Mode.Truncated"/> are two paths, and so
/// are <see cref="Reliability.Expected"/> and <see cref="Reliability.Attempt.Expected"/>; each level
/// declares its own, which is why several members here share a name with a member declared one level up.
/// Those members -- <see cref="Reliability.OutcomeNames"/>'s <c>unexpectedEof</c> and <c>connectFail</c>,
/// <see cref="Reliability.Mode"/>'s <c>truncated</c>,
/// <c>echoedBytes</c> and <c>trailerBytes</c>, and <see cref="Reliability.Attempt"/>'s <c>mode</c>,
/// <c>expected</c>, <c>truncated</c>, <c>echoedBytes</c> and <c>trailerBytes</c> -- each carry their
/// own one-member suppression of the two shadowing rules, because the shadowing is the declaration
/// rule rather than an accident (D14.17).</para>
/// <para>The record's <c>gates</c> keys are shared and live in <see cref="ArmKeys.Common.Gates"/>; its
/// <c>parameters</c> keys in <see cref="ArmKeys.Common.Parameters"/>.</para>
/// </remarks>
public static partial class ArmKeys
{
    /// <summary>Keys under <c>metrics</c> for the <c>reliability</c> kind, and the keys of its <c>attempt</c> records.</summary>
    public static class Reliability
    {
        /// <summary>Attempts that ran to a verdict.</summary>
        public const string ConnectAttempts = "connectAttempts";

        /// <summary>Attempts the pacer offered.</summary>
        public const string ScheduledAttempts = "scheduledAttempts";

        /// <summary>The observed outcome distribution: one member per outcome name.</summary>
        public const string Outcomes = "outcomes";

        /// <summary>The distribution the requested modes called for: one member per outcome name.</summary>
        public const string Expected = "expected";

        /// <summary>Unexpected end-of-stream observations, the partialFin mode excluded.</summary>
        public const string UnexpectedEof = "unexpectedEof";

        /// <summary>Unexpected end-of-stream observations the partialFin mode calls for.</summary>
        public const string ExpectedEarlyEof = "expectedEarlyEof";

        /// <summary>Attempts whose echo was cut short.</summary>
        public const string Truncated = "truncated";

        /// <summary>Attempts whose observation diverged from what their mode called for.</summary>
        public const string FidelityMismatch = "fidelityMismatch";

        /// <summary>Mismatches over attempts; null when no attempt ran.</summary>
        public const string FidelityRate = "fidelityRate";

        /// <summary>Attempts that never connected.</summary>
        public const string ConnectFail = "connectFail";

        /// <summary>The echo size the arm requested.</summary>
        public const string ExpectedBytes = "expectedBytes";

        /// <summary>The scheduled modes, in schedule order, comma separated.</summary>
        public const string ModeSchedule = "modeSchedule";

        /// <summary>Echo bytes read back across every attempt.</summary>
        public const string EchoedBytes = "echoedBytes";

        /// <summary>Bytes read after the expected echo, across every attempt.</summary>
        public const string TrailerBytes = "trailerBytes";

        /// <summary>The joint mode x observed distribution: one member per scheduled mode.</summary>
        public const string ByMode = "byMode";

        /// <summary>Per-attempt evidence records the arm wrote.</summary>
        public const string AttemptRecords = "attemptRecords";

        /// <summary>Qualifying attempts that did not fit the record cap.</summary>
        public const string AttemptRecordsOmitted = "attemptRecordsOmitted";

        /// <summary>Mean connect duration in milliseconds; null when none succeeded.</summary>
        public const string MeanConnectMs = "meanConnectMs";

        /// <summary>Mean request-send duration in milliseconds; null when none was measured.</summary>
        public const string MeanTransferMs = "meanTransferMs";

        /// <summary>Attempts per elapsed second; null when no time passed.</summary>
        public const string AchievedRate = "achievedRate";

        /// <summary>The mode mix the arm actually ran, after its own default was applied.</summary>
        public const string EffectiveModeMix = "effectiveModeMix";

        /// <summary>
        /// The outcome names every distribution is written under, in write order. This class is named
        /// after the members it declares rather than after a container, because it is never a JSON
        /// member of its own: <see cref="Outcomes"/>, <see cref="Expected"/> and each mode's
        /// <c>observed</c> write these leaves inside their own object.
        /// </summary>
        public static class OutcomeNames
        {
            /// <summary>The exchange completed as its mode called for.</summary>
            public const string Clean = "clean";

            /// <summary>The peer reset the connection.</summary>
            public const string Reset = "reset";

            /// <summary>The stream ended before the complete echo arrived.</summary>
            /// <remarks>Shadows <see cref="Reliability.UnexpectedEof"/>; see the shard's shadowing note.</remarks>
#pragma warning disable S3218
            // ReSharper disable once MemberHidesStaticFromOuterClass
            public const string UnexpectedEof = "unexpectedEof";
#pragma warning restore S3218

            /// <summary>The attempt ran out of time.</summary>
            public const string Timeout = "timeout";

            /// <summary>The connection could not be established.</summary>
            /// <remarks>Shadows <see cref="Reliability.ConnectFail"/>; see the shard's shadowing note.</remarks>
#pragma warning disable S3218
            // ReSharper disable once MemberHidesStaticFromOuterClass
            public const string ConnectFail = "connectFail";
#pragma warning restore S3218

            /// <summary>A half-closing mode saw its peer reset instead of echoing the trailer.</summary>
            public const string HalfCloseViolation = "halfCloseViolation";

            /// <summary>A socket, protocol or framing error outside the named outcomes.</summary>
            public const string OtherError = "otherError";
        }

        /// <summary>
        /// The mode names a plan's <c>modeMix</c> schedules, in the order the arm's default mix
        /// declares them. The arm writes them through <c>TcpCommand.Name</c>, so these declarations
        /// are the schedule's spelling and a test asserts they still agree with it.
        /// </summary>
        public static class ModeNames
        {
            /// <summary>A clean exchange.</summary>
            public const string Clean = "clean";

            /// <summary>Every n-th request is answered with a reset.</summary>
            public const string ResetAfterN = "resetAfterN";

            /// <summary>The peer closes its send side before the echo is complete.</summary>
            public const string PartialFin = "partialFin";

            /// <summary>The client half-closes and expects the trailer.</summary>
            public const string HalfClose = "halfClose";
        }

        /// <summary>
        /// The members of one <c>byMode</c> block: the tally, the observed distribution and the echo
        /// and trailer extremes, in write order. The block's own member name is the mode's
        /// <see cref="ModeNames"/> name, written by the record that holds it.
        /// </summary>
        /// <remarks>
        /// <see cref="Truncated"/>, <see cref="EchoedBytes"/> and <see cref="TrailerBytes"/> are
        /// declared again here rather than shared with the arm-wide keys of the same names, because
        /// one is a per-mode path and the other an arm-wide one (D14.17).
        /// </remarks>
        public static class Mode
        {
            /// <summary>Attempts this mode produced.</summary>
            public const string Attempts = "attempts";

            /// <summary>This mode's observed outcome distribution: one member per outcome name.</summary>
            public const string Observed = "observed";

            /// <summary>This mode's attempts whose echo was cut short.</summary>
            /// <remarks>Shadows <see cref="Reliability.Truncated"/>; see the shard's shadowing note.</remarks>
#pragma warning disable S3218
            // ReSharper disable once MemberHidesStaticFromOuterClass
            public const string Truncated = "truncated";
#pragma warning restore S3218

            /// <summary>Echo bytes this mode read back.</summary>
            /// <remarks>Shadows <see cref="Reliability.EchoedBytes"/>; see the shard's shadowing note.</remarks>
#pragma warning disable S3218
            // ReSharper disable once MemberHidesStaticFromOuterClass
            public const string EchoedBytes = "echoedBytes";
#pragma warning restore S3218

            /// <summary>Bytes this mode read after the expected echo.</summary>
            /// <remarks>Shadows <see cref="Reliability.TrailerBytes"/>; see the shard's shadowing note.</remarks>
#pragma warning disable S3218
            // ReSharper disable once MemberHidesStaticFromOuterClass
            public const string TrailerBytes = "trailerBytes";
#pragma warning restore S3218

            /// <summary>Smallest echo this mode read; null when the mode produced no attempt.</summary>
            public const string MinEchoedBytes = "minEchoedBytes";

            /// <summary>Largest echo this mode read; null when the mode produced no attempt.</summary>
            public const string MaxEchoedBytes = "maxEchoedBytes";

            /// <summary>Smallest trailer this mode read; null when the mode produced no attempt.</summary>
            public const string MinTrailerBytes = "minTrailerBytes";

            /// <summary>Largest trailer this mode read; null when the mode produced no attempt.</summary>
            public const string MaxTrailerBytes = "maxTrailerBytes";
        }

        /// <summary>
        /// The members of an <c>attempt</c> record, in write order: one client-side observation per
        /// qualifying attempt, so it can be joined 1:1 with the target ledger's verdict for the same
        /// connection id. The record's <c>type</c> is <see cref="ArmKeys.Common.Record.Type"/>, the
        /// same top-level skeleton key every record carries.
        /// </summary>
        public static class Attempt
        {
            /// <summary>The connection id the attempt used.</summary>
            public const string ConnectionId = "connectionId";

            /// <summary>The mode the attempt ran.</summary>
            /// <remarks>Shadows <see cref="Reliability.Mode"/>; see the shard's shadowing note.</remarks>
#pragma warning disable S3218
            // ReSharper disable once MemberHidesStaticFromOuterClass
            public const string Mode = "mode";
#pragma warning restore S3218

            /// <summary>How the exchange ended: exchanged, connectFail, cancelled or timeout.</summary>
            public const string Status = "status";

            /// <summary>The outcome the client observed.</summary>
            public const string Observed = "observed";

            /// <summary>The outcome the attempt's mode called for.</summary>
            /// <remarks>Shadows <see cref="Reliability.Expected"/>; see the shard's shadowing note.</remarks>
#pragma warning disable S3218
            // ReSharper disable once MemberHidesStaticFromOuterClass
            public const string Expected = "expected";
#pragma warning restore S3218

            /// <summary>Whether the echo was cut short.</summary>
            /// <remarks>Shadows <see cref="Reliability.Truncated"/>; see the shard's shadowing note.</remarks>
#pragma warning disable S3218
            // ReSharper disable once MemberHidesStaticFromOuterClass
            public const string Truncated = "truncated";
#pragma warning restore S3218

            /// <summary>Echo bytes the attempt read back.</summary>
            /// <remarks>Shadows <see cref="Reliability.EchoedBytes"/>; see the shard's shadowing note.</remarks>
#pragma warning disable S3218
            // ReSharper disable once MemberHidesStaticFromOuterClass
            public const string EchoedBytes = "echoedBytes";
#pragma warning restore S3218

            /// <summary>Bytes the attempt read after the expected echo.</summary>
            /// <remarks>Shadows <see cref="Reliability.TrailerBytes"/>; see the shard's shadowing note.</remarks>
#pragma warning disable S3218
            // ReSharper disable once MemberHidesStaticFromOuterClass
            public const string TrailerBytes = "trailerBytes";
#pragma warning restore S3218

            /// <summary>Whether the peer closed its send side.</summary>
            public const string Eof = "eof";

            /// <summary>Whether the connection was reset.</summary>
            public const string Reset = "reset";

            /// <summary>Whether the stream carried a framing or checksum error.</summary>
            public const string ProtocolError = "protocolError";

            /// <summary>Whether the attempt failed with a socket or I/O error outside the named outcomes.</summary>
            public const string OtherError = "otherError";

            /// <summary>Stopwatch ticks the connect took.</summary>
            public const string ConnectTicks = "connectTicks";

            /// <summary>Stopwatch ticks the request send took.</summary>
            public const string TransferTicks = "transferTicks";
        }
    }
}
