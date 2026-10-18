namespace WinForward.E2E.Analysis.Model;

/// <summary>
/// One pass's recorded permutation: the seed it was drawn from, the generator's identifier and the
/// product rows in the order the pass ran them.
/// </summary>
/// <param name="Seed">The pass's own seed; <see cref="OrderPermutation.MixedSeed"/> derives it from the campaign's base seed.</param>
/// <param name="Algorithm">The generator identifier the orchestrator wrote, or null when the file names none.</param>
/// <param name="Rows">The product rows in run order.</param>
internal sealed record OrderRecord(int Seed, string? Algorithm, IReadOnlyList<string> Rows);
