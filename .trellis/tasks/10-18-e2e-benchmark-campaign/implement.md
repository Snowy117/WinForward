# Implementation: campaign workstream

## B8 — Calibration

- [x] Preflight and record clock, firewall, versions, `BASE-singbox`.
- [x] Run three reference passes; write `thresholds.json` and the sensitivity proof.
- [x] Archive the calibration tree; commit the band file and record.
- [x] Report the band and the expected release duration to the user.

Result: `thresholds.json` `8ae8e31f…`, `coverage.json` `366e4523…`; latency p50 and CPU demoted to
exploratory (B 0.23770 and 0.12683 exceed the 0.05 and 0.10 business effects), memory primary at
B 0.10, loss primary but zero-variance (`unevaluable`). Calibration tree archived at
`~/bench-archive/2026-10-18-calibration/`.

## B9 — Release campaign

- [x] Run seven passes with the release caliber; archive every attempt.
- [x] Analyze with the pre-registered band.
- [x] Write the report and archive; verify checksums and manifest.
- [x] Commit the report and manifest; report to the user.

Result: `2026-10-19-release`, attempt 2 (attempt 1 voided by a Windows Update restart and archived).
Seven passes, pass 1 discarded, six measured; `release-inputs.json` total 7 / n 6 / insufficient
false. Memory family not comparable (dynamic memory fell below the pre-registered 5,500 MB floor
from pass 6), loss `unevaluable`, REL capability fail, latency/CPU report-only: no primary
inferential claim. Report `benchmarks/results/2026-10-19-release/README.md`; manifest
`benchmarks/results/2026-10-19-release.manifest.json`; archive
`~/bench-archive/2026-10-19-release/` (1,376 files, `sha256sum -c` passes).

