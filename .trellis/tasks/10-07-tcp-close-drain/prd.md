# TCP close drain: keep the client-facing socket and alias until the client acknowledges the close

## Goal

Finish what the parent task started: a client that half-closes must observe the upstream close
**every** time, not 92–98 % of the time. Today 23–50 of 601 attempts still hang to the client's own
10 s timeout even though the close frame is injected, because the relay stops being able to
retransmit at exactly the moment the client-visible close is needed.

## Background

Parent task: `../10-06-tcp-half-close-fidelity` (archived 2026-10-06). Its `research/verification/`
holds the full evidence; the short version:

- Baseline (AOT artifact behind the symptom report): 545 of 601 half-closing attempts hang, no FIN,
  no RST, no truncation (`timeout=556` of 1201 in the four-mode arm).
- After the parent's fix (every relay end injects its client-visible close before the retire):
  `timeout` 556 → 23 of 1201, `clean` 55 → 592; single-mode `halfClose` 548 → 50, `clean` 554 → 30.
- Residual mechanics, measured at Debug level: the close **is** injected exactly once per
  connection (601 of 601) with no injection-path warning. Once the relay ends and the session
  retires, neither the crafted FIN, nor the client-facing socket's own FIN, nor any unacknowledged
  tail data can be retransmitted — the reverse alias is gone and the socket is closed. A close packet
  (or a tail segment) dropped at the client therefore becomes a permanent gap: the client waits
  forever, which is exactly the observed shape (pure `timeout`, no truncation, no reset).

## Requirements

- **R1** — After the upstream ends, the client-visible close and the tail of the server→client
  stream must be deliverable reliably, i.e. retransmittable, until the client has acknowledged them
  (or a bounded deadline passes).
- **R2** — The client-visible close stays a single semantic event: a FIN for a clean end (never a
  reset), an RST|ACK for a stalled or faulted end (unchanged from the parent task).
- **R3** — No unbounded session or alias lifetime: the drain is bounded, and its cost is measured
  against the port budget this project treats as a first-class resource (every extra millisecond of
  alias lifetime is a port held).
- **R4** — No new per-packet allocation or delegate on the packet path; the drain runs per relay end,
  not per packet.

## Candidate directions

1. **Bounded close drain (preferred).** After the upstream ends, keep the client-facing socket open
   and keep the association resolvable until the client's acknowledgement covers the close (track the
   client's ACK number on the forward leg, e.g. as an advance-only maximum), then retire; a bounded
   deadline (a few hundred ms to a few seconds) is the fallback. The operating system's own TCP
   retransmission then delivers both the tail and the FIN, and the crafted FIN stays as the
   belt-and-braces path for the retired case.
2. **Bounded repeat of the crafted close.** Re-inject the crafted FIN a small number of times with a
   delay, without holding the alias. Smaller change, weaker guarantee: the retry count is a
   heuristic, and the association's pooled SYN template must be copied before the retire, so the
   injector can still build the frame.

Direction 1 is preferred because it fixes the class (any lost tail segment, not just the close) and
gives the retransmission responsibility back to TCP, at the cost of a bounded alias lifetime.

## Acceptance Criteria

- [ ] **AC1** — `modeMix: "halfClose=100"` (existing harness, 30 s @ 20 conn/s):
      `observed.clean == connectAttempts` and `observed.timeout == 0`.
- [ ] **AC2** — `modeMix: "clean=100"`: same.
- [ ] **AC3** — Four-mode REL arm (60 s @ 20 conn/s): `observed.timeout == 0` (or a documented,
      measured bound with its cause), with `unexpectedEof` and the (sing-box-erased) `reset` column
      unchanged.
- [ ] **AC4** — The drain is bounded and its cost measured: port/alias hold time and per-relay
      teardown latency reported from the same runs, with the added cost stated against the port
      budget.
- [ ] **AC5** — Regression tests: a client ACK that covers the close ends the drain immediately; a
      client that never acknowledges ends it at the deadline; an end without observed sequences still
      degrades to plain teardown. All gates green (build, tests, `dotnet format`, `jb inspectcode`).

## Out of Scope

- The upstream-RST fidelity question (the sing-box hop erases it; see
  `../10-06-e2e-competitor-benchmark/research/singbox-rst-to-fin.md`).
- Reworking the E2E harness (owned by the user; used unchanged here).

## Constraints

- `C:\wfbench` stays read-only on the VM; artifacts live in a sandbox directory that is deleted after
  the results are copied back. The parent task's `implement.md` documents the recipe, including the
  current-tree `appsettings.json` requirement and the orchestrator/watchdog hazards.
