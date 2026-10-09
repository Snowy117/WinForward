# Implementation notes — close drain as the primary fix

Scope of this delegation: **Phase B (B2 drain tests) + Phase C1/C2** of `implement.md`.
Phase C3 (the crafted-FIN removal, design §6/§7 point 7 and design §9 test 7) and Phase D (the VM
arms) are **not** part of this change; the crafted clean-end FIN path is deliberately retained, so
test 7 was deliberately not written.

Everything below was done in the main worktree (`/home/paff/Projects/WinForward`), uncommitted.
The AC0 evidence lands elsewhere and is referenced where it matters:
`research/ac0-residual-classification.md` (105 timed-out attempts of two `halfClose=100` arms:
92 = "every byte arrived, only the close missing", 13 = tail/response gap).

---

## 1. What landed, and why each file had to change

| File | Change | Why it must change |
|---|---|---|
| `src/WinForward.Runtime/TcpRedirect/TcpRedirectTable.cs` | `RelayPhase.Draining`; on `TcpRedirectAssociation`: `ClientAckMax`, `ObserveClientAck`, `ArmDrainAsync`, the drain fields and `IsSequenceCovered` | The association is the only object the per-packet path holds; the ACK observation and the drain wakeup must live on it to stay allocation- and gate-free (design §7 point 1). |
| `src/WinForward.Runtime/TcpRedirect/TcpSequenceObservation.cs` | `TrackClientAck(frame, layout, association)` + the private ACK-field read | The forward-leg read of the client's ACK is the drain's wakeup source; it belongs in the existing observation cluster, next to the sequence trackers (design §7 point 2). |
| `src/WinForward.Runtime/TcpRedirect/TcpProxyCoordinator.Injections.cs` | one call beside `TrackClientSequence` (line 256 → 259) | The observation must run on every forward packet, not only while draining (a piggybacked ACK arrives before the drain arms), and on the pre-rewrite bytes (design §7 point 3, R4). |
| `src/WinForward.Runtime/TcpRedirect/TcpRedirectAcceptor.cs` | clean-end branch: dispose relay → arm drain → await (ACK/deadline/session token) → teardown; internal `drainDeadline` seam + `s_defaultDrainDeadline`; `TryComputeDrainTargetAck` | The retire's *timing* is the fix; the ordering (dispose before arming, retire last) is load-bearing (design §7 points 4–5, §3). |
| `src/WinForward.Runtime/Logging/TcpRedirectLog.cs` | `tcp.redirect.drain` (Debug): association, endpoints, `Outcome`, `ElapsedMs` | R7/AC5: one event carrying the exit reason and the drain duration; no new metric system. |
| `tests/WinForward.Runtime.TcpRedirect.Tests/TcpCloseDrainTests.cs` (new) | 11 facts | The regression locks of design §9 items 1–6, the R7 `retired` reason, packet-path completion, and the Closing-phase guard. |
| `tests/WinForward.Runtime.TcpRedirect.Tests/TcpRelayEndCloseTests.cs` | `CreateAcceptor` passes a 250 ms drain deadline | The existing clean-end facts would otherwise sit on the production 5 s deadline; they pin the crafted close, not the drain. |
| `tests/WinForward.Runtime.TcpRedirect.Tests/SequenceTrackerTests.cs` | the reflection lock now pins exactly one reference field (the drain cell), null until armed | See §3, conflict 1. |
| `tests/WinForward.Integration.Tests/PacketPathWalkCountTests.cs` | same relaxation in `RedirectPacketTakesZeroSequenceGateEntries` | See §3, conflict 1. |

Kept untouched, exactly as required: `ClientResetInjector.TryInjectClientCloseAsync`,
`TcpResetBuilder.TryBuildFin`, the acceptor's clean-end injection branch, the abnormal-end RST path,
`TcpProxyRelay`, `ITcpRelayEndInfo.ServerStreamBytes`, `tcp.relay.ended`'s end-kind outcome, and the
store's atomic retire (`RetireSessionBodyUnderGate`, unchanged).

---

## 2. The pure-ACK path: re-verified end to end (the delegation asked for evidence)

Claim: every packet of a proxy-decided flow — a pure ACK included — reaches
`ReinjectExistingFlowDataAsync`, where the new `TrackClientAck` sits before any failure exit.

1. `FlowDispatcher.DispatchAsync` warm entry: a claimed proxy flow with a resolvable target returns
   `_executor.ProxyAsync(packet, target, ct)` directly (`FlowDispatcher.cs:200-211`). There is no
   flag or payload test on the way in. (With trace enabled the packet takes `DispatchSlowAsync`,
   which resolves the same flow, `FlowDispatcher.cs:237-254` → `ExecuteDecisionAsync` →
   `PacketAction.Proxy` → `ProxyAsync`; so the slow path converges on the same call.)
2. `NdisPacketActionExecutor.ProxyAsync`: skips only UDP; for TCP it awaits
   `tcpProxy.HandlePacketAsync(packet, server, ct)` unconditionally
   (`NdisPacketActionExecutor.cs:363-375`). No pure-ACK early exit exists.
3. `TcpProxyCoordinator.HandlePacketAsync`: a pure ACK is not a SYN
   (`TcpFrameRewriter.IsTcpSyn(layout)`, whose predicate is SYN set ∧ ACK clear,
   `TcpFrameRewriter.cs:102-107`), so it falls to `Table.TryResolveByOriginal` (line 441) and then
   to `ReinjectExistingFlowDataAsync` (line 443). The listener-port prefilter in front of the
   reverse probe (line 423) is a *candidate* filter: a miss falls through, and a hit on a pure ACK
   only costs one reverse probe that cannot match the reverse tuple (the reverse destination is the
   original client port, the ACK's source is the client port, but the reverse *source* is a listener
   port — a forward ACK's source port is the client's, so it can only collide numerically; the full
   tuple check then misses and the original probe still runs).
4. `ReinjectExistingFlowDataAsync`: the tracking pair is the first thing after `StageFrame`, before
   the rewrite and before every failure return (`TcpProxyCoordinator.Injections.cs:253-261`), so a
   frame that later fails the rewrite has still been observed.

Corollary used by test 5: the data path never tests `RelayPhase`, so a `Draining` association is
resolved exactly like a `Relaying` one (`TcpFrameRewriter.TryRewriteForwardLeg` has no phase gate;
the store, the sweep and attach are the only `Phase` readers).

## 3. Deviations from design.md, and the conflicts found

1. **The association's "no reference-typed instance field" lock had to be relaxed in two places.**
   Conflict: design §7 point 1 puts the drain's completion cell on the association, while
   `warm-path-dispatch.md` ("Sequence trackers are atomic, not locked") and
   `tcp-client-close-injection.md` ("The association holds no reference-typed instance field") state
   the opposite, and two spec-named tests assert it literally:
   `SequenceTrackerTests.TcpRedirectAssociationHoldsNoLockField` and
   `PacketPathWalkCountTests.RedirectPacketTakesZeroSequenceGateEntries`
   (the integration test's structural half; the measured half is
   `TcpRedirectWarmPathGateTests`' exact gate/probe counts, still green).
   Resolution taken (the task documents are authoritative): each lock now requires **exactly one**
   reference-typed instance field, requires it to be named `_drainCompletion`, and asserts it is
   null on a fresh association / on a live relaying one — so a second reference field fails
   immediately instead of slipping through a name-based exclusion, and the "null outside a drain"
   sentence is test-backed rather than a comment. The cell is not a lock or a gate, and the property
   the lock protects is unchanged and still measured by `TcpRedirectWarmPathGateTests` (0 gate
   entries, 1 reverse probe per warm packet). The spec text itself must be updated in Phase E; it
   currently contradicts the landed design.
2. **The deadline seam is a constructor parameter, not "the way the store's tombstone grace is".**
   The store's grace is a `private static readonly TimeSpan` with no seam (tests advance a
   `TimeProvider` and read the tombstone window). A drain deadline drives a real timer, so a fake
   clock cannot shorten it, and a mutable static seam would be process-wide test state
   (test-stability §2.7/2.8). Chosen instead: optional `TimeSpan? drainDeadline` on the **internal**
   `TcpRedirectAcceptor`'s optional sixth parameter (the class is internal; the tests construct it
directly). An `internal init TcpRedirectOptions.DrainDeadline` was added first and then **removed**:
   nothing in the tree ever set it, jb's `UnusedAutoPropertyAccessor.Global` flagged exactly that, and
   a test seam nobody uses is dead surface. The coordinator therefore constructs the acceptor with
   the default deadline, and `TcpRedirectOptions.cs`/`TcpProxyCoordinator.cs` end this change with no
   diff at all. Default 5 s; no configuration surface.
   **The 5 s figure and its basis**: AC0 (this task, `ac0-residual-classification.md`) found 10 of
   105 timeouts where the *entire* server→client response never arrived within 1–5 ms of relay life,
   so the client had never acknowledged any data; MSTCP's first retransmission is therefore its
   initial RTO (Windows default 3 s, minimum 300 ms), and the acknowledgement that ends the drain
   cannot exist before it. A sub-second deadline (e.g. 500 ms) would retire the alias before that
   first retransmission and fail deterministically; 5 s covers the default initial RTO with headroom
   for one re-armed backoff. A retransmission *lost* needs more than one RTO (five by default), and
   that tail is what AC5's drain-duration distribution sizes in Phase D; the value stays a constant,
   not a configuration key.
3. **`TrackClientAck` also requires the ACK control bit**, which design §5's "reads the ACK field
   (transport offset +8)" does not mention. Reason: the acknowledgement field is undefined without
   ACK — a SYN's is 0 — and a tracked 0 in serial space lies *ahead* of any target in the upper half
   of the sequence space, so the very first retransmitted SYN could end a drain before the close was
   acknowledged. The unit fact pins it (a SYN never moves the tracker).
4. **`ArmDrainAsync` (not `ArmDrain`)**: VSTHRD200 is fatal in `src/**` for a `Task`-returning
   method whose name lacks the suffix. It arms synchronously and returns the completion cell; the
   name is analyzer-driven. Its cell comes from `LazyInitializer.EnsureInitialized` (the
   `QuiescenceScope` precedent — the format gate's MA0173 asked for exactly that), the claim is a
   `CompareExchange` on `_draining`, and the target is published before the flag because an
   unpublished target reads as `-1` (all-ones) in the comparison.
5. **Factual refinement of the §3 ordering rationale.** The real FIN is emitted when
   `TcpProxyRelay.RunPumpAsync` returns and its `await using var localStream = new
   NetworkStream(_localSocket, ownsSocket: true)` (`TcpProxyRelay.cs:152`) disposes — i.e. *before*
   `Completion` completes. So "arm before dispose" would not literally wait for a FIN that does not
   exist yet; it would arm after the FIN but before the upstream release. The acceptor still disposes
   the relay before arming, which is what the design wants for the upstream budget (released at drain
   start, not at its end) and is safe because `TcpProxyRelay.DisposeAsync` is single-flight.
6. **`ServerStreamBytes < 0` is treated as unknown** (no drain). Defensive only: the counter starts
   at 0 and only increments; a future relay implementation is the reason the guard exists.
7. **Test file split.** The drain facts live in a new `TcpCloseDrainTests.cs` (298 effective lines)
   rather than in `TcpRelayEndCloseTests.cs` (now 301), because the combined file would have passed
   the 400-effective-line ceiling.
8. **`TcpRedirectTable.cs` is now 369 effective lines** (was 325; ceiling 400). It stays whole: 369
   is at the same level the repo previously accepted (367 in the `ConfigurationModels` precedent) and
   the change is the design's named landing point. The split axis when it next grows is
   `RelayPhase` + `TcpRedirectAssociation` → `TcpRedirectAssociation.cs`.

## 4. Ordering, lifetime and the other retire paths

- The drain's wait is `completion.WaitAsync(_drainDeadline, token)`, where `token` is the
  `RunAcceptLoopAsync` local (never `session.Token` re-read: the scope's `Token` throws once its
  lifetime CTS is released, and the accept loop is that CTS's owner). Every other teardown path
  funnels through `TcpRedirectSession.Retire()`, which cancels the scope; the wait then exits with
  reason `retired`, and the following `tearDownSession` is the no-op it already is for an
  already-retired session. The retire itself stays one store-gate critical section; the drain only
  moves when it runs.
- The arm/wakeup handshake is two-sided and mirrors `QuiescenceScope`'s documented seal-vs-decrement
  rule: the observing thread `Interlocked.CompareExchange`s the maximum (a full fence) before reading
  `_draining`; the arming thread publishes the target and the cell, claims `_draining` with a
  `CompareExchange`, `Interlocked.MemoryBarrier()`s, then re-checks the maximum. Exactly one side
  therefore observes the other. The cell is created with
  `TaskCreationOptions.RunContinuationsAsynchronously`, so the retire continuation can never run on
  the capture pump thread.
- **The arm never rewrites a `Closing` phase.** Another retire path can win the race between the
  acceptor's target computation and the arm; `ArmDrainAsync` writes `Draining` only when the phase is
  not already `Closing`, and `ArmingADrainNeverRewritesAClosingPhase` pins it. The drain's own
  completion is gated by the `_draining` flag, not by the phase, so the cosmetic race has no
  behavioural effect — the wait still exits immediately through the cancelled session token.
- **Double dispose is safe and the closed record is not duplicated.** The acceptor disposes the relay
  first; the store's `ReleaseRetiredAsync` later disposes the same instance, which joins the
  single-flight claim and only observes `Completion` (`TcpProxyRelay.cs:338-368`;
  `TcpProxyRelayTests.ConcurrentDisposalRunsTheOwnerTeardownOnce` is green).
  `tcp.redirect.closed` is written once per retire in `ReleaseRetiredAsync`
  (`TcpRedirectSessionStore.cs:280`), and `TheRetireNeverPrecedesTheDrainExit` asserts exactly one
  occurrence.
- No new lifetime owner: the completion cell and the deadline timer are per-clean-end objects owned by
  the acceptor's existing await, and the session/store scopes are untouched.

## 5. Tests: red → green

Baseline before any change (`dotnet test WinForward.slnx -c Release`, built tree): **1663 passed, 0
failed** across 14 assemblies (TcpRedirect project: 157). Final: **1674 passed, 0 failed**
(TcpRedirect project: 168; +11 = the new drain facts).

New facts in `TcpCloseDrainTests` (all 11 green):

| # | Fact | Pins |
|---|---|---|
| 1 | `CleanEndDrainsUntilTheClientAcknowledgesThenRetires` | design §9-1: order is inject → dispose → (wait) → teardown; `Outcome=acknowledged`; the retire has not run while the drain is armed. |
| 2 | `CleanEndWithoutAnAcknowledgementRetiresAtTheDeadline` | §9-2: a 150 ms deadline exits with `Outcome=deadline` and the same order. |
| 3 | `CleanEndWithoutObservedSequencesRetiresImmediatelyWithoutADrain` | §9-3: no template/ISN → no early dispose, no drain event, phase never `Draining`; the 30 s deadline proves immediacy structurally. |
| 4 | `CleanEndWithoutEndInfoRetiresImmediatelyWithoutADrain` | R8's third input: a relay without `ITcpRelayEndInfo` still gets the crafted close but no drain. |
| 5 | `TheRetireNeverPrecedesTheDrainExit` | §9-4, against a real `TcpRedirectSessionStore`: the `tcp.redirect.drain` record precedes `tcp.redirect.closed`, and `closed` occurs once. |
| 6 | `AStragglerDuringTheDrainResolvesToTheSameAssociationAndArmsNoSetup` | §9-5: a forward straggler and a reverse straggler both resolve to the draining association; one listener, one table claim, one session, no tombstone. |
| 7 | `APiggybackedAcknowledgementEndsTheDrainWithoutWaiting` | §9-6: the ACK observed before the relay ends makes the arm exit immediately (`acknowledged` while a 30 s deadline is armed). |
| 8 | `AnotherRetirePathEndsTheDrainImmediately` | the R7 `retired` reason and the hard constraint that every other retire path ends the drain at once (via `session.Token`). |
| 9 | `ClientAckTrackingIsAdvanceOnlyWrapsAndIgnoresFramesWithoutTheAckFlag` | the tracker's semantics: advance-only, serial (not unsigned) comparison across the 32-bit boundary, and the ACK-bit gate. |
| 10 | `AForwardAckThatCoversTheCloseCompletesTheArmedDrain` | design §9-1 on the real packet path: the dispatcher resolves the flow, the coordinator reads the ACK field of the pre-rewrite frame, and the armed drain completes (table claim and listener unchanged). |
| 11 | `ArmingADrainNeverRewritesAClosingPhase` | the phase-consistency guard: an association another teardown already moved to `Closing` stays `Closing` when a drain arms. |

Adapted existing facts: `SequenceTrackerTests.TcpRedirectAssociationHoldsNoLockField` (see §3.1) and
`PacketPathWalkCountTests.RedirectPacketTakesZeroSequenceGateEntries` (same relaxation; its measured
half — both trackers advanced — is unchanged). `TcpRelayEndCloseTests` keeps all 8 of its facts,
now with an injected 250 ms drain deadline; its clean-end order assertions (`inject → teardown`)
still hold because the drain sits between "inject" and "teardown" without adding steps to that
harness's order list.

**Red evidence (before the implementation).** The test project failed to compile against the
unmodified tree, for the right reason — the drain does not exist:

```
TcpCloseDrainTests.cs: error CS0117: 'RelayPhase' does not contain a definition for 'Draining'
TcpCloseDrainTests.cs: error CS1061: 'TcpRedirectAssociation' does not contain 'ArmDrain' / 'ClientAckMax'
TcpCloseDrainTests.cs: error CS0117: 'TcpSequenceObservation' does not contain 'TrackClientAck'
TcpCloseDrainTests.cs / TcpRelayEndCloseTests.cs: error CS1739: 'TcpRedirectAcceptor' has no parameter 'drainDeadline'
TEST_EXIT=1
```

Two behavioral failures were observed during the implementation, both real bugs of mine and both
fixed in the test/harness rather than papered over: the serial-space expectation of the wrap fact
(`0xFFFF_FF00` is *behind* 100, not ahead of it) and the ordering fact pre-attaching the relay to
the store, which made the acceptor's own attach fail and take the discarded-attach path.

**Falsifiability run (the tests are not vacuous).** With the drain block temporarily narrowed from
`endKind == CleanEnded` to `endKind == Stalled` (one token, then restored), the four acceptor-level
facts fail with the right reasons and the five others stay green:

```
CleanEndDrainsUntilTheClientAcknowledgesThenRetires        FAIL (WaitForAsync: never reached Draining)
CleanEndWithoutAnAcknowledgementRetiresAtTheDeadline       FAIL (order differs: no dispose/no drain)
TheRetireNeverPrecedesTheDrainExit                         FAIL (WaitForAsync: never reached Draining)
APiggybackedAcknowledgementEndsTheDrainWithoutWaiting      FAIL (order differs)
Failed: 4, Passed: 4 (of TcpCloseDrainTests at that moment)
```

## 6. The four gates (Release)

| Gate | Command | Result |
|---|---|---|
| Build | `dotnet build WinForward.slnx -c Release` | exit 0, **0 warnings / 0 errors** |
| Tests | `dotnet test WinForward.slnx -c Release -m:1 --no-build` | exit 0, **1674 passed / 0 failed** across all 14 assemblies. The first attempt ran the suite in parallel while lingering MSBuild nodes held ~1.2 GB and produced `OutOfMemoryException` at `Thread.StartCore` in four *unrelated* assemblies (an OOM inside `Thread.StartCore` reading as a product failure is exactly the documented host condition); the serial rerun on the same binaries was clean, so that red run is recorded as unproven, not as a finding |
| Format | `dotnet format WinForward.slnx --severity info --verify-no-changes --no-restore` | exit 0, **0 bytes** of output. Findings fixed on the way, none suppressed: MA0076 (two ints interpolated into an assertion message → two static messages), MA0003 + MA0173 (the arm's `CompareExchange` claim → `LazyInitializer.EnsureInitialized` + a named-free claim), xUnit2031 (`Assert.Single(fields.Where(...))` → the predicate overload) |
| Inspector (first run, 3 findings, all fixed) | same | `ConvertIfStatementToReturnStatement` (deadline guard → documented site suppression), `UnusedAutoPropertyAccessor.Global` (`TcpRedirectOptions.DrainDeadline` → property deleted), `UnusedMember.Local` (`StepInjector.Injections` → deleted) |
| Inspector | `jb inspectcode -f=Xml -e=HINT -o=/tmp/jb-inspectcode-10-09.xml WinForward.slnx` | `JB_EXIT=0` (not trusted); the XML at `/tmp/jb-inspectcode-10-09.xml` parses to **0 `<Issue>` entries** |

Suppressions in the final tree: exactly one, site-level and documented —
`// ReSharper disable once ConvertIfStatementToReturnStatement` on the deadline guard in
`TcpRedirectAcceptor.ResolveDrainDeadline`. The inspection asks for a ternary-throw expression; the
repo's `FrameBuilders.LayoutOf` precedent keeps the guard-clause form, which reads failure-first and
keeps the constructor parameter's name in `ParamName` (a `cond ? throw … : value` also has no
precedent here). No `#pragma`, no `.editorconfig` entry was added.

## 7. Inspector result

Final run (2026-10-09, toolset 2026.1.3, `Debug` configuration, `-e=HINT`):

```
Inspection report was written to /tmp/jb-inspectcode-10-09.xml
JB_EXIT=0
ISSUE_COUNT= 0
```

Parsed with `ElementTree` over `//Issue` (the exit code is not trusted). The first run on this tree
had three findings, each resolved by fixing the code, not by suppressing:

| Finding | Site | Resolution |
|---|---|---|
| `ConvertIfStatementToReturnStatement` | `TcpRedirectAcceptor.ResolveDrainDeadline` | The only suppression in this change, site-level and documented (see §6). |
| `UnusedAutoPropertyAccessor.Global` | `TcpRedirectOptions.DrainDeadline.init` | Property deleted: nothing set it (see §3.2). |
| `UnusedMember.Local` | `TcpCloseDrainTests.StepInjector.Injections` | Property and its counter deleted. |

A `CSharpErrors` finding did not appear in either run, so no cache was cleared.

## 8. Unresolved / follow-ups

- **Phase C3 (the removal) is not done**, by scope: the crafted clean-end FIN|ACK, its injector
  flavor and `TcpResetBuilder.TryBuildFin` are still in the tree, and design §9 test 7 was not
  written. When the removal lands, this drain's clean-end branch is what remains of the clean end.
- **Phase D / AC1–AC5 are not measured here** (no VM in this scope). What the drain event must be
  read for: p50/p99/max `ElapsedMs`, the `acknowledged`/`deadline`/`retired` split (a
  `deadline` cluster means the acknowledgement was never observed — a fragmented forward leg, a
  capture gap, or an initial RTO longer than the deadline), and the peak concurrent draining sessions
  against the budget. `ElapsedMs` measures the **wait** (arm to exit), not the relay disposal that
  precedes it. AC0's classification (92/105 lost-close, 13/105 gap, 10 of them whole-response)
  predicts mostly short drains (one RTT) plus an RTO-shaped tail at ~3 s; a sub-second p99 would
  contradict AC0 and mean the acknowledgement came from something other than a real retransmission,
  which is what AC5 is there to catch.
- **The deadline is a constant to revisit only with AC5 evidence** (`TcpRedirectAcceptor`,
  `s_defaultDrainDeadline`): no seam has to migrate — the two test seams
  (`TcpRedirectAcceptor`'s parameter and `TcpRedirectOptions.DrainDeadline`) keep working if the
  default moves.
- **The drain holds the alias, which AC0 shows is the *data* path's retransmission dependency, not
  only the close's.** The 10 whole-response timeouts mean MSTCP had to retransmit the entire
  server→client stream; that only reaches the client while the reverse index still resolves to this
  association, which is exactly what `Draining` keeps true.
- **Spec documents are now stale** and must be updated in Phase E
  (`.trellis/spec/backend/tcp-client-close-injection.md`: the clean-end FIN contract → the drain
  contract; `.trellis/spec/backend/tcp-local-redirect.md`: the close row and the residual
  paragraph; `.trellis/spec/backend/warm-path-dispatch.md` and `tcp-client-close-injection.md`:
  the "no reference-typed instance field" sentence now has one named exception).
- **Accepted residual (unchanged from the design):** MSTCP owns both the TCB and its retransmission
  budget; if it aborts the TCB with an RST late in the drain, the drain still ends at its deadline.
  The drain target is computed, not observed, so an MSTCP that emits more sequence space than
  `ServerStreamBytes` (it does not, per the parent's iteration 2) would push the target behind the
  real close; the deadline is the backstop.
- **`TcpRedirectTable.cs` at 369 effective lines** — see §3.8.

---

## State at the end of the later phases (2026-10-09)

The counts above describe this phase's end state. The removal then added test 7 (12 drain facts
in `TcpCloseDrainTests`) and the check-report fixes added four facts elsewhere (allocation gate
and layout parity), so the committed tree holds 1678 passing tests. `final-gates.md` and the
PRD's "Delivered" section carry the authoritative numbers.

