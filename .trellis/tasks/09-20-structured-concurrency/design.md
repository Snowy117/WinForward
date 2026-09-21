# Design: quiescence scope and lifetime enforcement

> Shared technical design for the `09-20-structured-concurrency` program. Child tasks add their own
> specifics; this document defines the architecture they must respect.

## 1. Problem and the invariant we enforce

The bug class fixed by `09-20-transport-lifecycle` was: background work outliving its owner. The
root causes were mechanical, not conceptual:

- fire-and-forget escape (`_ = FooAsync()`), which also defeats the existing VSTHRD110 analyzer;
- `DisposeAsync` not awaiting in-flight work, or awaiting it via an ad-hoc handle;
- non-idempotent / inconsistently guarded disposal;
- a `CancellationTokenSource` disposed while a token may still be read;
- owner/borrower confusion.

The invariant this program makes enforceable:

> **I1 — Admission**: every piece of asynchronous work that touches an owned resource is either
> awaited inline or registered with its owner's quiescence scope.
> **I2 — Quiescence**: when an owner's `DisposeAsync` returns, every registered piece of work has
> completed and the owner's cancellation source has been released safely.

Rust achieves the analogous guarantee with affine types and a borrow checker. C# cannot (reference
aliasing under GC, no lifetimes, `ref struct` cannot cross `await`). The achievable equivalent is:
make the unsafe syntax a build error, make the safe pattern the only door, and pin each contract
with a test.

## 2. The `QuiescenceScope` primitive

Name (P1): **`QuiescenceScope`** + **`WorkLease`**. Location (P2):
`src/WinForward.Runtime/QuiescenceScope.cs`, namespace `WinForward.Runtime` — `directory-structure.md`
defines that root namespace as the "调度词汇 referenced by all groups" slot, and all four Runtime
sub-namespaces use this type.

A single type replaces the three hand-rolled copies (`TcpRedirectSessionStore._inflightSetups`,
`UdpProxyCoordinator._inFlightTeardowns`, `UdpProxySession._activeSends`).

### 2.1 Surface (shape, not final signature)

```csharp
namespace WinForward.Runtime;

internal sealed class QuiescenceScope : IAsyncDisposable
{
    public QuiescenceScope(CancellationToken linkedTo = default);  // linked parent token
    public CancellationToken Token { get; }          // the scope-owned token

    // Accounting + admission, allocation-free.
    public bool TryEnter(out WorkLease lease);       // false once sealed (D2)
    public bool IsIdle { get; }                      // pending == 0

    public Exception? Fault { get; }                 // first recorded fault (D3)
    public void RecordFault(Exception exception);

    public void Cancel();
    public Task DrainAsync();                        // seal + cancel + join; single-flight
    public ValueTask DisposeAsync();                 // => new(DrainAsync())

    public void Run(Func<CancellationToken, Task> body, string name);  // spawned child (P7)
}

internal struct WorkLease : IDisposable             // allocation-free ticket, idempotent
{
    // Dispose() => scope.Exit()
}
```

`Run` is in scope (P7) for the genuinely spawned children (the long-running loops and the
`LayeredCaptureRunner` monitor): it starts one child, holds it until completion, and records its
fault, so owners stop hand-rolling a `_runTask` field. It must not become the common path — the hot
path uses `TryEnter` + `WorkLease`.

`WorkLease` is a **plain `struct`, not a `ref struct`**: it must survive across `await` (e.g. the
future `UdpProxySession.FinishSpanSendAsync`), which C# forbids for `ref struct`s. It is idempotent,
so a double-dispose is harmless.

### 2.2 Semantics (decided)

- **D1 — Accounting and admission are separate.** The scope counts in-flight work and joins it. The
  *admission policy* (may this owner accept new work?) stays in the owner as explicit state (e.g.
  `UdpProxySession._expiring`). The scope therefore never needs an `unseal`; `CancelExpiry` cannot
  corrupt it. Rationale: a reversible seal inside the scope would force re-arming the drain TCS and
  make the primitive's state machine unbounded.
- **D2 — Late `TryEnter` after seal is rejected, not extended.** Contrast with `TaskGroup`, which
  extends the logical join. Rationale: rejecting lets the drain TCS be allocated exactly once at
  seal time, which is what keeps `Enter`/`Exit` allocation-free. Owners that must not reject check
  their admission policy before entering.
- **D3 — A child fault does not cancel siblings.** `RecordFault` stores the first exception; the
  owner decides what to do. Rationale: a faulted TCP relay or UDP session must not take down the
  coordinator. This deliberately differs from `TaskGroup.RaceGroup`/`RunGroupAsync` semantics.
- **D4 — `DrainAsync` never throws for child faults.** It is best-effort join. Faults are observed
  via `Fault`/`RecordFault`, not by faulting the owner's disposal.
- **D5 — Late-enter/exit and drain are atomic under one gate.** `TryEnter` and the seal transition
  run under the same lock so `pending == 0 && sealed` cannot be missed.
- **D6 — Nesting is explicit composition.** A parent's `DrainAsync` awaits each child scope's
  `DrainAsync`; no `AsyncLocal` ambient parent (see 2.5).
- **D7 — Only `QuiescenceScope` owns a `CancellationTokenSource`.** The scope owns the CTS
  (`Token`/`Cancel`) and links it to the parent token, replacing the ad-hoc
  `TcpRedirectSession.Lifetime` and `UdpProxySession._lifetime`. This turns I2's "token is no longer
  read after dispose" into a structural property, not a per-site guard.
- **D8 — Terminology and decisions live in the Trellis docs (P9).** The repo has no `CONTEXT.md`,
  `CONTEXT-MAP.md`, or `docs/adr/`, and the user elected to keep this vocabulary in `.trellis/spec/`
  plus the task artifacts instead of creating them.
- **D9 — Fault observation is intrinsic to the child task body (F2).** A child never relies on an
  external hook to observe its own failure: its body records the fault (`scope.RecordFault`) and then
  rethrows, preserving the task's faulted completion. Rationale: an external
  `ContinueWith(OnlyOnFaulted)` observer is a *patch* for a task someone may abandon; making
  observation intrinsic makes an abandoned task safe **by construction**, which is what lets `WF0002`
  exist with no allowlist entry. See §4.4.

### 2.3 Allocation strategy and gate implementation (hot-path constraint)

`.trellis/spec/backend/hot-path.md` requires zero-allocation on the packet pipeline. Two hard rules:

- `Enter`/`Exit` are `Interlocked`/`lock` arithmetic only — no per-call allocation.
- The `_drained` `TaskCompletionSource` is allocated **once, lazily, at seal time**. The legacy
  `EnterSetup` pattern (allocate a TCS on every 0→1 transition) must not be carried over: on the
  UDP send path that would allocate per datagram.

**Lease contract (found while prototyping; C1 must document it on the type):** `WorkLease` must be a
**mutable** `struct`, not `readonly` — idempotency ("double-dispose is harmless") requires
`Dispose()` to null its scope field, which a `readonly` struct cannot do. It must also stay unboxed:
`using IDisposable l = lease;` or storing it in an interface-typed field boxes it and reintroduces a
hot-path allocation.

**Measured evidence** (throwaway microbench in `/tmp/wf-scope-bench/`; net10.0 10.0.12, workstation
GC, 20 M iterations, single-threaded, uncontended, no BDN statistics):

| Variant | ns/op | B/op |
|---------|------:|-----:|
| delegate-call baseline | 2.79 | 0.0000 |
| today's `UdpProxySession` shape (one gate, flag + counter) | 45.59 | 0.0000 |
| scope with `lock` + struct lease | 48.03 | 0.0000 |
| scope with packed-word CAS (bit 0 = sealed, rest = pending) | 21.58 | 0.0000 |
| capturing closure (`Run`'s cold-path cost) | — | 88 B/op |
| cached static lambda | — | 0.00 B/op |

Readings: (a) allocation is **0 B/op for every gate variant**, so the primitive adds no hot-path
allocation; (b) the scope costs **+2.4 ns/op over today's shape** (≈ +5%) — no regression, so R5
holds; (c) a packed-word CAS is **2.1× faster than today** (−24 ns/op) because it drops `Monitor`
entirely; (d) `Run`'s closure cost (≈ 88 B) is per-startup/per-event, not per packet, and hoisting a
loop body's delegate to a field makes it 0 B.

**Decision (2026-09-20): C1 picks the gate implementation using the repo's full benchmark suite**
(`benchmarks/WinForward.Benchmarks`: BDN Perf + Stability soak), not this microbench. C1 prototypes
both the `lock` and the packed-word CAS variants and records the comparison; the table above is
directional only — it is uncontended, so real contention could reorder the variants. C1's acceptance
is both: a warm `TryEnter`/`Exit` allocates 0 bytes **and** packet-path throughput is at least
today's.

### 2.4 Concurrency and lock ordering

`UdpProxySession` currently guards `_expiring`/`_activeSends` with `_activityGate`. With the scope,
`SendSpanAsync` checks admission under `_activityGate`, then `TryEnter`s the scope. The scope's
internal gate is a **leaf** (it touches only its own fields), so the order
`activityGate → scope.gate` is acyclic. C1 documents this order on the type.

### 2.5 Nesting

Scopes nest by composition, not by ambient state: a parent's `DrainAsync` cancels and awaits each
child scope's `DrainAsync` in order. No `AsyncLocal` parent. Rationale: explicit, allocation-free,
and testable. C3 applies this for `UdpProxyCoordinator` → per-session scopes.

## 3. Enforcement (C2)

### 3.1 Project and wiring (E3, decided)

New **`analyzers/WinForward.Analyzers/`** — a new top-level directory mirroring `benchmarks/` —
targeting `netstandard2.0` and referencing `Microsoft.CodeAnalysis.CSharp`. A new
**`src/Directory.Build.props`** (which must first `<Import>` the repo-root one, since MSBuild stops
at the nearest `Directory.Build.props`) references it:

```xml
<ProjectReference Include="../../analyzers/WinForward.Analyzers/WinForward.Analyzers.csproj"
                  OutputItemType="Analyzer" ReferenceOutputAssembly="false" PrivateAssets="all" />
```

so the rules apply to `src/**` exactly, and no self-reference cycle is possible (the analyzer project
does not live under `src/`). `Directory.Packages.props` gains the `Microsoft.CodeAnalysis.CSharp*`
pins — central package management is in use and none exist today. Analyzer tests live in
`tests/WinForward.Analyzers.Tests` using `Microsoft.CodeAnalysis.CSharp.Analyzer.Testing` + `xunit`.

### 3.2 Rules (E1, decided)

| Id | Rule | Notes |
|----|------|-------|
| `WF0001` | Do not discard an **unawaited** awaitable (`_ = <awaitable>`) | The escape that defeats VSTHRD110. Fix: `await`, or `scope.Run(...)`. |
| `WF0002` | Do not use `Task.ContinueWith` | Eliminated structurally (§4.4) — needs no allowlist entry. |
| `WF0003` | Do not `Task.Run` / `Task.Factory.StartNew` outside the primitive | Allowlisted to the primitive/analyzer projects. |

`WF0004` was **dropped**: a bare unawaited awaitable statement is exactly compiler warning `CS4014`,
already fatal under this repo's `TreatWarningsAsErrors=true`, and `WF0001` covers the `_ =` loophole
that defeats it. Rules key on **awaitable types** (`Task`, `Task<T>`, `ValueTask`, `ValueTask<T>`,
custom awaitables). Analyzer tests must prove the rule does **not** fire on the benign shapes that
exist in `src/` today:

- `_ = await FooAsync(...)` — awaited, only the result is discarded (`Socks5ControlConnection.cs:227`);
- `_ = task.Exception` — `AggregateException`, not awaitable (`TcpProxyRelay.cs:290`);
- `_ = Interlocked.Add(...)` / `_ = Increment(...)` — numeric (`RuntimeCounters.cs:104-112`);
- `_ = TryWrite(...)` / `_ = TryAdd(...)` — `bool` (`IPAddressValue.cs:103`, `Socks5Udp.cs:35`,
  `RuntimeCounters.cs:95-96`);
- `_ = character switch { ... }` — non-awaitable (`RuntimeLogging.cs:120`).

Rules are scoped to `src/**` by the 3.1 wiring, so `tests/**` and `benchmarks/**` are not flagged.

### 3.3 The audited escape hatch: one door, `Run` (E2, decided)

Exactly one sanctioned door, grep-discoverable, reason-required:

```csharp
public bool Run(Func<CancellationToken, Task> body, string reason);
```

It replaces both the `Detach("reason")` method and the `[Detach]` attribute forms, which are dropped.

The insight: an escape hatch that merely *sanctions* `_ = FooAsync()` fixes nothing — it renames the
problem. Fire-and-forget is dangerous for three separable reasons: the work is **untracked** (it may
outlive the owner), **unobserved** (its failures are invisible), and **unordered** w.r.t. shutdown.
`Run` fixes all three: the child enters the same in-flight set as `TryEnter` (so `DrainAsync` awaits
it), its exception is routed to `RecordFault`, and it cannot start after the seal.

Why a **factory** rather than an already-started `Task` (the `task.Detach("reason")` form): the
factory is invoked *after* the scope's admission check, so a sealed scope can refuse to start it and
return `false`. With an already-started task that refusal is impossible — the work is in flight
before the scope sees it, and D2/P5 could not be enforced.

`reason` must be non-empty; it is used as the child's diagnostic name (so a fault from an anonymous
child still names its site) and is the audit grep target (`rg '\.Run\('`).

`Run` is **cold-path only** — it necessarily allocates a delegate + task wrapper; the hot path stays
`TryEnter` + `WorkLease`. `Run` carries only 2 of the 6 legacy fire-and-forget sites; the rest are
deleted or solved structurally (see 4.1), which is the evidence that the door is narrow by design
rather than a blanket rename.

### 3.4 Rollout, severity, and suppression policy (E4, E5, decided)

Rules are explicitly `error` in `.editorconfig` (`dotnet_diagnostic.WF0001.severity = error`, …)
rather than relying on `TreatWarningsAsErrors`, so severity cannot silently downgrade if that switch
changes. C2 also adds `AnalyzerReleases.Shipped.md` / `AnalyzerReleases.Unshipped.md` (rule `RS2008`),
which the repo does not have today.

Because C2 precedes C3/C4, the legacy sites are temporarily allowlisted per-file via glob-scoped
`.editorconfig` entries with a written reason (matching `quality-guidelines.md`, which forbids global
suppression but permits glob-scoped, reason-documented entries). C3 removes the cluster's entries; C4
removes the rest. The program is not complete while any allowlist entry remains.

### 3.5 `.trellis/spec/backend/async-lifetime.md` (E7 + F3, decided)

The vocabulary and the primitive's contract live in a spec file rather than in the task artifacts,
because a task's `design.md` is archived when the task closes, while "what is this mechanism" is
needed by every future session.

`async-lifetime.md` holds: the glossary (quiescence, admission, seal, drain, owner/borrower, work lease,
in-flight/pending, teardown reason), invariants I1/I2, the `QuiescenceScope` durable contract
(surface, D1–D9, the allocation rule, the lock order), the `Run` door's admission rules, and the WF
rule table. It does **not** hold the program plan — migration order and the per-owner mapping stay in
§4 of this file.

Ownership over time: **C1 creates it** (the primitive's contract is C1's natural deliverable), **C2
adds the WF rule table**, **C3/C4 add per-owner notes**.

Naming: it is **not** `lifecycle.md`, because `backend/traffic-policy-lifecycle.md` already owns that
word for the *traffic-policy* lifecycle (domains, loop prevention, idle sweep). Creating the file
also means adding its row to `.trellis/spec/backend/index.md`, which lists every guide — C1 does both.

## 4. Migration (C3, C4)

### 4.1 Eliminating the fire-and-forget sites

| Site | Classification | Target shape |
|------|----------------|--------------|
| `TcpProxyRelay.cs:284` `_ = ShutdownSend(...)` | Spurious async | Make it a synchronous `Socket.Shutdown` call. |
| `TcpProxyRelay.cs:289-290` (`ObservePump`), `TcpRelayFaultObserver.cs:27` | Observer | **Deleted.** Fault observation becomes intrinsic to each pump body (§4.4). |
| `MultiAdapterCaptureLoop.cs:110` `_ = ForwardDegradationAsync(...)` | Notification | Owned callback, invoked synchronously. |
| `TcpRedirectSessionStore.cs:157` `_ = RunDisposeAsync(start)` | Eager start | `DisposeAsync` returns `new ValueTask(scope.DrainAsync())`; no discard needed. |
| `UdpProxySession.cs:312` `_ = receiveFailureHandler(this)` | **Re-entrancy** | Signal/join split — see 4.2. |
| `LayeredCaptureRunner.cs:144-146,149` | Real concurrency | Scope children (loop body is its own async method with a lease). |

### 4.2 The signal/join split (the only genuine re-entrancy)

`UdpProxySession.ReceiveLoopAsync` cannot await `receiveFailureHandler` because the handler disposes
the session, whose `DisposeAsync` awaits the receive loop — a cycle. The fix is an API shape, not a
scope feature:

- `Start(Func<UdpProxySession, Task>)` (`UdpProxySession.cs:124-128`) becomes
  `Start(Action<UdpProxySession>)` — a **synchronous signal**, so the callee cannot await anything.
- The loop keeps its tail behaviour of writing `_receiveFailure` and then invoking the signal
  (`UdpProxySession.cs:302-313`), minus the `_ =` discard.
- The coordinator supplies the action and maps it onto **its own** scope:
  `coordinatorScope.Run(ct => RemoveReceiveFailedSessionCoreAsync(session), "udp.receive-failure")`.
  Starting the teardown is synchronous; joining it happens in the coordinator's `DrainAsync`.
- `UdpProxyCoordinator._inFlightTeardowns` + `DrainInFlightTeardownsAsync`
  (`UdpProxyCoordinator.cs:37,308-320,461-471`) are deleted. The comment-invariant at `:304-306`
  ("every handler that will ever run has registered before this snapshot is taken") becomes a
  structural property: the seal makes a late `Run` fail instead of racing.
- During coordinator shutdown the scope is already sealed, so `Run` returns `false` and no teardown is
  spawned. That is safe — `DisposeCoreAsync` disposes every session anyway (`:289`) — and is strictly
  stronger than today's ordering assumption.

Rejected alternative: hosting the receive loop itself on the coordinator's scope, i.e.
`Run(async ct => { await session.RunReceiveLoopAsync(ct); if (session.Faulted) await TearDownAsync(session); })`.
It removes one handle, but it is **circular**: the coordinator's `DrainAsync` would await the session's
receive loop while `session.DisposeAsync()` — awaited by the coordinator at `:289` — awaits the
coordinator's scope, i.e. its own drain. The loop must stay on the session's scope (that is what makes
`session.DisposeAsync` a real quiescence point); only the *teardown* moves to the coordinator's scope.

### 4.3 Per-owner mapping

| Owner | Today | After |
|-------|-------|-------|
| `TcpRedirectSessionStore` (`:32-36,58-73,139-203`) | `_inflightSetups` + `_setupsDrained` + `_disposeTask` | `QuiescenceScope` |
| `UdpProxySession` (`:46,54-68,139-251`) | `_activeSends` + `_receiveLoop` + `_expiring` + `_receiveFailure` + `_disposeTask` + `_lifetime` | scope (accounting + drain + owned CTS) + `_expiring` (admission, stays) |
| `UdpProxyCoordinator` (`:24,28,37,308`) | `_inFlightTeardowns` + `_shutdown` | scope; session scopes as nested children; `_inFlightTeardowns` deleted (§4.2) |
| `TcpProxyRelay` (`:114,129,322`) | `Completion` property + `ObservePump` | scope tracks both pumps; `Completion` keeps its relay-level (stall early-exit) semantics |
| `TcpRedirectSession` (`:19-44`) | `AcceptLoop` property + `Lifetime` CTS | scope child + scope-owned CTS |
| `TcpRelayFaultObserver` (`:27`) | external `ContinueWith` on discarded relays | **file deleted** — observation is intrinsic (§4.4) |

### 4.4 Intrinsic fault observation — deletes both observer mechanisms (F2)

`TcpProxyRelay` runs two pumps and orchestrates them in `RunPumpAsync`, exposed as `Completion`
(`TcpProxyRelay.cs:126,129`). Two of its three exit paths abandon a still-running pump:

- the **stall fast-exit** (`:144-150`) cancels and returns without awaiting the other pump;
- the **fault path** (`:166-172`) cancels, rethrows, and abandons the pump that had not completed.

`ObservePump` (`:287-294`) exists only to attach `ContinueWith(OnlyOnFaulted)` to an abandoned pump so
its later exception is not reported as an unobserved task exception. `TcpRelayFaultObserver.Observe`
(`TcpRelayFaultObserver.cs:23`) is the same idea for a *whole discarded relay*, called from
`TcpProxyRelay.DisposeAsync` (`:308`) and from the acceptor's attach-failure branch
(`TcpRedirectAcceptor.cs:120`).

Both are patches for one violation: `Completion` can complete while a child is still running, which is
exactly what I2 forbids. The structural fix makes fault observation intrinsic to each pump body:

```csharp
if (!_scope.TryEnter(out var lease)) return PumpResult.Stalled;   // sealed ⇒ refuse
try { /* existing read/write loop */ }
catch (Exception exception) { _scope.RecordFault(exception); throw; }
finally { lease.Dispose(); }
```

This removes the symptom (unobserved exceptions) **and** the cause (the abandoned pump is now a
tracked child that `DrainAsync` joins). Deletions: all of `TcpRelayFaultObserver.cs`, `ObservePump`,
the `#pragma warning disable RCS1075` plus the empty `catch (Exception)` in `DisposeAsync`
(`:320-329`), and `TcpRedirectAcceptor.cs:120`. `ITcpRelay.Completion` keeps its relay-level
semantics — the stall fast-exit is deliberate and awaiting a stalled pump could hang — so this is
**not** a change to `Completion`.

Two findings from reading this code, to be settled by C3 tests rather than by argument:

- `TcpProxyRelay.cs:305-307` comments that "the dispose path discards the relay **without ever
  awaiting** its completion", yet `:320-323` does `await Completion` in a `finally` — a stale comment
  from an earlier patch.
- Because of that `await`, the external observers look **redundant for observation** today
  (`DisposeAsync` always awaits `Completion`, and `TcpRedirectAcceptor.DiscardUnattachedRelayAsync`
  calls `relay.DisposeAsync()`), leaving only the `tcp.relay.faulted` debug log as their live effect.
  C3 must verify by fault injection: delete the observers, then assert no unobserved task exception
  results and that `tcp.relay.faulted` is still recorded.

## 5. Compatibility, rollback, and constraints

- No wire/protocol/config/schema change; no user-visible behavior change.
- The primitive is additive; each child is an independent commit. Rollback = revert the child.
- Analyzer rules are inert if C2 is reverted; the primitive stays useful regardless.
- Constraints preserved: fail-closed proxy semantics (`error-handling.md`), zero-allocation packet
  pipeline (`hot-path.md`), and the existing quality gates (`quality-guidelines.md`).

## 6. Open technical risks

- **Allocation regression** if `Enter`/`Exit` acquire more than the existing `_activityGate`. C1
  must prove 0-byte warm Enter/Exit.
- **Analyzer false positives** on custom awaitables or legitimate discards. Mitigation: key on
  awaitable types; analyzer tests cover the benign cases found in `src/`.
- **Lock-order drift** when the scope gate is nested under per-owner gates. C1 documents the order;
  C3 verifies no reverse acquisition exists.
- **Phasing churn**: C2's allowlist must shrink monotonically; C3/C4 must not re-introduce sites.
- **Drain reliability is now bounded by pump cancellation responsiveness (F2).** The scope's
  `DrainAsync` awaits pumps that today nobody awaits (the stall fast-exit abandons one on purpose). If
  a pump does not respond to cancellation, the drain can hang where today's code would not.
  Mitigations: teardown closes the socket, and owners already bound their relay wait with
  `WaitAsync(token)` (`TcpRedirectAcceptor.cs:206`, documented as "a relay whose completion never
  settles"). C3 adds a test that a stalled/cancelled pump still reaches the drain, and must not
  convert `Completion` into "both pumps finished".
- **Observer redundancy is a claim, not a proof (§4.4).** The deletability of `TcpRelayFaultObserver`
  rests on `DisposeAsync`/`DiscardUnattachedRelayAsync` always awaiting `Completion`. C3 must verify by
  fault injection before deleting, and keep the `tcp.relay.faulted` log if the injection shows it was
  the only consumer.
- **Raw `Thread` is deliberately not banned (E6).** Both `src/` sites are already joined:
  `NdisCapture.cs:169` exposes its thread's exit as `new ValueTask(outcome.Task)`, which
  `DisposeAsync` awaits, and `SetupExecutor.cs:229`'s workers `return` when `_shutdown` is canceled.
  A rule here would only fire false positives on a legitimate pattern (dedicated thread for a
  blocking native call) and erode trust in the whole rule set.
