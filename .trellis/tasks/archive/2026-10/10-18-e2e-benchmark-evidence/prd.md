# E2E benchmark evidence: archive, manifest, re-analysis

## Goal

Make the campaign evidence independent of `/tmp`: archive the two historical campaigns with
checksums, publish a checked-in manifest, reproduce the baseline analysis, and later re-analyze the
archived E5-b2 tree under the new verdict rules.

## Scope

- B1: archive `2026-10-06-validation` and `2026-10-08-e5b2` under `~/bench-archive/` with
  `SHA256SUMS`; add a checked-in manifest under `benchmarks/results/`; run the committed analyzer on
  the archive; write the baseline section of `reanalysis-notes.md`.
- B10: re-run the new analyzer on the archived E5-b2 tree; record the differences; keep the old
  conclusions intact and label the new ones.
- Maintain the report template and the archive layout used by the campaign child.

## Non-goals

- No raw campaign data in git.
- No changes to the old conclusions beyond an explicit re-analysis note.
- No new campaign runs.

## Acceptance Criteria

- [ ] Both archive roots exist with a `SHA256SUMS` that verifies.
- [ ] `benchmarks/results/2026-10-08-e5b2.manifest.json` records campaign id, caliber, plan hashes,
      file hashes and archive path; it is checked in.
- [ ] The committed analyzer runs on the archived E5-b2 tree and exits 0.
- [ ] `reanalysis-notes.md` has a baseline section and, after B10, a new-rules section with the
      differences.
- [ ] No raw data is committed; `git status` is clean for this task's paths.

## Dependencies

- B0 (decision ledger) for the caliber and manifest fields.
- B5 (verdict model) for the new-rules re-analysis.
