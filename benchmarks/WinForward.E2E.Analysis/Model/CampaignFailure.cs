using System.Text.Json;

namespace WinForward.E2E.Analysis.Model;

/// <summary>
/// One campaign failure the orchestrator recorded: what failed, in which pass and row, at which stage,
/// and whether it was a clean-slate refusal.
/// </summary>
/// <param name="When">The instant the orchestrator recorded it, as it wrote it.</param>
/// <param name="Pass">The pass ordinal, or null for a campaign-level failure.</param>
/// <param name="Row">The row id, or null when the failure is not about one row.</param>
/// <param name="Arm">The arm name, or null when the failure is not about one arm.</param>
/// <param name="Stage">One of the orchestrator's stages: preflight, clean-slate, product-start, target, row, teardown.</param>
/// <param name="Error">The one-line reason.</param>
/// <param name="Detail">The orchestrator's extra context, or null.</param>
/// <param name="CleanSlate">Whether the failure refused a pass's clean slate.</param>
/// <remarks>
/// <b>The stage vocabulary is the record's contract.</b> The analysis does not reinterpret a stage: a
/// clean-slate record is a refused pass and a row-stage record is a row, and an unknown stage is printed
/// as the orchestrator spelled it rather than guessed at.
/// </remarks>
internal sealed record CampaignFailure(
    string? When,
    int? Pass,
    string? Row,
    string? Arm,
    string? Stage,
    string? Error,
    string? Detail,
    bool CleanSlate)
{
    /// <summary>The stage name the orchestrator writes when a pass's clean slate failed.</summary>
    private const string CleanSlateStage = "clean-slate";

    /// <summary>One record out of a failure log, or null when the element carries no stage at all.</summary>
    internal static CampaignFailure? From(JsonElement element)
    {
        var stage = JsonValue.String(element, "stage");
        if (string.IsNullOrEmpty(stage))
        {
            return null;
        }

        var pass = JsonValue.Number(element, "pass");
        return new CampaignFailure(
            JsonValue.String(element, "when"),
            pass is null ? null : (int)pass.Value,
            JsonValue.String(element, "row"),
            JsonValue.String(element, "arm"),
            stage,
            JsonValue.String(element, "error"),
            JsonValue.String(element, "detail"),
            JsonValue.IsTrue(element, "cleanSlate") || string.Equals(stage, CleanSlateStage, StringComparison.Ordinal));
    }
}
