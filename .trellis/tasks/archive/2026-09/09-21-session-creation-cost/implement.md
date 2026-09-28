# Session creation cost — execution plan

Artifacts: `prd.md` (R1–R5), `design.md` (method + outputs). All work is additive under
`benchmarks/**` and `research/**`; no `src/**` edits.

## Checklist

1. **Baseline capture** — re-run `UdpSessionBenchmarks` (Noop + real, 1/100/1000) on current
   HEAD, ≥3 repetitions; archive raw logs under `research/` plus a summary table.
2. **Stage probes** — add S0–S5/T variants + F baseline (design §2) as a new benchmark file
   (`benchmarks/WinForward.Benchmarks/Perf/SessionSetupDecompositionBenchmarks.cs`) or as
   parameterized additions to the existing class; every variant asserts its own readiness
   (non-vacuous).
3. **Noop decomposition run** — execute the stage matrix; write `research/noop-decomposition.md`.
4. **Framework split** — seam-based variants (design §3); write `research/framework-decomposition.md`;
   include the `udp.sessionFootprint` retained numbers alongside.
5. **Churn measurement** — burst matrix + sustained shape (design §4); write
   `research/churn-measurements.md`.
6. **Decision doc** — write `research/reconciliation-and-decision.md` (design §5), including the
   proposed `hot-path.md` §3 re-anchor diff (not applied) and the ranked reduction list with
   `unsafe` variants where applicable.
7. **Gates** — `dotnet build WinForward.slnx -c Release` zero-warning; `dotnet test -c Release`
   green; `dotnet format --verify-no-changes` exit 0 empty; `jb inspectcode` zero issues.
   Benchmark-only additions must not trip the analyzer gates (WF rules cover `src/**` only —
   confirm).
8. **Convergence** — re-read `prd.md` against findings; update R-status; present the decision-doc
   summary at final review.

## Validation commands

```bash
dotnet build WinForward.slnx -c Release
dotnet test WinForward.slnx -c Release
dotnet format WinForward.slnx --severity info --verify-no-changes --no-restore
dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- --filter '*SessionSetupDecomposition*' --job short
dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- --filter '*UdpSession*' --job short   # baseline + regression check
dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- --stability --scenario udpBurst --flows 16 --pps 4000 --burst-flows 48 --dial-delay-ms 5   # wave matrix entry; extend per design §4
```

## Risky files / rollback

- Everything added is under `benchmarks/**` + task `research/**`; rollback = delete the added
  files. No product files touched; if a required seam is missing, stop and record the gap
  (design §1) rather than editing `src/**`.

## Follow-up checks before `task.py start`

- [ ] `prd.md` / `design.md` / `implement.md` internally consistent; no blocking open questions.
- [ ] `implement.jsonl` / `check.jsonl` curated with real entries (no seeds).
- [ ] Final planning summary presented; implementation waits for fresh user approval.
