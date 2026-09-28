# Implement — ordered plan

Conventions: one process at a time; ≥3 runs per batch; raw logs + `/proc/loadavg` under
`research/raw/`; every number quoted with its window shape. `src/**` changes are behavior-zero
and land as one reversible commit group per candidate (design §4).

## Step 0 — Prep

- Confirm base: branch/HEAD recorded in `task.json`; worktree clean.
- Create `research/raw/`; copy `parse-bdn-alloc.py` from
  `archive/2026-09/09-22-session-creation-cost-redo/research/tools/` into this task's
  `research/tools/`.
- Context manifests (`implement.jsonl`, `check.jsonl`) curated before `task.py start`; `task.py
  validate` green.

## Step 1 — Probe cases (benchmark-side only, no src change)

Add to `benchmarks/WinForward.Benchmarks/Perf/SessionSetupDecompositionBenchmarks.cs`, per
design §3:

1. `StageT1_SessionTeardownInsideAsync` — C2-style direct fixture, disposal inside the window.
2. `StageT3_ExpiryRetireInsideAsync` — pipeline populate + `RemoveExpiredAsync` inside the window.
3. `StageT5_SessionTeardownNonThrowingParkAsync` — T1 with a benign-cancel park (attribution aid).
4. `ComponentC2a_ContextRecordOnlyAsync` (static-lambda + method-group variants).
5. `ComponentC2b_SessionConstructOnlyAsync`, `ComponentC2c_ScopeConstructOnlyAsync`
   (linked/unlinked), `ComponentC2d_SessionStartOnlyAsync`.
6. `ComponentC2e_MicroCases` — `Lock`, TCS, linked-source per-session sizes.

Rules: existing cases stay byte-identical; new fakes are additive; every new case asserts its
own stop condition; if a new fake adds per-session cost beyond F/H, extend the subtraction
explicitly.

Validation:

```text
dotnet build WinForward.slnx -c Release                                   # zero-warning
dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- --filter '*StageT*' '*ComponentC2*' --job short
# ≥3 full batches, archived; S0–S5/C1/C2 reproduce byte-identically (or within their spreads)
```

Rollback point **R1**: revert the benchmark file (no product impact).

## Step 2 — Attribution and adoption table (research)

- Parse the batches (existing convention); write `research/teardown-split.md` and
  `research/c2-split.md`: per-component B/session, exception counts per sub-case, derivations
  (coordinator ≈ S5−T1; retire-shape delta = T3−S5; T5 = harness-throw share), caveats.
- Assign each exception throw an origin: product / harness / BCL-inherent (code reading + T5).
- Fill the design §4 adoption table with measured sizes; decide A–F adopt/defer; record G's
  sizes for the follow-up pooling decision.

Gate: splits documented; every attributed throw has an origin; adoption table complete.

## Step 3 — Implement adopted candidates (src)

Order (cheapest/zero-risk first; A per its attribution):

1. **C** — cache the two method-group delegates as fields.
2. **B** — `UdpProxySessionContext` → `readonly record struct` (fallback: parameter-passing).
3. **D** — merge the two receive async methods.
4. **E** — drain-cell trim (skip `_joined` when idle at seal; keep the documented order).
5. **F** — coordinator sweep trim (only if T3 shows it material).
6. **A** — remove attributed product-origin teardown throws.

Per candidate: focused tests for the touched behavior; the existing contract tests
(`UdpProxy*` session lifecycle, `QuiescenceScope*`, admission/cooldown/capacity, the warm 0-B
gates) stay green; `dotnet build -c Release` zero-warning.

Rollback: per-group `git revert` (each group is self-contained).

## Step 4 — Re-measure (before/after, ≥3 runs)

```text
dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- --filter '*SessionSetupDecomposition*' --job short
dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- --filter '*UdpSession*' --job short
dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- --stability --scenario udpchurn \
  --burst-flows 48 --dial-delay-ms 0 --churn-waves 1 --socks5-external --output research/raw/churn-confirm.jsonl
```

Before/after tables appended to the research docs; every adopted candidate either reproduces
its expected saving or is reverted (policy from design §5).

## Step 5 — Anchors and spec

- Update `hot-path.md` §3/§6: measured T/C2 values, any moved T1/T2 numbers, new sub-anchors
  only if the split earns them (guard band + falsification condition); reconcile the
  `5,276` (pre-redo) vs `5,266.2` (redo) product-shaped Noop note.
- Route through `trellis-update-spec` (Phase 3.3) as its own reviewed diff.

## Step 6 — Full quality gates

```text
dotnet build WinForward.slnx -c Release
dotnet test WinForward.slnx -c Release
dotnet format WinForward.slnx --severity info --verify-no-changes --no-restore   # empty output
jb inspectcode -f=Xml -e=HINT -o=/tmp/jb-inspectcode.xml WinForward.slnx        # zero <Issue>
```

## Step 7 — Handoff

`research/` finalized (method, raw, splits, before/after); summary of adopted/deferred
candidates with sizes; hand to `trellis-check`; archive via `/trellis:finish-work`.

## Risky files / rollback points

| File | Risk | Rollback |
|---|---|---|
| `benchmarks/.../SessionSetupDecompositionBenchmarks.cs` | additive only | R1 (revert file) |
| `src/WinForward.Runtime/QuiescenceScope.cs` | D-contract semantics (cancel/join/order) | per-group revert; contract tests |
| `src/WinForward.Runtime/UdpProxy/UdpProxySession.cs` | teardown/ordering, warm gates | per-group revert; 0-B gates |
| `src/WinForward.Runtime/UdpProxy/UdpSessionSetup.cs` | setup path, delegates | per-group revert |
| `src/WinForward.Runtime/UdpProxy/UdpProxyCoordinator.cs` | expiry sweep only (F) | per-group revert |
| `.trellis/spec/backend/hot-path.md` | anchors are regression contracts | reviewed diff, own commit |

## Pre-start checks

- [ ] `prd.md` / `design.md` / `implement.md` reviewed and approved (final planning summary).
- [ ] `implement.jsonl` / `check.jsonl` curated (non-empty, real spec/research entries).
- [ ] `python3 ./.trellis/scripts/task.py validate 09-22-udp-teardown-session-tier-alloc` green.
