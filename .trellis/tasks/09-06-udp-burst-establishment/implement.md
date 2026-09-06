# Implementation plan — UDP flow-establishment burst benchmark

Ordered checklist. Validation gate after every step: zero-warning build
(`dotnet build -c Release` — `TreatWarningsAsErrors` is on).

## Steps

1. [x] `SoakOptions`: add `SoakScenario.Burst` + `"udpBurst"` parsing; add
       `--burst-flows` (default 48, positive int) and `--dial-delay-ms` (default 0,
       int ≥ 0) following the existing option-parse helpers.
2. [x] `SoakRunner`: register `("udpBurst", UdpBurstScenario.RunAsync)` and add it to
       the `All` list.
3. [x] `LoopbackSocks5UdpServer`: optional `TimeSpan associateDelay` constructor
       parameter (default `default`); `Task.Delay` before the UDP-ASSOCIATE reply in
       `HandleControlAsync`. Existing call sites compile unchanged.
       - Validation: `dotnet build -c Release` 0 warnings; `dotnet test` 501/501 green.
4. [x] `UdpBurstScenario` per design.md: warmup → control (5 s) → burst (adaptive
       timeout) → post (5 s) → drain (2 s) → single `udp.burstEstablishment` row.
       - Validation: smoke run (8 burst / 4 bg / 500 pps / delay 0) exit 0, one sane
         row: 8/8 first responses, p50 3.40 ms, all windows loss 0, unattributed 0.
5. [x] Sanity vs the limiter model:
       `--burst-flows 48 --dial-delay-ms 50 --flows 4 --pps 500` ⇒ timeToLast ≈
       ceil(48/8)×50 ms = 300 ms ± noise; p99 grows with delay.
       - Validation: 48×50 → timeToLast **324.4 ms** (model 300, +8%);
         48×100 → **621.5 ms** (model 600, +3.6%); `timeToIssueMs` < 0.4 ms.
6. [x] Baseline matrix (Linux dev box, fixed `--seed 42`, `--flows 16 --pps 4000`,
       `--payload-bytes 512`, `--output` into
       `benchmarks/results/2026-09-06-udp-burst/`):
       bursts 8/16/32/48/64/128 × dial delays 0/25/100 ms (18 runs) + one
       instrumented (census) pass at 48 × 25.
       - Deviation: census pass replaced by a TTL-loss corner probe
         (`128 × 4000 ms`) — the census is compile-time opt-in in the landed
         implementation and every matrix point shows zero rejection/drop counts, so
         the probe produces the loss-localization signal the census would have
         zeroed out (93.75 % first-datagram loss at the corner; see results README).
7. [x] Write `benchmarks/results/2026-09-06-udp-burst/README.md`: machine, runtime,
       invocation, table of (burst × delay) → p50/p99/timeToLast/background-loss,
       interpretation, and the follow-up recommendation (fix children vs environment
       ceiling). Update `benchmarks/README.md` scenario docs.
8. [x] Repo hygiene: full `dotnet test` green (501/501); `dotnet build -c Release`
       zero-warning; existing scenarios' quick runs verified by trellis-check
       (`--scenario udp --quick`, `--scenario baseline --quick` — same metric fields,
       loss 0, no series break).
9. [x] Sync parent PRD (`08-30-proxy-perf-stability`) child-task map with outcome
       (row added; flip to completed at archive time); proceed to Phase 3 (spec update
       if any harness convention was learned, commit).
       - Spec update done: `.trellis/spec/backend/udp-relay.md` gained the measured
         TTL × limiter-wave burst envelope contract (loss begins at
         N > 8 × floor(TTL/D); matrix re-run is the acceptance gate for any change
         there).
       - Follow-up children created per findings: `09-06-udp-burst-ttl-attribution`
         (small fix, matrix-gated) and `09-06-local-mux-transport` (structural
         research, go/no-go memo). Commit done with user approval.

## Review gates

- After step 3: no product-code behavior change (benchmarks project only).
- After step 6: numbers reviewed before any fix-task proposal is written.
- Step 8 is the last-iteration full-scope check (trellis-check).

## Rollback

All changes live in `benchmarks/WinForward.Benchmarks` (new scenario file + three small
additive diffs) plus docs. Rollback = delete `UdpBurstScenario.cs`, revert the three
diffs; no product code involved.
