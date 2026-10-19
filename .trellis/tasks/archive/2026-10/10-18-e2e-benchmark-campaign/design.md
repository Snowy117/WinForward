# Design: campaign workstream

## Calibration

- Preflight: clock offset, firewall state, product versions, targets, `BASE-singbox`.
- Three back-to-back release runs on `wf-aot-nativeudp`, same binary, same session.
- For each primary metric: `spread = (max − min) / min`; `band = max(business, 1.5 × spread)`.
- Write `thresholds.json` (schema in the parent `design.md`); if a band is too wide to be useful,
  mark the metric report-only.
- Record the sensitivity proof: injected 1 s delay moves p99.9 to about 1 s.

## Release run

- Seven passes: pass 1 is the discarded warm-up; passes 2–7 are measured.
- Archive every attempt; `attempt` records prior voided attempts.
- Analyze with `--thresholds <file>`; produce `tables.md` and `verdict.json`.
- The report prints the release caliber, primary family, band/MDD, coverage, attempts and limits.

## Archive

- Campaign root under `~/bench-archive/<date>-<campaign>/`; `SHA256SUMS`; checked-in manifest.
