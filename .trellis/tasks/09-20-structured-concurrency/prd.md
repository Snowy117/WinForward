# Structured concurrency: quiescence scope and lifetime enforcement

## Goal

Turn the async-lifetime bug class in `src/WinForward.Runtime` from "caught by review and per-site
patches" into "unrepresentable or build-breaking". Concretely: (1) replace the three hand-rolled
copies of the same "in-flight count + seal + join" mechanism with one audited quiescence primitive;
(2) add build-time enforcement that bans the fire-and-forget escape syntaxes, with a single
auditable escape hatch; and (3) migrate the async-lifecycle owners so that every `DisposeAsync`
returns only after its registered work has finished.

Direct follow-up to archived `09-20-transport-lifecycle`, whose journal recorded the deferred item
"structured concurrency (TaskScope) + lifetime analyzers". This parent owns the source requirement
set, the child-task map, cross-child acceptance, and final integration review.

Non-goal: no user-visible behavior change, no config/schema change.

## Source Material

- Code read this session: `src/WinForward.Runtime/TcpRedirect/TcpRedirectSessionStore.cs`,
  `.../TcpRedirect/TcpRedirectSession.cs`, `.../UdpProxy/UdpProxySession.cs`, plus the lifecycle
  inventory across the whole `src/` tree.
- Contracts to preserve: `.trellis/spec/backend/hot-path.md` (zero-alloc packet pipeline),
  `.trellis/spec/backend/quality-guidelines.md`, `.trellis/spec/backend/error-handling.md`,
  `.trellis/spec/backend/tcp-local-redirect.md`, `.trellis/spec/backend/udp-relay.md`.
- Analyzer infrastructure: `Directory.Build.props` / `Directory.Packages.props` / `.editorconfig`.

## Background (confirmed facts)

**Three hand-rolled copies of the same primitive** — this work formalizes, it does not invent:

- `TcpRedirectSessionStore.cs:35,58-73,177-179` — `_inflightSetups` count + `_setupsDrained` TCS +
  `EnterSetup`/`ExitSetup`; `EnterSetup` throws `ObjectDisposedException` once `_disposed`.
- `UdpProxyCoordinator.cs:37,308` — `_inFlightTeardowns` list + `DrainInFlightTeardownsAsync`.
- `UdpProxySession.cs:68,144-178,195` — `_activeSends` count + `TryBeginExpiry` seal-if-idle.

**Fire-and-forget inventory (the bug class)** — only two of these are real concurrency:

- `TcpRedirectSessionStore.cs:157` `_ = RunDisposeAsync(start)` — eager-start of drain.
- `UdpProxySession.cs:312` `_ = receiveFailureHandler(this)` — genuine re-entrancy avoidance.
- `TcpProxyRelay.cs:284` `_ = ShutdownSend(...)` — `Socket.Shutdown` is synchronous; task is spurious.
- `TcpProxyRelay.cs:289-290` and `TcpRelayFaultObserver.cs:27` — fault-observation `ContinueWith`.
- `MultiAdapterCaptureLoop.cs:110` `_ = ForwardDegradationAsync(...)` — notification callback.
- `LayeredCaptureRunner.cs:144-146` (`StartNew` LongRunning + `Unwrap`) and `:149` (`Task.Run`) —
  genuine long-running background loops.

**Owner quiescence handles (ad-hoc, no shared tracker):** `TcpProxyRelay.Completion`
(`TcpProxyRelay.cs:129`), `TcpRedirectSession.AcceptLoop` (`TcpRedirectSession.cs:23`),
`TcpRedirectSessionStore._setupsDrained`, `UdpSessionSlot.Completion` (`UdpProxyCoordinator.cs:494`),
`UdpProxySession._receiveLoop` (`UdpProxySession.cs:54`), `NdisCapturePump._runCompletion`
(`NdisCapture.cs:124`), `CaptureLifecycle._runTask`/`_cleanupTask` (`CaptureLifecycle.cs:36-37`),
`LayeredCaptureRunner._runTask` (`LayeredCaptureRunner.cs:63`), `IdleExpirySweeper._loop`
(`IdleExpirySweeper.cs:29`), `RuntimeHeartbeat._loop` (`RuntimeHeartbeat.cs:62`).

**Enforcement gap today:** the explicit `_ =` discard defeats VSTHRD110 (active), so no build-time
rule catches the fire-and-forget sites above. `IDisposableAnalyzers` is not referenced.
`AnalysisLevel=latest` (so CA2025 "do not pass IDisposable into unawaited tasks" is available).
`jb inspectcode` is a CI-only second gate, not a build-time red squiggle.

**Ownership facts (from `09-20-transport-lifecycle`):** composition owns the pools/executor
(`DurableCaptureBundle`); coordinators borrow. `SetupExecutor` is a single instance shared by BOTH
coordinators, so no single coordinator can own it.

**Hot-path constraint:** `hot-path.md` requires zero-allocation on the packet pipeline. The existing
`EnterSetup` allocates a new TCS at every 0→1 transition — unsuitable for the per-datagram send
counter. Any primitive must make `Enter`/`Exit` allocation-free; the drain TCS may only be
allocated once, at seal time.

## Technical Notes

**Enforcement mechanism (decided 2026-09-20): bespoke Roslyn analyzer.** R2 lands as a new
`analyzers/WinForward.Analyzers/` project (E3), wired into `src/**` from a new `src/Directory.Build.props`,
plus an analyzer test project. Rationale: build-time enforcement gives a red squiggle in
any IDE and a hard failure under the repo's `TreatWarningsAsErrors=true`; it is unit-testable and
IDE-agnostic. The existing packages cannot see the `_ =` discard, and `jb inspectcode` is
CI/commit-only and JetBrains-specific.

## Requirements (parent source set)

- **R1 — One audited quiescence primitive.** A single type provides: counted in-flight tracking
  (`Enter`/`Exit`, allocation-free), a seal-and-join `DrainAsync` (single-flight, idempotent),
  an owned cancellation source, and first-fault recording. It replaces the three hand-rolled copies.
- **R2 — Build-time enforcement.** The escape syntaxes are build errors: `_ = <awaitable>`
  (**unawaited** only — `_ = await X` is legitimate), `.ContinueWith(...)`, and
  `Task.Run`/`Task.Factory.StartNew` outside the primitive's allowlist. Bare unawaited statements are
  already fatal (`CS4014` under `TreatWarningsAsErrors`), so no separate rule is needed. Exactly one
  auditable escape hatch exists (`QuiescenceScope.Run`), is grep-discoverable, and requires a written
  reason. Mechanism: see Technical Notes.
- **R3 — Owner migration.** Migrated owners satisfy the invariant "`DisposeAsync` has returned ⇒
  no registered child is still running", pinned by a test per owner.
- **R4 — Eliminate the six fire-and-forget sites** by classification (see F1/F2): sync-ify
  (`ShutdownSend`), **delete** both `ContinueWith` observer mechanisms in favour of fault recording
  intrinsic to the child task body, owned callback (degradation notice), scope child (long-running
  loops), and signal/join split for the genuine re-entrancy case (`UdpProxySession.cs:312`, where
  the failing receive loop must not join its own teardown).
- **R5 — No performance regression.** Existing allocation gates stay green
  (`HotPathAllocationGateTests`); no new allocation on any steady-state packet path.
- **R6 — Integration acceptance.** Format / Release build / full test suite / `jb inspectcode`
  gates all green at parent completion.

## Task map (children)

Decided split; each child is independently verifiable and archived on its own:

- **C1 `quiescence-scope`** — the primitive + unit tests. No owner changes. (blocked by: nothing)
- **C2 `lifetime-analyzers`** — the `analyzers/WinForward.Analyzers/` project, the WF rules, the
  escape hatch, build wiring, analyzer tests. (blocked by: nothing)
- **C3 `lifecycle-migration-cluster`** — migrate the TCP/UDP lifecycle cluster and remove its
  escape sites. (blocked by: C1)
- **C4 `lifecycle-migration-rest`** — migrate the remaining owners (`CaptureLifecycle` /
  `LayeredCaptureRunner` / `NdisCapturePump` / `IdleExpirySweeper` / `RuntimeHeartbeat` /
  `Socks5*`). (blocked by: C1; follows C3)

Ordering is written here, not implied by tree position: C1 first. C3 depends on C1. C2 is
independent of C1, but its rules flag the legacy sites in C3/C4, so C2 must land with an explicit,
reason-documented allowlist that C3/C4 shrink to zero.

## Acceptance Criteria (parent / integration)

- [ ] One primitive type is the only in-flight/seal/join mechanism in `src/WinForward.Runtime`;
      the three hand-rolled copies are gone.
- [ ] Each banned pattern fails the build (demonstrated by an analyzer test or a scratch file).
- [ ] The escape hatch appears at most at the audited sites and each has a written reason.
- [ ] For every migrated owner, a test shows `DisposeAsync` completing only after children finish.
- [ ] `HotPathAllocationGateTests` green; no steady-state packet-path allocation delta.
- [ ] Full gates green: `dotnet format ... --verify-no-changes`, Release build zero-warning,
      `dotnet test -c Release`, `jb inspectcode -e=HINT` zero issues.

## Out of Scope

- L3 (typestate / a dedicated `Lifetime` wrapper type / `ref struct` leases) and L4 (source
  generator) from the 2026-09-20 discussion — possible later program, not this one.
- Any change to packet-path behavior or to the fail-closed proxy semantics.
- Migrating owners outside `WinForward.Runtime`. Q2 settled the split inside it: C3 migrates the
  TCP/UDP cluster, C4 the rest.

## Resolved Decisions

- **Q1 — enforcement mechanism:** bespoke Roslyn analyzer (`analyzers/WinForward.Analyzers/`), not
  existing-packages-only. See Technical Notes.
- **Q2 — migration breadth:** phased. C3 migrates the TCP/UDP lifecycle cluster (where the bug
  class actually bit); C4 migrates the remaining owners afterwards.

### Primitive decisions (confirmed 2026-09-20, user: "我同意你的推荐")

- **P1 — naming:** `QuiescenceScope` + `WorkLease`. Deliberately **not** `TaskScope`/`TaskGroup`
  named, because those carry "fault propagates to siblings" semantics that we reject (P6).
- **P2 — location:** `src/WinForward.Runtime/QuiescenceScope.cs`, namespace `WinForward.Runtime`
  (the root namespace is the "调度词汇 referenced by all groups" slot).
- **P3 — the scope owns the `CancellationTokenSource`** (linked from an optional parent token).
- **P4 — accounting and admission are separate:** the scope counts/joins; admission policy (e.g.
  `UdpProxySession._expiring`) stays in the owner. The scope has no `unseal`.
- **P5 — late `TryEnter` after seal is rejected**, not extended.
- **P6 — a child fault is recorded, not propagated:** siblings keep running and `DrainAsync` never
  throws for a child fault.
- **P7 — surface is `TryEnter` + `WorkLease` for the hot path, *and* `Run` for cold spawns.**
- **P8 — scopes nest by explicit composition** in `DisposeAsync`; no `AsyncLocal` ambient parent.
- **P9 — terminology and decisions live in the Trellis docs** (`.trellis/spec/` + task artifacts).
  Do **not** create `CONTEXT.md`, `CONTEXT-MAP.md`, or `docs/adr/`.

### Enforcement decisions (confirmed 2026-09-20, user: "可以")

- **E1 — four rules** (revised 2026-09-21; originally three). `WF0001` unawaited awaitable discard
  (must not fire on `_ = await X`), `WF0002` `.ContinueWith`, `WF0003`
  `Task.Run`/`Task.Factory.StartNew`, `WF0004` a bare unawaited awaitable **expression statement**
  (fires whether or not the enclosing method is `async`). `WF0004` was originally dropped on the
  assumption that `CS4014` covered that shape; measured 2026-09-21 on net10.0, `CS4014` fires
  **only** inside `async` methods, so a bare `FooAsync();` in a synchronous method is silently
  unobserved. Closing the fire-and-forget class at build time requires the rule. See `design.md`
  §3.2.
- **E2 — one door:** `bool QuiescenceScope.Run(Func<CancellationToken, Task> body, string reason)` —
  tracked (drained), fault-recorded, seal-respecting, reason-required. The `Detach` method and
  `[Detach]` attribute forms are dropped. See `design.md` §3.3.
- **E3 — analyzer project at `analyzers/WinForward.Analyzers/`**, referenced from a new
  `src/Directory.Build.props`, so the rules apply to `src/**` exactly and the analyzer cannot
  self-reference. Roslyn packages get pinned in `Directory.Packages.props`.
- **E4 — rollout is allowlist-then-shrink.** Rules are errors from C2, with per-file
  reason-documented `.editorconfig` exemptions that C3/C4 delete (final state: none).
- **E5 — severity is explicit `error` in `.editorconfig`**, and C2 adds
  `AnalyzerReleases.Shipped/Unshipped.md` (`RS2008`).
- **E6 — raw `new Thread(...)` is not banned.** Both `src/` sites are already joined; a rule would
  only produce false positives.
- **E7 — terminology and invariants go in a new `.trellis/spec/backend/async-lifetime.md`** (not folded
  into `error-handling.md`, whose subject is error policy).

### Migration decisions (confirmed 2026-09-20, user: "可以。继续。")

- **F1 — the UDP receive-failure path becomes a synchronous signal plus a coordinator-owned join.**
  `UdpProxySession.Start` changes from `Func<UdpProxySession, Task>` to `Action<UdpProxySession>`, and
  the coordinator maps that action to `coordinatorScope.Run(ct => RemoveReceiveFailedSessionCoreAsync(session), "udp.receive-failure")`.
  `UdpProxyCoordinator._inFlightTeardowns` and `DrainInFlightTeardownsAsync` are deleted; the
  "every handler has registered before the drain snapshot" comment-invariant becomes structural.
  The `_ =` at `UdpProxySession.cs:312` therefore disappears by structure, not by an escape hatch.
  Rejected: hosting the receive loop itself on the coordinator's scope — circular, because
  `session.DisposeAsync()` (awaited by the coordinator) would await the coordinator's own scope.
- **F2 — fault observation moves inside the child task; both observer mechanisms are deleted.**
  Each pump body records its own fault (`RecordFault` + rethrow) instead of an external
  `ContinueWith`. This deletes `TcpRelayFaultObserver.cs` (whole file),
  `TcpProxyRelay.ObservePump` (`:287-294`), the `#pragma warning disable RCS1075` and the empty
  `catch` in `TcpProxyRelay.DisposeAsync`, and the `TcpRelayFaultObserver.Observe` call at
  `TcpRedirectAcceptor.cs:120`. Consequences: a discarded relay is observed *by construction*
  (no "who remembers to call `Observe`"), observation is strictly stronger than the current
  `ExecuteSynchronously` continuation (same stack frame), and `WF0002` needs **no** allowlist entry.
  `ITcpRelay.Completion` keeps its relay-level semantics (the stall fast-exit at
  `TcpProxyRelay.cs:144-150` is deliberate and must not be changed into "both pumps finished");
  the scope's `DrainAsync` becomes the true quiescence point, and it now awaits the previously
  abandoned pump — see the risk in `design.md` §6.
- **F3 — `async-lifetime.md` carries the vocabulary, the invariants, and the primitive's durable
  contract.** Contents: glossary, I1/I2, the `QuiescenceScope` contract (surface, D1–D9, allocation
  rule, lock order), the `Run` door's admission rules, and the WF rule table. C1 creates it; C2 adds
  the WF table; C3/C4 add per-owner notes. The task's `design.md` keeps the *program plan* (migration
  order, per-owner mapping) — one document answers "what is this mechanism", the other "how is it
  being moved".

---
_Keep this file focused on requirements and acceptance. Technical design goes in `design.md`,
execution order in `implement.md`; both are required before `task.py start` (complex task)._
