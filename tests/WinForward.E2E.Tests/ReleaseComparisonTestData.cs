using WinForward.E2E.Analysis.Release;
using Xunit;

namespace WinForward.E2E.Tests;

/// <summary>
/// Builds synthetic release datasets and inputs for the comparison tests, so a case states only the
/// per-pass values and the thresholds fixture it wants and not the records around them.
/// </summary>
internal static class ReleaseComparisonTestData
{
    internal static ThresholdsFile ParseFixture(string name)
    {
        var parsed = ThresholdsFile.TryParse(RepoPaths.ThresholdFixture(name), out var file, out var error);
        Assert.True(parsed, error);
        Assert.NotNull(file);
        return file;
    }

    internal static ReleaseInputs Inputs(
        ThresholdsFile thresholds,
        IReadOnlyList<string> passIds,
        int required,
        int discardPass = 0)
    {
        var selected = ReleasePasses.TrySelect(passIds, discardPass, required, out var passes, out var error);
        Assert.True(selected, error);
        Assert.NotNull(passes);
        return new ReleaseInputs
        {
            ThresholdsPath = thresholds.Path,
            ThresholdsSha256 = thresholds.Sha256,
            Caliber = thresholds.Caliber,
            CaliberSha256 = thresholds.Caliber.Sha256,
            ReferenceRow = thresholds.ReferenceRow,
            Opponents = thresholds.Opponents,
            Primary = thresholds.Primary,
            WarmupSeconds = thresholds.WarmupSeconds ?? 5.0,
            Passes = passes,
        };
    }

    internal static ReleaseDataset Dataset(IReadOnlyList<string> passIds, params RowBuilder[] rows) =>
        new()
        {
            MeasuredPasses = passIds,
            Rows = [.. rows.Select(row => row.Build())],
            Resamples = 2000,
            Seed = 20261006,
        };

    internal static RowBuilder Row(string rowId, IReadOnlyList<string> passIds) => new(rowId, passIds);

    internal static string[] Passes(int count) =>
        [.. Enumerable.Range(1, count).Select(number => $"pass{number}")];

    internal static double[] Series(double centre, double relativeJitter, int count) =>
        [.. Enumerable.Range(0, count).Select(index => centre * (1.0 + (relativeJitter * ((index % 5) - 2) / 2.0)))];

    internal sealed class RowBuilder
    {
        private readonly string _rowId;

        private readonly IReadOnlyList<string> _passIds;

        private readonly Dictionary<string, ReleaseMetricReadings> _metrics = new(StringComparer.Ordinal);

        internal RowBuilder(string rowId, IReadOnlyList<string> passIds)
        {
            _rowId = rowId;
            _passIds = passIds;
        }

        internal RowBuilder With(string metric, double[] values)
        {
            Assert.Equal(_passIds.Count, values.Length);
            _metrics[metric] = new ReleaseMetricReadings
            {
                Values = _passIds
                    .Select((passId, index) => (PassId: passId, Value: values[index]))
                    .ToDictionary(entry => entry.PassId, entry => entry.Value, StringComparer.Ordinal),
            };
            return this;
        }

        internal RowBuilder WithCounts(string metric, (long Events, long Attempts)[] counts)
        {
            Assert.Equal(_passIds.Count, counts.Length);
            _metrics[metric] = new ReleaseMetricReadings
            {
                Values = _passIds.ToDictionary(passId => passId, _ => 0.0, StringComparer.Ordinal),
                Counts = _passIds
                    .Select((passId, index) => (PassId: passId, Count: counts[index]))
                    .ToDictionary(
                        entry => entry.PassId,
                        entry => new ReleaseCount(entry.Count.Events, entry.Count.Attempts),
                        StringComparer.Ordinal),
            };
            return this;
        }

        internal RowBuilder WithStatus(string metric, string status, string? reason = null)
        {
            _metrics[metric] = new ReleaseMetricReadings
            {
                Values = new Dictionary<string, double>(StringComparer.Ordinal),
                Status = status,
                StatusReason = reason,
                MissingPasses = _passIds.Count,
            };
            return this;
        }

        internal ReleaseRowReadings Build() => new(_rowId, _metrics);
    }
}
