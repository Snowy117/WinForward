using System.Globalization;
using System.Text.Json;
using Xunit;

namespace WinForward.E2E.Tests;

/// <summary>
/// The CLI's user-visible text, frozen before E2-d replaced the two argument parsers with one shared
/// walk (<c>research/cli-snapshots</c>). The commands in <c>before/index.json</c> are replayed
/// through <see cref="Program.Main"/> and compared -- exit code, stdout and stderr -- with what the
/// published binary printed, so a changed message, a changed exit code, or a help that lost a line
/// fails here instead of in a campaign.
/// </summary>
/// <remarks>
/// <para><b>Replaying the process.</b> The comparison is against the entry point rather than against
/// the parser's error string, because the text a user reads is the composition: the verb's message,
/// the help printed behind it, and the code the shell sees. <see cref="Program.Main"/> returns
/// before it opens a socket or creates a directory for every recorded case, which is what makes the
/// replay safe inside the test host.</para>
/// <para><b>The one registered change.</b> E2-d's only intended difference is the target help
/// sentence that names the exit code 1 E1 introduced, so the cases whose stdout carries that help
/// are compared against the <c>after/</c> tree, and
/// <see cref="TheRegisteredTargetHelpSentenceIsTheOnlyDifferenceBetweenTheTrees"/> pins the two
/// trees to that sentence. <c>INTENTIONAL.md</c> is the register.</para>
/// </remarks>
[Collection(CliSnapshotCollection.Name)]
public sealed class CliSnapshotTests
{
    private const string TargetHelpBefore = "Runs until Ctrl+C or SIGTERM. Exits 0 on a clean shutdown, 2 on a usage error.";
    private const string TargetHelpAfter = "Runs until Ctrl+C or SIGTERM. Exits 0 on a clean shutdown, 1 on a runtime error, 2 on a\nusage error.";
    private const string TargetHelpCase = "target-help";

    private static string Before => Path.Combine(RepoPaths.CliSnapshotsDirectory, "before");

    private static string After => Path.Combine(RepoPaths.CliSnapshotsDirectory, "after");

    [Fact]
    public async Task EveryRecordedCommandStillPrintsItsRecordedText()
    {
        var previousOut = Console.Out;
        var previousError = Console.Error;
        var previousDirectory = Directory.GetCurrentDirectory();
        try
        {
            // The recorded argv names plans by repository-relative path, so the replay has to resolve
            // them the way the collector did.
            Directory.SetCurrentDirectory(RepoPaths.Root);
            foreach (var recorded in ReadIndex())
            {
                var frozen = Text(Path.Combine(Before, $"{recorded.Stem}.stdout"));
                var expected = Path.Combine(CarriesTheRegisteredTargetHelp(frozen) ? After : Before, recorded.Stem);

                var stdout = new StringWriter();
                var stderr = new StringWriter();
                Console.SetOut(stdout);
                Console.SetError(stderr);
                var exit = await Program.Main(recorded.Argv).ConfigureAwait(false);
                Console.SetOut(previousOut);
                Console.SetError(previousError);

                Assert.Equal(ExitCode(expected), exit);
                Assert.Equal(Text($"{expected}.stdout"), PlatformLines(stdout.ToString()));
                Assert.Equal(Text($"{expected}.stderr"), PlatformLines(stderr.ToString()));
            }
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            Directory.SetCurrentDirectory(previousDirectory);
        }
    }

    [Fact]
    public void TheRegisteredTargetHelpSentenceIsTheOnlyDifferenceBetweenTheTrees()
    {
        foreach (var recorded in ReadIndex())
        {
            var frozen = Text(Path.Combine(Before, $"{recorded.Stem}.stdout"));

            Assert.Equal(Text(Path.Combine(Before, $"{recorded.Stem}.exit")), Text(Path.Combine(After, $"{recorded.Stem}.exit")));
            Assert.Equal(Text(Path.Combine(Before, $"{recorded.Stem}.stderr")), Text(Path.Combine(After, $"{recorded.Stem}.stderr")));
            Assert.Equal(frozen.Replace(TargetHelpBefore, TargetHelpAfter, StringComparison.Ordinal), Text(Path.Combine(After, $"{recorded.Stem}.stdout")));
        }

        // The substitution above is a no-op for the cases that carry no help, so the change itself
        // needs its own assertion: the target's help is the case that shows it.
        var help = Text(Path.Combine(After, $"{ReadIndex().Single(recorded => recorded.Name == TargetHelpCase).Stem}.stdout"));

        Assert.Contains(TargetHelpAfter, help, StringComparison.Ordinal);
        Assert.DoesNotContain(TargetHelpBefore, help, StringComparison.Ordinal);
    }

    /// <summary>
    /// Whether a recorded stdout is the target's help behind an error message. The target prints its
    /// help on every usage error, so the one registered sentence reaches eight of the cases; the rule
    /// reads the recorded text instead of naming them, because which commands print help is
    /// <see cref="Program"/>'s behavior and not a property of the snapshot list.
    /// </summary>
    private static bool CarriesTheRegisteredTargetHelp(string stdout) => stdout.Contains(TargetHelpBefore, StringComparison.Ordinal);

    private static List<RecordedCase> ReadIndex()
    {
        using var document = JsonDocument.Parse(Text(Path.Combine(Before, "index.json")));

        return
        [
            .. document.RootElement.EnumerateArray().Select(entry => new RecordedCase(
                entry.GetProperty("name").GetString() ?? string.Empty,
                entry.GetProperty("stem").GetString() ?? string.Empty,
                [.. entry.GetProperty("argv").EnumerateArray().Select(argument => argument.GetString() ?? string.Empty)]))
        ];
    }

    private static int ExitCode(string stem) =>
        int.Parse(Text($"{stem}.exit"), NumberStyles.Integer, CultureInfo.InvariantCulture);

    private static string Text(string path) => PlatformLines(File.ReadAllText(path));

    /// <summary>
    /// The snapshots were recorded on Unix, where a line ends in a single newline, while
    /// <see cref="Console"/> terminates a line with the platform's; that one difference is tolerated
    /// so the suite can run on Windows, and nothing else is.
    /// </summary>
    private static string PlatformLines(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);

    private sealed record RecordedCase(string Name, string Stem, string[] Argv);
}

/// <summary>
/// The snapshot tests own <see cref="Console.Out"/>, <see cref="Console.Error"/> and the current
/// directory for the length of a case, all three process-wide, so they may not run beside another
/// collection.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class CliSnapshotCollection
{
    internal const string Name = "cli-snapshots";
}
