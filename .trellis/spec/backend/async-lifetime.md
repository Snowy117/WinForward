# Async Lifetime (Quiescence Scopes)

> The durable contract for the `QuiescenceScope` primitive (task `09-20-quiescence-scope`, program
> `09-20-structured-concurrency`). Deliberately **not** `traffic-policy-lifecycle.md`, which owns the
> *traffic-policy* lifecycle (policy domains, loop prevention, idle sweep).

---

## Scope / Trigger

Read this before adding asynchronous work that touches a resource owned by an object with a
`DisposeAsync`, before writing or reviewing an owner's teardown (`DisposeAsync`, drain, cancellation
ordering), and whenever `_ = SomeAsync()`, `Task.Run`, `Task.Factory.StartNew` or `ContinueWith` appears
under `src/**`. The banned syntax and its one sanctioned alternative are in [WF rules](#wf-rules).

---

## Glossary

| Term | Meaning |
|------|---------|
| **Owner** | The object whose `DisposeAsync` must not return while work touching its resources is still running. |
| **Borrower** | Work that touches an owner's resources; admitted into the owner's scope, it releases a lease when it finishes. |
| **Quiescence scope** | The counted in-flight set plus its seal-and-join state (`QuiescenceScope`). |
| **Admission** | The owner's policy decision that new work may start (e.g. `UdpProxySession._expiring`); separate from *accounting* and owned by the owner. |
| **Seal** | The one-way transition after which the scope refuses new work. |
| **Drain** | Seal + cancel the owned token + join every outstanding lease. |
| **Work lease** | The `WorkLease` ticket returned by `TryEnter`; released exactly once when the admitted work completes. |
| **In-flight / pending** | Work admitted and not yet released; `pending` is the count. |
| **Teardown reason** | Explicit data describing *why* work ended (`UdpTeardownReason`: `SetupFailure`, `Expiry`, `Fault`, `AssociationLost`, `Shutdown`); only `SetupFailure` arms a cooldown (owned by [udp-flow-setup.md](./udp-flow-setup.md)). |

---

## Invariants

- **I1 — Admission**: every piece of asynchronous work that touches an owned resource is either awaited
  inline or registered with its owner's quiescence scope.
- **I2 — Quiescence**: when an owner's `DisposeAsync` returns, every registered piece of work has
  completed and the owner's cancellation source has been released safely.

C# cannot express this with lifetimes (`ref struct` cannot cross `await`; references alias under the GC),
so the achievable equivalent is a build error on the unsafe syntax (WF rules), `QuiescenceScope` as the
only sanctioned door, and a test pinning each contract.

---

## The `QuiescenceScope` contract

`src/WinForward.Runtime/QuiescenceScope.cs`, namespace `WinForward.Runtime` — the root namespace
`directory-structure.md` reserves for scheduling vocabulary shared across Runtime groups. Both types are
`internal`; `WinForward.Runtime` grants `InternalsVisibleTo` to the test and benchmark projects.

### Surface

```csharp
namespace WinForward.Runtime;

internal sealed class QuiescenceScope : IAsyncDisposable
{
    public QuiescenceScope(CancellationToken linkedTo = default);

    public CancellationToken Token { get; }        // scope-owned; never read after DrainAsync completes
    public bool IsIdle { get; }                    // pending == 0
    public bool IsSealed { get; }                  // sealed by DrainAsync; admission predicate, not the join
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
  admission policy. There is no `unseal` (a reversible seal would force re-arming the drain and make its
  state machine unbounded).
- **D2 — A late `TryEnter` after seal is rejected, not extended.** Rejection is what lets the drain
  completion cell be allocated once at seal and keeps `Enter`/`Exit` allocation-free. An owner that must
  not reject checks its admission policy *before* entering.
- **D3 — A child fault does not cancel siblings.** `RecordFault` stores the first exception
  (`Interlocked.CompareExchange`) together with its optional reporting site (`FaultSite`); the owner
  decides what to do.
- **D4 — `DrainAsync` never throws for child faults.** Faults are observed via `Fault`, never by
  faulting the owner's disposal.
- **D5 — Late enter/exit and the seal transition are atomic under the packed word.** `pending == 0 &&
  sealed` cannot be missed.
- **D6 — Nesting is explicit composition.** A parent's drain awaits each child scope's drain; there is no
  `AsyncLocal` ambient parent.
- **D7 — Only `QuiescenceScope` owns an owner-lifetime `CancellationTokenSource`.** `Token`/`Cancel`
  replace ad-hoc per-owner CTSes, so "the token is no longer read after dispose" becomes structural. The
  rule is "one owner-lifetime CTS per owner, owned by the scope", not "no other CTS may exist": an
  **operation- or epoch-scoped** source — a stall window, a per-attempt deadline — legitimately stays
  with the operation that creates it, is released *after* the drain, and is never the owner's lifetime
  handle (C4's D-C4-1). Precedents: `TcpProxyRelay`'s per-direction `StallWindow` (one re-armed CTS) and
  `Socks5ControlConnection._attemptCancellation` (one `CancelAfter` budget spanning an attempt), whose
  readers hold a scope lease so the deadline cannot be disposed under a live operation.
- **D8 — Terminology lives in this guide**, not in a `CONTEXT.md`/ADR set.
- **D9 — Fault observation is intrinsic to the child body.** A migrated child records its own fault (and
  may rethrow, because someone still awaits it); the scope never patches an abandoned task with an
  external `ContinueWith` observer.
- **D10 — `IsSealed` is the admission predicate; `DrainAsync` is the join.** They are deliberately
  different: a caller may observe sealed while leases are still outstanding, exactly the gap that makes a
  late `TryEnter` refusal meaningful (D2). Owners read `IsSealed` for their entry-point guards instead of
  keeping a parallel `_disposed` flag; `IsSealed == true` never implies `IsIdle == true`.
- **D11 — An owner keeps its own teardown single-flight; the scope's single-flight covers only the
  drain.** `DrainAsync` is single-flight and identity-stable, but it does **not** cover the owner work
  that runs *around* it (socket close, control-connection disposal, pool/limiter release). Reading
  `IsSealed` *before* calling `DrainAsync` is a TOCTOU check — sealing happens **inside** the drain — so
  two concurrent `DisposeAsync` callers can both pass it and both run the teardown. Every migrated owner
  therefore keeps an explicit one-shot claim for its teardown body (`Interlocked.Exchange` on an `int`
  field, or an equivalent lazy task); a later caller skips the teardown and joins `DrainAsync()` instead
  of returning early. Two consequences the reader must know: a late caller joins only the *drain*, so it
  does not observe an exception the claimant's owner teardown throws, and it may return before that
  teardown's own awaits finish. Owner teardown faults stay fail-fast to the **claimant** (D-C3-2); only
  child faults are recorded without throwing (D4).

### Allocation rule (hot path)

`TryEnter` / `Exit` must allocate **0 bytes**:

- `Enter`/`Exit` are interlocked arithmetic on a single packed word (bit 0 = sealed, bits 1+ = pending)
  only — no `lock`, no per-call object.
- The drain completion cell (`TaskCompletionSource`) is allocated **at most once, lazily**, by
  `LazyInitializer.EnsureInitialized` on the first drain call, with
  `TaskCreationOptions.RunContinuationsAsynchronously` — never on a `0 → 1` transition (a
  per-transition cell would allocate per datagram on the UDP send path).
- The owned `CancellationTokenSource` is allocated once in the constructor.
- `WorkLease` must stay **mutable** (idempotency nulls its own scope field) and **unboxed**: keep it a
  local — `using IDisposable lease = ...` or an interface/`object`-typed field boxes it and reintroduces a
  hot-path allocation.
- `Run` allocates (delegate, closure, state machine) and is **cold-path only**.

`QuiescenceScopeAllocationGateTests.WarmEnterExitPairAllocatesNoManagedBytes` pins the 0-byte gate.

### Lock order

The scope holds **no lock** — its state is a packed word mutated only by interlocked operations — so it
is a leaf by construction: no scope method acquires another lock and no scope method invokes owner code
while holding one. An owner may therefore hold its own gate across `TryEnter` — that stays permitted —
and no acquisition order can invert. Since 2026-09-30 (task `09-30-warm-path-lock-chain`) no in-tree
owner does: `UdpProxySession.SendSpanAsync` admits with **no** `_activityGate` held (the CAS is the
admission authority and the drain joins outstanding leases), and `_activityGate` survives only for the
lifecycle transitions (`State`, `TryBeginExpiry`, `CancelExpiry`). The packed gate was chosen by
measurement (`QuiescenceScopeBenchmarks`): 18.60 ns/op versus 49.50 ns/op for a `lock` gate, both at
0 B/op.

### Drain ordering

`DrainAsync` is single-flight and idempotent. Order:

1. seal the packed word (`Interlocked.Or`) and take ownership of the drain task; when the prior word's
   pending count was zero the join is vacuous — no lease was outstanding and the seal refuses every later
   `TryEnter`, so no `Exit` can ever signal — and the join cell is skipped entirely (the idle fast path;
   it saves a `TaskCompletionSource` per drain, measured 88 B/session by task
   09-22-udp-teardown-session-tier-alloc),
2. otherwise publish the join cell, then re-check the word (see the handshake note),
3. cancel the owned token,
4. await the join (all leases released),
5. dispose the owned CTS **last** — so a still-unwinding child can read the token — then complete the
   drain task.

Consumers therefore must not read `Token` after `DrainAsync` completes.
`QuiescenceScopeTests.TokenIsReleasedOnceDrained` pins the `ObjectDisposedException`.

There are exactly two cells, and the split is load-bearing:

- the **completion cell** (`_drained`) is allocated at most once (lazily, on the first drain call), and
  its `Task` is what every caller receives — concurrent and sequential callers observe the *same* `Task`
  instance (`QuiescenceScopeTests.DrainCallersObserveTheSameTaskInstance`,
  `ConcurrentDrainCallersObserveTheSameTaskInstance`); it completes only after the join and the CTS
  release;
- the **join cell** (`_joined`) is allocated by the single sealer (the one caller that wins the
  `Interlocked.Or` seal) **only when a lease was outstanding at seal**, and is what an exiting lease
  signals; an idle seal allocates no join cell — the join is vacuous, so the cell would never be read
  (see the ordering note above).

Because callers await `_drained.Task` rather than the drain method's own task, the drain runs detached
(`_ = DrainCoreAsync(...)`); it therefore contains its own faults so the detached task can never surface
as an unobserved task exception.

**Seal-vs-decrement handshake.** An exiting lease signals the drain by observing the packed word fall to
exactly `Sealed`; a last exit that decrements before the sealer publishes its join cell would otherwise be
lost. The sealer must publish the cell and then issue a **full fence** (`Interlocked.MemoryBarrier`)
before re-checking the word, so exactly one of the two sides always signals. The exiting side stays
allocation-free and fence-free (a plain volatile read of the cell after its `Interlocked.Add`).

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
the scope twice. Exactly one copy may be disposed — `Run` keeps its own copy and never disposes the one
it created after handing a copy to the child.

---

## WF rules

| Id | Rule | Fix | Status |
|----|------|-----|--------|
| `WF0001` | Do not discard an unawaited awaitable (`_ = <awaitable>`) | `await` it inline, or register it with `scope.Run(...)` | implemented — `UnawaitedAwaitableDiscardAnalyzer` |
| `WF0002` | Do not use `Task.ContinueWith` | observe faults intrinsically in the child body (D9) | implemented — `ContinueWithAnalyzer` |
| `WF0003` | Do not `Task.Run` / `Task.Factory.StartNew` outside the primitive | use `scope.Run(...)`, or a dedicated worker joined by its owner | implemented — `TaskRunAnalyzer` |
| `WF0004` | Do not write a bare unawaited awaitable expression statement | `await` it, or register it with `scope.Run(...)` | implemented — `BareAwaitableExpressionAnalyzer` |

Rules key on **awaitable types** (`Task`, `Task<T>`, `ValueTask`, `ValueTask<T>`, custom awaitables) so
the benign discards in `src/**` (`_ = await FooAsync(...)`, `_ = task.Exception`, `_ = TryWrite(...)`,
numeric `_ = Interlocked.Add(...)`) are not flagged. Rules are scoped to `src/**`.

`WF0004` exists because `CS4014` fires **only inside `async` methods**: `async Task M() { WorkAsync(); }`
is reported, but `void M() { WorkAsync(); }` compiles in silence and the call is never observed.
`WF0001` covers the explicit `_ = <awaitable>` form, which `CS4014` does not see either way.

### Analyzer implementation and the allowlist

The rules live in `analyzers/WinForward.Analyzers/` (a `netstandard2.0` project with
`EnforceExtendedAnalyzerRules`), are wired into `src/**` and nothing else by `src/Directory.Build.props`
(a `ProjectReference` with `OutputItemType="Analyzer"` and `ReferenceOutputAssembly="false"`), and are
declared `dotnet_diagnostic.WF000n.severity = error` in `.editorconfig`. Each `DiagnosticDescriptor` is
already `DiagnosticSeverity.Error` in category `WinForward.Lifetime`, so a rule stays fatal even where
warning treatment is relaxed. Two implementation details are load-bearing and easy to get wrong:

- receivers are matched through `OriginalDefinition`, because a `ContinueWith`/`Run` overload declared on
  `Task<TResult>` has `Task<TResult>` (not `Task`) as its `ContainingType` — keying on `Task` alone
  silently misses every generic-task receiver;
- `WF0004` excludes any `IAssignmentOperation` (not only `ISimpleAssignmentOperation`) and any
  `IAwaitOperation`, so `_x ??= FooAsync();` and `await Task.WhenAny(a, b);` are not flagged.

The temporary per-file allowlist is now **empty** — the program's completion condition: `.editorconfig`
carries no `severity = none` under `src/**` except the primitive's permanent exemption below, and the
only discard or spawn sites left in `src/**` are the primitive's own (`_ = RunChildAsync(...)`,
`_ = DrainCoreAsync(...)`) plus the awaited/non-awaitable discards documented above, each pinned by
`tests/WinForward.Analyzers.Tests`. A new exemption is debt to repay, not a fix.

### The primitive's permanent exemption

`WF0001` is exempted **permanently** for `src/WinForward.Runtime/QuiescenceScope.cs`. The primitive starts
its own children with `_ = RunChildAsync(...)` and detaches its drain with `_ = DrainCoreAsync(...)`; the
child is already admitted by `TryEnter`, its lease is released by `RunChildAsync`, and its fault is
recorded there — so neither discard is an unobserved fire-and-forget.

---

## Per-owner inventory

| Owner | Scope | Owns / rule |
|-------|-------|-------------|
| `TcpRedirectSessionStore` | root `new QuiescenceScope()`; `ShutdownToken => _scope.Token` | `TryEnterSetup(out WorkLease)` admits setups; no D11 claim — its single caller awaits the whole `DisposeCoreAsync` |
| `TcpRedirectSession` | nested `new QuiescenceScope(shutdown)` | `_retired` admission claim; `DisposeLifetimeAsync()` drains |
| `TcpProxyRelay` | `new QuiescenceScope()`; both pumps take leases | `_teardownStarted`; `StallWindow` per direction (D7 carve-out) |
| `UdpProxySession` | `new QuiescenceScope(context.Shutdown)` | `_teardownStarted`; `_activityGate` guards only `State`/`TryBeginExpiry`/`CancelExpiry` |
| `UdpProxyCoordinator` | `new QuiescenceScope()`; session scopes nest under it | `_disposeStarted`; `Run(…, "udp.receive-failure")` for the receive-failure signal |
| `UdpSessionSetup` | none — not an owner | reaches the coordinator only through `IUdpSessionSlotHost` |
| `Socks5ControlConnection` | `new QuiescenceScope(_connectCancellation)` | `_disposeStarted`; every attempt-token reader holds a lease; `_attemptCancellation` stays (D7) |
| `Socks5UdpAssociation` | `new QuiescenceScope()`; its only child is the control-stream watchdog | `_disposeStarted`; disposal seals **before** it releases the control connection, so the watchdog cannot read the owner's own teardown as association death |
| `Socks5UdpTransport` | none — an `Interlocked` disposal guard only | owns no CTS; disposes relay socket → self-traffic token → association and leaves the send gate undisposed ([udp-association-ownership.md](./udp-association-ownership.md)); the warm per-datagram path (see Deviations) |
| `FlowAttributionPipeline` | `new QuiescenceScope()` | `Admit` is synchronous and bounded and takes no lease; every setup work item takes one |
| `LayeredCaptureRunner` | `new QuiescenceScope(cancellationToken)` at the top of `RunAsync` | the run CTS; the monitor thread and the periodic tick live in `CaptureRefreshWorkers.cs` |
| `MultiAdapterCaptureLoop` | `new QuiescenceScope()` | the per-generation `IReadOnlyList<INdisPacketArrivalSignal?>`, released after every pump stops; `_disposeStarted` (D11); disposed by its `TransactionalCaptureRuntime` in cleanup |
| `TransactionalCaptureRuntime` (`CaptureLifecycle.cs`) | `new QuiescenceScope()`, linked at its single `CreateLinkedTokenSource` site | `_scope.Cancel()` in `StopAsync`; `_runTask` is caller-awaited, never a child |
| `IdleExpirySweeper`, `RuntimeHeartbeat` | `new QuiescenceScope()` | the loop is a `Run` child; `DisposeAsync` is just the scope's, so a second dispose joins instead of throwing |
| `NdisCapturePump` | none, deliberately | no CTS and no `IDisposable`; joined through the `ValueTask(outcome.Task)` bridge (see below) |

Ownership nests `DurableCaptureBundle` → UDP coordinator → session → transport → association → watchdog,
and the bundle's own order is sweeper → attribution pipeline → UDP coordinator → UDP pools → TCP
coordinator → pools → wake registry → setup executor.

`FlowAttributionPipeline` is an owner of the same shape: its `DisposeAsync` seals admission through the
pending index (a later `Admit` blocks fail-closed rather than attributing inline), fails every pending
entry closed through the executor, joins the in-flight leases and releases the retained copies. Its
retention pool is created and disposed by `DurableCaptureBundle`, which disposes the pipeline
immediately after the sweeper and the pool in the last block, so no retained lease outlives its pool.
Its per-adapter decided queue lives under the pipeline's own leaf gate, and delivery runs only on the
pump that owns the entry's adapter. The composite arrival signal disposes the borrowed driver signal
while `FlowAttributionWakeRegistry` owns the wake events — one per adapter handle, reused across
generations — and `DurableCaptureBundle` disposes the registry after the pipeline is sealed and every
pump has stopped.

Three lifetime handles stay outside the primitive, each already joined by its own owner, so I2 holds
without them:

- **`SetupExecutor`** (`src/WinForward.Runtime/SetupExecutor.cs`, `_shutdown`) — a synchronous
  `IDisposable` running dedicated worker `Thread`s. `Dispose` is D11 single-flight
  (`Interlocked.Exchange(ref _disposed, 1)`), cancels, `Thread.Join()`s every worker, then drains the
  ring and releases the CTS. It owns no *borrowed* work, so it has no seal/join of other owners' leases
  to express; `DurableCaptureBundle` creates it and disposes it after both proxy coordinators. Its ring
  bound is the sum of its finite-capacity producers (F8, 2026-10-01): `DefaultRingCapacity` is 2,048, the
  sum of the TCP pending-SYN index cap (`TcpPendingSynSetupIndex.DefaultCapacity`) and the
  deferred-attribution index cap (`FlowAttributionPendingIndex.DefaultCapacity`), both 1,024. UDP session
  setup is deliberately outside the inequality — one item per admitted session against a 16,384-flow
  session capacity (`ConfigurationLoader.DefaultUdpSessionCapacity`) — so no satisfiable bound covers it;
  a full ring can therefore refuse a UDP setup item, which is counted (`RuntimeCounters.UdpSetupRejections`)
  and fail-closed. The load-bearing form is the test
  `SetupExecutorTests.DefaultRingCapacityCoversBothFiniteProducerCaps`.
- **`NdisCapturePump`** (`src/WinForward.NdisApi/NdisCapture.cs`) — owns no CTS at all and no
  `IDisposable`; its thread is joined through the `ValueTask(outcome.Task)` completion bridge (E6). The
  per-adapter packet-arrival signal its idle path waits on (F5, 2026-10-01) is **borrowed** through
  `NdisCapturePumpOptions.PacketArrivalSignal`, and the `MultiAdapterCaptureLoop` that created the list
  releases it. It cannot reference the primitive (`internal` to `Runtime`, and the dependency direction
  is `Runtime → NdisApi`).
- **The CLI's process-root `shutdown` CTS** (`src/WinForward.Cli/Program.cs`,
  `using var shutdown = new CancellationTokenSource()`) — the application's root cancellation handle and
  the source every scope ultimately links to, not an owner of borrowed work.

Fault observation is intrinsic everywhere: each relay pump records its own fault (`RecordPumpFault` →
`RecordFault(exception, "tcp.relay.pump")`) and reports a **result** (`PumpResult.Faulted`) instead of
rethrowing; `RunPumpAsync` surfaces `_scope.Fault` on `Completion`; and `DisposeAsync` observes
`Completion` on every path (`ObserveCompletionAsync`), so a faulted `Completion` can never escape as an
unobserved task exception. The UDP receive-failure path is a signal/join split: `UdpProxySession`'s loop
tail calls a synchronous `Action<UdpProxySession>`, and the coordinator maps it to
`_scope.Run(…, "udp.receive-failure")` — the signal must return promptly (an awaited teardown there
deadlocks against the session's own disposal).

### Deviations a later reader must not "fix"

- **The TCP accept loop holds no scope lease.** The store joins `AcceptLoop` explicitly in
  `DisposeCoreAsync`, which is where its program-level quiescence comes from; a lease would deadlock
  against the in-loop `DisposeLifetimeAsync()` reached via `ObserveRelayCompletionAsync`, and a safe
  lease form cannot be ordered against thread-pool scheduling. Consequence: the *session's*
  `DisposeAsync` is not a quiescence point for the accept loop.
- **`TcpRedirectSession._retired` is retained** as the owner's admission flag. `Retire()` must only
  `Cancel()` — never seal or drain — because the accept loop and `ClientResetInjector` read
  `session.Token` while unwinding (a drain there would release the CTS under a live reader).
- **The capture monitor is a dedicated raw `Thread`, joined through the scope's lease.** `Run` invokes
  its body **inline on the caller thread** and `MonitorLoop` blocks in a native `WaitOne`, so `Run` would
  stall `RunAsync` for the monitor's lifetime. `WF0003`'s own message sanctions the alternative — "or use
  a dedicated worker its owner joins" — and a lease taken by the thread body is that join:
  `await _scope.DrainAsync()` *is* the join, so no completion bridge is needed. A raw `new Thread(...)` is
  not matched by `WF0003` (the rule is syntactic over `Task.Run`/`Task.Factory.StartNew`), so this is the
  sanctioned door rather than a suppression.
- **A caller-awaited entry-point task is never registered as a scope child.**
  `TransactionalCaptureRuntime`'s `_runTask` is returned by `StartAsync` and awaited by `StopAsync`;
  registering it would make `StopAsync` wait on a drain that waits on the run task. The same reasoning
  covers `LayeredCaptureRunner.RunAsync`'s returned task. Where an owner awaits its own tree, D11 plus
  this rule is the prevention (it was a real deadlock class in four owners).
- **`Socks5UdpTransport` has no scope on purpose.** It owns no CTS, and `SendSpanAsync` is the warm
  per-datagram path, so it gets only an allocation-free `Interlocked` guard that refuses a send whose
  owner is already disposed. It throws `ObjectDisposedException` rather than returning a bool because
  `IUdpProxyTransport.SendSpanAsync` has no fail-closed return channel (a silent success would corrupt the
  coordinator's send accounting). Its residual window — a sender preempted between the guard read and
  `_sendGate.WaitAsync` can still reach a disposed gate — is accepted: closing it absolutely would need a
  lease on the datagram path, which the hot-path contract forbids.
- **The SOCKS5 per-attempt deadline CTS is not linked to the scope token.** It is created before the
  connection exists, so it stays linked to the caller token, and the race is closed by admission plus
  release-after-drain instead.
