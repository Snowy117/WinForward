namespace WinForward.E2E.Contracts;

/// <summary>
/// The <c>metrics</c> object of a <c>"base"</c> result record: one elapsed time, then the metrics of
/// each phase the control ran, back to back inside one record.
/// </summary>
/// <remarks>
/// <para><b>Two nested blocks, one level of real nesting.</b> <c>latency</c> and <c>loss</c> are
/// objects of their own, so each phase's keys are its kind's own member names one level down
/// (<c>metrics/loss/sent</c>, <c>metrics/latency/tcp.sent</c>): the values are the shared
/// <see cref="Metrics.LatencyMetrics"/> and <see cref="Metrics.LossMetrics"/> records, whose writers
/// reference <see cref="ArmKeys.Latency"/> and <see cref="ArmKeys.Loss"/>, so a phase key is the arm's
/// own key rather than a copy of it that could drift.</para>
/// <para><b>Conditional fields.</b> The latency phase publishes no <c>tcp.*</c> key at all when the
/// control's plan declares a UDP-only protocol, and no <c>udp.*</c> key when it declares TCP-only: the
/// omission is a whole protocol block and only ever means "the phase did not run that protocol", never
/// "the value is unknown" (unknown is a written JSON <see langword="null"/>). The two container keys
/// <see cref="Control.Latency"/> and <see cref="Control.Loss"/> are always present, even when a block
/// inside them is not, and the loss phase is unconditional.</para>
/// <para>The record's <c>gates</c> keys are shared and live in <see cref="ArmKeys.Common.Gates"/>; its
/// <c>parameters</c> keys in <see cref="ArmKeys.Common.Parameters"/>, where
/// <see cref="ArmKeys.Common.Parameters.Latency"/> and <see cref="ArmKeys.Common.Parameters.Loss"/>
/// declare the two phase objects' names under <c>parameters</c>.</para>
/// </remarks>
public static partial class ArmKeys
{
    /// <summary>Keys under <c>metrics</c> for the <c>"base"</c> kind: the phase containers and the phase-level readings.</summary>
    public static class Control
    {
        /// <summary>Wall time the two phases took together.</summary>
        public const string ElapsedSeconds = "elapsedSeconds";

        // S3218 / MemberHidesStaticFromOuterClass: the published member name is `latency`, and
        // `ArmKeys.Latency` is the latency arm's own shard. The rule this file follows -- a member's
        // name is its key's name -- makes the collision the contract rather than an accident; the
        // same shadowing is already declared and justified on ArmKeys.Common.Parameters.Latency.
#pragma warning disable S3218
        // ReSharper disable once MemberHidesStaticFromOuterClass
        public const string Latency = "latency";
#pragma warning restore S3218

        // S3218 / MemberHidesStaticFromOuterClass: the same shadowing as `Latency` above, against
        // `ArmKeys.Loss`, which declares the loss phase's members one level down.
        /// <summary>The loss phase's container key.</summary>
#pragma warning disable S3218
        // ReSharper disable once MemberHidesStaticFromOuterClass
        public const string Loss = "loss";
#pragma warning restore S3218
    }
}
