# Design: evidence workstream

## Archive layout

```
~/bench-archive/
  2026-10-06-validation/
    raw/                     # the validation pass tree (copied, read-only)
    environment.json
    SHA256SUMS
  2026-10-08-e5b2/
    campaign-e5b2.zip
    ledger-main.jsonl
    ledger-direct.jsonl
    environment.json
    orch.log                 # if present
    clock-probe.out
    analysis/                # analyzer output
    reanalysis-notes.md
    SHA256SUMS
```

The archive is not a git tree. A small manifest under `benchmarks/results/` is the only committed
record; it names the archive path and hashes.

## Manifest schema

```json
{
  "campaign": "2026-10-08-e5b2",
  "caliber": {"plan": "plans-windows/full-shape-plan.json", "sha256": "..."},
  "rows": ["wf-aot-opt", "..."],
  "passes": 3,
  "archive": "~/bench-archive/2026-10-08-e5b2",
  "files": [{"path": "campaign-e5b2.zip", "sha256": "...", "bytes": 0}],
  "analysis": {"tables": "analysis/tables.md", "verdict": "analysis/verdict.json"}
}
```

## Baseline and re-analysis

- Baseline: `bash benchmarks/WinForward.E2E.Analysis/scripts/analyze.sh --raw <archive>/raw
  --ledger <archive>/ledger-main.jsonl --ledger <archive>/ledger-direct.jsonl --out
  <archive>/analysis`.
- B10: the same command with the new analyzer; `reanalysis-notes.md` records the verdict changes
  (especially `unevaluable`, Holm family, band) without editing the old output.
