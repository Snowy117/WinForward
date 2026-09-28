# UDP session teardown and session-tier allocation reduction

## Goal

Reduce the per-session managed allocations of the UDP **teardown path** (T leg, 1,458.5–1,967.9 B/session) and the **session-tier construction/start leg** (C2, 2,698.7 B/session) on the clean (out-of-process, fake-transport) measurement basis of the 09-22 redo: split each leg into attributable components first (with per-sub-case first-chance exception counts), then implement the measured reductions and re-measure on the same instruments with ≥3 runs, updating the regression anchors.

User value: the bookkeeping layer is 42 % of the clean whole-cycle cost (5,727 of ~13,700 B/session, redo §5); teardown and session tier are the two largest attributable legs and the scheduled engineering sequence (rank 2 → 3). The standing directive is "minimize allocation as much as possible"; anchors are regression guard bands, not targets.

## Background — confirmed facts

Research basis (`.trellis/tasks/archive/2026-09/09-22-session-creation-cost-redo/research/`):

- **Teardown leg** T = S5−S4: **1,458.5 B/session** (matched `InvocationCount=6` shape) / **1,967.9** (single-pass); 25–34 % of the Noop product-shaped total. Internal split (scope drain vs slot/tombstone removal vs transport dispose) is **unmeasured**.
- The teardown window carries **≈2 first-chance exceptions/session** (130 / 328 / 2,128 at N = 1/100/1000; byte-identical across every batch and both benchmark revisions; zero in S0–S4). Source not attributed.
- **Session tier** = S4−S2 = **2,890.8 B/session**; the C2 probe covers 2,698.7 of it (93.4 %) = context record + `UdpProxySession` + quiescence scope/CTS + `Start` (receive-loop state machine + receive-window lease rent). Internal split **unmeasured**.
- Anchors in force (`hot-path.md` §3): T1 Noop probe ≤6,000 raw / ≤5,500 product-shaped (measured 5,727.0 / 5,266.2); T2 capacity + admission + setup-start ≤1,500 (measured 1,432.9); T3a framework ≤8,200 (7,952.0); T3b churn ≤14,500 wave / ≤14,300 sustained (13,249–14,070 / 13,720–13,869); T3c echo-fed probe ≤17,500 (17,021.2).
- Real-dial instruments must run the loopback SOCKS5 server **out of process** when allocation counters are read (harness rule of the redo; in-process is a diagnostic whose number carries the harness share).
- Known doc inconsistency to reconcile during the spec update: `hot-path.md` §3 quotes the product-shaped Noop as 5,276 (from the pre-redo 5,737); the redo value is 5,266.2.

Code facts (inspected 2026-09-22, this task):

- `UdpProxySession` (`src/WinForward.Runtime/UdpProxy/UdpProxySession.cs`): per-session `QuiescenceScope` (:81); `DisposeCoreAsync` = scope cancel → transport dispose → await receive loop (OCE/ODE caught, :211-235) → `finally _scope.DrainAsync()` (:233).
- `QuiescenceScope` (`src/WinForward.Runtime/QuiescenceScope.cs`): the CTS is allocated in the constructor and is **linked** when the shutdown token can be canceled (:35-37); `DrainAsync` allocates the `_drained` TCS + a `_joined` TCS lazily at seal (:155-166) and runs a detached `DrainCoreAsync` (:180-202); `Cancel()` guards the disposed-CTS ODE (:111-121).
- `UdpProxySessionContext` is a `record` **class** with 12 positional members (:17-29) — one heap allocation per session whose fields are then copied into the session (the context instance itself is not retained).
- Receive path: `Start` → `ReceiveLoopAsync` → `ReceiveDatagramsAsync` — two Task-returning async methods (two boxed state machines + Task objects per session), one receive-window lease per loop (:249-303).
- Coordinator teardown (`UdpProxyCoordinator.cs`): `DisposeCoreAsync` (:219-273) copies the slot array, awaits each slot's `Completion` (OCE catch), disposes each session, then drains the coordinator scope and disposes the limiter; expiry retires per session via `TryBeginExpiry` → `RemoveSlotAsync` → session dispose (:191-204, :373-402).
- Probe infrastructure to extend: `SessionSetupDecompositionBenchmarks` (S0–S5 / C1 / C2 / F / H; `BenchmarkUdpTransport.ReceiveAsync` parks on `Task.Delay(Timeout.InfiniteTimeSpan, ct)`; the C2 fixture disposes sessions directly; F = 104.0, H = 356.8 harness subtraction).
- Contract constraints: `async-lifetime.md` D1–D11 (the scope owns the owner-lifetime CTS; drain = seal + cancel + join; D11 owner-teardown single-flight; the drain completion cell is allocated lazily at seal; the CTS is allocated once in the constructor); `udp-relay.md` §"UDP session lifetime, teardown reason, and fail-closed send drop" (2026-09-20); warm-path 0-B gates (`HotPathAllocationGateTests`, `Socks5UdpTransportSendTests.WarmSyncSendAllocatesNoManagedBytes`, `FlowTableClaimAndExpireCycleAllocatesNoManagedBytes`).
- Quality gates (per AGENTS.md): `dotnet build -c Release` zero-warning, full test suite green, `dotnet format --severity info --verify-no-changes` empty output, `jb inspectcode` zero `<Issue>`.

## Requirements

- **R1 — Teardown split probe (before any src change).** Split T into at least {session scope cancel + receive-loop join, transport dispose, coordinator slot/queue/association/cooldown machinery} using the existing stage-delta pattern; per-sub-case first-chance exception counts; ≥3 runs; both retire shapes named (wholesale coordinator dispose and per-session expiry retire).
- **R2 — C2 split probe (before any src change).** Split C2 into {context record, session object, scope/CTS incl. linked-source cost, `Start` + receive-loop state machines, receive-window rent, remainder}; per-sub-case exception counts; ≥3 runs; harness cost held constant across sub-cases or explicitly subtracted.
- **R3 — Exception attribution and removal.** Identify the source(s) of the ≈2 first-chance exceptions/session on the teardown path (code-level attribution + probe confirmation); eliminate the avoidable ones; document inherent ones with the reason and the shape they were measured in.
- **R4 — Implement the measured reductions** on both legs **without retained-memory trades** (no pools/slabs/CTS-reuse that retain capacity-sized memory; scope decision of 2026-09-22): adopt the candidates the split justifies, defer the rest with their measured sizes; behavior-zero; the D7/D11 and udp-relay contracts stay locked; warm-path 0-B gates stay green.
- **R5 — Re-measure and re-anchor.** Same instruments, ≥3 runs; before/after per component; update anchors in `hot-path.md` (T1/T2 unchanged unless the measured values move; new sub-anchors only if the split earns them) with headroom figures and falsification conditions; reconcile the 5,276/5,266.2 note above.
- **R6 — Research artifact.** Persist probe method, raw logs, and split tables under this task's `research/`, closing or narrowing the archived `noop-decomposition.md` gap list ("T internal split + exception source", "C2 internal split").

## Acceptance Criteria

- [ ] Teardown and C2 splits documented with per-component B/session (≥3 runs, spread reported) and per-sub-case exception counts; deterministic sub-cases reproduce within ≤1 B.
- [ ] Exception source attributed; avoidable sources removed (before/after exception counts reported); inherent ones documented with reasons.
- [ ] Implemented reductions measured before/after on the same instruments with quantified B/session savings; no behavior change (full test suite green; contract-locked teardown tests unchanged).
- [ ] No per-session allocation regression in any leg (Noop probe, T, C2, churn whole cycle re-verified out of process where real-dial).
- [ ] `hot-path.md` reflects the measured values (guard band + falsification condition) or carries an explicit "no anchor change" rationale.
- [ ] Quality gates all green: Release build zero-warning, full tests, `dotnet format --severity info --verify-no-changes` empty, `jb inspectcode` zero issues.
- [ ] `src/**` changes carry test coverage for the changed teardown/construct behavior.

## Out of scope

- **Retained-memory reductions** (managed pooling / slab / CTS-reuse protocols that retain capacity-sized memory) — scope decision 2026-09-22: probes first; pooling is a follow-up decision taken with the split numbers in hand.
- Control-connection reuse / pooling at the framework layer (redo rank 1 — product/protocol ADR, separate decision).
- Admission + capacity pre-seed reductions (rank 4) and the probe-shape response/retire residue split (3,342.2 B), except where the teardown probe necessarily touches retire.
- `unsafe` / `NativeMemory` slab (redo §5.1 verdict: no measurable advantage over a managed slab; revisit only if a managed variant measurably fails).
- .NET 11 runtime trial (separate lever).
- Packet hot path (0-B gates unchanged).
- Windows-side re-verification (Linux-only envelope, as recorded).

## Open Questions

- None blocking.
