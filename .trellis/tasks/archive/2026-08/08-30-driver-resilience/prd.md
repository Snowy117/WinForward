# Driver resilience: transient-error retry/backoff + offload pump-thread listener allocation

## Goal

Backlog #10 (R7 + R8, research synthesis `08-30-proxy-perf-stability-research`): make a long-running
WinForward host survive transient NDIS driver disturbances instead of exiting, and stop TCP SYN
listener allocation from head-of-line blocking the adapter capture pump.

## Background (re-validated against current source, post-batched-ioctls master)

Baseline `e5667af` research found R7/R8; both still hold after the six landed children, with line
numbers drifted:

### R7 — transient driver error → global fail-closed exit

Chain today: `NdisCapturePump.RunAsync` (`src/WinForward.NdisApi/NdisCapture.cs:75`) calls
`INdisPacketReader.TryReadPackets` synchronously; any failure throws `Win32Exception`
(`NdisApiDriver.cs:116-140`, interpretation in `NdisNativeCallStatus.InterpretBatchReadResult`).
The exception bubbles: `MultiAdapterCaptureLoop.RunPumpAsync` (`MultiAdapterCaptureLoop.cs:68-72`)
cancels sibling pumps and rethrows → runtime `RunAsync` → `Program.cs:223-226` catches → process
exits with code 3. One adapter's disturbance (removal / power transition / driver pause) kills
interception for **all** adapters. Spec contract to update: `windows-ndisapi.md` failure table
("Queue query native call fails | throw Win32Exception") and the pump-ordering notes nearby.

### R8 — TCP SYN listener allocation blocks the adapter pump

Chain today: pump awaits each packet's handler strictly in order (`NdisCapture.cs:85-92`, spec
`windows-ndisapi.md` pump batching: "packets within a batch are awaited strictly in index order")
→ `CapturePacketProcessor.ProcessAsync` → `TcpProxyCoordinator.HandleSynAsync`
(`TcpProxyCoordinator.cs:95`, holds the `_store.EnterSetup()` store-gate from atomic-retire D1) →
`TcpRedirectSetup.SetupNewRedirectAsync` (`TcpRedirectSetup.cs:55`) →
`TcpRedirectListenerFactory.CreateAsync` (`TcpRedirectListener.cs:11-34`). **`CreateAsync` is
`ValueTask` in name only**: `new Socket` + `Bind` + `Listen` all run synchronously on the pump
thread (`ValueTask.FromResult` tail). Under port exhaustion (VM-measured WSAEADDRINUSE regime,
96.65% failure pre-mitigation) or a slow bind, every later packet on that adapter waits
(head-of-line).

UDP already has the target pattern (spec `error-handling.md` §UDP-setup, task 08-28 D1 +
08-30-atomic-retire + 08-29 D6): non-async entry on the pump, per-flow bounded setup queue
(32 pkts / 32 KB, drop-oldest), background setup under a global `SemaphoreSlim(8)`, 8 MiB global
setup-memory budget with exactly-once credit, 5 s entry TTL, tombstone cooldown on genuine setup
failure. `UdpProxyCoordinator.cs:166` shows the `Task.Run` offload shape.

## Requirements

### R7: transient driver-error retry with backoff

1. Classify native read failures into transient (retryable: e.g. adapter temporarily unavailable
   around power transition / driver pause) vs permanent (adapter gone, handle invalidated,
   parameter errors). Classification must be evidence-based (ndisrd source / Win32 error codes)
   and recorded in design.md before implementation.
2. The capture pump retries transient read failures with bounded exponential backoff instead of
   propagating immediately. Retry budget and backoff curve are configuration-visible constants
   (not user-facing config) documented in design.md.
3. Retries are observable: a rate-limited warn log + counters (attempts, distinct incidents) so a
   degraded host is diagnosable.
4. When retries are exhausted (or the error is permanent), the affected pump stops
   fail-closed while the process and sibling adapters keep running (approved decision, see
   Decisions).
5. Driver-layer semantics stay throw-on-failure (retry lives at the pump/loop level) unless
   design.md argues otherwise; spec failure-table rows updated accordingly.

### R8: TCP SYN setup off the pump thread

1. `HandleSynAsync`'s new-flow setup path (listener allocation through rewritten-SYN injection)
   must not block the capture pump on socket `Bind`/`Listen`; the pump-side work per SYN becomes
   bounded, synchronous-fast (cheap checks + enqueue/claim + trigger), mirroring the UDP
   "never blocks the capture pump and never downgrades to pass" contract.
2. Preserve exactly-once flow claim semantics (existing concurrent-loser path,
   `TcpRedirectSetup.cs:96-105`) — retransmitted SYNs during a pending setup must not create a
   second listener/session.
3. The original SYN frame must still be rewritten-and-injected once setup completes (client
   retransmission is not relied upon for the common case); any hold-queue for pending SYNs is
   bounded with a documented budget + TTL, consistent with the UDP bounded-setup-memory pattern.
4. Capacity rejection (tcpFlowCapacity gate + RST|ACK, S4) and tombstone/TIME_WAIT grace behavior
   are unchanged and stay synchronous/instantaneous.
5. Store-gate (D1) holding rules must be re-audited: the setup gate must not be held across the
   background bind, and retire-vs-setup races handled explicitly.
6. Spec updates: `error-handling.md` gains the TCP-side "never blocks the pump" clause;
   `tcp-local-redirect.md` / `windows-ndisapi.md` pump-ordering notes reconcile "strict index
   order" with deferred SYN injection (reinjection-order contract scope clarified).

## Out of Scope

- #6 zero-copy data path, #8 hardening-bundle, batched-ioctls Phase 2, port-budget-windows,
  local-mux-transport, windows-real-nic (separate backlog items).
- Any change to UDP setup machinery beyond what TCP parity requires.
- Auto-recovery of an adapter that disappeared (adapter set re-enumeration / hot-add handling).
- New user-facing configuration surface (retry/backoff constants are internal).

## Acceptance Criteria

- [ ] R7: a pump-level transient read failure (fake reader injecting classified transient errors)
      is retried with backoff and the run continues; observable via new counters/tests.
- [ ] R7: permanent/exhausted failure escalates per the approved decision; escalation path has a
      test (fake reader forcing exhaustion) and a rate-limited log.
- [ ] R8: under a listener factory whose `CreateAsync` blocks arbitrarily long, the adapter pump
      continues processing other packets (test with a controllable fake factory + latency
      assertion on subsequent packets).
- [ ] R8: pending-setup SYN retransmission is absorbed by exactly-once claim (no second listener);
      original SYN injected exactly once after setup completes.
- [ ] R8: pending-SYN hold memory is bounded (budget + TTL test mirroring the UDP
      bounded-setup-memory scenario).
- [ ] Full test suite green; existing pump-ordering tests (`BatchedPassReinjectionE2eTests` etc.)
      and store-gate tests (`RetireSessionUnderGate` D1) still pass or are consciously updated
      with rationale.
- [ ] Windows-reality spot-check if feasible in VM: SYN-path p99 under churn unchanged or better;
      driver-error injection (adapter disable/enable) no longer kills the process (manual VM
      validation note in results).
- [ ] Spec files updated (`windows-ndisapi.md`, `error-handling.md`, `tcp-local-redirect.md` as
      needed); parent backlog table (#10 → done) synced at finish-work.

## Decisions

1. **R7 escalation scope when retries exhaust — single-pump degradation (approved)**: stop only
   the affected adapter's interception; the process and sibling adapters keep running, with loud
   rate-limited logging + counters. Rationale: #10's motivation is long-running-host resilience;
   an exited process protects nothing. Consequence (in scope): explicit cleanup for the degraded
   adapter — restore its adapter mode outside the global exit path — and existing sessions on that
   adapter degrade per-flow via existing injection-failure handling plus idle expiry.

## Open Questions

(none blocking)
