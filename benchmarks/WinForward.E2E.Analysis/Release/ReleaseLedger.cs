using WinForward.E2E.Analysis.Cli;
using WinForward.E2E.Analysis.Findings;
using WinForward.E2E.Analysis.Loading;
using WinForward.E2E.Analysis.Model;

namespace WinForward.E2E.Analysis.Release;

/// <summary>One arm's ledger attribution as the release path reads it: the target's count beside the client's.</summary>
public sealed record ReleaseLedgerArm
{
    /// <summary>The pass the window belongs to.</summary>
    public required string Pass { get; init; }

    /// <summary>The run the window belongs to; a lane reads as <c>row/lane</c>.</summary>
    public required string Row { get; init; }

    /// <summary>The row that owns the run.</summary>
    public required string Owner { get; init; }

    /// <summary>The arm.</summary>
    public required string Arm { get; init; }

    /// <summary>How many connections the ledger attributed to the window.</summary>
    public required int TcpConnections { get; init; }

    /// <summary>How many datagrams the window's endpoint census accounts for.</summary>
    public required double UdpDatagrams { get; init; }

    /// <summary>Whether an overlapping run's records make the count ambiguous.</summary>
    public required bool Unattributable { get; init; }

    /// <summary>The other runs whose windows overlap this one.</summary>
    public required IReadOnlyList<string> OverlappingRuns { get; init; }

    /// <summary>What the client says it opened in the window, or null when its counters do not cover the arm.</summary>
    public double? ClientConnections { get; init; }

    /// <summary>What the client says it sent as datagrams, or null.</summary>
    public double? ClientDatagrams { get; init; }

    /// <summary>How long the arm ran, which sets the datagram tolerance's floor.</summary>
    public double DurationSeconds { get; init; }
}

/// <summary>The campaign's ledger attribution, read with or without the clock correction.</summary>
public sealed record ReleaseLedgerAttribution
{
    /// <summary>Whether any pass has a ledger at all.</summary>
    public required bool Available { get; init; }

    /// <summary>How many ledger files the campaign holds, counted once across the passes.</summary>
    public required int LedgerFiles { get; init; }

    /// <summary>The clock correction's status for the read the caller asked for.</summary>
    public required ClockStatus Clock { get; init; }

    /// <summary>Whether the windows were translated by a clock offset.</summary>
    public required bool Corrected { get; init; }

    /// <summary>The campaign's pass ids, in natural-key order.</summary>
    public required IReadOnlyList<string> Passes { get; init; }

    /// <summary>One entry per attributed arm window, in pass, run and arm order.</summary>
    public required IReadOnlyList<ReleaseLedgerArm> Arms { get; init; }

    /// <summary>Per pass, how the ledger's records were attributed to runs, in words.</summary>
    public required IReadOnlyDictionary<string, string> Attribution { get; init; }
}

/// <summary>
/// The release path's read of the target ledger: the same attribution the legacy analysis performs, with
/// every arm window optionally translated onto the ledger's clock first.
/// </summary>
/// <remarks>
/// <para><b>The correction happens before attribution, not after it.</b> A record is attributed to an arm
/// by its own <c>utc</c> against the arm's window; moving the count afterwards cannot recover a record
/// that fell into a neighbouring arm's window, so the translated window is the one the selection uses.</para>
/// <para><b>A campaign without a clock log is read exactly as the legacy path reads it.</b> The correction
/// is a nullable argument, and <see langword="null"/> — or a log whose status is absent or incomplete —
/// selects the cached, untranslated views the legacy documents are built from, so the release read cannot
/// change a number the legacy report prints.</para>
/// <para><b>Both sides are exposed.</b> The release verdict needs the corrected count as evidence and the
/// uncorrected count as its negative control, so one call reads with a correction and another without it;
/// the two results are the same shape and differ only in <see cref="ReleaseLedgerAttribution.Corrected"/>.</para>
/// </remarks>
public static class ReleaseLedger
{
    /// <summary>
    /// Loads the campaign at <paramref name="campaignRoot"/> and attributes its ledgers, translating each
    /// arm window with <paramref name="clock"/> when that correction has evidence.
    /// </summary>
    /// <param name="campaignRoot">The campaign root: the directory holding the pass directories.</param>
    /// <param name="clock">The clock correction to apply, or null to read the windows the client wrote.</param>
    /// <param name="attribution">The attribution, when the campaign could be read.</param>
    /// <param name="error">Why it could not be, when it could not.</param>
    /// <returns>True when the campaign tree was loaded and its ledgers attributed.</returns>
    public static bool TryRead(
        string campaignRoot,
        ClockCorrection? clock,
        out ReleaseLedgerAttribution? attribution,
        out string? error)
    {
        ArgumentException.ThrowIfNullOrEmpty(campaignRoot);

        attribution = null;
        if (!AnalysisOptions.TryParse(["--raw", campaignRoot], out var options, out error)
            || !CampaignLoader.TryLoad(options, out var campaign, out error)
            || campaign is null)
        {
            return false;
        }

        var correction = clock is { Corrected: true } ? clock : null;
        var views = correction is null
            ? LedgerViewsBuilder.For(campaign)
            : LedgerViewsBuilder.ForCorrected(campaign, (passId, start, end) => Translate(correction, passId, start, end));
        attribution = Build(campaign, views, clock);
        return true;
    }

    private static (DateTimeOffset Start, DateTimeOffset End) Translate(
        ClockCorrection clock,
        string passId,
        DateTimeOffset start,
        DateTimeOffset end)
    {
        var window = clock.Apply(passId, start, end);
        return window.Corrected
            ? (Start: window.HostStart, End: window.HostEnd)
            : (Start: start, End: end);
    }

    private static ReleaseLedgerAttribution Build(
        CampaignModel campaign,
        LedgerViews views,
        ClockCorrection? clock)
    {
        var arms = new List<ReleaseLedgerArm>();
        foreach (var passId in campaign.PassIds)
        {
            if (!views.Passes.TryGetValue(passId, out var pass))
            {
                continue;
            }

            foreach (var arm in pass.PerArm)
            {
                arms.Add(new ReleaseLedgerArm
                {
                    Pass = arm.Pass,
                    Row = arm.Row,
                    Owner = arm.Owner,
                    Arm = arm.Arm,
                    TcpConnections = arm.TcpConnections,
                    UdpDatagrams = arm.UdpDatagrams,
                    Unattributable = arm.Unattributable,
                    OverlappingRuns = arm.OverlappingRuns,
                    ClientConnections = arm.ClientConnections,
                    ClientDatagrams = arm.ClientDatagrams,
                    DurationSeconds = arm.DurationSeconds,
                });
            }
        }

        return new ReleaseLedgerAttribution
        {
            Available = views.Available,
            LedgerFiles = campaign.LedgerFileCount,
            Clock = clock?.Status ?? ClockStatus.Absent,
            Corrected = clock is { Corrected: true },
            Passes = [.. campaign.PassIds],
            Arms = arms,
            Attribution = views.Attribution,
        };
    }
}
