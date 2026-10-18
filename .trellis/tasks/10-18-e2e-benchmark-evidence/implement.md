# Implementation: evidence workstream

## B1 — Archive and baseline

- [ ] Create the two archive roots under `~/bench-archive/`.
- [ ] Copy the zip, ledgers, environment and logs; compute `SHA256SUMS` in each root.
- [ ] Run the analyzer on the archived tree; store `analysis/`.
- [ ] Write `benchmarks/results/2026-10-08-e5b2.manifest.json`; verify hashes.
- [ ] Commit `chore(e2e): ...` with the manifest and notes only.

## B10 — Re-analysis

- [ ] Run the new analyzer on the archived E5-b2 tree.
- [ ] Append the new-rules section to `reanalysis-notes.md`; list changed verdicts and why.
- [ ] Commit `docs(e2e): ...`.
