# C1 — `QuiescenceScope` primitive: technical design

Parent: `../09-20-structured-concurrency/design.md` §2 is the normative design for this type. This
file makes it implementable and adds what only C1 needs: the state machine, the test strategy, the
gate-choice protocol, and the hazards the implementer must not trip over.

## 1. Deliverable and boundary

C1 ships one type pair plus its contract, and nothing else.

- new `src/WinForward.Runtime/QuiescenceScope.cs` — `QuiescenceScope` + `WorkLease`
- new `tests/WinForward.Core.Tests/QuiescenceScopeTests.cs`
- new `benchmarks/WinForward.Benchmarks/Perf/QuiescenceScopeBenchmarks.cs` (gate choice, §7)
- new `.trellis/spec/backend/async-lifetime.md` (the durable contract, §8) plus its row in
  `backend/index.md`

Out of boundary: no owner is migrated (C3/C4), no analyzer is added (C2), no packet-path behaviour
changes. `WinForward.Runtime.csproj` already grants `InternalsVisibleTo` to `WinForward.Core.Tests`
and `WinForward.Benchmarks`, so the types stay `internal` and remain directly testable and
benchmarkable. `directory-structure.md`'s 400-effective-line cap applies; keeping both types in the
file named for the main type is allowed, splitting `WorkLease` into its own file is allowed too.

## 2. Surface

```csharp
namespace WinForward.Runtime;

internal sealed class QuiescenceScope : IAsyncDisposable
{
    public QuiescenceScope(CancellationToken linkedTo = default);

    public CancellationToken Token { get; }        // scope-owned, never read after DrainAsync completes
    public bool IsIdle { get; }                    // pending == 0
    public Exception? Fault { get; }               // first recorded fault (D3)

    public bool TryEnter(out WorkLease lease);     // false once sealed (D2)
    public void RecordFault(Exception exception);
    public void Cancel();
    public bool Run(Func<CancellationToken, Task> body, string name);
    public Task DrainAsync();                      // seal + cancel + join; single-flight, never throws for child faults (D4)
    public ValueTask DisposeAsync();               // => new(DrainAsync())
}

internal struct WorkLease : IDisposable          // mutable; Dispose() => one Exit(); idempotent per copy
```

`DrainAsync` disposes the owned `CancellationTokenSource` **after** the join (see §5), so `Token`
must not be read after it completes. Nothing outside `WinForward.Runtime` sees these types.

## 3. State

```csharp
private readonly object _gate = new();           // variant A only
private int _pending;                            // guard: gate (A) | Interlocked packed word (B)
private bool _sealed;                            // guard: gate (A) | bit 0 of _state (B)
private TaskCompletionSource? _drained;          // allocated at most once, at seal
private CancellationTokenSource _cts;            // ctor; disposed at the end of DrainAsync
private Exception? _fault;                       // first writer wins
private int _disposed;                           // guards ctor-region state after drain
```

Variant A (`lock`) uses `_gate` for `TryEnter`, `Exit`, and the seal transition. Variant B
(packed word) encodes `sealed` in bit 0 and `pending` in bits 1+, enters with a
`CompareExchange` add, exits with `Interlocked.Add(ref _state, -2)`, and signals when that add
returns exactly `1`. Both variants must uphold the same rule:

> **The transition into `sealed && pending == 0` signals `_drained` exactly once.**

Variant B needs the TCS write ordered before the count can be observed as zero: the sealer writes
`_drained` with `Volatile.Write` and then re-checks the packed word, so the "already idle at seal
time" case is covered even when the last `Exit` happens-before the sealer's write. `_drained` is
created with `TaskCreationOptions.RunContinuationsAsynchronously` so a drain continuation never runs
inline on the thread that happened to release the last lease.

## 4. Allocation contract

- `TryEnter`/`Exit` allocate **0 bytes**. The drain TCS is allocated once, lazily, at seal — never on
  a `0 → 1` transition (the legacy `EnterSetup` shape allocates a TCS per datagram on the UDP path
  and must not be carried over).
- The `CancellationTokenSource` is allocated once in the constructor.
  `CreateLinkedTokenSource(default)` returns a plain CTS, so an unlinked scope costs one CTS, not two.
- `Run` allocates (delegate, closure, state machine). It is the cold path only; the primitive must
  not be used via `Run` on a per-packet path.
- `WorkLease` must be a **mutable** `struct`. Idempotency requires `Dispose()` to null its scope
  field, which a `readonly` struct cannot do. It must also stay **unboxed**: `using IDisposable l =
  lease;`, an interface-typed field, or an `object` field boxes it and reintroduces a hot-path
  allocation.

## 5. Drain

`DrainAsync` is single-flight and idempotent: the first caller seals, cancels, and awaits the join;
every caller gets the same task. It never throws for a child fault (D4). Order:

1. seal + take ownership of the drain task (single-flight under the gate)
2. `_cts.Cancel()`
3. await the join (all leases released)
4. dispose the owned CTS

The CTS is disposed last precisely so a child that is still unwinding can still read the token.
Consumers therefore must not read `Token` after `DrainAsync` completes — document that on the
property. `Cancel()` after drain is a no-op, not an `ObjectDisposedException`.

## 6. Lock ordering and `Run`

The scope's gate is a **leaf**: no scope method takes another lock, and no scope method calls out to
owner code while holding it. The only permitted order is `ownerGate → scope.gate`, which is what
makes `UdpProxySession`'s "check admission under `_activityGate`, then `TryEnter`" acyclic.
Document this on the type.

`Run(body, name)` is the one audited escape from the fire-and-forget ban (E2):

1. `if (!TryEnter(out var lease)) return false;` — a sealed scope **refuses and never invokes
   `body`**. This is why `Run` takes a factory rather than an already-started task: a task that is
   already running cannot be refused, which would make rejection (D2/P5) unenforceable.
2. start the child with `Token`; on a fault, `RecordFault` and **swallow** (do not rethrow — see the
   hazard below); release the lease in `finally`.

**Hazard — do not rethrow in `Run`.** D9/F2 says a migrated pump body records its fault and rethrows,
because someone still awaits that pump. `Run`'s child is different: the scope starts it and never
hands the task out, so nothing can observe a rethrown exception and it would surface as an
unobserved task exception. `Run`'s child records the fault and completes normally; the scope's
`Fault` property is the observation point (D4).

**Hazard — at most one lease copy may be disposed.** Idempotency is per copy (the copy nulls its own
field), so two different copies of the same lease both release. `Run` therefore must not dispose the
lease it created after passing a copy to the child.

## 7. Gate-choice protocol (variant A vs B)

The parent decision defers the gate implementation here and asks for the repo's benchmark suite, not
the throwaway microbench (`../09-20-structured-concurrency/design.md` §2.3). C1 therefore adds a
`Perf` benchmark class that runs **both** variants through the same operations, and decides with:

```text
dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- --filter *QuiescenceScope* --job short
dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- --stability --scenario udp --quick
```

Decision rule (per `benchmarks/README.md`: allocation bytes are an exact gate, sub-2× ns/pps deltas
are noise on a dev box):

1. both variants must measure 0 B/op in the allocation test;
2. the stability `udp` run must not regress from today (it exercises the un-migrated code, so it is a
   no-regression check, and C3 re-runs it after the migration);
3. between the variants, prefer the faster one only when the delta is outside noise; otherwise prefer
   the `lock` variant for readability.

Record the raw BDN and stability output under `research/` and cite the numbers in the final report.

## 8. `.trellis/spec/backend/async-lifetime.md`

Not `lifecycle.md`: `backend/traffic-policy-lifecycle.md` already uses that word for the
traffic-policy lifecycle, and two nearly-identically-named guides would be ambiguous. C1 also adds the
new row to `.trellis/spec/backend/index.md`, which lists every guide.

C1 creates the file with: the glossary (quiescence, admission, seal, drain, owner/borrower, work
lease, in-flight/pending, teardown reason), invariants I1/I2, the primitive's durable contract
(surface, D1–D9, the allocation rule, the lock order), and the `Run` door's admission rules. The WF
rule table section is created as a stub and filled by C2. The program plan (migration order,
per-owner mapping) stays in the parent's `design.md` §4 — not here.

## 9. Test strategy

`tests/WinForward.Core.Tests/QuiescenceScopeTests.cs`:

- enter/exit accounting: after `Dispose`, `IsIdle` is true
- a lease disposed twice leaves the count intact (no underflow)
- `DrainAsync` completes only after every outstanding lease is released (dispose some, assert the
  drain task is not complete, release the rest, assert it completes)
- `DrainAsync` on an idle scope completes
- once sealed, `TryEnter` returns false and `Run` returns false **without invoking its body**
- `DrainAsync` is single-flight (concurrent callers observe the same completion) and idempotent
- `DrainAsync` cancels `Token`
- `RecordFault` keeps the first exception and `DrainAsync` does not throw because of a fault
- a faulted `Run` child raises **no** unobserved task exception (subscribe
  `TaskScheduler.UnobservedTaskException`, drop the reference, `GC.Collect()` +
  `WaitForPendingFinalizers()`, assert it never fired) and sets `Fault`
- nesting: a parent scope linked to a child scope's constructor token drains by composition
- reading `Token` after `DrainAsync` completes throws `ObjectDisposedException` (pins §5)
- concurrency stress: many threads entering/exiting while another drains, bounded by a timeout, to
  catch a lost wakeup — especially for variant B's seal-vs-decrement race

`tests/WinForward.Core.Tests/HotPathAllocationGateTests.cs` (or a sibling file) gains one gate:

```csharp
// warm first
var before = GC.GetAllocatedBytesForCurrentThread();
for (var i = 0; i < count; i++) { Assert.True(scope.TryEnter(out var lease)); lease.Dispose(); }
Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
```

## 10. Risks

| Risk | Mitigation |
|---|---|
| `Run` rethrows → unobserved task exception | swallow after `RecordFault` (§6); explicit test |
| two lease copies disposed → count underflow | document "one copy only"; `Run` keeps its copy; test |
| variant B seal-vs-decrement race loses the signal | volatile TCS + sealer re-check; stress test |
| drain continuation runs inline on the last-exit thread | `RunContinuationsAsynchronously` |
| `Token` read after drain | dispose the CTS last, document it, test it |
| gate choice made on a dev-box noise delta | allocation + stability are the gates; ties go to `lock` |
