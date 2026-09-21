# Quiescence scope primitive

## Goal

Implement the `QuiescenceScope` primitive (`design.md` §2): allocation-free counted in-flight
tracking (`TryEnter`/`WorkLease`), seal-and-join `DrainAsync` (single-flight, idempotent), an owned
linked `CancellationTokenSource`, first-fault recording, and the cold-path `Run` door. Ship its
durable contract as `.trellis/spec/backend/async-lifetime.md`. No owner changes.

## Requirements

- `QuiescenceScope` + `WorkLease` live in `src/WinForward.Runtime/QuiescenceScope.cs`, namespace
  `WinForward.Runtime` (`design.md` §2, P1/P2).
- Surface per `design.md` §2.1; semantics D1–D9 per §2.2. `WorkLease` is a **mutable** struct so
  `Dispose` can be idempotent, must stay unboxed, and is used as a local (`design.md` §2.3).
- `TryEnter`/`Exit` allocate 0 bytes; the drain `TaskCompletionSource` is created at most once, at
  seal.
- The gate implementation (`lock` vs packed-word CAS) is chosen by measurement: prototype both and
  compare with the full `benchmarks/WinForward.Benchmarks` suite, recording the numbers
  (`design.md` §2.3).
- Lock order: the scope's gate is a leaf; `ownerGate → scope.gate` is the only permitted order
  (`design.md` §2.4).
- Creates `.trellis/spec/backend/async-lifetime.md`: glossary, I1/I2, the primitive's durable contract,
  and the `Run` door's admission rules (`design.md` §3.5).
- No owner is migrated in this task.

## Acceptance Criteria

- [ ] Unit tests cover seal/join atomicity (D5), late-enter rejection (D2/P5), idempotent and
      single-flight `DrainAsync` (D4), first-fault recording without sibling cancellation (D3/P6),
      nested composition (D6), and owned-CTS release (D7).
- [ ] An allocation-gate test proves a warm `TryEnter`/`Exit` pair allocates 0 bytes, and
      `HotPathAllocationGateTests` stays green.
- [ ] The `lock` vs CAS comparison is recorded with benchmark numbers, and packet-path throughput is
      at least today's.
- [ ] `async-lifetime.md` exists, states the primitive contract plus I1/I2, and is registered in
      `.trellis/spec/backend/index.md`.
- [ ] Full gates green (format / Release zero-warning / tests / `jb inspectcode`).

## Notes

- Depends on nothing; it is the blocking prerequisite for C3 and C4.
- This child is a complex task: write its own `design.md` and `implement.md` before `task.py start`,
  referencing the parent `design.md` §2, §2.3, §2.4, and §3.5.
- **Follow-up recorded, not done here:** `HotPathAllocationGateTests.EstablishedUdpDatagramPathAllocatesNoManagedBytes`
  is green only inside the full suite, because its measured window spans `await`s that may resume on
  another pool thread while it reads the per-thread allocation counter (run alone: 4/4 failures, 600 B
  observed; the `HotPathAllocationGateTests` class alone: 1 of 10 failing). The gate file is
  unmodified by this task and the weakness is pre-existing, so hardening it is outside C1's boundary —
  it is scheduled in `09-20-lifecycle-migration-cluster`, which changes the span-send path that gate
  guards. Contract recorded in `hot-path.md` → "An allocation gate must evaluate on one thread".
