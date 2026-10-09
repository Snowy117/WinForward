using WinForward.E2E.Contracts;
using WinForward.E2E.Contracts.Json;

namespace WinForward.E2E.Client;

internal sealed class ArmOutcome
{
    /// <summary>
    /// What the arm ran with, after its own defaults were applied. The value is typed rather than a
    /// name/value bag, so an arm constructs the members it published and every other member stays
    /// absent from the record.
    /// </summary>
    internal required ArmParameters Parameters { get; init; }

    /// <summary>
    /// The arm's <c>metrics</c> object. It is carried as an <see cref="IJsonWritable"/> rather than a
    /// dictionary so the record writer has one call for every arm, and it is required so an arm
    /// cannot reach the record with no metrics at all.
    /// </summary>
    internal required IJsonWritable Metrics { get; init; }

    /// <summary>
    /// The arm's <c>gates</c> object: a name/value map whose keys are the
    /// <see cref="ArmKeys.Common.Gates"/> constants. The ceiling on directly measurable latency is a
    /// fraction of a millisecond rather than a count, so the values are doubles; every counter is a
    /// whole number far below 2^53 and therefore exact.
    /// </summary>
    internal Dictionary<string, double> Gates { get; } = new(StringComparer.Ordinal);

    internal List<string> Notes { get; } = [];
}
