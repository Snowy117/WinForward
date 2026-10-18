using System.Globalization;

namespace WinForward.E2E.Analysis.Release;

/// <summary>Validates the release inputs and the dataset before a comparison reads a value.</summary>
/// <remarks>
/// These checks are programmer contracts, not release refusals: the CLI's input path already refused a
/// campaign whose pass tree disagreed with the thresholds file. A failure here means the caller paired a
/// thresholds file with an unrelated dataset, and the exception is the only correct outcome.
/// </remarks>
internal static class ReleaseComparisonValidation
{
    /// <summary>Checks that the inputs are the record built from this thresholds file.</summary>
    /// <param name="thresholds">The parsed thresholds file.</param>
    /// <param name="inputs">The release inputs.</param>
    internal static void InputsAgree(ThresholdsFile thresholds, ReleaseInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(thresholds);
        ArgumentNullException.ThrowIfNull(inputs);

        if (!string.Equals(inputs.ReferenceRow, thresholds.ReferenceRow, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"the release inputs name reference row '{inputs.ReferenceRow}', but the thresholds file names '{thresholds.ReferenceRow}'",
                nameof(inputs));
        }

        if (!inputs.Opponents.SequenceEqual(thresholds.Opponents, StringComparer.Ordinal))
        {
            throw new ArgumentException("the release inputs' opponents are not the thresholds file's opponents", nameof(inputs));
        }

        if (!inputs.Primary.SequenceEqual(thresholds.Primary))
        {
            throw new ArgumentException("the release inputs' primary family is not the thresholds file's primary family", nameof(inputs));
        }
    }

    /// <summary>Checks the dataset's pass count, pass identity and row identity.</summary>
    /// <param name="inputs">The release inputs.</param>
    /// <param name="dataset">The per-pass readings.</param>
    internal static void Dataset(ReleaseInputs inputs, ReleaseDataset dataset)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(dataset);

        if (dataset.Resamples < 1)
        {
            throw new ArgumentException("the release dataset needs at least one resample", nameof(dataset));
        }

        if (dataset.MeasuredPasses.Count == 0)
        {
            throw new ArgumentException("the release dataset holds no measured pass", nameof(dataset));
        }

        EnsurePassCounts(inputs);
        EnsureMeasuredPasses(inputs, dataset);
        EnsureRows(dataset);
    }

    private static void EnsurePassCounts(ReleaseInputs inputs)
    {
        if (inputs.Passes.N != inputs.Passes.Measured.Count || inputs.Passes.N == 0)
        {
            throw new ArgumentException("the release inputs' measured pass count does not match their measured pass list", nameof(inputs));
        }

        if (inputs.Passes.Required < ReleaseOptions.MinimumMinPasses)
        {
            throw new ArgumentException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"the release inputs require {inputs.Passes.Required} passes, below the minimum of {ReleaseOptions.MinimumMinPasses}"),
                nameof(inputs));
        }

        var duplicate = inputs.Passes.Measured
            .GroupBy(number => number)
            .FirstOrDefault(group => group.Skip(1).Any())?.Key;
        if (duplicate is not null)
        {
            throw new ArgumentException(
                string.Create(CultureInfo.InvariantCulture, $"the release inputs list pass {duplicate} twice"),
                nameof(inputs));
        }
    }

    private static void EnsureMeasuredPasses(ReleaseInputs inputs, ReleaseDataset dataset)
    {
        var numbers = dataset.MeasuredPasses
            .Select(passId => ParsePass(passId, nameof(dataset)))
            .ToList();
        var duplicate = numbers
            .GroupBy(number => number)
            .FirstOrDefault(group => group.Skip(1).Any())?.Key;
        if (duplicate is not null)
        {
            throw new ArgumentException(
                string.Create(CultureInfo.InvariantCulture, $"the release dataset lists pass {duplicate} twice"),
                nameof(dataset));
        }

        if (!numbers.ToHashSet().SetEquals(inputs.Passes.Measured))
        {
            throw new ArgumentException("the release dataset's measured passes are not the release inputs' measured passes", nameof(dataset));
        }
    }

    private static void EnsureRows(ReleaseDataset dataset)
    {
        var duplicate = dataset.Rows
            .GroupBy(row => row.RowId, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Skip(1).Any())?.Key;
        if (duplicate is not null)
        {
            throw new ArgumentException(
                $"the release dataset lists row '{duplicate}' twice",
                nameof(dataset));
        }
    }

    private static int ParsePass(string passId, string parameterName) =>
        ReleasePasses.TryNumber(passId, out var number)
            ? number
            : throw new ArgumentException(
                $"the release dataset holds an unnumbered pass '{passId}'",
                parameterName);
}
