using System.Text.Json;

namespace WinForward.E2E.Analysis.Model;

/// <summary>
/// One arm's own file: the <c>result</c> record the arm published, its <c>armSummary</c>, its 1 Hz
/// <c>sample</c> series, its <c>samplerError</c> and <c>error</c> records, and the lines that were not
/// a JSON object.
/// </summary>
/// <param name="Name">The arm's plan name, e.g. <c>LAT</c>.</param>
/// <param name="FileName">The file the records came from, as the run's roster or the directory named it.</param>
/// <param name="Result">The <c>result</c> record, or null when the arm has none (it ran but did not finish).</param>
/// <param name="ArmSummary">The <c>armSummary</c> header record, or null.</param>
/// <param name="Samples">Every <c>sample</c> record, in file order.</param>
/// <param name="SamplerErrors">Every <c>samplerError</c> record, in file order.</param>
/// <param name="Errors">Every <c>error</c> record, in file order.</param>
/// <param name="BadLines">Lines that were not a JSON object; counted rather than thrown on.</param>
/// <remarks>
/// <para><b>The record kinds are the file's own, not a schema.</b> A file may hold several kinds in any
/// order: the arm writes its samples while it runs and its <c>result</c> when it ends.</para>
/// <para><b>A missing file is an empty arm, not an error.</b> The roster can name a file the run never
/// wrote; §15 reports such an arm as present without a result, which is the distinction the campaign's
/// own gap analysis needs.</para>
/// </remarks>
internal sealed record ArmRecords(
    string Name,
    string FileName,
    JsonElement? Result,
    JsonElement? ArmSummary,
    IReadOnlyList<JsonElement> Samples,
    IReadOnlyList<JsonElement> SamplerErrors,
    IReadOnlyList<JsonElement> Errors,
    int BadLines)
{
    /// <summary>
    /// The order arms are loaded in: the roster's own order does not survive a run that wrote a file
    /// the roster never named, so this is the order the reference reads them in.
    /// </summary>
    internal static IReadOnlyList<string> LoadOrder { get; } =
        ["IDLE", "LAT", "LATLOAD", "DNS", "DNSALT", "LOSS", "REL", "THRU", "MIX", "PERSIST", "BASE"];

    /// <summary>
    /// The arm's kind — <c>latency</c>, <c>loss</c>, <c>reliability</c>, <c>throughput</c>, <c>dns</c>,
    /// <c>mix</c> or <c>persistent</c> — which decides what the arm's own denominators are called.
    /// </summary>
    /// <remarks>
    /// The result record is the authority and the summary is only read when there is no result: a run
    /// that ended without one still knows what it was trying to measure, and an arm that wrote a result
    /// carrying no kind is not silently re-described by its header.
    /// </remarks>
    internal string? Kind => Result is not null
        ? JsonValue.String(Result, "kind")
        : JsonValue.String(ArmSummary, "kind");

    /// <summary>The <c>type</c> value a record carries, or the empty string when it carries none.</summary>
    internal const string ResultKind = "result";

    internal const string ArmSummaryKind = "armSummary";

    internal const string SampleKind = "sample";

    internal const string SamplerErrorKind = "samplerError";

    internal const string ErrorKind = "error";
}

/// <summary>
/// One run's arms, keyed by name and kept in the order they were loaded.
/// </summary>
/// <remarks>
/// The order matters downstream: a run's samples are the concatenation of its arms' samples in this
/// order, and the per-pass value lists that the medians are taken over follow it. A set with the same
/// members in another order would produce different <c>(n=…)</c> cells from the same tree.
/// </remarks>
internal sealed class ArmTable
{
    private readonly List<ArmRecords> _arms = [];
    private readonly Dictionary<string, ArmRecords> _byName = new(StringComparer.Ordinal);

    /// <summary>How many arms the run has.</summary>
    internal int Count => _arms.Count;

    /// <summary>The arms in load order.</summary>
    internal IReadOnlyList<ArmRecords> All => _arms;

    /// <summary>The arm names in ordinal order, which is how every table prints them.</summary>
    internal IReadOnlyList<string> SortedNames
    {
        get
        {
            var names = new List<string>(_byName.Keys);
            names.Sort(StringComparer.Ordinal);
            return names;
        }
    }

    /// <summary>Adds an arm; a name seen twice replaces the earlier records, as a dict would.</summary>
    internal void Add(ArmRecords arm)
    {
        ArgumentNullException.ThrowIfNull(arm);

        if (_byName.ContainsKey(arm.Name))
        {
            for (var index = 0; index < _arms.Count; index++)
            {
                if (string.Equals(_arms[index].Name, arm.Name, StringComparison.Ordinal))
                {
                    _arms[index] = arm;
                    break;
                }
            }

            _byName[arm.Name] = arm;
            return;
        }

        _arms.Add(arm);
        _byName.Add(arm.Name, arm);
    }

    /// <summary>Whether the run loaded an arm of this name.</summary>
    internal bool Contains(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        return _byName.ContainsKey(name);
    }

    /// <summary>The arm of this name, or null when the run never loaded one.</summary>
    internal ArmRecords? Find(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        return _byName.GetValueOrDefault(name);
    }
}
