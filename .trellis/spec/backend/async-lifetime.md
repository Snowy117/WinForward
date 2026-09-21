# Async Lifetime (Quiescence Scopes)

> Established 2026-09-20 by task `09-20-quiescence-scope` (program `09-20-structured-concurrency`).
> This is the durable contract for the `QuiescenceScope` primitive that makes background work
> outlive-safe. It is deliberately **not** `traffic-policy-lifecycle.md`, which owns the
> *traffic-policy* lifecycle (policy domains, loop prevention, idle sweep).

---

## Scope / Trigger

Read this before:

- adding asynchronous work that touches a resource owned by an object with a `DisposeAsync`, or
- writing or reviewing an owner's teardown (`DisposeAsync`, drain, cancellation ordering), or
- seeing `_ = SomeAsync()`, `Task.Run`, `Task.Factory.StartNew`, or `ContinueWith` in `src/**`.

The banned syntax and the one sanctioned alternative are listed under [WF rules](#wf-rules) below.

---

## Glossary

| Term | Meaning |
|------|---------|
| **Owner** | The object whose `DisposeAsync` must not return while work touching its resources is still running. |
| **Borrower** | Work that touches an owner's resources; it is admitted into the owner's scope and releases a lease when it finishes. |
| **Quiescence scope** | The counted in-flight set plus its seal-and-join state (`QuiescenceScope`). |
| **Admission** | The owner's policy decision that new work may start (e.g. `UdpProxySession._expiring`). It is separate from *accounting* and stays in the owner. |
| **Seal** | The one-way transition after which the scope refuses new work. |
| **Drain** | Seal + cancel the owned token + join every outstanding lease. |
| **Work lease** | The `WorkLease` ticket returned by `TryEnter`; released exactly once when the admitted work completes. |
| **In-flight / pending** | Work admitted and not yet released; `pending` is the count. |
| **Teardown reason** | Explicit data describing *why* work ended (`SetupFailure`, `Expiry`, `Fault`, `Shutdown`); only a genuine setup failure arms a cooldown. |

---

## Invariants

- **I1 — Admission**: every piece of asynchronous work that touches an owned resource is either
  awaited inline or registered with its owner's quiescence scope.
- **I2 — Quiescence**: when an owner's `DisposeAsync` returns, every registered piece of work has
  completed and the owner's cancellation source has been released safely.

C# cannot express this with lifetimes (`ref struct` cannot cross `await`, references alias under the
GC). The achievable equivalent is: make the unsafe syntax a build error (WF rules, added by C2), make
`QuiescenceScope` the only sanctioned door, and pin each contract with a test.

---

## The `QuiescenceScope` contract

Location: `src/WinForward.Runtime/QuiescenceScope.cs`. Namespace: `WinForward.Runtime` — the root
namespace that `directory-structure.md` reserves for scheduling vocabulary shared across Runtime
groups. Both types are `internal`; `WinForward.Runtime` grants `InternalsVisibleTo` to the test and
benchmark projects.

### Surface

```csharp
namespace WinForward.Runtime;

internal sealed class QuiescenceScope : IAsyncDisposable
{
    public QuiescenceScope(CancellationToken linkedTo = default);

    public CancellationToken Token { get; }        // scope-owned; never read after DrainAsync completes
    public bool IsIdle { get; }                    // pending == 0
    public Exception? Fault { get; }               // first recorded fault
    public string? FaultSite { get; }              // the first fault's reporting site, if supplied

    public bool TryEnter(out WorkLease lease);     // false once sealed
    public void RecordFault(Exception exception, string? site = null);
    public void Cancel();
    public bool Run(Func<CancellationToken, Task> body, string name);
    public Task DrainAsync();                      // seal + cancel + join; single-flight, idempotent
    public ValueTask DisposeAsync();               // => new(DrainAsync())
}

internal struct WorkLease : IDisposable          // mutable; Dispose() => one Exit(); idempotent per copy
```

### Decisions (normative)

- **D1 — Accounting and admission are separate.** The scope counts and joins; the owner keeps its own
  admission policy. The scope has no `unseal` (a reversible seal would force re-arming the drain and
  make its state machine unbounded).
- **D2 — Late `TryEnter` after seal is rejected, not extended.** Rejection is what lets the drain
  completion cell be allocated exactly once at seal, keeping `Enter`/`Exit` allocation-free. An owner
  that must not reject checks its admission policy *before* entering.
- **D3 — A child fault does not cancel siblings.** `RecordFault` stores the first exception
  (`Interlocked.CompareExchange`) together with its optional reporting site (exposed as `FaultSite`);
  the owner decides what to do. This deliberately differs from any `RaceGroup`/`RunGroup` semantics.
- **D4 — `DrainAsync` never throws for child faults.** Faults are observed via `Fault`, not by
  faulting the owner's disposal.
- **D5 — Late-enter/exit and the seal transition are atomic under one gate.** `pending == 0 && sealed`
  cannot be missed.
- **D6 — Nesting is explicit composition.** A parent's drain awaits each child scope's drain; there is
  no `AsyncLocal` ambient parent.
- **D7 — Only `QuiescenceScope` owns a `CancellationTokenSource`.** `Token`/`Cancel` replace ad-hoc
  per-owner CTSes, so "the token is no longer read after dispose" becomes structural.
- **D8 — Terminology lives in this guide**, not in a `CONTEXT.md`/ADR set.
- **D9 — Fault observation is intrinsic to the child body.** A migrated child records its own fault
  (and may rethrow, because someone still awaits it); the scope never patches an abandoned task with
  an external `ContinueWith` observer.

### Allocation rule (hot path)

`TryEnter` / `Exit` must allocate **0 bytes**:

- `Enter`/`Exit` are interlocked arithmetic on a single packed word (bit 0 = sealed, bits 1+ = pending)
  only — no `lock`, no per-call object.
- The drain completion cell (`TaskCompletionSource`) is allocated **once, lazily, at seal**, with
  `TaskCreationOptions.RunContinuationsAsynchronously` — never on a `0 → 1` transition (a per-transition
  cell would allocate per datagram on the UDP send path).
- The owned `CancellationTokenSource` is allocated once in the constructor.
- `WorkLease` must stay **mutable** (idempotency nulls its own scope field) and **unboxed**: keep it a
  local — `using IDisposable lease = ...` or an interface/`object`-typed field boxes it and
  reintroduces a hot-path allocation.
- `Run` allocates (delegate, closure, state machine) and is **cold-path only**.

`QuiescenceScopeAllocationGateTests.WarmEnterExitPairAllocatesNoManagedBytes` pins the 0-byte gate.

### Lock order

The scope holds **no lock** — its state is a packed word mutated only by interlocked operations — so it
is a leaf by construction: no scope method acquires another lock and no scope method invokes owner code
while holding one. An owner may therefore hold its own gate across `TryEnter` (e.g. `UdpProxySession`
checks admission under `_activityGate`, then enters); no acquisition order can invert. This was chosen
by measurement (`QuiescenceScopeBenchmarks`): the packed gate measured 18.60 ns/op vs 49.50 ns/op for a
`lock` gate, both at 0 B/op.

### Drain ordering

`DrainAsync` is single-flight and idempotent. Order:

1. seal the packed word (`Interlocked.Or`) and take ownership of the drain task,
2. publish the join cell, then re-check the word (see the handshake note),
3. cancel the owned token,
4. await the join (all leases released),
5. dispose the owned CTS **last** — so a still-unwinding child can read the token — then complete the
   drain task.

Consumers therefore must not read `Token` after `DrainAsync` completes.
`QuiescenceScopeTests.TokenIsReleasedOnceDrained` pins the `ObjectDisposedException`.

There are exactly two cells, and the split is load-bearing:

- the **completion cell** (`_drained`) is allocated at most once, at seal, and its `Task` is what every
  caller receives — concurrent and sequential callers observe the *same* `Task` instance
  (`QuiescenceScopeTests.DrainCallersObserveTheSameTaskInstance`,
  `ConcurrentDrainCallersObserveTheSameTaskInstance`); it completes only after the join and the CTS
  release;
- the **join cell** (`_joined`) is allocated by the single sealer (the one caller that wins the
  `Interlocked.Or` seal) and is what an exiting lease signals.

Because callers await `_drained.Task` rather than the drain method's own task, the drain runs detached
(`_ = DrainCoreAsync(...)`); it therefore contains its own faults so the detached task can never
surface as an unobserved task exception.

**Seal-vs-decrement handshake.** An exiting lease signals the drain by observing the packed word fall to
exactly `Sealed`; a last exit that decrements before the sealer publishes its join cell would otherwise
be lost. The sealer must publish the cell and then issue a **full fence**
(`Interlocked.MemoryBarrier`) before re-checking the word, so exactly one of the two sides always
signals. The exiting side stays allocation-free and fence-free (a plain volatile read of the cell after
its `Interlocked.Add`).

---

## The `Run` door (admission rules)

`Run(Func<CancellationToken, Task> body, string name)` is the **only** sanctioned escape from the
fire-and-forget ban, and it is `grep`-discoverable (`rg '\.Run\('`) with a required non-empty `name`:

1. it takes a **factory**, not an already-started task — a sealed scope refuses (`false`) and never
   invokes `body`, which is impossible to guarantee for work already in flight;
2. the child is admitted into the same in-flight set as `TryEnter`, so `DrainAsync` joins it (it is
   therefore *tracked* and *ordered* w.r.t. shutdown);
3. a fault the child throws is recorded via `RecordFault(exception, name)` and **swallowed** — nothing
   can await the child, so rethrowing would surface an unobserved task exception; `Fault` is the
   observation point (it is therefore *observed*). The child's `name` is stored as the fault's
   `FaultSite`, so a fault from an otherwise anonymous child still names its site
   (`QuiescenceScopeTests.FaultedRunChildRecordsItsName`).

`Run` must not be used on a per-packet path (it allocates). The hot path is `TryEnter` + `WorkLease`.

Lease rule: disposal is idempotent *per copy*, so disposing two different copies of one lease releases
the scope twice. Exactly one copy may be disposed — `Run` keeps its own copy and never disposes the
one it created after handing a copy to the child.

---

## WF rules

| Id | Rule | Fix | Status |
|----|------|-----|--------|
| `WF0001` | Do not discard an unawaited awaitable (`_ = <awaitable>`) | `await` it inline, or register it with `scope.Run(...)` | stub — filled by `09-20-lifetime-analyzers` (C2) |
| `WF0002` | Do not use `Task.ContinueWith` | observe faults intrinsically in the child body (D9) | stub — C2 |
| `WF0003` | Do not `Task.Run` / `Task.Factory.StartNew` outside the primitive | use `scope.Run(...)`, or a dedicated worker joined by its owner | stub — C2 |

Rules key on **awaitable types** (`Task`, `Task<T>`, `ValueTask`, `ValueTask<T>`, custom awaitables) so
the benign discards in `src/**` (`_ = await FooAsync(...)`, `_ = task.Exception`, `_ = TryWrite(...)`,
numeric `_ = Interlocked.Add(...)`) are not flagged. Rules are scoped to `src/**`. The primitive's own
`Run` child start is part of this mechanism and is covered by C2's rollout (see the parent program
design).
