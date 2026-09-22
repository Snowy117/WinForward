# Implementation plan — session-creation-cost redo

## Status (2026-09-22): all steps executed

- Steps 1–2 (harness + gates) done; `trellis-check` pass re-verified all four gates green
  (inspectcode 0 issues, format exit 0 empty, build 0 warnings, 804 tests).
- Step 3 A/B gate reproduced (in-process ≤1 B of the archived values; harness share 75,489.6).
- Step 4 campaign done: 6 framework-ladder runs, 6 probe runs + 2 provenance-confirmation runs,
  3 stage runs, 27 churn wave cells, 6 sustained runs, 1 in-process sanity cell, 3 footprint runs
  (`research/raw/`, 52 `.log`/`.load` pairs + 37 `.jsonl`).
- Steps 5–6 done: three corrected research docs + errata banners on the three archived reports;
  a `trellis-check` numeric audit found and this session fixed 5 doc defects (dial-delay claim,
  two basis labels, teardown share, S3 bracket) plus label nits.
- Step 7 done: `hot-path.md` §3 framework bullet re-anchored (clean values, out-of-process rule,
  falsification sentence) and §6 extended; T1/T2 untouched.
- Deviations from the plan (all documented): the external probe is echo-fed (design §4.1), the
  helper exposes `ControlEndpoint` only, and one probe-shape expectation in design §9 was
  superseded by measurement (ASSOCIATE 959.0 vs the pre-run ≈2.9 KB estimate).

Ordered checklist. Each step leaves the tree green; the numbered rollback points are listed in
`design.md` §6. Phase 2 dispatches `trellis-implement` per step and `trellis-check` at the
review gates.

## Step 0 — Pre-flight

- [ ] Confirm branch `feat/transport-lifecycle`, clean tree (`git status`).
- [ ] Read `.trellis/spec/backend/hot-path.md` (§3 ledger + §6 gates — the re-anchor target),
  `.trellis/spec/backend/udp-relay.md` (bound/limit contract that must not change), and
  `.trellis/spec/backend/quality-guidelines.md` (gate policy).
- [ ] Re-read the archived evidence base:
  `archive/2026-09/09-21-session-creation-cost/research/{noop-decomposition,framework-decomposition,churn-measurements,reconciliation-and-decision}.md`.

## Step 1 — Harness: child server mode + parent helper + wiring

- [ ] `Stability/` child mode: `--stability --serve-socks5-udp --flows N [--dial-delay-ms D]`
  builds `EchoReceiver` + `LoopbackSocks5UdpServer`, prints `{"controlPort":P,"echoPort":E}`
  on stdout, exits on stdin EOF / SIGTERM (`design.md` §3).
- [ ] `SoakOptions` + `SoakRunner`: parse/dispatch the new mode; add `--socks5-external` to the
  scenario options.
- [ ] `Stability/ExternalLoopbackSocks5UdpServer.cs`: spawn/handshake/dispose helper
  (`design.md` §4) with the failure behavior spelled out there.
- [ ] `UdpChurnScenario`: `--socks5-external` uses the helper instead of the in-process
  receiver/server (same `--burst-flows` / `--dial-delay-ms` knobs); in-process default intact.
- [ ] `UdpSessionBenchmarks` + `FrameworkSetupBenchmarks`: `[GlobalSetup]` starts the helper
  when `WINFORWARD_BENCH_EXTERNAL_SERVER=1`; otherwise the in-process server (default).
  Use the helper for the *server-using* variants only; relay-socket/self-traffic variants and
  the Noop rows are unchanged.
- [ ] No `src/**` change. Comment style: no narration of the task; only non-obvious constraints.

## Step 2 — Harness gate (rollback point 1)

- [ ] `dotnet build WinForward.slnx -c Release` zero-warning.
- [ ] Smoke: `--stability --serve-socks5-udp --flows 8` starts, prints the handshake line, and
  exits on stdin EOF; `ExternalLoopbackSocks5UdpServer` kills it in `DisposeAsync`.
- [ ] `dotnet test WinForward.slnx -c Release` green.

## Step 3 — A/B artifact gate (mandatory, `design.md` §7)

- [ ] Ladder, out-of-process: `WINFORWARD_BENCH_EXTERNAL_SERVER=1 dotnet run -c Release
  --project benchmarks/WinForward.Benchmarks -- --filter '*FrameworkSetup*' --job short`
- [ ] Ladder, in-process (default): same command without the env var.
- [ ] Record both; expected: create+dispose ≈8.3 KB vs ≈83.7 KB/session; if the delta does not
  reproduce (~75 KB), STOP — re-diagnose before any campaign run.

## Step 4 — Full campaign, out-of-process (raw logs to `research/raw/`)

All runs: one process at a time, capture `/proc/loadavg` + ISO timestamp before/after
(`*.load` files). ≥3 runs per cell. Use the archived command lines with the additions below.

- [ ] Framework ladder: `WINFORWARD_BENCH_EXTERNAL_SERVER=1 dotnet run -c Release --project
  benchmarks/WinForward.Benchmarks -- --filter '*SessionSetupDecomposition*' '*FrameworkSetup*'
  --job short` (3 runs)
- [ ] Real probe: `... -- --filter '*UdpSession*' --job short` (3 runs)
- [ ] Noop probe + stage decomposition (clean cross-check, R4): plain `--filter '*UdpSession*'`
  / `'*SessionSetupDecomposition*'` runs (3 runs each)
- [ ] Churn wave matrix: 9 cells (N∈{48,128,256} × D∈{0,5,20} ms), 3 runs each:
  `... --stability --scenario udpchurn --burst-flows N --dial-delay-ms D --churn-waves 1
  --socks5-external --output research/raw/udpchurn-run{R}-N{N}-D{D}.jsonl`
- [ ] Churn sustained: D∈{0,5} ms, 3 runs each:
  `... --stability --scenario udpchurn --burst-flows 48 --dial-delay-ms D --churn-waves 0
  --duration 30 --socks5-external --output research/raw/udpchurn-sustained-D{D}-run{R}.jsonl`
- [ ] Footprint: `... --stability --scenario footprint --output research/raw/footprint-run{R}.jsonl`
  (3 runs; fake transports, unchanged path)
- [ ] Budget: the wave+sustained campaign took ~40 min on this box in the original task; keep
  runs sequential and record observed load.

## Step 5 — Analysis (rollback point 3)

- [ ] Corrected framework table (ladder + probe-derived difference) with the harness share
  quantified; the probe-derived difference must attribute ≥95 % to named components.
- [ ] Corrected churn tables (wave matrix + sustained) and the corrected whole-cycle figure;
  re-attribute the response/retire residue with the receiver now out of process. The external
  probe is echo-fed (design §4.1): report it with that shape named and use it to attribute the
  recorded docs' previously-unattributed "1.6–3.3 KB/session response/retire residue".
- [ ] R4 cross-check table: recorded vs re-run for Noop / stages / footprint, within recorded
  spreads or explained.
- [ ] Recompute the reconciliation arithmetic (clean client framework + bookkeeping + residue
  ≈ churn whole cycle) and state the corrected anchors for T1/T2/T3.

## Step 6 — Research docs + errata (rollback point 4)

- [ ] `research/framework-decomposition.md` (corrected), `research/churn-measurements.md`
  (corrected), `research/reconciliation-and-decision.md` (corrected; includes the re-ranked
  reduction list against corrected magnitudes and the re-anchored budget with falsification
  conditions). Follow the original docs' conventions (shape ladder, marginal convention,
  spread discipline).
- [ ] Errata banners (2–4 lines each, linking to the corrected numbers) on the three archived
  reports; archived bodies stay untouched as the historical record.

## Step 7 — Spec re-anchor (rollback point 5)

- [ ] `hot-path.md` §3: T3 framework sub-anchor and churn anchor corrected (clean values);
  state explicitly that the pre-redo figures were harness-inflated and why; keep T1/T2 intact
  (they were measured clean); update §6 test requirements if they quote affected numbers.
- [ ] Diff reviewed as its own change before commit.

## Step 8 — Gates and commit

- [ ] `dotnet format WinForward.slnx --severity info --verify-no-changes --no-restore` → exit 0,
  empty output.
- [ ] `jb inspectcode -f=Xml -e=HINT -o=/tmp/jb-inspectcode.xml WinForward.slnx` → zero `<Issue>`.
- [ ] `dotnet build -c Release` zero-warning; `dotnet test -c Release` green.
- [ ] Commit (conventional, referencing the task); then `/trellis:finish-work`.

## Review gates

| Gate | Where | What must hold |
|---|---|---|
| Harness gate | end of Step 2 | default path byte-identical behavior; no `src/**` diff |
| Artifact gate | Step 3 | ~75 KB/session in-process vs out-of-process delta reproduced |
| Attribution gate | Step 5 | ≥95 % named attribution; no 64 KiB-class term |
| Doc gate | Step 6 | every corrected number carries shape + run spread + raw path |
| Spec gate | Step 7 | anchors carry falsification conditions |
