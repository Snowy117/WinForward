# B9 release campaign — final record

- Campaign `2026-10-19-release` (attempt 2). Orchestrator pid 3692 started 2026-10-19 16:01
  host / 01:01 VM and completed 23:49 host / 08:49 VM: 7 passes, 9 rows each, 63 loaded runs,
  empty `failures.jsonl`, `=== no failures ===`.
- Attempt 1 was voided by a planned Windows Update restart during pass 4 and is archived at
  `~/bench-archive/2026-10-19-b9-attempt1-aborted/` (SHA256SUMS verified).
- Publish incident: the automated download stopped at 51 % and `unzip` failed (`PUBLISH_EXIT=9`);
  the VM zip was complete (4,617,909 B, `unzip -t` clean). Steps 2–5 of `publish-campaign.sh`
  were run by hand against that zip: extract, copy both ledgers, same release invocation.
  `release-inputs.json`: total 7, discarded [1], measured [2..7], n 6, required 6,
  insufficient false; thresholds `8ae8e31f…`, caliber `da148a1d…`, warmup 5 s.
- Archive `~/bench-archive/2026-10-19-release/`: 1,376 files, 33 MB; `sha256sum -c` passes;
  contains `raw/`, the four release documents, `thresholds.json`, `coverage.json`, both frozen
  ledgers (92,986 / 32,637 lines, identical to `raw/`), logs, Proxifier profile and incident
  note, `report.md`. `manifest.json` there records passes/artifacts/ledger hashes.
- Committed manifest `benchmarks/results/2026-10-19-release.manifest.json`; report
  `benchmarks/results/2026-10-19-release/README.md`.
- Outcome: the memory family is **not comparable** — preflight 1,831 MB, pass 2 start 6,367 MB,
  pass 6 5,310 MB, pass 7 4,075 MB; the 5 GB artificial occupancy was reclaimed by dynamic
  memory. The analyzer prints `different` for all three opponents, but the campaign's
  pre-registered environment floor (5,500 MB) was violated, so the report withholds the primary
  claim. Loss is `unevaluable` (zero-variance); REL fails at 18.8–19.6 pp on every measured row;
  latency and CPU are exploratory/report-only.
- Clock: offsets +1456.7 → +1955.1 ms (VM − host), drift 67.6–74.1 ms/pass (limit 100), residual
  ≤0.97 ms; corrected ledger 58 connection / 1 datagram mismatch against 293 / 265 uncorrected.
- Gates: 20 cells invalidated (all proxybridge LATLOAD p50/p99 and DNS/DNSALT rate/p50 on passes
  3, 4, 6, 7); findings 7 correctness-failure (proxybridge MIX foreign connections in all 7
  passes), 4 path-interference (direct-lane latency), 28 harness-error (gate invalidations),
  570 measurement-caveat (uncorrected ledger mismatches, control drift, ceiling), 8 informational.
- Instrument fixes carried: Proxifier global `Default.ppx` (`E2E3B10E…`) installed for the
  LocalSystem service; ProxiFyre `app-config.json` restored (`FE69DA07…`); WinDivert re-registered
  after the reboot and Running; Windows Update paused/disabled; firewall already off.
- Next: commit report + manifest + these research notes; mark B8/B9 done; archive the four
  remediation tasks plus 10-11; final goal completion.
