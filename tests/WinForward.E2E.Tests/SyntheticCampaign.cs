using System.Diagnostics;
using System.Formats.Tar;
using System.Globalization;
using System.IO.Compression;
using System.Text.Json.Nodes;
using Xunit;

namespace WinForward.E2E.Tests;

/// <summary>
/// A throwaway copy of the frozen synthetic campaign, with the analysis binary driven the way the
/// campaign driver drives it.
/// </summary>
/// <remarks>
/// The frozen archive is never written to: every fact extracts it into its own directory, mutates the
/// copy and analyzes that. The tree's paths are part of its compared output, so the extracted root is
/// kept out of the assertions — only the mutated campaign is ever read as a campaign.
/// </remarks>
internal sealed class SyntheticCampaign : IDisposable
{
    private readonly string _root;

    internal SyntheticCampaign()
    {
        _root = Path.Combine(Path.GetTempPath(), $"wf-campaign-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        Extract(Path.Combine(_root, "tree"));
    }

    /// <summary>A fresh output directory, so two runs in one fact cannot read each other's files.</summary>
    internal string Output(string name)
    {
        var directory = Path.Combine(_root, name);
        Directory.CreateDirectory(directory);
        return directory;
    }

    /// <summary>A copy of the whole extracted tree, whose raw directory the fact may mutate.</summary>
    internal string Copy(string name)
    {
        var destination = Path.Combine(_root, name);
        CopyTree(Path.Combine(_root, "tree"), destination);
        return Path.Combine(destination, "raw");
    }

    /// <summary>Runs the analysis and asserts it accepted the tree.</summary>
    internal static void Run(string raw, string output, params string[] extra)
    {
        var (exitCode, log) = RunRaw(raw, output, extra);
        Assert.True(exitCode == 0, $"the analysis exited {exitCode}:{Environment.NewLine}{log}");
    }

    /// <summary>Runs the analysis and returns its exit code with both streams.</summary>
    private static (int ExitCode, string Log) RunRaw(string raw, string output, params string[] extra)
    {
        var info = new ProcessStartInfo(AnalysisBinary())
        {
            WorkingDirectory = RepoPaths.Root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        info.ArgumentList.Add("--raw");
        info.ArgumentList.Add(raw);
        info.ArgumentList.Add("--out");
        info.ArgumentList.Add(output);
        foreach (var argument in extra)
        {
            info.ArgumentList.Add(argument);
        }

        using var process = new Process();
        process.StartInfo = info;
        Assert.True(process.Start());
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, stdout + Environment.NewLine + stderr);
    }

    /// <summary>Every finding in one run's verdict, as the objects the document carries.</summary>
    private static List<JsonObject> Findings(string output) =>
        [.. Json(output, "verdict.json")["findings"]!.AsArray().Select(finding => finding!.AsObject())];

    /// <summary>The findings whose kind is <paramref name="kind"/>, in document order.</summary>
    internal static List<JsonObject> FindingsOfKind(string output, string kind) =>
        [.. Findings(output).Where(finding => finding["kind"]!.GetValue<string>() == kind)];

    /// <summary>One JSON document of an output directory, parsed.</summary>
    internal static JsonNode Json(string directory, string name) =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(directory, name)))!;

    /// <summary>Writes one pass's <c>order.json</c>.</summary>
    internal static void WriteOrder(string raw, string pass, int seed, string algorithm, IEnumerable<string> rows) =>
        File.WriteAllText(
            Path.Combine(raw, pass, "order.json"),
            new JsonObject
            {
                ["pass"] = PassNumber(pass),
                ["seed"] = seed,
                ["algorithm"] = algorithm,
                ["rows"] = Rows(rows),
            }.ToJsonString());

    /// <summary>Replaces one pass's <c>order.txt</c>.</summary>
    internal static void WriteOrderText(string raw, string pass, IEnumerable<string> rows) =>
        File.WriteAllText(Path.Combine(raw, pass, "order.txt"), string.Join(", ", rows));

    /// <summary>Writes one pass's <c>clean-slate.json</c> the way a refused pass leaves it.</summary>
    internal static void WriteCleanSlate(string raw, string pass, bool passed, string reason) =>
        File.WriteAllText(
            Path.Combine(raw, pass, "clean-slate.json"),
            new JsonObject
            {
                ["pass"] = PassNumber(pass),
                ["passed"] = passed,
                ["reason"] = reason,
            }.ToJsonString());

    /// <summary>Writes the campaign's <c>failures.jsonl</c>.</summary>
    internal static void WriteFailures(string raw, params JsonObject[] failures) =>
        File.WriteAllLines(Path.Combine(raw, "failures.jsonl"), failures.Select(failure => failure.ToJsonString()));

    /// <summary>Writes the campaign's derived <c>failures.json</c> array.</summary>
    internal static void WriteFailureDocument(string raw, params JsonObject[] failures) =>
        File.WriteAllText(Path.Combine(raw, "failures.json"), new JsonArray([.. failures]).ToJsonString());

    /// <summary>One failure record in the shape the orchestrator writes.</summary>
    internal static JsonObject Failure(
        string stage,
        int? pass = null,
        string? row = null,
        string? arm = null,
        string? error = null,
        bool cleanSlate = false) =>
        new()
        {
            ["when"] = "2026-10-18T21:00:00.0000000+08:00",
            ["pass"] = pass is null ? null : JsonValue.Create(pass.Value),
            ["row"] = row is null ? null : JsonValue.Create(row),
            ["arm"] = arm is null ? null : JsonValue.Create(arm),
            ["stage"] = stage,
            ["error"] = error,
            ["detail"] = null,
            ["cleanSlate"] = cleanSlate,
        };

    /// <summary>Replaces one row directory with the skeleton the orchestrator writes when it did not run.</summary>
    internal static void WriteSkeleton(string raw, string pass, string row, string stage, string error)
    {
        var directory = Path.Combine(raw, pass, row);
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }

        Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Combine(directory, "run.json"),
            new JsonObject
            {
                ["type"] = "run",
                ["label"] = $"{row}-p{PassNumber(pass)}",
                ["failed"] = true,
                ["stage"] = stage,
                ["error"] = error,
                ["pass"] = PassNumber(pass),
                ["row"] = row,
                ["startedUtc"] = "2026-10-06T08:10:00.0000000Z",
                ["arms"] = new JsonArray(),
            }.ToJsonString());
    }

    /// <summary>Sets <c>run.json.failed</c> on one row, keeping the roster it wrote.</summary>
    internal static void SetRunFailed(string raw, string pass, string row, string stage, string error)
    {
        var path = Path.Combine(raw, pass, row, "run.json");
        var document = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        document["failed"] = true;
        document["stage"] = stage;
        document["error"] = error;
        File.WriteAllText(path, document.ToJsonString());
    }

    /// <summary>Deletes one row's directory.</summary>
    internal static void DeleteRow(string raw, string pass, string row)
    {
        var directory = Path.Combine(raw, pass, row);
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>Deletes a whole pass directory.</summary>
    internal static void DeletePass(string raw, string pass)
    {
        var directory = Path.Combine(raw, pass);
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static int PassNumber(string pass) =>
        int.Parse(pass["pass".Length..], CultureInfo.InvariantCulture);

    private static JsonArray Rows(IEnumerable<string> rows) =>
        new([.. rows.Select(ToNode)]);

    private static JsonNode ToNode(string value) => JsonValue.Create(value);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static string AnalysisBinary() => Path.Combine(
        AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "WinForward.E2E.Analysis.exe" : "WinForward.E2E.Analysis");

    private static void CopyTree(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    private static void Extract(string destination)
    {
        using var archive = File.OpenRead(RepoPaths.FrozenSyntheticTree);
        using var gzip = new GZipStream(archive, CompressionMode.Decompress);
        using var reader = new TarReader(gzip);
        while (reader.GetNextEntry() is { } entry)
        {
            if (string.IsNullOrEmpty(entry.Name) || string.Equals(entry.Name, "./", StringComparison.Ordinal))
            {
                continue;
            }

            var target = Path.GetFullPath(Path.Combine(destination, entry.Name));
            Assert.StartsWith(Path.GetFullPath(destination), target, StringComparison.Ordinal);
            if (entry.EntryType == TarEntryType.Directory)
            {
                Directory.CreateDirectory(target);
                continue;
            }

            if (entry.DataStream is null)
            {
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var output = File.Create(target);
            entry.DataStream.CopyTo(output);
        }
    }
}
