using System.Text.Json;
using WinForward.E2E.Analysis.Model;
using WinForward.E2E.Contracts;

namespace WinForward.E2E.Analysis.Loading;

/// <summary>
/// Reads one run directory — a row of a pass, a dual lane, or a flat tree — into a
/// <see cref="ClientRun"/>.
/// </summary>
/// <remarks>
/// <para><b>The roster decides which arms exist, the directory decides what they are called.</b>
/// <c>run.json</c> names each arm and the file it wrote; a file the roster does not mention is still
/// loaded, under its own name, because a campaign that wrote an unplanned file did measure something.
/// Arms are loaded in the reference's own order so the sample series and every derived list follow it.</para>
/// <para><b>A lane is loaded differently, and deliberately.</b> <see cref="LoadDual"/> attaches the
/// dual lanes to the row their <em>labels</em> name, not to the row whose directory holds them: the
/// shipped orchestrator writes one <c>&lt;pass&gt;/dual</c> for the whole pass and rebuilds it for every
/// dual row, so the directory a lane sits in says nothing about which row it belongs to. The
/// pass-level directory is offered to the rows that have no lanes of their own, in row order, until the
/// row the lanes name is reached — which is also why an earlier row can end up holding a copy.</para>
/// </remarks>
internal static class RunLoader
{
    /// <summary>The run's own file; its absence is reported as a load error rather than thrown.</summary>
    internal const string RunFile = "run.json";

    /// <summary>The target's own verdict, written beside the run.</summary>
    private const string ProxyTruthFile = "proxy-truth.json";

    /// <summary>The subdirectory holding one dual row's two lanes.</summary>
    internal const string DualDirectory = "dual";

    private const string JsonlSuffix = ".jsonl";

    private const string LaneProxied = "proxied";

    private const string LaneDirect = "direct";

    private const string DualLabelMarker = "-dual-";

    private static readonly string[] s_lanes = [LaneProxied, LaneDirect];

    /// <summary>
    /// Loads one run directory.
    /// </summary>
    /// <param name="passId">The pass the run belongs to.</param>
    /// <param name="runId">The row id, as the directory spells it.</param>
    /// <param name="directory">The run's directory.</param>
    /// <param name="requireTruth">
    /// Whether a missing <c>proxy-truth.json</c> is a load error. A lane's truth lives one level up, in
    /// the dual directory, so a lane is loaded without requiring its own.
    /// </param>
    /// <param name="isLane">Whether this is a dual lane: a lane publishes no config files of its own.</param>
    internal static ClientRun Load(string passId, string runId, string directory, bool requireTruth = true, bool isLane = false)
    {
        ArgumentNullException.ThrowIfNull(passId);
        ArgumentNullException.ThrowIfNull(runId);
        ArgumentNullException.ThrowIfNull(directory);

        var run = new ClientRun(passId, runId, directory);
        run.Document = ReadDocument(run);
        LoadArms(run);

        var truth = PosixPathText.Join(directory, ProxyTruthFile);
        if (File.Exists(truth))
        {
            run.ProxyTruth = ReadJsonFile(run, truth, ProxyTruthFile);
        }
        else if (requireTruth && !isLane)
        {
            run.LoadErrors.Add("no " + ProxyTruthFile);
        }

        if (!isLane)
        {
            LoadConfigs(run, directory);
        }

        return run;
    }

    /// <summary>Reads the row's <c>config*</c> files and digests them.</summary>
    private static void LoadConfigs(ClientRun run, string directory)
    {
        foreach (var path in PythonGlob.ConfigFiles(directory))
        {
            try
            {
                var bytes = File.ReadAllBytes(path);
                var digest = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes))[..12];
                run.Configs.Add(new ConfigFile(Path.GetFileName(path), digest, bytes.LongLength));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                run.Configs.Add(new ConfigFile(Path.GetFileName(path), "unreadable", 0));
            }
        }
    }

    /// <summary>Attaches the row's own <c>dual</c> subdirectory, when it has one.</summary>
    internal static void LoadDual(ClientRun row)
    {
        ArgumentNullException.ThrowIfNull(row);

        var directory = PosixPathText.Join(row.Directory, DualDirectory);
        if (Directory.Exists(directory))
        {
            AttachDual(row, directory);
        }
    }

    /// <summary>
    /// Attaches the lanes of one dual directory to <paramref name="row"/>, and returns the row id the
    /// lanes' labels name — or null when no label names one.
    /// </summary>
    internal static string? AttachDual(ClientRun row, string directory)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(directory);

        var lanes = new List<(string Lane, ClientRun Run)>(2);
        foreach (var lane in s_lanes)
        {
            var laneDirectory = PosixPathText.Join(directory, lane);
            if (Directory.Exists(laneDirectory))
            {
                lanes.Add((lane, Load(row.PassId, row.RunId + "/" + lane, laneDirectory, requireTruth: false, isLane: true)));
            }
        }

        var owner = Owner(lanes);
        foreach (var (lane, laneRun) in lanes)
        {
            if (owner is not null && !string.Equals(owner, row.RunId, StringComparison.Ordinal))
            {
                laneRun.RunId = owner + "/" + lane;
            }

            if (string.Equals(lane, LaneProxied, StringComparison.Ordinal))
            {
                row.DualProxied = laneRun;
            }
            else
            {
                row.DualDirect = laneRun;
            }
        }

        if (lanes.Count > 0)
        {
            row.DualTruth = ReadJsonFile(row, PosixPathText.Join(directory, ProxyTruthFile), DualDirectory + "/" + ProxyTruthFile);
            if (row.DualTruth is null)
            {
                row.LoadErrors.Add("no " + DualDirectory + "/" + ProxyTruthFile);
            }
        }

        return owner;
    }

    /// <summary>The row id the lanes' labels name, taken from the first label that names one.</summary>
    private static string? Owner(IReadOnlyList<(string Lane, ClientRun Run)> lanes)
    {
        foreach (var (_, run) in lanes)
        {
            var label = run.Label;
            var marker = label?.IndexOf(DualLabelMarker, StringComparison.Ordinal) ?? -1;
            if (marker >= 0)
            {
                return label![..marker];
            }
        }

        return null;
    }

    private static JsonElement? ReadDocument(ClientRun run)
    {
        var document = ReadJsonFile(run, PosixPathText.Join(run.Directory, RunFile), RunFile);
        if (document is not { ValueKind: JsonValueKind.Object } value || !value.EnumerateObject().Any())
        {
            run.LoadErrors.Add("no " + RunFile);
            return null;
        }

        return document;
    }

    /// <summary>
    /// One JSON file as the reference's <c>_load_json_file</c> answers it, with the load error recorded
    /// on <paramref name="run"/>.
    /// </summary>
    /// <remarks>
    /// <para>A document that is just <see langword="null"/> reads as no value here, because the reference
    /// parses it into Python's <c>None</c> and every caller tests <c>is None</c> — the run has no truth,
    /// §15 prints <c>no</c>, and a dual truth reads as <c>no dual/proxy-truth.json</c>. A
    /// <see cref="JsonElement"/> cannot carry that distinction for this model, so the file's own
    /// <see langword="null"/> and its absence are answered the same way, which is what the reference does
    /// with them.</para>
    /// <para>The note names the failure the way the reference's <c>exc.__class__.__name__</c> would: the
    /// two implementations raise different exception types for the same unreadable file, and this string
    /// is part of §15's compared bytes. A truncated <c>run.json</c> is reachable — the harness creates it
    /// in place, so an interrupted write leaves a file that is valid UTF-8 and not valid JSON.</para>
    /// </remarks>
    private static JsonElement? ReadJsonFile(ClientRun run, string path, string label)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var value = JsonReader.ReadFile(path, out var error);
        if (error is not null)
        {
            run.LoadErrors.Add($"{label} unreadable ({ReferenceName(error)})");
            return null;
        }

        return value is { ValueKind: JsonValueKind.Null } ? null : value;
    }

    /// <summary>The name CPython would print for the failure <paramref name="error"/> reports.</summary>
    private static string ReferenceName(Exception error) => error switch
    {
        JsonException => "JSONDecodeError",
        UnauthorizedAccessException => "PermissionError",
        FileNotFoundException or DirectoryNotFoundException => "FileNotFoundError",
        _ => "OSError",
    };

    private static void LoadArms(ClientRun run)
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        var roster = new List<string>();
        foreach (var entry in Roster(run.Document))
        {
            var name = JsonValue.String(entry, ArmKeys.Run.Arm.Name);
            if (string.IsNullOrEmpty(name))
            {
                continue;
            }

            var file = JsonValue.String(entry, ArmKeys.Run.Arm.File) ?? name + JsonlSuffix;
            if (!files.TryAdd(name, file))
            {
                files[name] = file;
                continue;
            }

            roster.Add(name);
        }

        foreach (var candidate in PythonGlob.ArmFiles(run.Directory))
        {
            var name = Path.GetFileNameWithoutExtension(candidate);
            if (!files.TryAdd(name, Path.GetFileName(candidate)))
            {
                continue;
            }

            roster.Add(name);
        }

        foreach (var name in ArmRecords.LoadOrder.Where(files.ContainsKey))
        {
            run.Arms.Add(ArmLoader.Load(name, PosixPathText.Join(run.Directory, files[name])));
        }

        roster.Sort(StringComparer.Ordinal);
        foreach (var name in roster.Where(name => !run.Arms.Contains(name)))
        {
            run.Arms.Add(ArmLoader.Load(name, PosixPathText.Join(run.Directory, files[name])));
        }
    }

    /// <summary>The roster entries of <c>run.json</c>'s <c>arms</c> array, or nothing when it has none.</summary>
    private static IEnumerable<JsonElement> Roster(JsonElement? document)
    {
        if (JsonValue.Member(document, ArmKeys.Run.Arms) is not { ValueKind: JsonValueKind.Array } arms)
        {
            yield break;
        }

        foreach (var entry in arms.EnumerateArray())
        {
            yield return entry;
        }
    }
}
