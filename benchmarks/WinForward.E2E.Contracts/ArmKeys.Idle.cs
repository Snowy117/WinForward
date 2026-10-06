namespace WinForward.E2E.Contracts;

/// <summary>
/// The <c>metrics</c> object of an <c>idle</c> result record: the keys the arm writes, in write
/// order. A constant here is the member name under <c>metrics</c>, so <see cref="Idle.ElapsedSeconds"/>
/// declares the path <c>metrics/elapsedSeconds</c>.
/// </summary>
/// <remarks>
/// Nothing in this family is conditional: an idle arm always publishes <c>elapsedSeconds</c>, and it
/// cannot run without measuring one. The arm's <c>gates</c> keys are shared and live in
/// <see cref="ArmKeys.Common.Gates"/>; its <c>parameters</c> keys in <see cref="ArmKeys.Common.Parameters"/>.
/// Its record's <c>latency</c> object is always empty, because the arm records no histogram.
/// </remarks>
public static partial class ArmKeys
{
    /// <summary>Keys under <c>metrics</c> for the <c>idle</c> kind.</summary>
    public static class Idle
    {
        /// <summary>Measured wall time of the arm, in seconds.</summary>
        public const string ElapsedSeconds = "elapsedSeconds";
    }
}
