using System.Text;
using WinForward.E2E.Client;
using WinForward.E2E.Client.Arms;
using WinForward.E2E.Contracts;
using WinForward.E2E.Contracts.Json;
using WinForward.E2E.Contracts.Metrics;
using WinForward.E2E.Tests.Shapes;
using WinForward.E2E.Wire;
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
        var failures = new List<string>();
        foreach (var contract in ContractRegistry.s_all)
        {
            var (kind, _, metrics, _) = contract;
            CountDifferences(failures, kind, metrics);
        }

        Assert.True(failures.Count == 0, string.Join('\n', failures));
    }

    /// <summary>
    /// Walks the metrics tree and asserts, at every record in it, that the keys the record declares are
    /// exactly the keys it publishes: the paths directly under the record's own location, one per
    /// property, plus one property for every block it holds whose members are dotted into the object the
    /// record opened (that block publishes no key of its own, unlike a block that arrives as an object
    /// and writes its container key). The nullable count pairs the record's nullable properties with the
    /// registered null cases the same way.
    /// </summary>
    private static void CountDifferences(List<string> failures, string kind, MetricsContract metrics) =>
        CountDifferences(failures, kind, metrics.Prefix, metrics.PropertyCount, metrics.NullablePropertyCount, metrics.Declared, metrics.Nullable, metrics.Blocks);

    private static void CountDifferences(
        List<string> failures,
        string kind,
        string prefix,
        int propertyCount,
        int nullablePropertyCount,
        IReadOnlyList<string> declared,
        IReadOnlyList<string> nullable,
        IReadOnlyList<MetricsBlock> blocks)
    {
        var own = declared.Where(path => path.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
        var nested = Nested(blocks, own);
        var properties = own.Count(path => !nested.Contains(path))
            + blocks.Count(static block => !block.PublishesContainerKey);
        if (properties != propertyCount)
        {
            failures.Add(
                $"{kind} {prefix}: the record declares {propertyCount} key-publishing propert(ies) "
                + $"({blocks.Count} block holder(s), {blocks.Count(static block => !block.PublishesContainerKey)} of them publishing no container key) "
                + $"but ArmKeys declares {properties} key(s) directly under it");
        }

        var ownNullable = nullable.Where(path => path.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
        var nestedNullable = Nested(blocks, ownNullable);
        var nullables = ownNullable.Count(path => !nestedNullable.Contains(path));
        if (nullables != nullablePropertyCount)
        {
            failures.Add(
                $"{kind} {prefix}: the record declares {nullablePropertyCount} nullable propert(ies) "
                + $"but {nullables} null case(s) are registered directly under it");
        }

        foreach (var block in blocks)
        {
            CountDifferences(failures, kind, block.Prefix, block.PropertyCount, block.NullablePropertyCount, declared, nullable, block.Blocks);
        }
    }

    /// <summary>The paths of <paramref name="paths"/> that belong to one of the nested blocks.</summary>
    private static HashSet<string> Nested(IReadOnlyList<MetricsBlock> blocks, IReadOnlyList<string> paths) =>
        blocks
            .SelectMany(block => paths.Where(path => path.StartsWith(block.Prefix, StringComparison.Ordinal)))
            .ToHashSet(StringComparer.Ordinal);

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
            foreach (var block in contract.Metrics.AllBlocks().Where(static block => block.OmittedWhen != ShapeFlags.None))
            {
                var written = await WrittenMetricsAsync(contract, block.OmittedWhen);
                var blockPaths = contract.Metrics.Under(block.Prefix).ToArray();
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

            // Which gates an arm publishes is the arm's business, but every one of them is declared.
            var published = JsonPaths.Under(observations, ArmKeys.Common.Record.Gates);
            if (published.Count == 0)
            {
                failures.Add($"{contract.Kind}: the record publishes no {ArmKeys.Common.Record.Gates} member");
            }

            failures.AddRange(published.Where(path => !RecordContract.Gates.Contains(path)).Select(path => $"{contract.Kind} {path}: published but not declared"));

            // Parameters are the other way round: the object publishes exactly the members the arm
            // set, so the set the kind publishes and the set its contract declares must be equal in
            // both directions -- a parameter that started being published without being declared, and
            // one that stopped being published, both fail here.
            failures.AddRange(Differences(
                $"{contract.Kind} parameters",
                contract.Parameters,
                JsonPaths.Under(observations, ArmKeys.Common.Record.Parameters)));
        }

        Assert.True(failures.Count == 0, string.Join('\n', failures));
    }

    [Fact]
    public async Task ARecordThatNestsAnotherRecordsMetricsWritesItAsOneObject()
    {
        // The control kind publishes one elapsed time and then the metrics of each phase it ran, so a
        // typed value is nested inside the metrics object: it has to arrive as one JSON value with its
        // own braces, not as a run of members of the object around it.
        var outcome = new ArmOutcome
        {
            Parameters = new ArmParameters { Seconds = 10.0 },
            Metrics = new ControlMetrics
            {
                ElapsedSeconds = 10.2,
                Latency = new LatencyMetrics { Tcp = null, Udp = null },
                Loss = LossShape.Metrics(ShapeFlags.None),
            },
        };

        var observations = await PublishAsync(outcome);

        var nested = observations[$"metrics/{ArmKeys.Control.Loss}"];
        Assert.Equal(JsonPaths.KindObject, nested[0].Kind);
        Assert.True(
            observations.ContainsKey($"metrics/{ArmKeys.Control.Loss}/{ArmKeys.Loss.Sent}"),
            "the nested metrics value was flattened into its parent");
    }

    /// <summary>
    /// The mode names the reliability contract declares are the ones the arm publishes: the arm writes
    /// them through <c>TcpCommand.Name</c>, so a renamed mode would otherwise move the block without
    /// moving the key.
    /// </summary>
    [Fact]
    public void TheDeclaredReliabilityModeNamesAreTheScheduledOnes()
    {
        foreach (var (mode, name) in ReliabilityShape.s_modes)
        {
            Assert.Equal(TcpCommand.Name(mode), name);
        }
    }

    /// <summary>
    /// The declared loss window is the one the record publishes (audit #6): both arms take W from the
    /// plan's <c>lossWindowMs</c> and publish it as the window their arrived/late/never split used, so
    /// the value a consumer reads back is the one the classification ran with.
    /// </summary>
    [Fact]
    public async Task ThePublishedLossWindowIsTheDeclaredOne()
    {
        var loss = ContractRegistry.s_all.Single(contract => contract.Kind == "loss");
        var lossObservations = await PublishAsync(loss, ShapeFlags.None);
        Assert.Equal(
            200.0,
            lossObservations[$"metrics/{ArmKeys.Loss.Window}"][0].Value.GetDouble());

        var mix = ContractRegistry.s_all.Single(contract => contract.Kind == "mix");
        var mixObservations = await PublishAsync(mix, ShapeFlags.None);
        Assert.Equal(
            200.0,
            mixObservations[$"metrics/{ArmKeys.Mix.Classes}/{ArmKeys.Mix.ClassNames.Udp}/{ArmKeys.Mix.UdpClass.Window}"][0].Value.GetDouble());
    }

    /// <summary>
    /// The per-attempt evidence records the reliability arm writes into its own file are part of the
    /// contract too: one record is published through the production writer and compared with the keys
    /// <see cref="ArmKeys.Reliability.Attempt"/> declares, in both directions and in write order.
    /// </summary>
    [Fact]
    public async Task TheAttemptRecordPublishesExactlyTheDeclaredKeys()
    {
        using var stream = new MemoryStream();
        await using var sink = new JsonlSink(stream, JsonlPolicy.Propagate, envelope: null, s_noFlush);
        var evidence = new ReliabilityArm.AttemptEvidence(sink);
        await evidence.RecordAsync(
            new ReliabilityAttempt
            {
                Observed = ReliabilityOutcome.Reset,
                Expected = ReliabilityOutcome.Clean,
                Mode = TcpMode.ResetAfterN,
                ConnectionId = 0x5245_0001u,
            },
            CancellationToken.None);
        await sink.CompleteAsync();

        var observations = JsonPaths.FlattenJsonl(Encoding.UTF8.GetString(stream.ToArray()));
        var declared = DeclaredKeys.Under(typeof(ArmKeys.Reliability.Attempt), string.Empty);
        declared.Insert(0, ArmKeys.Common.Record.Type);

        Assert.Equal(declared, JsonPaths.TopLevel(observations));
    }

    private static ArmOutcome OutcomeWithNotes(params string[] notes)
    {
        var outcome = new ArmOutcome
        {
            Parameters = new ArmParameters { Seconds = 1.0 },
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
