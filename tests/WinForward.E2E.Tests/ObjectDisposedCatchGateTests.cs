using System.Runtime.CompilerServices;
using Xunit;

namespace WinForward.E2E.Tests;

/// <summary>
/// The teardown gate of D19.2 ⑨: every <c>catch (ObjectDisposedException)</c> the harness writes is one
/// of a registered set of shapes, and the shape says whether the catch books anything. Teardown is not a
/// measurement, so a catch that swallows one is either empty or books the explicit non-observation the
/// site publishes; a catch that writes a counter, a verdict or an observation is a measurement fabricated
/// from teardown and fails here.
/// </summary>
/// <remarks>
/// <para><b>The baseline.</b> <c>benchmarks/WinForward.E2E</c> holds <b>31</b> of these catches in
/// <b>19</b> files (the premise re-verification's count, <c>research/semantic-fixes/E3-premises.md</c>
/// item 19), and the registry below is that list, per file and in source order. The three sites whose
/// catch no longer writes a counter -- <c>MixBulkLoop</c>, <c>MixPageLoop</c>'s page connection
/// and <c>DnsServer</c>'s stream handler -- are registered as <see cref="Ignored"/> like their
/// 19 siblings: putting the increment back is an <see cref="Unregistered"/> shape and fails.</para>
/// <para><b>What is not in the baseline.</b> <c>WinForward.E2E.Contracts</c> holds three more of these
/// catches, in <c>JsonlSink</c>, and they are deliberately outside this gate: the sink's policy is
/// "swallow and count the write failure" (D14.7), so one of them books a
/// <c>ledgerWriteErrors</c> failure by design -- that is a real write failure, not teardown.</para>
/// <para><b>Why the gate scans sources.</b> The shape of a catch body is exactly what a mutation that
/// re-books teardown changes, and it is the only check the counters have: the sockets these catches sit
/// over are owned by the call they wrap, so nothing in the harness can dispose one under them (measured:
/// disposing a socket under a pending receive ends the operation as <c>SocketException(OperationAborted)</c>,
/// which the socket sites book in a socket-error arm -- their own, or the transport's one level down --
/// and never here).</para>
/// </remarks>
public sealed class ObjectDisposedCatchGateTests
{
    private const string Harness = "benchmarks/WinForward.E2E";

    /// <summary>The body is a comment and nothing else: the teardown books nothing.</summary>
    private const string Ignored = "ignored";

    /// <summary>The body ends the loop or the method without booking anything.</summary>
    private const string Ends = "ends";

    /// <summary>The body answers the caller with a connect failure, which is the caller's own result.</summary>
    private const string ConnectFailure = "connectFailure";

    /// <summary>The body returns the explicit torn-down outcome: no verdict, no record (D19.2 ⑨).</summary>
    private const string NoVerdict = "noVerdict";

    /// <summary>The body books the explicit cancelled status: no observation (D19.2 ⑨).</summary>
    private const string NoObservation = "noObservation";

    /// <summary>A shape the registry does not register: a counter, a verdict or an observation.</summary>
    private const string Unregistered = "unregistered";

    /// <summary>
    /// The registered sites, harness-relative and in source order per file. The counts are the baseline's
    /// shapes: 22 bodies that only explain why nothing is booked, six that end the loop or the method,
    /// one connect failure, and the two explicit teardown outcomes.
    /// </summary>
    private static readonly Dictionary<string, string[]> s_registry = new(StringComparer.Ordinal)
    {
        ["Client/Arms/DnsTcpPhase.cs"] = [Ignored, Ignored],
        ["Client/Arms/DnsUdpPhase.cs"] = [Ignored, Ignored],
        ["Client/Arms/LossArm.cs"] = [Ignored, Ignored],
        ["Client/Arms/MixBulkLoop.cs"] = [Ignored],
        ["Client/Arms/MixPageLoop.cs"] = [Ignored, Ignored],
        ["Client/Arms/MixUdpLoop.cs"] = [Ignored, Ignored],
        ["Client/Arms/PersistentArm.cs"] = [Ignored],
        ["Client/Arms/ReliabilityExchange.cs"] = [NoObservation],
        ["Client/Arms/ThroughputArm.cs"] = [Ignored, Ignored],
        ["Client/FrameBuffer.cs"] = [ConnectFailure, Ignored],
        ["Client/Lanes/LaneEngine.cs"] = [Ignored, Ignored],
        ["Client/ResourceSampleWriter.cs"] = [Ignored],
        ["Program.cs"] = [Ignored],
        ["Target/DnsServer.cs"] = [Ends, Ends, Ignored],
        ["Target/TargetLog.cs"] = [Ignored],
        ["Target/TcpAcceptLoop.cs"] = [Ends],
        ["Target/TcpConnectionProtocol.cs"] = [NoVerdict],
        ["Target/TcpTargetServer.cs"] = [Ends],
        ["Target/UdpEchoServer.cs"] = [Ends, Ends, Ignored],
    };

    /// <summary>The bodies a site that ends a loop or a method may have, and nothing else.</summary>
    private static readonly string[] s_exits = ["return;", "return false;", "break;", "return \"unknown\";"];

    [Fact]
    public void EveryTeardownCatchIsRegisteredAndBooksNothing()
    {
        var root = RepoRoot();
        var files = Directory
            .EnumerateFiles(Path.Combine(root, Harness), "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToArray();

        var found = new Dictionary<string, List<Site>>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            var relative = Path.GetRelativePath(Path.Combine(root, Harness), file).Replace('\\', '/');
            var sites = SitesIn(relative, File.ReadAllText(file));
            if (sites.Count > 0)
            {
                found[relative] = sites;
            }
        }

        var failures = new List<string>();
        foreach (var (file, registered) in s_registry)
        {
            if (!found.TryGetValue(file, out var sites))
            {
                failures.Add($"{file}: no catch (ObjectDisposedException) was found, but the registry has {registered.Length}");
                continue;
            }

            if (sites.Count != registered.Length)
            {
                failures.Add($"{file}: {sites.Count} catch(es) found, {registered.Length} registered");
            }

            for (var index = 0; index < Math.Min(sites.Count, registered.Length); index++)
            {
                if (sites[index].Shape != registered[index])
                {
                    failures.Add(
                        $"{file}:{sites[index].Line}: registered as '{registered[index]}' but the body is "
                        + $"'{sites[index].Shape}' ({sites[index].Body})");
                }
            }
        }

        foreach (var file in found.Keys.Except(s_registry.Keys, StringComparer.Ordinal))
        {
            failures.Add($"{file}: {found[file].Count} catch(es) the registry does not know");
        }

        // The two counts the premise re-verification established, so a site removed by a refactor is as
        // visible as one added.
        Assert.Equal(31, found.Sum(pair => pair.Value.Count));
        Assert.Equal(19, found.Count);

        // No site of any shape may book a counter: the ignored bodies are empty and the two explicit
        // outcomes write a named status, so this holds for the whole set rather than for the five the
        // batch moved.
        foreach (var (file, sites) in found)
        {
            failures.AddRange(sites
                .Where(site => site.Body.Contains("Interlocked", StringComparison.Ordinal))
                .Select(site => $"{file}:{site.Line}: the teardown arm books a counter ({site.Body})"));
        }

        Assert.True(failures.Count == 0, string.Join('\n', failures));
    }

    /// <summary>
    /// The gate's own control, in both directions: a body that books a counter has to be seen as
    /// unregistered, and a body that only explains why nothing is booked has to be seen as ignored. A
    /// scanner that quietly stopped matching would otherwise look exactly like a clean tree, which is the
    /// one failure mode a source-scanning gate cannot detect by scanning.
    /// </summary>
    [Fact]
    public void TheGateSeesACounterInATeardownArmAndAnIgnoredOne()
    {
        const string snippet = """
            catch (ObjectDisposedException)
            {
                Interlocked.Increment(ref counters._bulkErrors);
            }
            catch (ObjectDisposedException exception)
            {
                /* teardown closed the socket first */
            }
            catch (ObjectDisposedException)
            {
                return CommandOutcome.TornDown;
            }
            """;

        var sites = SitesIn("snippet.cs", snippet);

        Assert.Equal(3, sites.Count);
        Assert.Equal(Unregistered, sites[0].Shape);
        Assert.Equal(Ignored, sites[1].Shape);
        Assert.Equal(NoVerdict, sites[2].Shape);
    }

    /// <summary>The repository root, taken from this file's own compile-time path: the gate reads sources.</summary>
    private static string RepoRoot([CallerFilePath] string testFile = "")
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(testFile)!, "..", ".."));
        Assert.True(File.Exists(Path.Combine(root, "WinForward.slnx")), $"'{root}' is not the repository root");
        return root;
    }

    /// <summary>One catch site: the line it starts on, the shape of its body and the body itself.</summary>
    private sealed record Site(int Line, string Shape, string Body);

    /// <summary>
    /// Every <c>catch (ObjectDisposedException)</c> in one file, in source order. The body is taken by
    /// matching braces from the catch's own block, so a nested block cannot end the body early, and its
    /// comments are stripped before the shape is read: a comment about a counter is not a counter.
    /// </summary>
    private static List<Site> SitesIn(string file, string text)
    {
        const string marker = "catch (ObjectDisposedException";
        var sites = new List<Site>();
        for (var index = text.IndexOf(marker, StringComparison.Ordinal); index >= 0; index = text.IndexOf(marker, index + marker.Length, StringComparison.Ordinal))
        {
            var open = text.IndexOf('{', index);
            Assert.True(open > index, $"{file}: the catch at line {LineOf(text, index)} has no body");

            var depth = 0;
            var close = open;
            for (; close < text.Length; close++)
            {
                if (text[close] == '{')
                {
                    depth++;
                }
                else if (text[close] == '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        break;
                    }
                }
            }

            var body = WithoutComments(text[(open + 1)..close]);
            sites.Add(new Site(LineOf(text, index), ShapeOf(body), body));
        }

        return sites;
    }

    /// <summary>
    /// The registered shape of one body: the empty (comment-free) body of an ignored teardown, one of the
    /// exits that end the site without booking anything, or one of the three explicit answers. Anything
    /// else -- a counter, a verdict, an observation -- is unregistered.
    /// </summary>
    private static string ShapeOf(string body) => body switch
    {
        "" => Ignored,
        _ when s_exits.Contains(body, StringComparer.Ordinal) => Ends,
        _ when body.StartsWith("return new LaneOpenResult(", StringComparison.Ordinal) => ConnectFailure,
        "return CommandOutcome.TornDown;" => NoVerdict,
        "result._status = ExchangeStatus.Cancelled;" => NoObservation,
        _ => Unregistered,
    };

    /// <summary>One body with its comments removed and its whitespace collapsed to single spaces.</summary>
    private static string WithoutComments(string body)
    {
        var kept = new List<char>(body.Length);
        for (var index = 0; index < body.Length; index++)
        {
            switch (body[index])
            {
                case '/' when index + 1 < body.Length && body[index + 1] == '/':
                    while (index < body.Length && body[index] != '\n')
                    {
                        index++;
                    }

                    break;
                case '/' when index + 1 < body.Length && body[index + 1] == '*':
                    var end = body.IndexOf("*/", index + 2, StringComparison.Ordinal);
                    index = end < 0 ? body.Length : end + 1;
                    break;
                default:
                    kept.Add(body[index]);
                    break;
            }
        }

        return string.Join(' ', new string([.. kept]).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>The 1-based line <paramref name="index"/> falls on.</summary>
    private static int LineOf(string text, int index) => text.Take(index).Count(character => character == '\n') + 1;
}
