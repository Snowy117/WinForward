using System.Text;
using WinForward.E2E.Client;
using WinForward.E2E.Contracts;
using WinForward.E2E.Contracts.Json;
using WinForward.E2E.Contracts.Metrics;
using Xunit;

namespace WinForward.E2E.Tests;

/// <summary>
/// The shape contract of a migrated kind: the bytes its record publishes, flattened with the shared
/// path alphabet, against the paths <c>ArmKeys</c> declares. Both sides of the comparison are the
/// production ones -- the arm's <c>WriteTo</c>, the record writer and the JSONL sink -- so a key
/// written to the wrong level, written twice, declared but never written, or written but never
/// declared shows up as a two-way diff instead of an analyzer quietly reading <c>n/a</c>.
/// </summary>
public sealed class ContractShapeTests
{
    // Long enough that the periodic flush never fires inside a test; a memory stream needs no
    // draining for its bytes to be readable right after the write.
    private static readonly TimeSpan s_noFlush = TimeSpan.FromMinutes(1);

    private static readonly string?[] s_threeNotes = ["first", "second", "third"];

    [Fact]
    public async Task EveryMigratedKindWritesExactlyTheMetricPathsItsKeysDeclare()
    {
        var failures = new List<string>();
        foreach (var contract in ContractRegistry.s_all)
        {
            var observations = await PublishAsync(contract, ShapeFlags.None);
            var actual = JsonPaths.Under(observations, contract.Metrics.Prefix);

            failures.AddRange(MetricDifferences(contract, actual));

            // Every array of the record is registered with its measured arity: an array's element
            // count is not part of the path alphabet, so it is asserted here instead of being read
            // off the path set.
            var arrays = observations
                .Where(pair => pair.Value[0].Kind == JsonPaths.KindArray)
                .Select(pair => pair.Key)
                .ToArray();
            failures.AddRange(Differences(
                $"{contract.Kind} arrays",
                [.. contract.Metrics.Arrays.Select(array => array.Path)],
                arrays));
            foreach (var array in contract.Metrics.Arrays)
            {
                var length = observations[array.Path][0].ArrayLength;
                if (length != array.Length)
                {
                    failures.Add($"{contract.Kind} array {array.Path}: declared {array.Length} element(s), measured {length}");
                }
            }
        }

        Assert.True(failures.Count == 0, string.Join('\n', failures));
    }

    [Fact]
    public async Task TheMetricsAreWrittenInKeyDeclarationOrder()
    {
        foreach (var contract in ContractRegistry.s_all)
        {
            var observations = await PublishAsync(contract, ShapeFlags.None);
            var actual = JsonPaths
                .Under(observations, contract.Metrics.Prefix)
                .Where(path => !contract.Metrics.Dynamic.Any(container => container.Covers(path)));

            Assert.Equal(contract.Metrics.Declared, actual);
        }
    }

    [Fact]
    public void TheKeySetAndTheRecordDeclareTheSameMembers()
    {
        foreach (var contract in ContractRegistry.s_all)
        {
            var (kind, _, metrics) = contract;

            // A block-holder property publishes no key of its own: it publishes the keys of the block
            // it holds, and each block registers its own property count, so the record's own
            // key-publishing properties are its property count minus one per block.
            var properties = metrics.PropertyCount - metrics.Blocks.Count + metrics.Blocks.Sum(static block => block.PropertyCount);
            Assert.True(
                properties == metrics.Declared.Count,
                $"{kind}: the metrics record declares {properties} key-publishing propert(ies) "
                + $"({metrics.PropertyCount} own, {metrics.Blocks.Count} block holder(s), "
                + $"{metrics.Blocks.Sum(static block => block.PropertyCount)} block propert(ies)) "
                + $"but ArmKeys declares {metrics.Declared.Count} key(s)");

            var nullable = metrics.NullablePropertyCount + metrics.Blocks.Sum(static block => block.NullablePropertyCount);
            Assert.True(
                nullable == metrics.Nullable.Count,
                $"{kind}: the metrics record declares {nullable} nullable propert(ies) but {metrics.Nullable.Count} null case(s) are registered");
        }
    }

    [Fact]
    public async Task AnUnknownReadingIsNullAndNeverAMissingKey()
    {
        var failures = new List<string>();
        foreach (var contract in ContractRegistry.s_all)
        {
            var observations = await PublishAsync(contract, ShapeFlags.UnknownReadings);
            var actual = JsonPaths.Under(observations, contract.Metrics.Prefix);

            // An unknown value never removes a key: the unknown record has the same paths as the
            // measured one, and every nullable path is present carrying null.
            failures.AddRange(MetricDifferences(contract, actual));
            foreach (var path in contract.Metrics.Nullable)
            {
                if (!observations.TryGetValue(path, out var found))
                {
                    failures.Add($"{contract.Kind} {path}: the null case is missing from the record");
                }
                else if (found[0].Kind != JsonPaths.KindNull)
                {
                    failures.Add($"{contract.Kind} {path}: an unknown reading published as {found[0].Kind}, not null");
                }
            }
        }

        Assert.True(failures.Count == 0, string.Join('\n', failures));
    }

    /// <summary>
    /// A conditional block is omitted whole, and only in the flag state that does not run it: that
    /// state publishes every key of the other blocks, no key of this one, and nothing else is missing.
    /// The states together account for exactly the paths the contract declares conditional, so a key
    /// that vanished from both would be a record that can never publish it, which a conditional
    /// declaration must not hide.
    /// </summary>
    [Fact]
    public async Task AConditionalBlockIsOmittedWholeInItsFlagState()
    {
        var failures = new List<string>();
        foreach (var contract in ContractRegistry.s_all)
        {
            var omitted = new List<string>();
            foreach (var block in contract.Metrics.Blocks.Where(static block => block.OmittedWhen != ShapeFlags.None))
            {
                var written = await WrittenMetricsAsync(contract, block.OmittedWhen);
                var blockPaths = contract.Metrics.Declared
                    .Where(path => path.StartsWith(block.Prefix, StringComparison.Ordinal))
                    .ToArray();
                omitted.AddRange(blockPaths);

                failures.AddRange(blockPaths
                    .Where(written.Contains)
                    .Select(path => $"{contract.Kind} {block.OmittedWhen}: {path} is published although the {block.Prefix} block did not run"));

                failures.AddRange(Differences(
                    $"{contract.Kind} {block.OmittedWhen} (every key of the blocks that ran)",
                    [.. contract.Metrics.Declared.Except(blockPaths)],
                    written));
            }

            failures.AddRange(Differences($"{contract.Kind} conditional keys", contract.Metrics.Conditional, omitted));
        }

        Assert.True(failures.Count == 0, string.Join('\n', failures));
    }

    /// <summary>
    /// The members of a data-driven container are named by the run, so they are checked by shape
    /// rather than by set equality, and a container that counted nothing is still published as an empty
    /// object: the key exists, its members do not.
    /// </summary>
    [Fact]
    public async Task DataDrivenContainersAreWrittenByShapeAndStayWhenEmpty()
    {
        var failures = new List<string>();
        foreach (var contract in ContractRegistry.s_all)
        {
            var written = await WrittenMetricsAsync(contract, ShapeFlags.EmptyContainers);
            failures.AddRange(MetricDifferences(contract, written));

            foreach (var container in contract.Metrics.Dynamic)
            {
                if (!written.Contains(container.Path))
                {
                    failures.Add($"{contract.Kind} {container.Path}: an empty container is still a written key");
                }

                failures.AddRange(written
                    .Where(container.Covers)
                    .Select(path => $"{contract.Kind} {path}: published although the run counted nothing"));
            }
        }

        Assert.True(failures.Count == 0, string.Join('\n', failures));
    }

    /// <summary>
    /// The record's <c>latency</c> object carries one histogram per measurement class, and a
    /// histogram is written only when it holds a sample: the object is always there (an arm that
    /// measures nothing publishes <c>latency: {}</c>), while its members come and go with the
    /// measurement. The two runs below are the two flag states of that condition.
    /// </summary>
    [Fact]
    public async Task OnlyNonEmptyLatencyHistogramsAreWritten()
    {
        var idle = ContractRegistry.s_all.Single(contract => contract.Kind == "idle");

        var empty = await PublishAsync(idle, ShapeFlags.None);
        Assert.True(empty.ContainsKey(ArmKeys.Common.Record.Latency), "the latency object itself is missing");
        Assert.Empty(JsonPaths.Under(empty, ArmKeys.Common.Record.Latency));

        var measured = new LatencySet();
        measured.TcpConnect.Record(150_000);
        measured.TcpRtt.Record(2_500_000);
        var withTcp = await PublishAsync(idle, ShapeFlags.None, measured);

        var declared = RecordContract.Histograms(
            ArmKeys.Common.LatencyRecord.TcpConnect,
            ArmKeys.Common.LatencyRecord.TcpRtt);
        var failures = Differences(
            "latency histograms",
            declared,
            JsonPaths.Under(withTcp, ArmKeys.Common.Record.Latency)).ToList();

        // The other two histograms are declared by ArmKeys and absent from the record: that is the
        // conditional omission, and it is asserted rather than assumed.
        failures.AddRange(Differences(
            "empty histograms",
            [],
            [.. RecordContract
                .Histograms(ArmKeys.Common.LatencyRecord.UdpRtt, ArmKeys.Common.LatencyRecord.DnsRtt)
                .Where(withTcp.ContainsKey)]));

        Assert.True(failures.Count == 0, string.Join('\n', failures));
    }

    /// <summary>
    /// An array's arity is not part of the path alphabet, so each array is asserted separately: the
    /// same path names the array and its elements, and only the arity says how many there were.
    /// </summary>
    [Fact]
    public async Task TheNotesArrayCarriesItsArity()
    {
        var empty = await PublishAsync(OutcomeWithNotes());
        var notes = empty[ArmKeys.Common.Record.Notes];
        Assert.Equal(JsonPaths.KindArray, notes[0].Kind);
        Assert.Equal(0, notes[0].ArrayLength);

        var filled = await PublishAsync(OutcomeWithNotes("first", "second", "third"));
        var written = filled[ArmKeys.Common.Record.Notes];
        Assert.Equal(JsonPaths.KindArray, written[0].Kind);
        Assert.Equal(3, written[0].ArrayLength);
        Assert.Equal<IEnumerable<string?>>(s_threeNotes, written.Skip(1).Select(observation => observation.Value.GetString()));
    }

    [Fact]
    public async Task EveryPublishedGateAndParameterIsDeclared()
    {
        var failures = new List<string>();
        foreach (var contract in ContractRegistry.s_all)
        {
            var observations = await PublishAsync(contract, ShapeFlags.None);

            failures.AddRange(Differences(
                $"{contract.Kind} record root",
                RecordContract.Skeleton,
                JsonPaths.TopLevel(observations)));

            // Which gates and parameters an arm publishes is the arm's business; that every one of
            // them is declared is the contract.
            foreach (var (group, declared) in new[]
            {
                (ArmKeys.Common.Record.Gates, RecordContract.Gates),
                (ArmKeys.Common.Record.Parameters, RecordContract.Parameters),
            })
            {
                var published = JsonPaths.Under(observations, group);
                if (published.Count == 0)
                {
                    failures.Add($"{contract.Kind}: the record publishes no {group} member");
                }

                failures.AddRange(published.Where(path => !declared.Contains(path)).Select(path => $"{contract.Kind} {path}: published but not declared"));
            }
        }

        Assert.True(failures.Count == 0, string.Join('\n', failures));
    }

    [Fact]
    public async Task ARecordThatNestsAnotherRecordsMetricsWritesItAsOneObject()
    {
        // The control kind publishes one elapsed time and then the metrics of each phase it ran, so a
        // typed value is nested inside a metrics object an arm is still building: it has to arrive as
        // one JSON value with its own braces, not as a run of members of the object around it.
        var outcome = new ArmOutcome
        {
            Parameters = { [ArmKeys.Common.Parameters.Seconds] = 10.0 },
            Metrics = new DictionaryMetrics
            {
                ["elapsedSeconds"] = 10.2,
                ["latency"] = new IdleMetrics { ElapsedSeconds = 5.001 },
            },
        };

        var observations = await PublishAsync(outcome);

        var nested = observations["metrics/latency"];
        Assert.Equal(JsonPaths.KindObject, nested[0].Kind);
        Assert.True(observations.ContainsKey("metrics/latency/elapsedSeconds"), "the nested metrics value was flattened into its parent");
    }

    private static ArmOutcome OutcomeWithNotes(params string[] notes)
    {
        var outcome = new ArmOutcome
        {
            Parameters = { [ArmKeys.Common.Parameters.Seconds] = 1.0 },
            Metrics = new IdleMetrics { ElapsedSeconds = 1.0 },
        };
        outcome.Notes.AddRange(notes);
        return outcome;
    }

    private static async Task<Dictionary<string, List<JsonPathObservation>>> PublishAsync(
        KindContract contract,
        ShapeFlags flags,
        LatencySet? latency = null)
    {
        var outcome = contract.Outcome(flags);
        var arm = new ArmSpec { Name = contract.Kind.ToUpperInvariant(), Kind = contract.Kind };
        return JsonPaths.FlattenJsonl(await WriteResultRecordAsync(arm, outcome, latency));
    }

    /// <summary>The canonical paths a kind published under <c>metrics</c>, in document order.</summary>
    private static async Task<List<string>> WrittenMetricsAsync(KindContract contract, ShapeFlags flags) =>
        JsonPaths.Under(await PublishAsync(contract, flags), contract.Metrics.Prefix);

    /// <summary>
    /// The declared metrics paths against the published ones, in both directions. A member of a
    /// declared data-driven container is explained by its container and checked by shape rather than by
    /// set equality; every other published path must be declared, and every declared path published.
    /// </summary>
    private static List<string> MetricDifferences(KindContract contract, IReadOnlyList<string> published)
    {
        var failures = new List<string>();
        var members = new List<string>();
        foreach (var container in contract.Metrics.Dynamic)
        {
            var found = published.Where(container.Covers).ToArray();
            members.AddRange(found);
            if (!published.Contains(container.Path))
            {
                failures.Add($"{contract.Kind} {container.Path}: the declared container is missing from the record");
            }

            failures.AddRange(found
                .Where(path => !container.IsMemberName(container.MemberOf(path)))
                .Select(path => $"{contract.Kind} {path}: not a member name {container.Path} may publish"));
        }

        failures.AddRange(Differences($"{contract.Kind} metrics", contract.Metrics.Declared, [.. published.Except(members)]));
        return failures;
    }

    private static async Task<Dictionary<string, List<JsonPathObservation>>> PublishAsync(ArmOutcome outcome)
    {
        var arm = new ArmSpec { Name = "SHAPE", Kind = "idle" };
        return JsonPaths.FlattenJsonl(await WriteResultRecordAsync(arm, outcome, latency: null));
    }

    /// <summary>
    /// Publishes one record through the production writer and the production sink, to a stream the
    /// test can read: the record under test is the byte sequence the harness writes.
    /// </summary>
    private static async Task<string> WriteResultRecordAsync(ArmSpec arm, ArmOutcome outcome, LatencySet? latency)
    {
        using var stream = new MemoryStream();
        await using var sink = new JsonlSink(stream, JsonlPolicy.Propagate, envelope: null, s_noFlush);
        var options = new ClientOptions { Label = "shape" };

        await ClientRunner.WriteResultAsync(sink, options, arm, outcome, latency ?? new LatencySet(), 1_000_000, 2_000_000);
        var record = Encoding.UTF8.GetString(stream.ToArray());
        await sink.CompleteAsync();
        return record;
    }

    private static IEnumerable<string> Differences(string what, IReadOnlyCollection<string> declared, IReadOnlyCollection<string> actual)
    {
        var missing = declared.Except(actual).Order(StringComparer.Ordinal).ToArray();
        var extra = actual.Except(declared).Order(StringComparer.Ordinal).ToArray();
        if (missing.Length == 0 && extra.Length == 0)
        {
            return [];
        }

        return
        [
            $"{what}: {declared.Count} declared path(s), {actual.Count} written",
            $"  declared but not written: {string.Join(", ", missing)}",
            $"  written but not declared: {string.Join(", ", extra)}",
        ];
    }
}
