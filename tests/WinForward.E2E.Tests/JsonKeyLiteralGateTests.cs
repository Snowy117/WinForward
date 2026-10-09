using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using WinForward.E2E.Contracts;
using Xunit;

namespace WinForward.E2E.Tests;

/// <summary>
/// The literal gate: the client's writers may not spell a JSON key that
/// <c>ArmKeys</c> declares, because a literal is how a key stops being renamed with the contract it
/// belongs to.
/// </summary>
/// <remarks>
/// <para><b>What is scanned.</b> Every file under <c>Client/Arms/</c>, plus the client's record
/// writers: <c>Client/ClientRunner.cs</c> (the arm loop), <c>Client/ArmRecordWriter.cs</c>
/// (the <c>result</c>, <c>armSummary</c> and <c>error</c> records), <c>Client/RunFileWriter.cs</c>
/// (<c>run.json</c>), and the resource sampler's two files,
/// <c>Client/ResourceSampler.cs</c> (the <c>sample</c> record) and
/// <c>Client/ResourceSampleWriter.cs</c> (the counters, the process census and <c>samplerError</c>).
/// The target's ledger writers are scanned too -- <c>Target/TcpTargetServer.cs</c>,
/// <c>Target/UdpEchoServer.cs</c>, <c>Target/DnsServer.cs</c> and <c>Target/TargetRunner.cs</c> -- and
/// the other files of <c>Target/</c> publish no key at all, which is why they are not listed.</para>
/// <para><b>What counts as a literal.</b> A string literal in a key position, which is either the
/// first argument of one of the <c>Utf8JsonWriter</c> members that take a property name or the index
/// of a dictionary being written under a key. That is deliberately narrower than "the name appears in
/// the file": the same spelling is also a JSON <em>value</em> in these very files (the reliability arm
/// publishes <c>observed: "clean"</c>, and <c>clean</c> is also a <c>byMode</c> member name; the target
/// names its record kinds <c>tcpSummary</c>/<c>targetSummary</c> and a connection's mode <c>unknown</c>
/// the same way), and a value is not a key. A console message or a file path that happens to spell a
/// key name (<c>Console.WriteLine("… listening on …")</c>, <c>"target-ledger.jsonl"</c>) is not a key
/// either, so the property-name members are named one by one instead of being matched as
/// <c>Write*</c>; <see cref="TheGateSeesAKeyLiteralAndNothingElse"/> is the control that holds both
/// halves of that statement.</para>
/// <para><b>How it is scanned.</b> Each file is read as one text rather than line by line, so a call
/// the formatter wrapped still has a key position on it: a literal in the argument list of a
/// <c>Write*</c> call is a key wherever the line breaks fall.</para>
/// <para><b>The known keys.</b> Every string constant declared by <c>ArmKeys</c>, read through
/// <c>typeof(...)</c> literals because the trim and AOT analyzers cannot follow a type held in a
/// variable.</para>
/// </remarks>
public sealed partial class JsonKeyLiteralGateTests
{
    private const string Harness = "benchmarks/WinForward.E2E";

    /// <summary>
    /// The record writers of both binaries, harness-relative, each one named so a missing file fails
    /// the gate.
    /// </summary>
    private static readonly string[] s_writerFiles =
    [
        "Client/ClientRunner.cs",
        "Client/ArmRecordWriter.cs",
        "Client/RunFileWriter.cs",
        "Client/ResourceSampler.cs",
        "Client/ResourceSampleWriter.cs",
        "Target/TcpTargetServer.cs",
        "Target/UdpEchoServer.cs",
        "Target/DnsServer.cs",
        "Target/TargetRunner.cs",
    ];

    /// <summary>The first argument of a property-name <c>Utf8JsonWriter.Write*</c> call.</summary>
    [GeneratedRegex("""\.Write(?:StartObject|EndObject|StartArray|EndArray|PropertyName|Null|Boolean|Number|String|RawValue|Base64String)\(\s*"([^"]*)["]""")]
    private static partial Regex WriteName();

    /// <summary>The index of a dictionary being written, which is a property name too.</summary>
    [GeneratedRegex("""\[\s*"([^"]*)["]\s*\]""")]
    private static partial Regex IndexerName();

    private static readonly HashSet<string> s_knownKeys = new(
        [
            .. DeclaredKeys.Under(typeof(ArmKeys.Common.Record), string.Empty),
            .. DeclaredKeys.Under(typeof(ArmKeys.Common.Gates), string.Empty),
            .. DeclaredKeys.Under(typeof(ArmKeys.Common.Parameters), string.Empty),
            .. DeclaredKeys.Under(typeof(ArmKeys.Common.ArmSummary), string.Empty),
            .. DeclaredKeys.Under(typeof(ArmKeys.Common.ErrorRecord), string.Empty),
            .. DeclaredKeys.Under(typeof(ArmKeys.Common.LatencyRecord), string.Empty),
            .. DeclaredKeys.Under(typeof(ArmKeys.Common.LatencyRecord.Histogram), string.Empty),
            .. DeclaredKeys.Under(typeof(ArmKeys.Run), string.Empty),
            .. DeclaredKeys.Under(typeof(ArmKeys.Run.Arm), string.Empty),
            .. DeclaredKeys.Under(typeof(ArmKeys.Run.TargetObject), string.Empty),
            .. DeclaredKeys.Under(typeof(ArmKeys.Idle), string.Empty),
            .. DeclaredKeys.Under(typeof(ArmKeys.Throughput), string.Empty),
            .. DeclaredKeys.Under(typeof(ArmKeys.Latency), string.Empty),
            .. DeclaredKeys.Under(typeof(ArmKeys.Dns), string.Empty),
            .. DeclaredKeys.Under(typeof(ArmKeys.Loss), string.Empty),
            .. DeclaredKeys.Under(typeof(ArmKeys.Persistent), string.Empty),
            .. DeclaredKeys.Under(typeof(ArmKeys.Control), string.Empty),
            .. DeclaredKeys.Under(typeof(ArmKeys.Mix), string.Empty),
            .. DeclaredKeys.Under(typeof(ArmKeys.Mix.ClassNames), string.Empty),
            .. DeclaredKeys.Under(typeof(ArmKeys.Mix.PageClass), string.Empty),
            .. DeclaredKeys.Under(typeof(ArmKeys.Mix.BulkClass), string.Empty),
            .. DeclaredKeys.Under(typeof(ArmKeys.Mix.DnsClass), string.Empty),
            .. DeclaredKeys.Under(typeof(ArmKeys.Mix.UdpClass), string.Empty),
            .. DeclaredKeys.Under(typeof(ArmKeys.Mix.DesktopLane), string.Empty),
            .. DeclaredKeys.Under(typeof(ArmKeys.Reliability), string.Empty),
            .. DeclaredKeys.Under(typeof(ArmKeys.Reliability.OutcomeNames), string.Empty),
            .. DeclaredKeys.Under(typeof(ArmKeys.Reliability.ModeNames), string.Empty),
            .. DeclaredKeys.Under(typeof(ArmKeys.Reliability.Mode), string.Empty),
            .. DeclaredKeys.Under(typeof(ArmKeys.Reliability.Attempt), string.Empty),
            .. DeclaredKeys.Under(typeof(ArmKeys.Sample), string.Empty),
            .. DeclaredKeys.Under(typeof(ArmKeys.Sample.Counters), string.Empty),
            .. DeclaredKeys.Under(typeof(ArmKeys.Sample.ProcessEntry), string.Empty),
            .. DeclaredKeys.Under(typeof(ArmKeys.Sample.SamplerError), string.Empty),
            .. DeclaredKeys.Under(typeof(ArmKeys.Ledger.Envelope), string.Empty),
            .. DeclaredKeys.Under(typeof(ArmKeys.Ledger.TcpRecord), string.Empty),
            .. DeclaredKeys.Under(typeof(ArmKeys.Ledger.TcpSummary), string.Empty),
            .. DeclaredKeys.Under(typeof(ArmKeys.Ledger.UdpSummary), string.Empty),
            .. DeclaredKeys.Under(typeof(ArmKeys.Ledger.UdpSummary.SourceEntry), string.Empty),
            .. DeclaredKeys.Under(typeof(ArmKeys.Ledger.DnsSummary), string.Empty),
            .. DeclaredKeys.Under(typeof(ArmKeys.Ledger.TargetSummary), string.Empty),
            .. DeclaredKeys.Under(typeof(ArmKeys.Ledger.TargetSummary.TcpTotals), string.Empty),
            .. DeclaredKeys.Under(typeof(ArmKeys.Ledger.TargetSummary.UdpTotals), string.Empty),
            .. DeclaredKeys.Under(typeof(ArmKeys.Ledger.TargetSummary.DnsTotals), string.Empty),
            .. DeclaredKeys.Under(typeof(ArmKeys.Ledger.VerdictNames), string.Empty),
        ],
        StringComparer.Ordinal);

    [Fact]
    public void NoWriterSpellsAKnownKeyAsALiteral()
    {
        var root = RepoRoot();
        var files = GatedFiles(root);

        Assert.NotEmpty(s_knownKeys);

        var failures = new List<string>();
        foreach (var file in files)
        {
            failures.AddRange(KeyLiteralsIn(file)
                .Where(literal => s_knownKeys.Contains(literal.Key))
                .Select(literal => $"{Path.GetRelativePath(root, file)}:{literal.Line}: the key '{literal.Key}' is written as a literal"));
        }

        Assert.True(failures.Count == 0, string.Join('\n', failures));
    }

    /// <summary>
    /// The gate's own control, in both directions: a literal in a key position has to be seen, and the
    /// literals these writers are full of -- a log line, a ledger path, a published value -- have to be
    /// left alone. A scanner that quietly stopped matching would otherwise look exactly like a clean
    /// tree, which is the one failure mode a source-scanning gate cannot detect by scanning.
    /// </summary>
    [Fact]
    public void TheGateSeesAKeyLiteralAndNothingElse()
    {
        const string snippet = """
            writer.WriteNumber("connections", Interlocked.Read(ref _connectionCount));
            writer.WriteString(ArmKeys.Ledger.TcpRecord.Mode, "unknown");
            await TargetLog.ReportAsync(string.Create(
                CultureInfo.InvariantCulture,
                $"e2e target: the udpSummary record could not be written ({ledger.WriteErrors} ledger write error(s) so far)"));
            await Console.Out.WriteLineAsync($"e2e target listening on tcp {address}:{port}, ledger {Path.GetFullPath(options.LedgerPath)}");
            var ledgerPath = "target-ledger.jsonl";
            census[key] = datagrams["received"];
            """;

        var lines = snippet.Split('\n');
        var literals = KeyLiteralsIn(snippet, lines);

        // WriteName's matches come first, then IndexerName's: the key of a dictionary being written is
        // a property name too, while a value, a message and a file path are not key positions at all.
        Assert.Equal(["connections", "received"], literals.Select(literal => literal.Key));
        Assert.Contains(literals[0].Key, s_knownKeys);
    }

    /// <summary>
    /// The repository root, taken from this file's own compile-time path rather than from the test
    /// assembly's output directory: the gate reads sources, so it must know where the sources are.
    /// </summary>
    private static string RepoRoot([CallerFilePath] string testFile = "")
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(testFile)!, "..", ".."));
        Assert.True(File.Exists(Path.Combine(root, "WinForward.slnx")), $"'{root}' is not the repository root");
        return root;
    }

    /// <summary>
    /// The files the gate scans, with the record writers checked one by one: an arm source, or a
    /// writer, that the gate could not find would pass while checking nothing, which is the one
    /// failure mode a gate of this shape has to rule out for itself.
    /// </summary>
    private static List<string> GatedFiles(string root)
    {
        var arms = Directory
            .EnumerateFiles(Path.Combine(root, Harness, "Client", "Arms"), "*.cs", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal)
            .ToList();
        var writers = new List<string>();
        foreach (var name in s_writerFiles)
        {
            var writer = Path.Combine(root, Harness, name);
            Assert.True(File.Exists(writer), $"'{writer}' does not exist");
            writers.Add(writer);
        }

        Assert.True(arms.Count > 0, $"no arm source was found under {Path.Combine(root, Harness, "Client", "Arms")}");
        return [.. arms, .. writers];
    }

    /// <summary>The literals in key position of one file, read from disk.</summary>
    private static List<(string Key, int Line)> KeyLiteralsIn(string file)
    {
        var lines = File.ReadAllLines(file);
        return KeyLiteralsIn(string.Join('\n', lines), lines);
    }

    /// <summary>
    /// One string literal in a key position, with the line it was found on. The text is scanned as one
    /// piece, so a call whose argument list is wrapped still yields its key; a literal on a line that is
    /// a comment is skipped, because prose about a key is not a key.
    /// </summary>
    private static List<(string Key, int Line)> KeyLiteralsIn(string text, string[] lines)
    {
        var literals = new List<(string Key, int Line)>();
        foreach (var regex in new[] { WriteName(), IndexerName() })
        {
            foreach (Match match in regex.Matches(text))
            {
                var line = LineOf(text, match.Groups[1].Index);
                if (!lines[line - 1].TrimStart().StartsWith("//", StringComparison.Ordinal))
                {
                    literals.Add((match.Groups[1].Value, line));
                }
            }
        }

        return literals;
    }

    /// <summary>The 1-based line <paramref name="index"/> falls on.</summary>
    private static int LineOf(string text, int index)
    {
        var line = 1;
        for (var position = 0; position < index; position++)
        {
            if (text[position] == '\n')
            {
                line++;
            }
        }

        return line;
    }
}
