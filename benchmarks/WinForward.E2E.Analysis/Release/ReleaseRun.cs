namespace WinForward.E2E.Analysis.Release;

/// <summary>What one release invocation produced, or why it refused before producing a verdict.</summary>
/// <param name="ExitCode">The process exit code the analysis returns: 0 for a built record, 2 for a refusal.</param>
/// <param name="Inputs">The provenance record, when the run's inputs agreed.</param>
/// <param name="Error">The refusal, when they did not.</param>
public sealed record ReleaseOutcome(int ExitCode, ReleaseInputs? Inputs, string? Error);

/// <summary>
/// The release path's single entry point: read the thresholds file, apply the invocation's overrides,
/// verify the caliber and select the measured passes.
/// </summary>
/// <remarks>
/// <b>The exit code is part of the seam.</b> A release refusal is an input error — the caller can fix
/// the file, the flag or the tree — and the analysis returns 2 for it just as it does for a missing
/// campaign tree. Returning the code from the same call that builds the record keeps the CLI from
/// having to interpret a nullable record as success: there is no state in which the process writes a
/// <c>release-inputs.json</c> for inputs that were not accepted.
/// </remarks>
public static class ReleaseRun
{
    /// <summary>The exit code the analysis uses for an invocation or an input it refuses.</summary>
    public const int InputError = 2;

    /// <summary>
    /// Runs the release input path. The caller has already loaded the campaign, so the pass ids and the
    /// warm-up window are the ones the legacy path is using.
    /// </summary>
    /// <param name="options">The release flags the invocation declared.</param>
    /// <param name="warmupSeconds">The warm-up window the run uses.</param>
    /// <param name="passIds">The campaign's pass ids.</param>
    /// <param name="workingDirectory">
    /// The directory <c>caliber.path</c> is resolved from. A relative <c>--thresholds</c> path is opened
    /// from the process's working directory, exactly as the invocation spelled it; for a campaign that
    /// directory and this one are the same, because <c>scripts/analyze.sh</c> changes neither.
    /// </param>
    /// <returns>The built record, or the refusal and its exit code.</returns>
    /// <exception cref="ArgumentException">The invocation is not a release run, which the caller's parse already refused.</exception>
    public static ReleaseOutcome Execute(
        ReleaseOptions options,
        double warmupSeconds,
        IReadOnlyList<string> passIds,
        string workingDirectory)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrEmpty(workingDirectory);

        if (!options.Release || options.ThresholdsPath is null)
        {
            throw new ArgumentException("the release path needs both '--release' and '--thresholds'", nameof(options));
        }

        if (ThresholdsFile.TryParse(options.ThresholdsPath, out var thresholds, out var error)
            && thresholds is not null
            && ReleaseInputs.TryBuild(
                thresholds,
                options,
                warmupSeconds,
                passIds,
                workingDirectory,
                out var inputs,
                out error)
            && inputs is not null)
        {
            return new ReleaseOutcome(ExitCode: 0, Inputs: inputs, Error: null);
        }

        return new ReleaseOutcome(ExitCode: InputError, Inputs: null, Error: error);
    }
}
