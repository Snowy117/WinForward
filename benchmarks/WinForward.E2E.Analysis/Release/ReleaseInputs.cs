using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using WinForward.E2E.Analysis.Json;
using WinForward.E2E.Analysis.Model;

namespace WinForward.E2E.Analysis.Release;

/// <summary>
/// The provenance record a release run writes before any verdict exists: what caliber it measured
/// against, which thresholds file judged it, what family it claims and which passes it actually read.
/// </summary>
/// <remarks>
/// <para><b>The inputs are a document, not a log line.</b> A release report is read months later beside
/// the campaign it describes, and the two questions a reader has — "which plan produced these numbers"
/// and "which pre-registered band judged them" — have to be answerable from the run's own bytes. The
/// thresholds digest and the plan digest are therefore recorded from the values that were verified at
/// run time, not from the file's declaration, so the artifact cannot claim a hash the run did not
/// check.</para>
/// <para><b>A short campaign is recorded, not refused.</b> Fewer measured passes than the thresholds
/// file requires is a fact the verdict has to state; this record carries
/// <c>insufficient</c> so the writer can say so without re-deriving the selection.</para>
/// <para><b>Two bases, both the caller's.</b> <c>caliber.path</c> is resolved from the
/// <c>workingDirectory</c> this record is built with. A relative <c>--thresholds</c> path is opened by
/// <see cref="ThresholdsFile"/> from the process's working directory, exactly as the invocation
/// spelled it; <c>scripts/analyze.sh</c> changes neither directory, so for a campaign both are the
/// directory the analysis was started in.</para>
/// </remarks>
public sealed record ReleaseInputs
{
    /// <summary>The schema name of the document this record renders.</summary>
    private const string SchemaName = "release-inputs/1";

    /// <summary>The thresholds file, as the invocation spelled it.</summary>
    public required string ThresholdsPath { get; init; }

    /// <summary>The SHA-256 of the thresholds file's raw bytes.</summary>
    public required string ThresholdsSha256 { get; init; }

    /// <summary>The caliber the run measured against.</summary>
    public required CaliberDeclaration Caliber { get; init; }

    /// <summary>The SHA-256 the caliber plan was verified to have.</summary>
    public required string CaliberSha256 { get; init; }

    /// <summary>The row the inferential family is drawn against.</summary>
    public required string ReferenceRow { get; init; }

    /// <summary>The rows the reference is compared with.</summary>
    public required IReadOnlyList<string> Opponents { get; init; }

    /// <summary>The inferential family, in the thresholds file's order.</summary>
    public required IReadOnlyList<PrimaryMetric> Primary { get; init; }

    /// <summary>The per-arm warm-up window the run trims.</summary>
    public required double WarmupSeconds { get; init; }

    /// <summary>What the thresholds file asks the clock correction to prove.</summary>
    /// <remarks>
    /// The rule is carried on the inputs, not written into the provenance document: it changes whether a
    /// release invocation is refused, and the <c>release-inputs/1</c> schema stays as it was.
    /// </remarks>
    public required ClockRule Clock { get; init; }
    /// <summary>The pass selection the run measured over.</summary>
    public required ReleasePasses Passes { get; init; }

    /// <summary>
    /// Builds the record from a parsed thresholds file, the invocation's overrides, the campaign's pass
    /// ids and the working directory the caliber path is resolved from — verifying the caliber and its
    /// declared durations before anything is returned.
    /// </summary>
    /// <param name="thresholds">The parsed thresholds file.</param>
    /// <param name="options">The invocation's release overrides.</param>
    /// <param name="warmupSeconds">The warm-up window the run actually uses; it has to be finite.</param>
    /// <param name="passIds">The campaign's pass ids.</param>
    /// <param name="workingDirectory">The directory <c>caliber.path</c> is resolved from.</param>
    /// <param name="inputs">The record, when every input agreed.</param>
    /// <param name="error">Why they did not, when they did not.</param>
    /// <returns>True when the thresholds file, the caliber plan and the pass tree all agree.</returns>
    public static bool TryBuild(
        ThresholdsFile thresholds,
        ReleaseOptions options,
        double warmupSeconds,
        IReadOnlyList<string> passIds,
        string workingDirectory,
        out ReleaseInputs? inputs,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(thresholds);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrEmpty(workingDirectory);

        inputs = null;
        if (!double.IsFinite(warmupSeconds))
        {
            // The legacy walk accepts whatever double.TryParse reads, and "NaN" is one of those; a
            // release run records the window in JSON, where NaN is not a number and a reader could not
            // reproduce the trim. This is the release path's own guard, not a change to the legacy flag.
            error = string.Create(
                CultureInfo.InvariantCulture,
                $"release: '--warmup-seconds' is {warmupSeconds}, which is not a finite number");
            return false;
        }

        if (!MatchesWarmup(thresholds, warmupSeconds, out error))
        {
            return false;
        }

        var minPasses = options.MinPasses ?? thresholds.MinPasses ?? ReleaseOptions.DefaultMinPasses;
        var discardPass = options.DiscardPass ?? thresholds.DiscardPass ?? ReleaseOptions.DefaultDiscardPass;
        if (!ReleasePasses.TrySelect(passIds, discardPass, minPasses, out var passes, out error) || passes is null)
        {
            return false;
        }

        if (!TryVerifyCaliber(thresholds.Caliber, workingDirectory, out var verified, out error))
        {
            return false;
        }

        inputs = new ReleaseInputs
        {
            ThresholdsPath = thresholds.Path,
            ThresholdsSha256 = thresholds.Sha256,
            Caliber = thresholds.Caliber with { Sha256 = verified },
            CaliberSha256 = verified,
            ReferenceRow = thresholds.ReferenceRow,
            Opponents = thresholds.Opponents,
            Primary = thresholds.Primary,
            WarmupSeconds = warmupSeconds,
            Clock = thresholds.Clock,
            Passes = passes,
        };
        return true;
    }

    /// <summary>
    /// The document this record writes, ending in exactly one newline: two-space indentation, members in
    /// the order a reader needs them, and the arm durations in the plan's own order.
    /// </summary>
    /// <returns>The file's text, with its trailing newline.</returns>
    public string ToJson()
    {
        var seconds = VerbatimJson.Object(
            2,
            [.. RowProfiles.PlanArms["full"].Select(arm => (arm, VerbatimJson.Integer(Caliber.Seconds[arm])))]);
        var passes = VerbatimJson.Object(
            1,
            ("total", VerbatimJson.Integer(Passes.Total)),
            ("discarded", Integers(2, Passes.Discarded)),
            ("measured", Integers(2, Passes.Measured)),
            ("n", VerbatimJson.Integer(Passes.N)),
            ("required", VerbatimJson.Integer(Passes.Required)),
            ("insufficient", VerbatimJson.Boolean(Passes.Insufficient)));
        var document = VerbatimJson.Object(
            0,
            ("schema", VerbatimJson.String(SchemaName)),
            ("thresholds", VerbatimJson.Object(
                1,
                ("path", VerbatimJson.String(ThresholdsPath)),
                ("sha256", VerbatimJson.String(ThresholdsSha256)))),
            ("caliber", VerbatimJson.Object(
                1,
                ("path", VerbatimJson.String(Caliber.Path)),
                ("sha256", VerbatimJson.String(CaliberSha256)),
                ("seconds", seconds))),
            ("referenceRow", VerbatimJson.String(ReferenceRow)),
            ("opponents", VerbatimJson.StringArray(1, Opponents)),
            ("primary", VerbatimJson.Array(1, [.. Primary.Select(RenderPrimary)])),
            ("warmupSeconds", VerbatimJson.Number(WarmupSeconds)),
            ("passes", passes));
        return document + "\n";
    }

    private static bool MatchesWarmup(ThresholdsFile thresholds, double warmupSeconds, out string? error)
    {
        error = null;
        if (thresholds.WarmupSeconds is not { } declared)
        {
            return true;
        }

        var declaredText = VerbatimJson.Number(declared);
        var usedText = VerbatimJson.Number(warmupSeconds);
        if (string.Equals(declaredText, usedText, StringComparison.Ordinal))
        {
            return true;
        }

        error = string.Concat(
            ThresholdsJson.Prefix,
            "'warmupSeconds' is ",
            declaredText,
            ", but this run trims ",
            usedText,
            " s (--warmup-seconds); the calibrated window has to be the window the run analyses");
        return false;
    }

    private static bool TryVerifyCaliber(
        CaliberDeclaration caliber,
        string workingDirectory,
        out string verified,
        out string? error)
    {
        verified = string.Empty;
        string resolved;
        try
        {
            resolved = Path.GetFullPath(caliber.Path, workingDirectory);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or IOException)
        {
            error = $"{ThresholdsJson.Prefix}'caliber.path' is '{caliber.Path}', which cannot be resolved: {exception.Message}";
            return false;
        }

        if (!File.Exists(resolved))
        {
            error = string.Concat(
                ThresholdsJson.Prefix,
                "'caliber.path' names '",
                caliber.Path,
                "', which does not exist under '",
                workingDirectory,
                "'");
            return false;
        }

        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(resolved);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            error = $"{ThresholdsJson.Prefix}cannot read caliber plan '{caliber.Path}': {exception.Message}";
            return false;
        }

        verified = Convert.ToHexStringLower(SHA256.HashData(bytes));
        if (!string.Equals(verified, caliber.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            error = string.Concat(
                ThresholdsJson.Prefix,
                "'caliber.sha256' is '",
                caliber.Sha256,
                "', but '",
                caliber.Path,
                "' hashes to '",
                verified,
                "'");
            return false;
        }

        return TryCheckDurations(bytes, caliber, out error);
    }

    /// <summary>
    /// The declared arm durations against the plan the run just hashed: the digest pins the plan's
    /// bytes, and this pins the report metadata to those same bytes, so a mistyped MIX or REL cannot be
    /// printed beside numbers the plan did not declare.
    /// </summary>
    private static bool TryCheckDurations(byte[] planBytes, CaliberDeclaration caliber, out string? error)
    {
        if (!TryReadPlanDurations(planBytes, caliber.Path, out var durations, out error) || durations is null)
        {
            return false;
        }

        foreach (var arm in RowProfiles.PlanArms["full"])
        {
            if (!durations.TryGetValue(arm, out var seconds))
            {
                error = $"{ThresholdsJson.Prefix}'caliber.seconds.{arm}' is declared, but '{caliber.Path}' has no arm '{arm}'";
                return false;
            }

            if (seconds != caliber.Seconds[arm])
            {
                error = string.Create(
                    CultureInfo.InvariantCulture,
                    $"{ThresholdsJson.Prefix}'caliber.seconds.{arm}' is {caliber.Seconds[arm]}, but '{caliber.Path}' declares {seconds}");
                return false;
            }
        }

        error = null;
        return true;
    }

    private static bool TryReadPlanDurations(
        byte[] planBytes,
        string planPath,
        out Dictionary<string, int>? durations,
        out string? error)
    {
        durations = null;
        error = null;
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(planBytes);
        }
        catch (JsonException exception)
        {
            error = $"{ThresholdsJson.Prefix}caliber plan '{planPath}' is not valid JSON: {exception.Message}";
            return false;
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = $"{ThresholdsJson.Prefix}'caliber.path' names '{planPath}', whose root is not a JSON object";
                return false;
            }

            if (!document.RootElement.TryGetProperty("arms", out var arms) || arms.ValueKind != JsonValueKind.Array)
            {
                error = $"{ThresholdsJson.Prefix}'caliber.seconds' cannot be checked: '{planPath}' has no 'arms' array";
                return false;
            }

            var declared = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var arm in arms.EnumerateArray())
            {
                if (arm.ValueKind != JsonValueKind.Object
                    || !arm.TryGetProperty("name", out var nameElement)
                    || nameElement.ValueKind != JsonValueKind.String
                    || !arm.TryGetProperty("seconds", out var secondsElement))
                {
                    error = $"{ThresholdsJson.Prefix}'caliber.seconds' cannot be checked: '{planPath}' holds an arm without a name and seconds";
                    return false;
                }

                var name = nameElement.GetString() ?? string.Empty;
                if (!TryReadPlanSeconds(secondsElement, name, planPath, out var seconds, out error))
                {
                    return false;
                }

                declared[name] = seconds;
            }

            durations = declared;
            return true;
        }
    }

    private static bool TryReadPlanSeconds(
        JsonElement element,
        string arm,
        string planPath,
        out int duration,
        out string? error)
    {
        duration = 0;
        if (element.ValueKind != JsonValueKind.Number
            || !element.TryGetDouble(out var seconds)
            || seconds <= 0
            || seconds > int.MaxValue
            || Math.Abs(seconds - Math.Round(seconds, MidpointRounding.AwayFromZero)) > 0)
        {
            error = $"{ThresholdsJson.Prefix}'{planPath}' declares arm '{arm}' with {element.GetRawText()} seconds, which is not a positive integer";
            return false;
        }

        duration = (int)Math.Round(seconds, MidpointRounding.AwayFromZero);
        error = null;
        return true;
    }

    private static string RenderPrimary(PrimaryMetric metric) => VerbatimJson.Object(
        2,
        ("metric", VerbatimJson.String(metric.Metric)),
        ("kind", VerbatimJson.String(metric.Kind)),
        ("band", VerbatimJson.Number(metric.Band)),
        ("business", metric.Business is { } business ? VerbatimJson.Number(business) : VerbatimJson.Null),
        ("mdd", metric.Mdd is { } mdd ? VerbatimJson.Number(mdd) : VerbatimJson.Null));

    private static string Integers(int level, IReadOnlyList<int> values) =>
        VerbatimJson.Array(level, [.. values.Select(value => VerbatimJson.Integer(value))]);
}
