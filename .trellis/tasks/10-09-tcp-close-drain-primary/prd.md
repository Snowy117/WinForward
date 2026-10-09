# Close drain as the primary fix: retire on the client's acknowledgement, drop the crafted FIN

## Goal

A client that half-closes must observe the upstream close (and the trailing data) **every** time,
not 92–98 % of the time. The parent task's fix (`7cababb`) works around the real defect — the
session dying with relay completion — by hand-crafting a FIN|ACK before the retire, and a
single-shot crafted close can never retransmit. This task makes the bounded close drain the primary
fix: on a clean relay end, dispose the relay first so MSTCP's graceful close emits the real,
retransmittable FIN while the alias is still alive, retire only once the client's acknowledgement
covers the close (bounded deadline as the fallback), and **remove the crafted FIN|ACK path** the
drain supersedes.

User value: the request-then-FIN pattern (HTTP/1.0-style clients, long-polling, streaming
responses) works through the redirect with the same reliability as a direct connection, and the
close path is carried by the operating system's TCP — with its retransmission timers — instead of
by a guessed, single-shot packet.

## Background

Parent task: `../archive/2026-10/10-06-tcp-half-close-fidelity` (archived 2026-10-06); its
`research/verification/` holds the full evidence. Sibling draft: `../10-07-tcp-close-drain`
(planning), which chose the same drain direction but keeps the crafted FIN and defers its removal
to a follow-up; **this task supersedes that draft** with the sharpened design agreed 2026-10-09:
the drain is the primary fix, and the crafted FIN is removed inside this change; the A/B is
measured as two builds from frozen snapshots of the same revision (as-landed vs drain-only), so
no switch ever enters the code.

Confirmed facts (all measured or code-confirmed; anchors in the parent's research):

- Baseline: 545 of 601 half-closing attempts hang with no FIN, no RST, no truncation
  (`timeout=556` of 1201 in the four-mode arm). After `7cababb`: `timeout` 23 of 1201,
  `clean` 592; single-mode `halfClose=100` 50 hangs of 601, `clean=100` 30.
- The residual is not a sequencing bug (sequencing the crafted FIN at exactly the client's receive
  sequence did not move the residual: 45 → 50) and not an injection bug (injected exactly once per
  connection, 601/601, no injection-path warning).
- The root property is code-confirmed: after the retire
  (`TcpRedirectSessionStore.RetireSessionBodyUnderGate`, `TcpRedirectSessionStore.cs:262`), a
  reverse-leg straggler (MSTCP's retransmitted FIN or tail segment) resolves to a tombstone hit and
  is consumed as `Dropped`, and so does the client's ACK — the one participant that retransmits
  (the operating system's TCP) is disconnected from the client at exactly the moment the close is
  needed.
- The crafted FIN is sequenced from bytes *accepted by MSTCP's send buffer*, not bytes on the
  wire, so it can overtake tail data still queued in the local stack — the one mechanism that can
  put an out-of-order FIN in front of the client.
- `closesocket` with the default linger is graceful: the TCB survives the handle and retransmits
  queued data and the FIN on its RTO timer. This is the enabling assumption the drain rests on, and
  AC1 falsifies it if wrong (`design.md` §3, §8).
- The data path never gates on `RelayPhase` — only the store, the sweep and attach do
  (`TcpRedirectSessionStore.cs:152`, `:314`) — so a `Draining` phase needs no packet-path
  changes: stragglers resolve to the still-registered association exactly as during relaying.
- Serial-arithmetic sequence comparison already exists
  (`TcpRedirectAssociation.IsSequenceAhead`, `TcpRedirectTable.cs:181`), and the advance-only
  observer shape (`ObserveClientSequence`, `TcpRedirectTable.cs:158`) is the template for the
  ACK tracker.

The design rule this task applies (agreed with the user 2026-10-09): **craft a packet only for a
semantic the real stack cannot produce.** An abnormal end needs an RST and a graceful
`closesocket` emits a FIN — so the crafted RST|ACK stays. A clean end needs an in-order,
retransmittable FIN — which is exactly what the real stack already does — so the crafted FIN goes.

## Requirements

- **R1** — On a clean relay end the session is not retired when the relay completes. The relay is
  disposed first (its graceful close emits the real FIN and releases the upstream no later than
  today), then the retire waits until the client's acknowledgement covers the close, with a bounded
  deadline as the fallback.
- **R2** — The crafted clean-end FIN|ACK injection is removed:
  `ClientResetInjector.TryInjectClientCloseAsync`, `TcpResetBuilder.TryBuildFin`, their tests,
  and the clean-end branch of the acceptor's injection. The abnormal-end crafted RST|ACK is
  unchanged. The A/B is realized outside the code: two builds from frozen snapshots of the same
  revision — the as-landed tree and a drain-only variant that removes only this injection — so the
  committed code never carries a switch.
- **R3** — The drain target is computed, not observed: `serverISN + 2 + delivered` (SYN-ACK and
  FIN each consume one sequence number), from the trackers the association already holds and the
  relay's `ITcpRelayEndInfo.ServerStreamBytes` (kept from `7cababb`; verified exact by the
  parent's iteration 2).
- **R4** — The client's acknowledgement is tracked on **every** forward-leg packet (advance-only,
  serial arithmetic), not only while draining: the ACK of our FIN can be piggybacked on the
  client's own FIN before the drain is armed, and the drain must never miss a wakeup that already
  passed. Arming checks the tracker first and exits immediately when it already covers the target.
- **R5** — The drain is bounded: an internal default deadline in the seconds range that covers at
  least one MSTCP RTO from an unacknowledged stream — AC0 measured whole-response losses, where the
  client never ACKed any data, so a sub-RTO deadline would fail by construction (candidate: ~5 s,
  confirmed or adjusted by AC5's duration distribution). It is injectable for tests the way the
  store's tombstone grace is, with no new configuration surface. Every other
  teardown path (injection failure, fragment teardown, capacity, shutdown, sweep) ends the drain
  immediately — the drain's wait is bound to `session.Token`, and the retire keeps its single
  atomic store-gate critical section unchanged.
- **R6** — No new per-packet allocation or delegate on the packet path: the forward leg pays one
  4-byte read plus a signed compare per packet; the per-clean-end cost is one completion source
  (created with `TaskCreationOptions.RunContinuationsAsynchronously` — the retire must never run
  as a continuation on the capture pump thread) and one deadline timer.
- **R7** — Observability: one drain event carrying the association, the exit reason
  (`acknowledged` / `deadline` / `retired`) and the elapsed drain time — AC4's cost
  measurement without a new metric system. The `tcp.relay.ended` end-kind outcome from
  `7cababb` stays.
- **R8** — Degradation is identical to today's injection degradation: when either initial sequence
  number or the delivered byte count was never observed, there is no drain and the session retires
  immediately (today's behaviour, race included) — strictly no worse than the crafted FIN, which
  needs the same sequences.

## Acceptance Criteria

- [x] **AC0 — met 2026-10-09.** The `halfClose=100` arms' own `type: "attempt"` records show
      a mix dominated by a lost close: 92 of 105 timed-out attempts (87.6 %) had every byte
      (8192 + 768) and no `eof`; 13 were gap-shaped (3 tail gaps, 10 whole-response stalls) — in
      both readings the one participant that retransmits was disconnected by the retire. Evidence:
      `research/ac0-residual-classification.md` and `research/verification/ac0/`.
- [x] **AC1 — met 2026-10-09.** drain-only `halfClose=100`: clean 601/601, timeout 0, and all 601
      drains exited `acknowledged` (max 1 ms). The arm also settles the §3 enabling assumption in
      the drain's favour: the alias was held to the acknowledgement and MSTCP's own close arrived,
      so the TCB survived the handle close. Evidence: `research/drain-verification.md`. This arm also falsifies
      the §3 enabling assumption: if MSTCP tore the TCB down at the handle close, the residual
      would stay and the fallback (hold the socket handle in the drain) applies.
- [x] **AC2 — met 2026-10-09.** drain-only `clean=100`: clean 601/601, timeout 0,
      `truncated = 0`.
- [x] **AC3 — met 2026-10-09.** drain-only four-mode REL (60 s @ 20 conn/s): timeout 0,
      `unexpectedEof = 590` (reference 586), `reset = 0`, `clean = 607` (reference 592). The four
      residual `otherError` (0.33 %, all `resetAfterN`) are the out-of-scope upstream-RST fidelity
      issue: the target's zero-linger abort truncates the echo and the sing-box hop washes the RST
      into a FIN, so the client sees EOF mid-frame; the product's relay ended clean and its drain
      was acknowledged.
- [x] **AC4 — met 2026-10-09; removal approved.** Adjacent arms, same plan (`planHash
      0f215d26f7c0b8b6`): as-landed 601/601 clean / timeout 0 with 601 crafted injections, drain-only
      601/601 clean / timeout 0 with none; drain exits identical (0–2 ms). The crafted FIN had no
      observable effect and carried the overtake hazard, so the removal proceeds.
- [x] **AC5 — met 2026-10-09.** Across the four arms: 3004 drains, 100 % `acknowledged`, zero
      `deadline`, zero `retired`; ElapsedMs p50 0 / p99 0 / max 2 ms; peak concurrent drains 1.
      No extra port or socket is held — the drain extends an existing session slot and its table
      claims (six aggregate milliseconds over the four arms; worst case 5 s × 20 conn/s = 100
      concurrent ≈ 0.6 % of the 16,384 capacity). **Known limitation:** with no loss on this
      topology the deadline exit was never exercised on the wire; its 5 s boundary rests on the
      unit tests and on AC0's whole-response losses, not on a measured RTO-scale distribution.
- [ ] **AC6** — Regression tests, each named in `design.md` §9: drain ends on the observed
      acknowledgement; ends at the deadline; sequences never observed → no drain; the retire never
      precedes the drain's exit; a straggler during the drain resolves to the same association; the
      acknowledgement piggybacked on the client's own FIN; a clean end injects no crafted packet
      (the removal, failing while `7cababb`'s path exists). All gates green:
      `dotnet build -c Release` zero-warning, `dotnet test -c Release`,
      `dotnet format --verify-no-changes` empty, `jb inspectcode` zero issues.

## Out of Scope

- The upstream-RST fidelity question (the sing-box hop erases it; see
  `../10-06-e2e-competitor-benchmark/research/singbox-rst-to-fin.md`).
- The abnormal-end RST path: the crafted RST is single-shot in exactly the same way, but RST is
  unmeasured here (the hop erases upstream RSTs) and semantically expects no ACK; applying the same
  drain shape to it is a separate decision with its own evidence.
- The disposition of `../10-07-tcp-close-drain`: this task supersedes its design; whether its
  draft directory is archived or kept is the user's call, not this task's.
- Reworking the E2E harness (owned by the user; used unchanged here).
- Performance work beyond "no regression" (R6 is a constraint, not a tuning goal).

## Constraints

- `C:\wfbench` stays read-only on the VM; artifacts live in a sandbox directory that is deleted
  after the results are copied back. The parent task's `implement.md` documents the recipe,
  including the current-tree `appsettings.json` requirement, the orchestrator/watchdog hazards,
  and the `192.168.100.0/24` traffic path; `benchmarks/WinForward.E2E/AGENTS.local.md` governs
  the Windows side.
- Nothing outside `src/WinForward.Runtime/TcpRedirect/`, `src/WinForward.Protocols/` and the
  matching test project is edited, except the spec entries the change updates
  (`.trellis/spec/backend/tcp-client-close-injection.md`, `tcp-local-redirect.md`).
- The pre-commit quality gates in `AGENTS.md` apply in full.

## Delivered (2026-10-09)

The drain landed as the primary fix and the crafted clean-end FIN was removed in the same change;
the abnormal-end crafted RST|ACK is untouched. Evidence: `research/drain-verification.md`,
`research/ac0-residual-classification.md`, `research/removal-notes.md`,
`research/check-fixes.md`, `research/spec-sync.md` and `research/verification/`.

| arm | shape | result |
|---|---|---|
| AC0 baseline (`0aa6919`, functionally `7cababb`) | `halfClose=100` ×2 | 105 of 1202 timed out: 92 with every byte received and no `eof`, 13 gap-shaped |
| AC1 drain-only | `halfClose=100` | clean 601/601, timeout 0; 601 drains, all `acknowledged` |
| AC2 drain-only | `clean=100` | clean 601/601, timeout 0 |
| AC3 drain-only | four-mode REL | timeout 0, `unexpectedEof` 590, `reset` 0 (the four `otherError` are the out-of-scope upstream-RST fidelity issue) |
| AC4 A/B | as-landed vs drain-only | identical outcomes (601/601, timeout 0 both) → the crafted FIN was redundant and was removed |
| AC5 cost | all arms | 3004 drains, 100 % `acknowledged`, p50 0 / p99 0 / max 2 ms, peak concurrency 1, no extra port |
| final certification | the committed revision, `halfClose=100` | clean 601/601, timeout 0, 601 drains `acknowledged`, the removed events absent |

Deliberate deviations from the plan, recorded rather than smoothed over:

- **The A/B is two builds from frozen snapshots of the same revision, not an in-code switch.** A
  switch would have had to be deleted again before the commit; the snapshot variant leaves no trace
  and the certification arm then ran on the exact final tree.
- **One commit instead of two.** The drain and the removal edit the same files, and Phase D/AC4 had
  already certified the FIN-free shape directly, so a two-commit split would have required
  reconstructing an intermediate revision that was never verified. Reverting the commit restores
  `7cababb`'s behaviour.
- **The degradation coverage is a strengthening, not a new fact**:
  `CleanEndWithoutObservedSequencesRetiresImmediatelyWithoutADrain` gained the frame-absence and
  disposal-count assertions instead of a near-duplicate fact.
- **`design.md` §3's original causality was wrong and is corrected there**: the real FIN is emitted
  by the client-facing socket close inside `RunPumpAsync`, before `Completion` completes; disposing
  the relay before arming is about releasing the upstream budget, not about emitting the FIN.
- **The A/B arms predate the F2/F4/F5/F6 hardening** (allocation-gate coverage, comment precision,
  span oracle, single-flight target publication). Those changes are behaviour-neutral on the drain
  path, and the final certification arm ran on the post-removal, post-hardening revision.

Known limitations, stated so they are not mistaken for coverage:

- **The deadline exit has no wire evidence.** This topology loses no packets: every drain on every
  arm ended in 0–2 ms with the acknowledgement, so the 5 s boundary (and the RTO-scale wait behind
  it) rests on the unit tests and on AC0's whole-response losses, not on a measured distribution.
- **The AC4 arms were built from the pre-hardening snapshots** (see above); only the final
  certification arm ran on the exact committed revision.

