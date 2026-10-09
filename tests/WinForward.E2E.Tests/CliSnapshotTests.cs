using System.Globalization;
using System.Text.Json;
using Xunit;

namespace WinForward.E2E.Tests;

/// <summary>
/// The CLI's user-visible text, frozen before the two argument parsers became one shared walk
/// (<c>research/cli-snapshots</c>). The commands in <c>before/index.json</c> are replayed
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
/// <para><b>The registered changes.</b> The before tree is what the last binary carrying the two
/// separate parsers printed, and every change to user-visible text registers its substitution in
/// <c>INTENTIONAL.md</c>: the target help sentence naming the exit code 1, and the receive-loop
/// option line. <see cref="TheRegisteredChangesAreTheOnlyDifferenceBetweenTheTrees"/> pins the two
/// trees to exactly those substitutions.</para>
/// <para><b>The cases the before tree never printed.</b> A command the before binary already carried
/// is replayable against the before tree; one whose option it refused as unknown is not, so those
/// cases live only in the after tree and are replayed against it
/// (<see cref="TheCasesTheBeforeTreePredatesStillPrintTheirRecordedText"/>). A stem is a case's
/// identity, which is why the collector appends new ones instead of inserting them.</para>
/// </remarks>
[Collection(CliSnapshotCollection.Name)]
public sealed class CliSnapshotTests
{
    private const string TargetHelpBefore = "Runs until Ctrl+C or SIGTERM. Exits 0 on a clean shutdown, 2 on a usage error.";
    private const string TargetHelpAfter = "Runs until Ctrl+C or SIGTERM. Exits 0 on a clean shutdown, 1 on a runtime error, 2 on a\nusage error.";
    private const string TargetHelpCase = "target-help";
    private const string UdpPortHelpBefore = "  --udp-port <n>       UDP echo listener port (default 30010)\n";
    private const string UdpPortHelpAfter =
        "  --udp-port <n>       UDP echo listener port (default 30010)\n"
        + "  --udp-receivers <n>  UDP receive loops each datagram listener runs (default: half the\n"
        + "                       processors, clamped to 2..8)\n";
    private const string UdpReceiverOption = "--udp-receivers";

    private static string Before => Path.Combine(RepoPaths.CliSnapshotsDirectory, "before");

    private static string After => Path.Combine(RepoPaths.CliSnapshotsDirectory, "after");

    [Fact]
    public async Task EveryRecordedCommandStillPrintsItsRecordedText()
    {
        var previousDirectory = Directory.GetCurrentDirectory();
        try
        {
            // The recorded argv names plans by repository-relative path, so the replay has to resolve
            // them the way the collector did.
            Directory.SetCurrentDirectory(RepoPaths.Root);
            foreach (var recorded in ReadIndex(Before))
            {
                await ReplayAsync(
                    recorded,
                    Before,
                    WithRegisteredChanges(Text(Path.Combine(Before, $"{recorded.Stem}.stdout")))).ConfigureAwait(false);
            }
        }
        finally
        {
            Directory.SetCurrentDirectory(previousDirectory);
        }
    }

    /// <summary>
    /// The cases the before tree does not have, because the binary it recorded them with would have
    /// refused their option as unknown: the receive-loop refusals are replayed against the tree that
    /// recorded them, so a reworded count message fails here like any other.
    /// </summary>
    [Fact]
    public async Task TheCasesTheBeforeTreePredatesStillPrintTheirRecordedText()
    {
        var frozen = ReadIndex(Before).Select(recorded => recorded.Stem).ToHashSet(StringComparer.Ordinal);
        var added = ReadIndex(After).Where(recorded => !frozen.Contains(recorded.Stem)).ToArray();

        Assert.NotEmpty(added);

        var previousDirectory = Directory.GetCurrentDirectory();
        try
        {
            Directory.SetCurrentDirectory(RepoPaths.Root);
            foreach (var recorded in added)
            {
                await ReplayAsync(recorded, After, stdout: null).ConfigureAwait(false);
            }
        }
        finally
        {
            Directory.SetCurrentDirectory(previousDirectory);
        }
    }

    [Fact]
    public void TheRegisteredChangesAreTheOnlyDifferenceBetweenTheTrees()
    {
        foreach (var recorded in ReadIndex(Before))
        {
            var frozen = Text(Path.Combine(Before, $"{recorded.Stem}.stdout"));

            Assert.Equal(Text(Path.Combine(Before, $"{recorded.Stem}.exit")), Text(Path.Combine(After, $"{recorded.Stem}.exit")));
            Assert.Equal(Text(Path.Combine(Before, $"{recorded.Stem}.stderr")), Text(Path.Combine(After, $"{recorded.Stem}.stderr")));
            Assert.Equal(WithRegisteredChanges(frozen), Text(Path.Combine(After, $"{recorded.Stem}.stdout")));
        }

        // The substitutions above are no-ops for the cases that carry no help, so each change needs its
        // own assertion: the target's help is the case that shows both of them.
        var stem = ReadIndex(Before).Single(recorded => recorded.Name == TargetHelpCase).Stem;
        var help = Text(Path.Combine(After, $"{stem}.stdout"));

        Assert.Contains(TargetHelpAfter, help, StringComparison.Ordinal);
        Assert.Contains(UdpPortHelpAfter, help, StringComparison.Ordinal);
        Assert.DoesNotContain(TargetHelpBefore, help, StringComparison.Ordinal);

        // The option is the one thing the before tree cannot contain; the sentence it replaced is
        // the other, and its absence is stated by the line above.
        Assert.DoesNotContain(UdpReceiverOption, Text(Path.Combine(Before, $"{stem}.stdout")), StringComparison.Ordinal);
    }

    /// <summary>
    /// One recorded command replayed through the entry point, with the exit code, stdout and stderr a
    /// user would have seen. The three are compared against the tree that recorded them, except for a
    /// stdout the caller already put through <see cref="WithRegisteredChanges"/>.
    /// </summary>
    private static async Task ReplayAsync(RecordedCase recorded, string tree, string? stdout)
    {
        var previousOut = Console.Out;
        var previousError = Console.Error;
        try
        {
            var written = new StringWriter();
            var reported = new StringWriter();
            Console.SetOut(written);
            Console.SetError(reported);
            var exit = await Program.Main(recorded.Argv).ConfigureAwait(false);
            Console.SetOut(previousOut);
            Console.SetError(previousError);

            Assert.Equal(ExitCode(Path.Combine(tree, recorded.Stem)), exit);
            Assert.Equal(
                stdout ?? Text(Path.Combine(tree, $"{recorded.Stem}.stdout")),
                PlatformLines(written.ToString()));
            Assert.Equal(Text(Path.Combine(tree, $"{recorded.Stem}.stderr")), PlatformLines(reported.ToString()));
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
        }
    }

    /// <summary>
    /// A recorded stdout with the registered changes applied to it. The substitution is a no-op for a
    /// case that carries neither sentence, which is how "everything else is byte for byte identical"
    /// is asserted by the same comparison.
    /// </summary>
    private static string WithRegisteredChanges(string stdout) => stdout
        .Replace(TargetHelpBefore, TargetHelpAfter, StringComparison.Ordinal)
        .Replace(UdpPortHelpBefore, UdpPortHelpAfter, StringComparison.Ordinal);

    private static List<RecordedCase> ReadIndex(string tree)
    {
        using var document = JsonDocument.Parse(Text(Path.Combine(tree, "index.json")));

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
