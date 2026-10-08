using System.Text.Json;
using WinForward.E2E.Contracts;

namespace WinForward.E2E.Analysis.Model;

/// <summary>
/// One client run: a row of a pass, one lane of a dual phase, or the flat tree itself.
/// </summary>
/// <remarks>
/// <para><b>Loaded runs, not rows.</b> The same row exists once per pass, so a three-pass campaign
/// holds three runs of it. <see cref="CampaignModel.Rows"/> is the flat list §15 prints a line for;
/// <see cref="CampaignModel.RowIds"/> is the distinct set the campaign is described by.</para>
/// <para><b>The run id is owned by the row the lanes name.</b> A shipped orchestrator writes one
/// <c>&lt;pass&gt;/dual</c> for the whole pass and rebuilds it per dual row, so a lane's own directory
/// cannot say which row it belongs to; the row is named by the lane's label instead, which is why
/// <see cref="RunId"/> is the only mutable member here.</para>
/// </remarks>
internal sealed class ClientRun
{
    internal ClientRun(string passId, string runId, string directory)
    {
        ArgumentNullException.ThrowIfNull(passId);
        ArgumentNullException.ThrowIfNull(runId);
        ArgumentNullException.ThrowIfNull(directory);

        PassId = passId;
        RunId = runId;
        Directory = directory;
    }

    /// <summary>The pass this run belongs to; <c>flat</c> for a flat tree's implicit pass.</summary>
    internal string PassId { get; }

    /// <summary>The row's id, or the row the dual lanes name; rewritten by the dual-owner rule.</summary>
    internal string RunId { get; set; }

    /// <summary>The directory the run was read from.</summary>
    internal string Directory { get; }

    /// <summary>The parsed <c>run.json</c>, or null when the file is missing, empty or unreadable.</summary>
    internal JsonElement? Document { get; set; }

    /// <summary>The run's own <c>label</c>, when it is a string.</summary>
    internal string? Label => JsonValue.String(Document, ArmKeys.Run.Label);

    /// <summary>The arms found on disk, in the order the reference loads them.</summary>
    internal ArmTable Arms { get; } = new();

    /// <summary>The <c>config*</c> files the row published, by path order, with their digests.</summary>
    internal List<ConfigFile> Configs { get; } = [];

    /// <summary>How many <c>config*</c> files the row published.</summary>
    internal int ConfigCount => Configs.Count;

    /// <summary>The parsed <c>proxy-truth.json</c>, or null when it is missing or unreadable.</summary>
    internal JsonElement? ProxyTruth { get; set; }

    /// <summary>The dual phase's proxied lane, or null.</summary>
    internal ClientRun? DualProxied { get; set; }

    /// <summary>The dual phase's direct lane, or null.</summary>
    internal ClientRun? DualDirect { get; set; }

    /// <summary>The dual phase's <c>proxy-truth.json</c>, or null.</summary>
    internal JsonElement? DualTruth { get; set; }

    /// <summary>Everything about the run that could not be read, in the order it was noticed.</summary>
    internal IList<string> LoadErrors { get; } = [];
}
