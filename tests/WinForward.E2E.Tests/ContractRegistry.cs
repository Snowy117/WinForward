using WinForward.E2E.Client;
using WinForward.E2E.Contracts;
using WinForward.E2E.Tests.Shapes;

namespace WinForward.E2E.Tests;

/// <summary>The switches a kind's shape factory publishes under.</summary>
[Flags]
internal enum ShapeFlags
{
    /// <summary>Every reading is known and every block the arm can publish is published.</summary>
    None = 0,

    /// <summary>Readings with no measurement behind them publish as JSON null, not as a missing key.</summary>
    UnknownReadings = 1,

    /// <summary>The arm ran only the tcp half, so it publishes no <c>udp.*</c> key at all.</summary>
    TcpOnly = 2,

    /// <summary>The arm ran only the udp half, so it publishes no <c>tcp.*</c> key at all.</summary>
    UdpOnly = 4,

    /// <summary>The run observed no value for its data-driven containers: they are still written, empty.</summary>
    EmptyContainers = 8,
}

/// <summary>An array the kind publishes, and how many elements the measured record carries.</summary>
internal sealed record ArrayArity(string Path, int Length);

/// <summary>
/// One block of a metrics record: the keys it owns are the declared paths under
/// <paramref name="Prefix"/>, and its own record declares one property per key it publishes.
/// </summary>
/// <param name="Prefix">The declared path prefix of the block's keys, e.g. <c>metrics/tcp.</c>.</param>
/// <param name="PropertyCount">Public properties of the block's record, one of which holds each nested block.</param>
/// <param name="NullablePropertyCount">Nullable properties of the block's record, which must equal its registered null cases.</param>
/// <param name="OmittedWhen">
/// The flag state whose arm did not run this block, so the factory publishes none of its keys; <see cref="ShapeFlags.None"/>
/// when the arm always publishes it.
/// </param>
/// <param name="PublishesContainerKey">
/// Whether the record that holds this block publishes a key of its own for it. A block that arrives as
/// an object of its own (the MIX <c>classes</c> object, a phase) publishes its container key; one whose
/// members are dotted into the object the holder opened (the latency <c>tcp.</c>/<c>udp.</c> families)
/// publishes none, and its holder's property is not a key of the holder's own.
/// </param>
/// <param name="Blocks">The blocks the block's own record holds, in write order; empty for a leaf block.</param>
internal sealed record MetricsBlock(
    string Prefix,
    int PropertyCount,
    int NullablePropertyCount,
    ShapeFlags OmittedWhen,
    bool PublishesContainerKey,
    IReadOnlyList<MetricsBlock> Blocks)
{
    /// <summary>This block and every block nested inside it, in declaration order.</summary>
    internal IEnumerable<MetricsBlock> SelfAndNested() =>
        Blocks.SelectMany(static block => block.SelfAndNested()).Prepend(this);
}

/// <summary>
/// A declared object whose member names come from the run rather than from a constant.
/// </summary>
/// <param name="Path">The declared container path, e.g. <c>metrics/rcodes</c>.</param>
/// <param name="IsMemberName">Whether a member name published under that container is one the contract allows.</param>
/// <remarks>
/// The container's own path is declared by <c>ArmKeys</c>, so a container the writer forgets is still a
/// missing declared key; its members are checked by shape, because a name that is data cannot be
/// declared as a constant and would otherwise read as "written but not declared" on every run.
/// </remarks>
internal sealed record DynamicMembers(string Path, Func<string, bool> IsMemberName)
{
    /// <summary>True when <paramref name="path"/> is one member of this container.</summary>
    internal bool Covers(string path) => path.StartsWith($"{Path}/", StringComparison.Ordinal);

    /// <summary>The member name <paramref name="path"/> publishes under this container.</summary>
    internal string MemberOf(string path) => path[(Path.Length + 1)..];
}

/// <summary>
/// One metrics value object together with the <c>ArmKeys</c> subtree that declares its members, the
/// nullable readings that publish as null, the keys the record may omit, and its arrays.
/// </summary>
/// <param name="Prefix">The record member the keys live under, e.g. <c>metrics</c>.</param>
/// <param name="PropertyCount">Public properties of the record, one of which holds each registered block.</param>
/// <param name="NullablePropertyCount">Nullable properties of the record, which must equal <paramref name="Nullable"/>'s size once the blocks' are added.</param>
/// <param name="Declared">The paths the key subtree declares, in document order.</param>
/// <param name="Nullable">Declared paths whose unknown value is written as JSON null.</param>
/// <param name="Conditional">Declared paths the record may omit when the arm publishes no such quantity.</param>
/// <param name="Arrays">Declared array paths and the measured arity of each.</param>
/// <param name="Blocks">The typed blocks the record holds, each with its own key prefix.</param>
/// <param name="Dynamic">The declared containers whose member names are data.</param>
internal sealed record MetricsContract(
    string Prefix,
    int PropertyCount,
    int NullablePropertyCount,
    IReadOnlyList<string> Declared,
    IReadOnlyList<string> Nullable,
    IReadOnlyList<string> Conditional,
    IReadOnlyList<ArrayArity> Arrays,
    IReadOnlyList<MetricsBlock> Blocks,
    IReadOnlyList<DynamicMembers> Dynamic)
{
    /// <summary>The declared paths under <paramref name="prefix"/>'s location, in declaration order.</summary>
    internal IEnumerable<string> Under(string prefix) =>
        Declared.Where(path => path.StartsWith(prefix, StringComparison.Ordinal));

    /// <summary>Every block of the record's tree, the blocks' own blocks included.</summary>
    internal IEnumerable<MetricsBlock> AllBlocks() => Blocks.SelectMany(static block => block.SelfAndNested());
}

/// <summary>
/// One migrated kind: the explicit factory that builds the outcome the arm would publish, and the
/// keys that outcome is allowed to contain.
/// </summary>
/// <param name="Kind">The plan kind the factory stands for.</param>
/// <param name="Outcome">The explicit factory, one per flag state the kind's shape can take.</param>
/// <param name="Metrics">The contract of the kind's <c>metrics</c> object.</param>
/// <param name="Parameters">
/// The <c>parameters</c> paths this kind publishes, and no others: the arm sets one member of
/// <see cref="ArmParameters"/> per path, so the emitted set and this declaration are compared as
/// sets. A kind whose parameters nest another arm's parameters (the control's two phases) declares
/// those leaf paths under its own phase prefix.
/// </param>
/// <remarks>
/// A required property added to a metrics record breaks the factory at compile time, and the key and
/// property counts are asserted to match, so neither half of the pair can grow alone.
/// </remarks>
internal sealed record KindContract(
    string Kind,
    Func<ShapeFlags, ArmOutcome> Outcome,
    MetricsContract Metrics,
    IReadOnlyList<string> Parameters);

/// <summary>
/// The migrated kinds, one shape file each. A kind is registered here when its arm publishes a typed
/// metrics record; the record-level parts every kind shares live in <see cref="RecordContract"/>.
/// </summary>
internal static class ContractRegistry
{
    internal static readonly KindContract[] s_all =
    [
        IdleShape.s_contract,
        ThroughputShape.s_contract,
        LatencyShape.s_contract,
        DnsShape.s_contract,
        MixShape.s_contract,
        ControlShape.s_contract,
        LossShape.s_contract,
        ReliabilityShape.s_contract,
        PersistentShape.s_contract,
    ];
}

/// <summary>The parts of a result record every kind shares, declared once in <c>ArmKeys.Common</c>.</summary>
internal static class RecordContract
{
    internal static List<string> Skeleton => DeclaredKeys.Under(typeof(ArmKeys.Common.Record), string.Empty);

    internal static List<string> Gates => DeclaredKeys.Under(typeof(ArmKeys.Common.Gates), ArmKeys.Common.Record.Gates);

    /// <summary>
    /// The declared paths of the histogram named <paramref name="name"/>: the histogram itself, and
    /// the eight leaves every histogram carries. The leaves are one nested class rather than four
    /// copies, so the path of a leaf is this composition and not the class name of
    /// <c>ArmKeys.Common.LatencyRecord.Histogram</c>.
    /// </summary>
    private static List<string> Histogram(string name)
    {
        var histogram = $"{ArmKeys.Common.Record.Latency}/{name}";
        return [histogram, .. DeclaredKeys.Under(typeof(ArmKeys.Common.LatencyRecord.Histogram), histogram)];
    }

    /// <summary>The declared paths of the histograms that hold at least one sample.</summary>
    internal static List<string> Histograms(params string[] names) => [.. names.SelectMany(Histogram)];
}

/// <summary>The fixture data and predicates the shape factories share.</summary>
internal static class ShapeData
{
    /// <summary>The protocol a latency-shaped run took under the factory's flags.</summary>
    internal static string ProtocolOf(ShapeFlags flags) => flags.HasFlag(ShapeFlags.UdpOnly)
        ? "udp"
        : flags.HasFlag(ShapeFlags.TcpOnly)
            ? "tcp"
            : "tcp+udp";

    /// <summary>
    /// The counts of a data-driven container, in the order the run observed them: an empty container is
    /// written empty rather than left out.
    /// </summary>
    internal static Dictionary<string, long> Counts(ShapeFlags flags, (string Name, long Count)[] counts) =>
        flags.HasFlag(ShapeFlags.EmptyContainers)
            ? new Dictionary<string, long>(StringComparer.Ordinal)
            : counts.ToDictionary(static count => count.Name, static count => count.Count, StringComparer.Ordinal);

    /// <summary>An rcode member name: the decimal value of a 4-bit rcode.</summary>
    internal static bool IsRcodeName(string member) =>
        int.TryParse(member, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var code)
        && code is >= 0 and < 16;

    /// <summary>A query-type member name: one of the five the arm names, or <c>typeN</c> for any other type.</summary>
    internal static bool IsQueryTypeName(string member) =>
        member is "A" or "AAAA" or "CNAME" or "HTTPS" or "TXT"
        || (member.StartsWith("type", StringComparison.Ordinal)
            && ushort.TryParse(member.AsSpan("type".Length), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out _));
}
