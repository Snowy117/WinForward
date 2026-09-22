# Implement — ordered plan

Conventions: one process at a time; ≥3 runs per batch; raw logs + `/proc/loadavg` under
`research/raw/`; every number quoted with its window shape. `src/**` changes are behavior-zero and
land as one reversible commit group per candidate (design §4).

## Step 0 — Prep

- Confirm base: branch/HEAD recorded in `task.json`; worktree clean.
- Create `research/raw/`; copy `parse-bdn-alloc.py` from
  `archive/2026-09/09-22-udp-teardown-session-tier-alloc/research/tools/` into this task's
  `research/tools/`.
- Context manifests (`implement.jsonl`, `check.jsonl`) curated before `task.py start`; `task.py
  validate` green.

## Step 1 — Probe cases (benchmark-side only, no src change)

Add to `benchmarks/WinForward.Benchmarks/Perf/SessionSetupDecompositionBenchmarks.cs`, per design
§3:

1. `ComponentA1_AssociationTablePreSeed`, `ComponentA2_SessionDictionaryPreSeed`,
   `ComponentA3_CooldownTableConstruction` (pre-seed split).
2. `ComponentA4_SlotAndQueueObjects`, `ComponentA5_CompletionCell`, `ComponentA6_ColdRent`,
   `ComponentA7_EnqueueCycle` (admission components; A7 uses `UdpSetupQueueBudget`,
   `NativeBufferPool` and the slot's queue end to end and must land at ≈0 managed).
3. `ComponentA8_WarmRentAdmission` (production-shaped: recycling admission-only executor) and
   `ComponentA9_DictionaryGrowthAfterPreSeed` (pre-seed 1024 + 1024 adds; amortized per-add).

Rules: existing cases stay byte-identical; new fakes additive and private; keep-alive via sink
fields (never `GC.KeepAlive` over a value type); every case asserts its stop condition.

Validation:

```text
dotnet build WinForward.slnx -c Release                                   # zero-warning
dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- --filter '*ComponentA*' --job short
# ≥3 full batches of the family, archived; existing cases reproduce byte-identically / within spreads
```

Rollback point **R1**: revert the benchmark file (no product impact).

## Step 2 — Attribution and adoption table (research)

- Parse the batches; write `research/admission-split.md`: per-component B/session, the S0
  reconciliation (A1+A2+A3 vs 488.9), the S1 breakdown and the cold-rent overcharge
  (`S1 − A8`), the growth share (A9), and the shape caveats.
- Fill design §4 with measured sizes and adopt/defer decisions per the carried rule
  (≥ ~64 B/session, low/medium risk, memory shape unchanged).

Gate: splits documented; every adopted/deferred item carries a measured size; the cold/warm and
capacity-clamp caveats are named wherever the numbers are quoted.

## Step 3 — Implement adopted candidates (src)

Measured decisions are in design §4 / `research/admission-split.md`: **A is the only conditional
adoption** (inline the per-slot `BoundedSetupQueue` object, gated on the A4b micro-case ≥ 64
B/session); B and C end as recorded deferred verdicts; D is done (probe/doc shapes).

For A: add the `ComponentA4b_BoundedSetupQueueObject` micro-case first and size it; if it clears
the threshold, convert `BoundedSetupQueue` to a mutable struct with the `WorkLease`-style
keep-as-a-direct-field rule (its call sites already use `slot.SetupQueue` member access; watch for
explicit copies in src/tests/benchmarks — captured locals hoist safely, explicit `var copy =`
does not). If the conversion turns out unsafe or awkward, stop and report; defer A with the
measured size instead. Per candidate: focused tests (incl. `BoundedSetupQueueTests` when its
shape is touched), the contract suites (`UdpProxy*`, admission/cooldown/capacity,
`HotPathAllocationGateTests`) stay green; `dotnet build -c Release` zero-warning.

Rollback: per-group `git revert` (each group is self-contained).

## Step 4 — Re-measure (before/after, ≥3 runs)

```text
dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- --filter '*SessionSetupDecomposition*' --job short
dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- --filter '*PopulateSessionsNoop*' --job short
dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- --stability --scenario udpchurn \
  --burst-flows 48 --dial-delay-ms 0 --churn-waves 1 --socks5-external --output research/raw/churn-confirm.jsonl
```

Before/after tables appended to the research doc; every adopted candidate either reproduces its
expected saving or is reverted. T2 (capacity + admission + setup start ≤1,500) re-verified.

## Step 5 — Anchors and spec

- Update `hot-path.md` §3 T2 (measured value, headroom, falsification condition) and the §3
  sub-anchor sentence (capacity 489 / admission 829 / …) with the split and the shape caveats;
  §6 test-requirement numbers if they move.
- Route through `trellis-update-spec` (Phase 3.3) as its own reviewed diff.

## Step 6 — Full quality gates

```text
dotnet build WinForward.slnx -c Release
dotnet test WinForward.slnx -c Release
dotnet format WinForward.slnx --severity info --verify-no-changes --no-restore   # empty output
jb inspectcode -f=Xml -e=HINT -o=/tmp/jb-inspectcode.xml WinForward.slnx        # zero <Issue>
```

## Step 7 — Handoff

`research/` finalized (method, raw, splits, before/after); adopted/deferred summary with sizes;
hand to `trellis-check`; archive via `/trellis:finish-work`.

## Risky files / rollback points

| File | Risk | Rollback |
|---|---|---|
| `benchmarks/.../SessionSetupDecompositionBenchmarks.cs` | additive only | R1 (revert file) |
| `src/WinForward.Runtime/UdpProxy/UdpProxySession.cs`/`UdpProxyCoordinator*.cs` | admission path, warm gates | per-group revert; contract tests |
| `src/WinForward.Core/BoundedSetupQueue.cs` | public Core type + tests; single-slot fast path contract | per-group revert; its own tests |
| `.trellis/spec/backend/hot-path.md` | anchors are regression contracts | reviewed diff, own commit |

## Pre-start checks

- [ ] `prd.md` / `design.md` / `implement.md` reviewed and approved (final planning summary).
- [ ] `implement.jsonl` / `check.jsonl` curated (non-empty, real spec/research entries).
- [ ] `python3 ./.trellis/scripts/task.py validate 09-22-udp-admission-capacity-alloc` green.
