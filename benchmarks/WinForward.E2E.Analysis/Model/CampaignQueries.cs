namespace WinForward.E2E.Analysis.Model;

/// <summary>
/// The lookups every section makes over a loaded campaign: one pass's rows, one row in one pass, and the
/// two control blocks a pass is read against.
/// </summary>
internal static class CampaignQueries
{
    /// <summary>One pass's rows, or an empty list for a pass that was not loaded.</summary>
    internal static IReadOnlyList<ClientRun> InPass(this CampaignModel campaign, string passId)
    {
        ArgumentNullException.ThrowIfNull(campaign);
        ArgumentNullException.ThrowIfNull(passId);

        return campaign.Passes.TryGetValue(passId, out var rows) ? rows : [];
    }

    /// <summary>One row's run in one pass, or null when that pass did not run it.</summary>
    internal static ClientRun? InPass(this CampaignModel campaign, string passId, string rowId)
    {
        ArgumentNullException.ThrowIfNull(campaign);
        ArgumentNullException.ThrowIfNull(rowId);

        return campaign.InPass(passId).FirstOrDefault(run => string.Equals(run.RunId, rowId, StringComparison.Ordinal));
    }

    /// <summary>One row's run by id, searched across the lanes as well, or null.</summary>
    internal static ClientRun? ById(this CampaignModel campaign, string passId, string runId)
    {
        ArgumentNullException.ThrowIfNull(campaign);
        ArgumentNullException.ThrowIfNull(runId);

        return campaign.InPass(passId)
            .SelectMany(run => new[] { run, run.DualProxied, run.DualDirect })
            .FirstOrDefault(candidate => candidate is not null
                && string.Equals(candidate.RunId, runId, StringComparison.Ordinal));
    }

    /// <summary>Every loaded run of one row, pass by pass; the lanes are not among them.</summary>
    internal static IReadOnlyList<ClientRun> RunsOf(this CampaignModel campaign, string rowId)
    {
        ArgumentNullException.ThrowIfNull(campaign);
        ArgumentNullException.ThrowIfNull(rowId);

        return [.. campaign.Rows.Where(run => string.Equals(run.RunId, rowId, StringComparison.Ordinal))];
    }

    /// <summary>Whether a row id is one of the campaign's two control blocks.</summary>
    internal static bool IsControl(string rowId) =>
        RowProfiles.IsControlPre(rowId) || RowProfiles.IsControlPost(rowId);

    /// <summary>Whether a row's own profile declares a dual phase.</summary>
    internal static bool IsDualRow(string rowId) => RowProfiles.Find(rowId) is { Dual: true };

}
