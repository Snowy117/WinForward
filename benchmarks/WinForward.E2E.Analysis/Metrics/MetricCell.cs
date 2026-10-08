using WinForward.E2E.Analysis.Model;

namespace WinForward.E2E.Analysis.Metrics;

/// <summary>
/// One metric on one row, before any value is read: whether the row ever ran the arm at all, whether the
/// row's traffic can support the metric, and the reason when either answer is no.
/// </summary>
/// <remarks>
/// <para><b>The status separates a design absence from a data gap.</b> "This row never ran that arm"
/// is the campaign's own plan, "the arm is declared but no file was found" is a gap that should be
/// reported as one, and "this product does not carry UDP" is a property of the traffic. Only the first
/// two are absences of a measurement; the third is a measurement that would mean nothing.</para>
/// <para><b>The port-53 arm is its own case.</b> A row whose port-53 path is not relayed measures its
/// own wiring rather than the product, so the status says so and the pair comparability refuses to
/// compare it across rows.</para>
/// </remarks>
internal static class MetricStatus
{
    /// <summary>Nothing is wrong with this metric on this row.</summary>
    internal const string Ok = "ok";

    /// <summary>The row's plan never runs the arm.</summary>
    internal const string NotInPlan = "not-in-plan";

    /// <summary>The row's plan runs the arm but no arm file or result was found.</summary>
    internal const string DeclaredAbsent = "declared-absent";

    /// <summary>The product cannot carry UDP, so every UDP-derived number for this row measures nothing.</summary>
    internal const string NotCarried = "not-carried";

    /// <summary>The port-53 arm is a direct-path measurement on this row.</summary>
    internal const string DnsCarriage = "dns-carriage";

    /// <summary>The status and its reason for one metric on one row, before any value is read.</summary>
    /// <param name="campaign">The loaded campaign the row's arms are looked for in.</param>
    /// <param name="arm">The arm the metric is read from, or null for a metric read from the samples.</param>
    /// <param name="udpPath"><c>udp</c> or <c>dns</c> when the value rides on a UDP path, else null.</param>
    /// <param name="dns53">Whether this is the port-53 DNS arm, whose carriage differs per row.</param>
    /// <param name="rowId">The row the metric is asked about.</param>
    /// <returns>The status name and, when it is not <see cref="Ok"/>, the reason in the reference's words.</returns>
    internal static (string Status, string? Reason) Resolve(
        CampaignModel campaign,
        string? arm,
        string? udpPath,
        bool dns53,
        string rowId)
    {
        ArgumentNullException.ThrowIfNull(campaign);
        ArgumentNullException.ThrowIfNull(rowId);

        var profile = RowProfiles.Find(rowId);
        if (arm is not null)
        {
            var declared = RowProfiles.Planned(rowId);
            var observed = campaign.RunsOf(rowId).Any(run => run.Arms.Contains(arm));
            if (!observed)
            {
                if (declared is not null && !declared.Contains(arm, StringComparer.Ordinal))
                {
                    return (NotInPlan, $"not measured in this row: {arm} is not in the {profile!.Plan} plan");
                }

                return declared is not null
                    ? (DeclaredAbsent, $"{arm} is declared in the {profile!.Plan} plan but no arm file or result was found")
                    : (DeclaredAbsent, $"{arm} was not measured");
            }
        }

        if (profile is not null && udpPath is not null
            && string.Equals(profile.Udp, RowProfiles.UdpNotCarried, StringComparison.Ordinal))
        {
            return (NotCarried, RowProfiles.NotCarriedCell);
        }

        if (profile is not null && dns53
            && !string.Equals(profile.Udp53, RowProfiles.Udp53Relayed, StringComparison.Ordinal))
        {
            return (DnsCarriage, $"port-53 arm is a direct-path measurement here: {RowProfiles.Udp53Label(profile.Udp53)}");
        }

        return (Ok, null);
    }
}

/// <summary>
/// One metric's per-pass readings: the values, the reasons for the passes without one, and the status
/// that says whether the absence is a design statement or a gap.
/// </summary>
/// <remarks>
/// <para><b>A value and a reason, per pass.</b> The reason is what turns into <c>n/a (reason)</c> when
/// nothing could be read, and a pass the harness wrote as JSON <see langword="null"/> is counted separately
/// because a zero denominator renders as an <b>empty cell</b> rather than as a zero.</para>
/// <para><b>The values are read in natural-key order.</b> The order is the one the bootstrap and the
/// medians see, and the reference fixes it by sorting the pass ids rather than relying on insertion.</para>
/// </remarks>
internal sealed class MetricCell
{
    /// <summary>Per pass, the value that pass contributed.</summary>
    internal Dictionary<string, double> Values { get; } = new(StringComparer.Ordinal);

    /// <summary>Per pass, why that pass contributed no value.</summary>
    internal Dictionary<string, string> Reasons { get; } = new(StringComparer.Ordinal);

    /// <summary>One of the <see cref="MetricStatus"/> names.</summary>
    internal string Status { get; init; } = MetricStatus.Ok;

    /// <summary>The status's own reason, or null when the status is <see cref="MetricStatus.Ok"/>.</summary>
    internal string? StatusReason { get; init; }

    /// <summary>How many passes the harness wrote as JSON null, which is a zero denominator.</summary>
    internal int NullPasses => Reasons.Values.Count(why => string.Equals(why, ArmAccess.NullRateReason, StringComparison.Ordinal));

    /// <summary>The values in natural-key pass order.</summary>
    internal IReadOnlyList<double> SortedValues()
    {
        var ordered = NaturalKey.Sort(Values.Keys);
        var values = new List<double>(ordered.Count);
        foreach (var passId in ordered)
        {
            values.Add(Values[passId]);
        }

        return values;
    }

    /// <summary>
    /// The distinct reasons, in ordinal order, joined with <c>; </c> — the reference's own summary for a
    /// cell that has nothing to print but several explanations.
    /// </summary>
    /// <param name="limit">How many reasons to keep, as the reference's own two.</param>
    /// <returns>The summary, or <c>no data</c> when no pass gave a reason.</returns>
    internal string ReasonSummary(int limit = 2)
    {
        var unique = Reasons.Values.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        return unique.Count == 0 ? "no data" : string.Join("; ", unique.Take(limit));
    }
}
