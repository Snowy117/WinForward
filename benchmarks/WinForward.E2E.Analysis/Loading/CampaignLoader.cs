using System.Text.Json;
using WinForward.E2E.Analysis.Cli;
using WinForward.E2E.Analysis.Model;

namespace WinForward.E2E.Analysis.Loading;

/// <summary>
/// Finds a campaign tree's passes and rows and reads them into a <see cref="CampaignModel"/>.
/// </summary>
/// <remarks>
/// <para><b>A pass is a <c>pass*</c> directory, and a row is a directory holding a <c>run.json</c> or at
/// least one arm file.</b> The first rule is what keeps a <c>dual</c> directory or a plans folder from
/// being read as a pass; the second is what keeps a row that ran nothing from disappearing. Directories
/// are listed in natural-key order, which is the order rows are introduced in and therefore the order
/// <c>verdict.json</c>'s <c>rows</c> is derived from.</para>
/// <para><b><c>--flat</c> reads the tree one level up.</b> The immediate subdirectories of <c>--raw</c>
/// are the rows of one implicit pass called <c>flat</c>; <c>--raw</c> itself is a row when it looks like
/// one, which is the shape of a one-off run directory. The preamble says so in words and
/// <c>verdict.json</c>'s <c>flat_mode</c> says so as a boolean.</para>
/// <para><b>A pass with no rows is not a pass.</b> A tree whose <c>pass*</c> directories hold no rows at
/// all has nothing to analyse, and is refused rather than reported as an empty campaign.</para>
/// </remarks>
internal static class CampaignLoader
{
    private const string PassPrefix = "pass";

    private const string FlatPassId = "flat";

    private const string OrderFile = "order.txt";

    private const string EnvironmentFile = "environment.json";

    /// <summary>
    /// Reads the tree the options point at. Returns false with the reason on
    /// <paramref name="error"/> when there is nothing to analyse.
    /// </summary>
    internal static bool TryLoad(AnalysisOptions options, out CampaignModel? campaign, out string? error)
    {
        ArgumentNullException.ThrowIfNull(options);

        campaign = null;
        error = null;
        if (!Directory.Exists(options.Raw))
        {
            error = $"input directory not found: {options.Raw}";
            return false;
        }

        var (passes, ledgerPaths, order) = options.Flat ? DiscoverFlat(options) : DiscoverPasses(options);
        if (passes.Count == 0)
        {
            error = options.Flat
                ? $"no rows found under {options.Raw} (immediate subdirectories with run.json or *.jsonl)"
                : $"no rows found under {options.Raw} (pass*/<row>/ directories)";
            return false;
        }

        campaign = new CampaignModel
        {
            Raw = PosixPathText.Normalize(options.Raw),
            Flat = options.Flat,
            Passes = passes,
            LedgerPaths = ledgerPaths,
            Order = order,
            Environment = ReadEnvironment(options.Raw),
            WarmupSeconds = options.WarmupSeconds,
            Resamples = options.Resamples,
            Seed = options.Seed,
            MinPasses = AnalysisOptions.DefaultMinPasses,
        };
        return true;
    }

    private static (Dictionary<string, IReadOnlyList<ClientRun>> Passes, Dictionary<string, IReadOnlyList<string>> Ledgers, Dictionary<string, IReadOnlyList<string>> Order) DiscoverFlat(
        AnalysisOptions options)
    {
        var passes = new Dictionary<string, IReadOnlyList<ClientRun>>(StringComparer.Ordinal);
        var ledgers = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        var order = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

        if (LooksLikeRow(options.Raw))
        {
            var run = RunLoader.Load(FlatPassId, DirectoryName(options.Raw), options.Raw);
            RunLoader.LoadDual(run);
            passes[FlatPassId] = [run];
            AttachLedger(options, ledgers, FlatPassId, options.Raw);
            return (passes, ledgers, order);
        }

        var rows = new List<ClientRun>();
        foreach (var child in ListDirectories(options.Raw))
        {
            if (!LooksLikeRow(child))
            {
                continue;
            }

            var run = RunLoader.Load(FlatPassId, DirectoryName(child), child);
            RunLoader.LoadDual(run);
            rows.Add(run);
        }

        if (rows.Count > 0)
        {
            passes[FlatPassId] = rows;
        }

        AttachLedger(options, ledgers, FlatPassId, options.Raw);
        return (passes, ledgers, order);
    }

    private static (Dictionary<string, IReadOnlyList<ClientRun>> Passes, Dictionary<string, IReadOnlyList<string>> Ledgers, Dictionary<string, IReadOnlyList<string>> Order) DiscoverPasses(
        AnalysisOptions options)
    {
        var passes = new Dictionary<string, IReadOnlyList<ClientRun>>(StringComparer.Ordinal);
        var ledgers = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        var order = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

        foreach (var passDirectory in ListDirectories(options.Raw))
        {
            var passId = DirectoryName(passDirectory);
            if (!passId.StartsWith(PassPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            var rows = new List<ClientRun>();
            string? passDual = null;
            foreach (var rowDirectory in ListDirectories(passDirectory))
            {
                if (string.Equals(DirectoryName(rowDirectory), RunLoader.DualDirectory, StringComparison.Ordinal))
                {
                    passDual = rowDirectory;
                    continue;
                }

                if (!LooksLikeRow(rowDirectory))
                {
                    continue;
                }

                var run = RunLoader.Load(passId, DirectoryName(rowDirectory), rowDirectory);
                RunLoader.LoadDual(run);
                rows.Add(run);
            }

            AttachPassDual(passDual, rows);
            if (rows.Count > 0)
            {
                passes[passId] = rows;
            }

            AttachLedger(options, ledgers, passId, passDirectory);
            if (ReadOrder(passDirectory) is { Count: > 0 } ran)
            {
                order[passId] = ran;
            }
        }

        return (passes, ledgers, order);
    }

    /// <summary>The rows <c>order.txt</c> says the pass ran, or null when the file is absent or unreadable.</summary>
    private static List<string>? ReadOrder(string passDirectory)
    {
        var path = PosixPathText.Join(passDirectory, OrderFile);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return
            [
                .. File.ReadAllText(path, System.Text.Encoding.UTF8)
                    .Trim()
                    .Split([' ', '\t', '\r', '\n', ','], StringSplitOptions.RemoveEmptyEntries),
            ];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// The campaign's <c>environment.json</c>: the one beside the pass directories, then the one beside
    /// <c>--raw</c> itself; the first that parses wins.
    /// </summary>
    private static JsonElement? ReadEnvironment(string raw)
    {
        var candidate = new[]
        {
            PosixPathText.Join(raw, EnvironmentFile),
            PosixPathText.Join(PosixPathText.Parent(raw), EnvironmentFile),
        }.FirstOrDefault(File.Exists);
        return candidate is null ? null : JsonReader.ReadFile(candidate, out _);
    }

    /// <summary>
    /// Offers a pass-level dual directory to the rows that have no lanes of their own, in row order,
    /// stopping at the row the lanes name.
    /// </summary>
    private static void AttachPassDual(string? passDual, IReadOnlyList<ClientRun> rows)
    {
        if (passDual is null)
        {
            return;
        }

        var claimed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (row.DualProxied is null && row.DualDirect is null)
            {
                continue;
            }

            claimed.Add(row.RunId);
        }

        foreach (var row in rows)
        {
            if (claimed.Contains(row.RunId))
            {
                continue;
            }

            if (string.Equals(RunLoader.AttachDual(row, passDual), row.RunId, StringComparison.Ordinal))
            {
                break;
            }
        }
    }

    private static void AttachLedger(
        AnalysisOptions options,
        Dictionary<string, IReadOnlyList<string>> ledgers,
        string passId,
        string passDirectory)
    {
        var paths = LedgerLocator.Locate(options, passDirectory);
        if (paths.Count > 0)
        {
            ledgers[passId] = paths;
        }
    }

    /// <summary>Whether a directory is a row: it holds a <c>run.json</c> or at least one arm file.</summary>
    private static bool LooksLikeRow(string directory) =>
        Directory.Exists(directory)
        && (File.Exists(PosixPathText.Join(directory, RunLoader.RunFile)) || PythonGlob.ArmFiles(directory).Count > 0);

    /// <summary>The immediate subdirectories of one directory, in natural-key order.</summary>
    private static List<string> ListDirectories(string directory)
    {
        var children = new List<string>();
        var byName = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var child in Directory.EnumerateDirectories(directory))
        {
            var name = DirectoryName(child);
            children.Add(name);
            byName[name] = child;
        }

        return [.. NaturalKey.Sort(children).Select(name => byName[name])];
    }

    /// <summary>A directory's own name, with no trailing separator.</summary>
    private static string DirectoryName(string directory) =>
        Path.GetFileName(Path.TrimEndingDirectorySeparator(directory));
}
