using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using WinForward.E2E.Analysis.Model;

namespace WinForward.E2E.Analysis.Release;

/// <summary>
/// The pre-registered thresholds file a release run is judged against: the caliber, the reference row,
/// the inferential family, the capability gates and the pass-count rules, plus the file's own SHA-256.
/// </summary>
/// <remarks>
/// <para><b>Parsing is the validation.</b> There is no partially accepted document: a file with an
/// unknown key, a missing required member, a band at or below zero, an undeclared row, an equivalence
/// family outside the inferential family or a rule that contradicts the analysis's own catalogue is
/// refused whole, and the error names the field as a dotted path so the caller can find it. A release
/// claim is only as good as the file behind it, and a parser that filled a default in for a typo would
/// turn a decision into an accident.</para>
/// <para><b>The hash is over the file's bytes.</b> It is computed here, once, so the artifact that
/// records the run and the report that quotes it name the same digest; formatting the file again (a
/// trailing newline, a re-indent) is a different file and is correctly a different digest.</para>
/// <para><b>What the file does not declare is not invented.</b> <see cref="Exploratory"/> is resolved to
/// the published metrics neither the primary family nor the capability gates claim, because the report
/// must account for every metric; the pass-count members stay nullable so the invocation's own override
/// can apply before the documented defaults do.</para>
/// </remarks>
public sealed record ThresholdsFile
{
    /// <summary>The only schema version this analysis understands.</summary>
    public const int SupportedVersion = 1;

    private static readonly string[] s_rootKeys =
    [
        "version",
        "caliber",
        "referenceRow",
        "opponents",
        "primary",
        "exploratory",
        "capability",
        "equivalence",
        "minPasses",
        "discardPass",
        "warmupSeconds",
        "clock",
    ];

    private static readonly string[] s_caliberKeys = ["path", "sha256", "seconds"];

    private static readonly string[] s_clockKeys = ["require", "maxDriftMs"];

    private static readonly string[] s_releaseArms = [.. RowProfiles.PlanArms["full"]];

    /// <summary>The path the caller named, kept as it was spelled so an error can quote it.</summary>
    public required string Path { get; init; }

    /// <summary>The SHA-256 of the file's raw bytes, lower-case hexadecimal.</summary>
    public required string Sha256 { get; init; }

    /// <summary>The schema version the file declared, always <see cref="SupportedVersion"/>.</summary>
    public required int Version { get; init; }

    /// <summary>The release caliber the file was calibrated for.</summary>
    public required CaliberDeclaration Caliber { get; init; }

    /// <summary>The row every primary comparison is drawn against.</summary>
    public required string ReferenceRow { get; init; }

    /// <summary>The rows the reference is compared with, in the file's order.</summary>
    public required IReadOnlyList<string> Opponents { get; init; }

    /// <summary>The inferential family, in the file's order.</summary>
    public required IReadOnlyList<PrimaryMetric> Primary { get; init; }

    /// <summary>Every published metric that makes no claim, in the catalogue's order when the file omitted the list.</summary>
    public required IReadOnlyList<string> Exploratory { get; init; }

    /// <summary>The capability gates, keyed by metric.</summary>
    public required IReadOnlyDictionary<string, CapabilityRule> Capability { get; init; }

    /// <summary>The pre-registered TOST families; empty unless the owner declared one.</summary>
    public required IReadOnlyList<EquivalenceFamily> Equivalence { get; init; }

    /// <summary>The measured-pass floor the file declares, or null when it leaves the choice to the invocation.</summary>
    public int? MinPasses { get; private init; }

    /// <summary>The pass the file declares discarded, or null when it declares none.</summary>
    public int? DiscardPass { get; private init; }

    /// <summary>The per-arm warm-up window the calibration used, or null when it declared none.</summary>
    public double? WarmupSeconds { get; private init; }

    /// <summary>What the clock correction is asked to prove.</summary>
    public required ClockRule Clock { get; init; }

    /// <summary>
    /// Reads one thresholds file and validates it, returning false with a field-naming reason when the
    /// document cannot be the contract behind a release claim.
    /// </summary>
    /// <param name="path">The file to read; a relative path is resolved from the process's working directory.</param>
    /// <param name="file">The parsed contract, when it is accepted.</param>
    /// <param name="error">Why it was refused, when it was.</param>
    /// <returns>True when the whole document was accepted.</returns>
    public static bool TryParse(string path, out ThresholdsFile? file, out string? error)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        file = null;
        if (!TryReadBytes(path, out var bytes, out error)
            || !TryParseDocument(bytes, out var document, out error)
            || document is null)
        {
            return false;
        }

        using (document)
        {
            if (!ThresholdsJson.TryMembers(document.RootElement, string.Empty, s_rootKeys, out var root, out error)
                || root is null)
            {
                return false;
            }

            return TryBuild(root, path, Convert.ToHexStringLower(SHA256.HashData(bytes)), out file, out error);
        }
    }

    private static bool TryBuild(
        Dictionary<string, JsonElement> root,
        string path,
        string sha256,
        out ThresholdsFile? file,
        out string? error)
    {
        file = null;
        if (!TryReadVersion(root, out var version, out error))
        {
            return false;
        }

        if (!TryReadCaliber(root, out var caliber, out error) || caliber is null)
        {
            return false;
        }

        if (!TryReadReference(root, out var referenceRow, out error))
        {
            return false;
        }

        if (!TryReadOpponents(root, referenceRow, out var opponents, out error) || opponents is null)
        {
            return false;
        }

        if (!TryReadFamily(root, out var primary, out var capability, out var exploratory, out var equivalence, out error)
            || primary is null
            || capability is null
            || exploratory is null
            || equivalence is null)
        {
            return false;
        }

        if (!TryReadLimits(root, out var minPasses, out var discardPass, out var warmupSeconds, out var clock, out error)
            || clock is null)
        {
            return false;
        }

        file = new ThresholdsFile
        {
            Path = path,
            Sha256 = sha256,
            Version = version,
            Caliber = caliber,
            ReferenceRow = referenceRow,
            Opponents = opponents,
            Primary = primary,
            Exploratory = exploratory,
            Capability = capability,
            Equivalence = equivalence,
            MinPasses = minPasses,
            DiscardPass = discardPass,
            WarmupSeconds = warmupSeconds,
            Clock = clock,
        };
        return true;
    }

    private static bool TryReadFamily(
        Dictionary<string, JsonElement> root,
        out List<PrimaryMetric>? primary,
        out Dictionary<string, CapabilityRule>? capability,
        out List<string>? exploratory,
        out List<EquivalenceFamily>? equivalence,
        out string? error)
    {
        primary = null;
        capability = null;
        exploratory = null;
        equivalence = null;
        if (!ThresholdsMetrics.TryReadPrimary(root, out primary, out error) || primary is null)
        {
            return false;
        }

        if (!ThresholdsMetrics.TryReadCapability(root, out capability, out error) || capability is null)
        {
            return false;
        }

        if (!TryCheckClaims(primary, capability, out error))
        {
            return false;
        }

        if (!ThresholdsMetrics.TryReadExploratory(root, primary, capability, out exploratory, out error) || exploratory is null)
        {
            return false;
        }

        return ThresholdsMetrics.TryReadEquivalence(root, primary, out equivalence, out error) && equivalence is not null;
    }

    /// <summary>
    /// One metric cannot be both a decision and a gate: the release verdict would have to report a
    /// difference claim and a capability bound for the same number, with no rule for which one wins.
    /// </summary>
    private static bool TryCheckClaims(
        List<PrimaryMetric> primary,
        Dictionary<string, CapabilityRule> capability,
        out string? error)
    {
        var claim = primary.Select(entry => entry.Metric).FirstOrDefault(capability.ContainsKey);
        if (claim is not null)
        {
            error = $"{ThresholdsJson.Prefix}'capability.{claim}' names a metric that is also in 'primary'";
            return false;
        }

        error = null;
        return true;
    }

    private static bool TryReadLimits(
        Dictionary<string, JsonElement> root,
        out int? minPasses,
        out int? discardPass,
        out double? warmupSeconds,
        out ClockRule? clock,
        out string? error)
    {
        minPasses = null;
        discardPass = null;
        warmupSeconds = null;
        clock = null;
        return ThresholdsJson.TryReadOptionalInteger(
                root,
                "minPasses",
                "minPasses",
                ReleaseOptions.MinimumMinPasses,
                out minPasses,
                out error)
            && ThresholdsJson.TryReadOptionalInteger(root, "discardPass", "discardPass", 0, out discardPass, out error)
            && ThresholdsJson.TryReadOptionalCount(root, "warmupSeconds", "warmupSeconds", out warmupSeconds, out error)
            && TryReadClock(root, out clock, out error)
            && clock is not null;
    }

    private static bool TryReadBytes(string path, out byte[] bytes, out string? error)
    {
        bytes = [];
        error = null;
        try
        {
            bytes = File.ReadAllBytes(path);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            error = $"{ThresholdsJson.Prefix}cannot read '{path}': {exception.Message}";
            return false;
        }
    }

    private static bool TryParseDocument(byte[] bytes, out JsonDocument? document, out string? error)
    {
        try
        {
            document = JsonDocument.Parse(bytes);
            error = null;
            return true;
        }
        catch (JsonException exception)
        {
            document = null;
            error = $"{ThresholdsJson.Prefix}the document is not valid JSON: {exception.Message}";
            return false;
        }
    }

    private static bool TryReadVersion(Dictionary<string, JsonElement> root, out int version, out string? error)
    {
        if (!ThresholdsJson.TryRequired(root, "version", "version", out var element, out error)
            || !ThresholdsJson.TryInt(element, "version", out version, out error))
        {
            version = 0;
            return false;
        }

        if (version == SupportedVersion)
        {
            return true;
        }

        error = string.Create(
            CultureInfo.InvariantCulture,
            $"{ThresholdsJson.Prefix}'version' is {version}, which this analysis does not support (expected {SupportedVersion})");
        return false;
    }

    private static bool TryReadCaliber(Dictionary<string, JsonElement> root, out CaliberDeclaration? caliber, out string? error)
    {
        caliber = null;
        if (!ThresholdsJson.TryRequired(root, "caliber", "caliber", out var element, out error)
            || !ThresholdsJson.TryMembers(element, "caliber", s_caliberKeys, out var members, out error)
            || members is null
            || !ThresholdsJson.TryReadRequiredString(members, "path", "caliber", out var path, out error)
            || !ThresholdsJson.TryReadRequiredString(members, "sha256", "caliber", out var sha256, out error)
            || !TryCheckSha256(sha256, out error)
            || !ThresholdsJson.TryRequired(members, "seconds", "caliber.seconds", out var secondsElement, out error)
            || !TryReadSeconds(secondsElement, out var seconds, out error)
            || seconds is null)
        {
            return false;
        }

        caliber = new CaliberDeclaration(path, sha256, seconds);
        return true;
    }

    private static bool TryCheckSha256(string digest, out string? error)
    {
        if (digest.Length != 64)
        {
            error = string.Create(
                CultureInfo.InvariantCulture,
                $"{ThresholdsJson.Prefix}'caliber.sha256' is {digest.Length} character(s), which is not the 64 of a SHA-256");
            return false;
        }

        if (!digest.All(char.IsAsciiHexDigit))
        {
            error = $"{ThresholdsJson.Prefix}'caliber.sha256' is not hexadecimal";
            return false;
        }

        error = null;
        return true;
    }

    private static bool TryReadSeconds(JsonElement element, out Dictionary<string, int>? seconds, out string? error)
    {
        seconds = null;
        if (!ThresholdsJson.TryMembers(element, "caliber.seconds", s_releaseArms, out var members, out error)
            || members is null)
        {
            return false;
        }

        var declared = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var arm in s_releaseArms)
        {
            if (!TryReadArmDuration(members, arm, out var duration, out error))
            {
                return false;
            }

            declared[arm] = duration;
        }

        seconds = declared;
        return true;
    }

    private static bool TryReadArmDuration(
        Dictionary<string, JsonElement> members,
        string arm,
        out int duration,
        out string? error)
    {
        var path = ThresholdsJson.Join("caliber.seconds", arm);
        if (!ThresholdsJson.TryRequired(members, arm, path, out var element, out error)
            || !ThresholdsJson.TryInt(element, path, out duration, out error))
        {
            duration = 0;
            return false;
        }

        if (duration > 0)
        {
            return true;
        }

        error = string.Create(CultureInfo.InvariantCulture, $"{ThresholdsJson.Prefix}'{path}' must be > 0 (was {duration})");
        return false;
    }

    private static bool TryReadReference(Dictionary<string, JsonElement> root, out string referenceRow, out string? error)
    {
        if (!ThresholdsJson.TryReadRequiredString(root, "referenceRow", string.Empty, out referenceRow, out error))
        {
            return false;
        }

        if (!RowProfiles.Declared.ContainsKey(referenceRow))
        {
            error = RowProfiles.IsCalibrationControl(referenceRow)
                ? $"{ThresholdsJson.Prefix}'referenceRow' names the calibration control '{referenceRow}', which is report-only and cannot be a product reference"
                : $"{ThresholdsJson.Prefix}'referenceRow' names an undeclared row '{referenceRow}'";
            return false;
        }

        return true;
    }

    private static bool TryReadOpponents(
        Dictionary<string, JsonElement> root,
        string referenceRow,
        out List<string>? opponents,
        out string? error)
    {
        opponents = null;
        if (!ThresholdsJson.TryRequired(root, "opponents", "opponents", out var element, out error)
            || !ThresholdsJson.RequireArray(element, "opponents", out error))
        {
            return false;
        }

        var declared = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var index = 0;
        foreach (var entry in element.EnumerateArray())
        {
            var path = ThresholdsJson.Indexed("opponents", index);
            if (!ThresholdsJson.TryString(entry, path, out var row, out error))
            {
                return false;
            }

            if (!RowProfiles.Declared.ContainsKey(row))
            {
                error = RowProfiles.IsCalibrationControl(row)
                    ? $"{ThresholdsJson.Prefix}'{path}' names the calibration control '{row}', which is report-only and cannot be a product opponent"
                    : $"{ThresholdsJson.Prefix}'{path}' names an undeclared row '{row}'";
                return false;
            }

            if (!seen.Add(row))
            {
                error = $"{ThresholdsJson.Prefix}'opponents' lists '{row}' twice";
                return false;
            }

            if (string.Equals(row, referenceRow, StringComparison.Ordinal))
            {
                error = $"{ThresholdsJson.Prefix}'opponents' lists the reference row '{row}'";
                return false;
            }

            declared.Add(row);
            index++;
        }

        opponents = declared;
        return true;
    }

    private static bool TryReadClock(Dictionary<string, JsonElement> root, out ClockRule? clock, out string? error)
    {
        clock = new ClockRule(Require: false, MaxDriftMs: ClockRule.DefaultMaxDriftMs);
        error = null;
        if (!root.TryGetValue("clock", out var element))
        {
            return true;
        }

        if (!ThresholdsJson.TryMembers(element, "clock", s_clockKeys, out var members, out error) || members is null)
        {
            clock = null;
            return false;
        }

        var require = false;
        if (members.TryGetValue("require", out var requireElement)
            && !ThresholdsJson.TryBool(requireElement, "clock.require", out require, out error))
        {
            clock = null;
            return false;
        }

        var maxDriftMs = ClockRule.DefaultMaxDriftMs;
        if (members.TryGetValue("maxDriftMs", out var driftElement)
            && (!ThresholdsJson.TryNumber(driftElement, "clock.maxDriftMs", out maxDriftMs, out error) || maxDriftMs < 0))
        {
            clock = null;
            error ??= string.Create(
                CultureInfo.InvariantCulture,
                $"{ThresholdsJson.Prefix}'clock.maxDriftMs' must be >= 0 (was {maxDriftMs})");
            return false;
        }

        clock = new ClockRule(require, maxDriftMs);
        return true;
    }
}
