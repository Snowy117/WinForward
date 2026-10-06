namespace WinForward.E2E.Contracts;

/// <summary>
/// The <c>metrics</c> object of a <c>throughput</c> result record: the keys the arm writes, in write
/// order. A constant here is the member name under <c>metrics</c>, so <see cref="Throughput.BytesSent"/>
/// declares the path <c>metrics/bytesSent</c>.
/// </summary>
/// <remarks>
/// <para>Nothing in this family is conditional: after a run every one of these keys is present,
/// whatever the streams managed to do, because a run that transferred nothing says so through zeros
/// and <c>budgetReached</c> rather than by leaving a key out.</para>
/// <para>Two of them are nullable instead: <see cref="Throughput.GoodputBps"/> and <see cref="Throughput.GoodputMbps"/> are
/// <see langword="null"/> when the measured duration is zero, which is a missing rate rather than a
/// rate of zero. Unknown is always a written <see langword="null"/> here, never an absent key.</para>
/// <para>This arm records no histogram, so its record's <c>latency</c> object is always empty. Its
/// <c>gates</c> keys are shared and live in <see cref="ArmKeys.Common.Gates"/>; its <c>parameters</c>
/// keys in <see cref="ArmKeys.Common.Parameters"/>.</para>
/// </remarks>
public static partial class ArmKeys
{
    /// <summary>Keys under <c>metrics</c> for the <c>throughput</c> kind.</summary>
    public static class Throughput
    {
        /// <summary>Echoed frame bytes the client read back: the measured transfer.</summary>
        public const string Bytes = "bytes";

        /// <summary>Frame bytes the client wrote.</summary>
        public const string BytesSent = "bytesSent";

        /// <summary>Echoed frames, derived from <see cref="Bytes"/> and the frame length.</summary>
        public const string Frames = "frames";

        /// <summary>Frames the client wrote.</summary>
        public const string FramesSent = "framesSent";

        /// <summary>Frames the client read back.</summary>
        public const string FramesEchoed = "framesEchoed";

        /// <summary>Streams that connected; <c>streams - connectFailures</c>.</summary>
        public const string StreamConnects = "streamConnects";

        /// <summary>Streams whose connect attempt failed.</summary>
        public const string ConnectFailures = "connectFailures";

        /// <summary>Sends the kernel refused with an error.</summary>
        public const string SendFailures = "sendFailures";

        /// <summary>Declared byte budget for the arm: <c>targetBytesPerSecond * seconds</c>.</summary>
        public const string BudgetBytes = "budgetBytes";

        /// <summary>True when the run ended because the budget was spent rather than because time ran out.</summary>
        public const string BudgetReached = "budgetReached";

        /// <summary>Budget bytes no frame claimed: a frame is never part-budgeted.</summary>
        public const string BudgetRemainingBytes = "budgetRemainingBytes";

        /// <summary>Measured wall time of the arm, in seconds.</summary>
        public const string ElapsedSeconds = "elapsedSeconds";

        /// <summary>Echoed bytes per second; <see langword="null"/> when no time passed.</summary>
        public const string GoodputBps = "goodputBps";

        /// <summary>Echoed bits per second, in megabits; <see langword="null"/> when no time passed.</summary>
        public const string GoodputMbps = "goodputMbps";

        /// <summary>Smallest echoed-byte count of any stream, or 0 when there were no streams.</summary>
        public const string PerStreamMinBytes = "perStreamMinBytes";

        /// <summary>Largest echoed-byte count of any stream.</summary>
        public const string PerStreamMaxBytes = "perStreamMaxBytes";

        /// <summary>Frames that failed their checksum.</summary>
        public const string Corrupt = "corrupt";

        /// <summary>Frames that decoded to something other than a valid frame.</summary>
        public const string ProtocolErrors = "protocolErrors";

        /// <summary>The effective aggregate send rate the streams were paced to.</summary>
        public const string TargetBytesPerSecond = "targetBytesPerSecond";
    }
}
