using WinForward.E2E.Analysis.Model;

namespace WinForward.E2E.Analysis.Checks;

/// <summary>
/// Whether an arm's own gate says its in-flight window deferred part of its schedule, which turns the
/// percentiles computed from that arm into a mixture of direct round trips and deferred ones.
/// </summary>
/// <remarks>
/// <para><b>The gate is the arm's own counter, the comparison is <c>&gt; 0</c>.</b> Zero is the
/// measured-and-clean case and must not be rendered as unmeasurable; the reference substitutes the
/// overflow cell for a latency row only when a pass actually reports a non-zero count.</para>
/// <para><b>One pass is enough.</b> The cell the caller is about to print aggregates that pass with the
/// others, so part of the number it would show was measured through the deferred queue.</para>
/// <para><b>Only a cell that would otherwise carry a number is replaced.</b> A class the arm does not
/// measure at all keeps saying so: the ceiling is not why it is missing, and the caller asks this
/// question after it has established that something is there to replace.</para>
/// </remarks>
internal static class WindowOverflow
{
    /// <summary>Whether any pass of this arm reports a non-zero deferred count.</summary>
    /// <param name="campaign">The loaded campaign the passes are read from.</param>
    /// <param name="rowId">The row whose arm is asked about.</param>
    /// <param name="armName">The arm whose own gate is read.</param>
    /// <returns><see langword="true"/> when the arm's window deferred work in at least one pass.</returns>
    internal static bool Reached(CampaignModel campaign, string rowId, string armName)
    {
        ArgumentNullException.ThrowIfNull(campaign);
        ArgumentNullException.ThrowIfNull(rowId);
        ArgumentNullException.ThrowIfNull(armName);

        foreach (var passId in campaign.PassIds)
        {
            var run = campaign.InPass(passId, rowId);
            if (run is null)
            {
                continue;
            }

            var (value, _) = ArmAccess.Number(run, armName, "gates/windowOverflow");
            if (value is > 0)
            {
                return true;
            }
        }

        return false;
    }
}
