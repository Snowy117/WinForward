# C1 — execution plan

Ordered checklist. Every step ends green on its own; do not batch steps 5–6.

## 1. The primitive

- [ ] Create `src/WinForward.Runtime/QuiescenceScope.cs` with `QuiescenceScope` + `WorkLease` per
      `design.md` §2–§6: `internal sealed class`, `internal struct` lease, `namespace
      WinForward.Runtime`. Variant A (`lock`) first — it is the simpler shape and the baseline.
- [ ] Honour the allocation contract (§4): no allocation in `TryEnter`/`Exit`, the drain TCS created
      only at seal with `RunContinuationsAsynchronously`, one CTS in the constructor.
- [ ] Document on the type: the lock order (`ownerGate → scope.gate`, scope gate is a leaf), the
      "at most one lease copy may be disposed" rule, and "`Token` must not be read after
      `DrainAsync` completes".
- [ ] Do **not** rethrow in `Run`'s child (§6 hazard); the scope's `Fault` is the observation point.

## 2. Unit tests

- [ ] `tests/WinForward.Core.Tests/QuiescenceScopeTests.cs` covering every bullet in `design.md` §9,
      including the unobserved-exception test for a faulted `Run` child, the double-dispose test, and
      the concurrency stress test bound by a timeout.

## 3. Allocation gate

- [ ] Add the warm `TryEnter`/`Exit` zero-byte gate to `tests/WinForward.Core.Tests`
      (`HotPathAllocationGateTests.cs` or a sibling file), using the existing
      `GC.GetAllocatedBytesForCurrentThread()` idiom. `HotPathAllocationGateTests` stays green.

## 4. Contract spec

- [ ] Create `.trellis/spec/backend/async-lifetime.md` (§8): glossary, I1/I2, the primitive's durable
      contract (surface, D1–D9, allocation rule, lock order), the `Run` admission rules, and a stub WF
      rule table for C2. English, per `backend/index.md`.
- [ ] Add its row to `.trellis/spec/backend/index.md`.

## 5. Gate choice (variant A vs B)

- [ ] Add `benchmarks/WinForward.Benchmarks/Perf/QuiescenceScopeBenchmarks.cs` running both gate
      variants through the same operations (`[MemoryDiagnoser]`, matching the existing `Perf/` style).
- [ ] Implement variant B (packed-word CAS) behind the same tests; confirm §2 tests still pass.
- [ ] Measure:
      `dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- --filter *QuiescenceScope* --job short`
      and `dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- --stability --scenario udp --quick`.
- [ ] Decide per `design.md` §7's rule (allocation is the exact gate; sub-2× deltas are noise → prefer
      `lock`), keep the winner, delete the loser, and save the raw output under `research/`.

## 6. Validation and report

- [ ] `dotnet format WinForward.slnx --severity info --verify-no-changes --no-restore` → exit 0, empty
      output.
- [ ] `dotnet build WinForward.slnx -c Release` → zero warnings.
- [ ] `dotnet test WinForward.slnx -c Release` → green (baseline 744 + the new tests).
- [ ] `jb inspectcode -f=Xml -e=HINT -o=/tmp/jb-inspectcode.xml WinForward.slnx` → zero `<Issue>`.
- [ ] Report: files changed, the measured numbers, the chosen variant, and any deviation from
      `design.md`.

## Risky files and rollback

Everything C1 touches is new: `QuiescenceScope.cs`, `QuiescenceScopeTests.cs`, the benchmark class,
`async-lifetime.md`, and one row in `index.md`. Rollback is deleting those and the row — no owner, no
packet path, and no existing test is modified except the additive allocation gate.

## Follow-up checks before `task.py start`

- [ ] `implement.jsonl` and `check.jsonl` have real entries (this file does not replace them).
- [ ] No other child has started editing `src/WinForward.Runtime/` — C1 owns no shared file with C2,
      and C3/C4 wait for C1.
