using System.Globalization;
using WinForward.E2E.Analysis.Stats;

namespace WinForward.E2E.Analysis.Release;

/// <summary>
/// The campaign's clock correction: what <c>clock.jsonl</c> says the VM clock was doing around each pass,
/// and the translation that puts the client's arm windows on the ledger's clock.
/// </summary>
/// <remarks>
/// <para><b>Sign.</b> A probe writes <c>offsetMs = vm - host</c>, so the ledger's instant is the client's
/// minus the offset: a VM running two seconds ahead records <c>+2000</c> and its windows move two seconds
/// back. The conversion is a subtraction, never an addition, and <see cref="Apply"/> is the one place it
/// happens.</para>
/// <para><b>The offset is one number per pass.</b> The pre and post phase medians are two points on a
/// line in the VM's own time, and the pass's offset is that line at the midpoint between the two anchors.
/// Every window of the pass is translated by it unchanged, because a window's two edges are a duration the
/// client measured with a monotonic stopwatch, and a drifting offset is not a reason to rescale it.
/// <see cref="OffsetAt"/> exposes the same line at any one VM instant as a diagnostic, which differs
/// from the applied pass offset by at most half the pass's drift; the release clock rule bounds that
/// drift at 100 ms, so a gated pass reads at most 50 ms away from an arm-midpoint interpolation, inside
/// the ledger's one-second summary band.</para>
/// <para><b>A pass with one phase is corrected by that phase and disclosed.</b> Interpolating across a
/// missing sample would invent the other end, so the single median is used and the pass is
/// <see cref="ClockPhaseKind.PreOnly"/> or <see cref="ClockPhaseKind.PostOnly"/>, which makes the campaign
/// <see cref="ClockStatus.Partial"/>. A pass with no probe at all keeps its VM window and is
/// <see cref="ClockPhaseKind.Missing"/>.</para>
/// <para><b>Absence is a value, not an error.</b> A campaign without <c>clock.jsonl</c> reads as
/// <see cref="ClockStatus.Absent"/> and every window stays exactly where the client wrote it; a release
/// run reports itself as uncorrected rather than failing to read its own campaign.</para>
/// </remarks>
public sealed class ClockCorrection
{
    /// <summary>The file's name at the campaign root.</summary>
    public const string FileName = ClockLogReader.FileName;

    private readonly Dictionary<int, ClockPass> _passes;

    private ClockCorrection(
        string sourcePath,
        ClockStatus status,
        int samples,
        int badLines,
        string? problem,
        List<ClockPass> passes)
    {
        SourcePath = sourcePath;
        Status = status;
        Samples = samples;
        BadLines = badLines;
        Problem = problem;
        Passes = passes;
        _passes = passes.ToDictionary(pass => pass.Pass);
    }

    /// <summary>What the file supports, in the release vocabulary: absent, incomplete, partial or complete.</summary>
    public ClockStatus Status { get; }

    /// <summary>The file the correction was read from, as the caller's campaign root spells it.</summary>
    public string SourcePath { get; }

    /// <summary>How many valid probes the file held.</summary>
    public int Samples { get; }

    /// <summary>How many lines were not a valid probe; a campaign discloses this rather than dropping it.</summary>
    public int BadLines { get; }

    /// <summary>Why the file could not be read, when it could not; otherwise null.</summary>
    public string? Problem { get; }

    /// <summary>One entry per probed pass, in pass order; a pass with no probe has no entry.</summary>
    public IReadOnlyList<ClockPass> Passes { get; }

    /// <summary>Whether any window can be corrected at all.</summary>
    public bool Corrected => Status is ClockStatus.Complete or ClockStatus.Partial;

    /// <summary>
    /// Reads the campaign root's <c>clock.jsonl</c>. A missing or unreadable file, and every malformed
    /// line, become a status and a count rather than an exception.
    /// </summary>
    /// <param name="campaignRoot">The campaign root: the directory holding the pass directories and the campaign files.</param>
    /// <returns>The correction, never null; its <see cref="Status"/> says whether anything can be applied.</returns>
    public static ClockCorrection Read(string campaignRoot)
    {
        ArgumentException.ThrowIfNullOrEmpty(campaignRoot);

        var path = Path.Combine(campaignRoot, FileName);
        var log = ClockLogReader.Read(path);
        var byPass = new Dictionary<int, List<ClockProbe>>();
        foreach (var probe in log.Probes)
        {
            if (!byPass.TryGetValue(probe.Pass, out var probes))
            {
                probes = [];
                byPass[probe.Pass] = probes;
            }

            probes.Add(probe);
        }

        var passes = new List<ClockPass>(byPass.Count);
        foreach (var number in byPass.Keys.Order())
        {
            passes.Add(BuildPass(number, byPass[number]));
        }

        return new ClockCorrection(
            path,
            StatusOf(log, passes),
            log.Probes.Count,
            log.BadLines,
            log.Unreadable,
            passes);
    }

    /// <summary>One pass's evidence, or null when the log carries no probe for it.</summary>
    /// <param name="passId">The pass id the tree spells, <c>pass&lt;N&gt;</c>.</param>
    public ClockPass? Pass(string passId)
    {
        if (string.IsNullOrEmpty(passId)
            || !TryPassNumber(passId, out var number)
            || !_passes.TryGetValue(number, out var pass))
        {
            return null;
        }

        return pass;
    }

    /// <summary>
    /// The drift line's offset at one VM instant, or null when the pass has no evidence: the interpolated
    /// diagnostic view, which the attribution does not use. Outside the two phase anchors the line is
    /// extrapolated; a window always lies between them in a campaign that probed its own pass.
    /// </summary>
    /// <param name="passId">The pass id the tree spells, <c>pass&lt;N&gt;</c>.</param>
    /// <param name="vmUtc">The instant on the client's clock.</param>
    public double? OffsetAt(string passId, DateTimeOffset vmUtc)
    {
        if (Pass(passId) is not { OffsetMs: not null } pass)
        {
            return null;
        }

        return pass.Kind switch
        {
            ClockPhaseKind.PreOnly => pass.PreOffsetMs,
            ClockPhaseKind.PostOnly => pass.PostOffsetMs,
            _ => OnLine(
                pass.PreOffsetMs!.Value,
                pass.PreAnchorUtc!.Value,
                pass.PostOffsetMs!.Value,
                pass.PostAnchorUtc!.Value,
                vmUtc),
        };
    }

    /// <summary>
    /// Translates one arm window from the client's clock to the ledger's, by the pass's own offset: the
    /// drift line at the midpoint between the two phase anchors, so every window of a pass moves by the
    /// same amount and differs from an arm-midpoint interpolation by at most half the pass's drift. A pass
    /// with no evidence keeps its window unchanged and reports <see cref="ClockPhaseKind.Missing"/>.
    /// </summary>
    /// <param name="passId">The pass id the tree spells, <c>pass&lt;N&gt;</c>.</param>
    /// <param name="vmStart">The window's start on the client's clock.</param>
    /// <param name="vmEnd">The window's end on the client's clock.</param>
    /// <exception cref="ArgumentException">The window ends before it starts.</exception>
    public CorrectedWindow Apply(string passId, DateTimeOffset vmStart, DateTimeOffset vmEnd)
    {
        ArgumentException.ThrowIfNullOrEmpty(passId);
        if (vmEnd < vmStart)
        {
            throw new ArgumentException("the window ends before it starts", nameof(vmEnd));
        }

        var pass = Pass(passId);
        if (pass?.OffsetMs is not { } offset)
        {
            return new CorrectedWindow
            {
                PassId = passId,
                VmStart = vmStart,
                VmEnd = vmEnd,
                HostStart = vmStart,
                HostEnd = vmEnd,
                OffsetMs = 0.0,
                Kind = ClockPhaseKind.Missing,
            };
        }

        var shift = TimeSpan.FromTicks(Ticks(offset));
        return new CorrectedWindow
        {
            PassId = passId,
            VmStart = vmStart,
            VmEnd = vmEnd,
            HostStart = vmStart - shift,
            HostEnd = vmEnd - shift,
            OffsetMs = offset,
            Kind = pass.Kind,
        };
    }

    private static ClockStatus StatusOf(ClockLog log, IReadOnlyList<ClockPass> passes)
    {
        if (!log.Present)
        {
            return ClockStatus.Absent;
        }

        if (log.Probes.Count == 0)
        {
            return ClockStatus.Incomplete;
        }

        return passes.Any(pass => pass.Kind is ClockPhaseKind.PreOnly or ClockPhaseKind.PostOnly)
            ? ClockStatus.Partial
            : ClockStatus.Complete;
    }


    private static ClockPass BuildPass(int number, List<ClockProbe> probes)
    {
        var pre = probes.Where(probe => probe.Phase == ClockPhase.Pre).ToList();
        var post = probes.Where(probe => probe.Phase == ClockPhase.Post).ToList();
        var preOffset = Median(pre);
        var postOffset = Median(post);
        var preAnchor = Anchor(pre);
        var postAnchor = Anchor(post);
        var kind = KindOf(preOffset, postOffset);
        return new ClockPass
        {
            PassId = PassIdOf(number),
            Pass = number,
            Kind = kind,
            Samples = probes.Count,
            PreOffsetMs = preOffset,
            PostOffsetMs = postOffset,
            DriftMs = preOffset is { } before && postOffset is { } after ? Math.Abs(after - before) : null,
            ResidualMs = ResidualOf(kind, probes, pre, post, preOffset, postOffset, preAnchor, postAnchor),
            OffsetMs = OffsetOf(kind, preOffset, postOffset, preAnchor, postAnchor),
            PreAnchorUtc = preAnchor,
            PostAnchorUtc = postAnchor,
        };
    }

    private static ClockPhaseKind KindOf(double? preOffset, double? postOffset)
    {
        if (preOffset is not null && postOffset is not null)
        {
            return ClockPhaseKind.Interpolated;
        }

        if (preOffset is not null)
        {
            return ClockPhaseKind.PreOnly;
        }

        return postOffset is not null ? ClockPhaseKind.PostOnly : ClockPhaseKind.Missing;
    }


    private static double? OffsetOf(
        ClockPhaseKind kind,
        double? preOffset,
        double? postOffset,
        DateTimeOffset? preAnchor,
        DateTimeOffset? postAnchor) => kind switch
        {
            ClockPhaseKind.PreOnly => preOffset,
            ClockPhaseKind.PostOnly => postOffset,
            ClockPhaseKind.Interpolated => OnLine(
                preOffset!.Value,
                preAnchor!.Value,
                postOffset!.Value,
                postAnchor!.Value,
                Midpoint(preAnchor.Value, postAnchor.Value)),
            _ => null,
        };


    private static double? ResidualOf(
        ClockPhaseKind kind,
        List<ClockProbe> probes,
        List<ClockProbe> pre,
        List<ClockProbe> post,
        double? preOffset,
        double? postOffset,
        DateTimeOffset? preAnchor,
        DateTimeOffset? postAnchor) => kind switch
        {
            ClockPhaseKind.Interpolated => Spread(
                probes,
                probe => OnLine(
                    preOffset!.Value,
                    preAnchor!.Value,
                    postOffset!.Value,
                    postAnchor!.Value,
                    probe.VmUtc)),
            ClockPhaseKind.PreOnly => Spread(pre, _ => preOffset!.Value),
            ClockPhaseKind.PostOnly => Spread(post, _ => postOffset!.Value),
            _ => null,
        };


    private static double? Median(List<ClockProbe> probes) => probes.Count == 0
        ? null
        : DescriptiveStats.Median([.. probes.Select(probe => probe.OffsetMs)]);

    /// <summary>The VM instant the phase's median offset is anchored at: the middle probe's own instant.</summary>
    /// <remarks>
    /// One phase's five probes are a few hundred milliseconds apart, so any representative instant moves
    /// the line by a fraction of a millisecond; the middle probe is chosen because it is stable against
    /// one outlier timestamp.
    /// </remarks>
    private static DateTimeOffset? Anchor(List<ClockProbe> probes) => probes.Count == 0
        ? null
        : probes.Select(probe => probe.VmUtc).Order().ElementAt((probes.Count - 1) / 2);

    private static DateTimeOffset Midpoint(DateTimeOffset from, DateTimeOffset to)
    {
        var ticks = from.UtcTicks + ((to.UtcTicks - from.UtcTicks) / 2);
        return new(ticks, TimeSpan.Zero);
    }


    private static double OnLine(
        double fromOffset,
        DateTimeOffset from,
        double toOffset,
        DateTimeOffset to,
        DateTimeOffset at)
    {
        var span = to.UtcTicks - from.UtcTicks;
        if (span == 0)
        {
            // Two anchors on the same instant describe a flat offset; the mean is the only reading the
            // samples support.
            return (fromOffset + toOffset) / 2.0;
        }

        var fraction = (at.UtcTicks - from.UtcTicks) / (double)span;
        return fromOffset + ((toOffset - fromOffset) * fraction);
    }

    private static double? Spread(List<ClockProbe> probes, Func<ClockProbe, double> line) => probes.Count == 0
        ? null
        : probes.Max(probe => Math.Abs(probe.OffsetMs - line(probe)));

    private static long Ticks(double milliseconds) =>
        (long)Math.Round(milliseconds * TimeSpan.TicksPerMillisecond, MidpointRounding.AwayFromZero);

    private static string PassIdOf(int number) => "pass" + number.ToString(CultureInfo.InvariantCulture);

    private static bool TryPassNumber(string passId, out int number)
    {
        number = 0;
        if (!passId.StartsWith("pass", StringComparison.Ordinal))
        {
            return false;
        }

        var digits = passId.AsSpan(4);
        return !digits.IsEmpty
            && int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out number)
            && number >= 1;
    }
}
