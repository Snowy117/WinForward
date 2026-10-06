# TCP half-close fidelity: deliver the upstream close (and the trailing data) to a client that half-closes

## Goal

A client that finishes sending and calls `shutdown(SD_SEND)` must keep using the connection: the
target's trailing response bytes must reach it, and it must then observe the server's close instead
of hanging until its own timeout. Today 545 of 601 half-closing attempts stall — which breaks the
request-then-FIN pattern that HTTP/1.0-style clients, long-polling and streaming responses rely on.

## Background

Measured 2026-10-06 with the E2E harness on the dev VM: client process → WinForward (NDIS redirect,
native AOT) → sing-box 1.14.2 on loopback → target on the Linux host, 60 s @ 20 conn/s, four modes at
25 % each.

| mode | attempts | client half-closes | expected | observed |
|---|---|---|---|---|
| `clean` | 301 | yes | `clean` | part of `clean=56` |
| `halfClose` (3 x 256 B trailers after the FIN) | 300 | yes | `clean` | part of `timeout=550` |
| `partialFin` | 300 | no | `unexpectedEof` | `unexpectedEof` — 300 ✓ |
| `resetAfterN` | 300 | no | `reset` | 0 (separate defect, out of scope) |

Sources: `research/symptom-report.md` (symptoms and numbers), `research/repro.md` (reproduction
commands), `research/evidence-notes.md` (analysis, fault localisation, harness caveats).

Facts that bound the fix:

- **The forward path works.** The target sends its trailers only after it reads the client's FIN
  (`benchmarks/WinForward.E2E/Target/TcpTargetServer.cs:264-268`) and its ledger records
  `verdict=halfClose` with `bytesEchoed=8192`. The FIN was forwarded and the trailers left the target.
- **The relay already implements half-close** and is unit-tested green
  (`src/WinForward.Runtime/TcpRedirect/TcpProxyRelay.cs:176-179`,
  `tests/WinForward.Runtime.TcpRedirect.Tests/TcpProxyRelayTests.cs:54`).
- **The fault is on the client-facing return path** (`relay → local listener socket → reverse-leg
  rewrite → client`). Proxifier, measured in the same topology through the same sing-box hop,
  delivered every half-closing connection (`clean=46` of `expected clean=46`, `truncated=0`), so the
  requirement is achievable and the environment is not the cause.
- **`timeout` today means "the client never saw the end of the stream"**, not "no bytes arrived": the
  harness discards the bytes received when an attempt times out
  (`benchmarks/WinForward.E2E/Client/Arms/ReliabilityArm.cs:355`, `:364-365`). The fix must satisfy
  both readings, and verification must tell them apart.

## Requirements

- **R1** — After the client half-closes, every byte the target sends afterwards reaches the client.
- **R2** — After the client half-closes, the client observes the upstream close (FIN → EOF). Ending
  the connection must not depend on the client's own timeout.
- **R3** — No regression where the client does not half-close, and none in the full-duplex path
  (echo integrity, ordering, throughput).
- **R4** — The relay's end is observable: an operator must be able to tell a clean end from a stalled
  or faulted one from the product's own log. Today every end logs the literal `outcome=completed`
  (`src/WinForward.Runtime/TcpRedirect/TcpRedirectAcceptor.cs:235`), which is why this class of defect
  needed harness forensics to localise.
- **R5** — The packet path stays allocation-free: no new per-packet or per-chunk allocations and no
  new per-chunk delegates (`AGENTS.md` performance-first constraint).

## Acceptance Criteria

- [ ] **AC1** — Existing harness, no harness changes, `modeMix: "halfClose=100"` (30 s): after the fix
      `observed.clean == connectAttempts` and `observed.timeout == 0`. Baseline today: `clean` is a
      small minority and `timeout` dominates.
- [ ] **AC2** — `modeMix: "clean=100"`: `observed.clean == connectAttempts` and `observed.timeout == 0`.
- [ ] **AC3** — Full four-mode REL arm (60 s @ 20 conn/s): `observed.clean ≈ 601` and
      `observed.timeout ≈ 0`, with the `reset`/`unexpectedEof` columns keeping their current shape
      (they belong to symptom 1).
- [ ] **AC4** — `dotnet test WinForward.slnx -c Release` green, with a new relay-level regression test
      that fails without the fix: the client-first ordering, relay undisposed, local peer must observe
      the trailers and then EOF (`design.md` §5.1).
- [ ] **AC5** — `dotnet build WinForward.slnx -c Release` zero-warning;
      `dotnet format WinForward.slnx --severity info --verify-no-changes --no-restore` empty;
      `jb inspectcode` reports zero issues.
- [ ] **AC6** — From a real run's output, a clean relay end is distinguishable from a stalled/faulted
      one (R4).

## Delivered (2026-10-06)

The mechanism was measured rather than assumed, and it was not the one this PRD's background
sketched: the relay *does* emit a client-facing FIN (its `NetworkStream` is disposed as `RunPumpAsync`
returns), but that instant coincides with `Completion` completing, so the FIN races the retire that
releases the reverse index — and a client that already half-closed loses that race. The single-mode
arms also cleared the trailers: `clean` (no trailers at all) fails identically to `halfClose`
(≈92 % hangs, `halfCloseViolation=0`), so the missing piece was always the close event.

Delivered in `7cababb` (fix) and `c828548` (spec), verified on the VM through the unchanged harness
(`research/verification/`): `timeout` 556 → 23 of 1201 in the four-mode REL arm (`clean` 55 → 592),
548 → 50 of 601 on `halfClose=100`, 554 → 30 on `clean=100`. Gates green: zero-warning Release build,
all test projects, `dotnet format --verify-no-changes` empty, `jb inspectcode` zero issues.

Acceptance criteria honestly: **AC4, AC5 and AC6 are met** (relay-level regression tests fail before
the fix and pass after, all gates green, the relay end kind reaches `tcp.relay.ended`), while
**AC1–AC3 are met in the direction and magnitude they care about but not to the letter** — the
residual 2–8 % is a different defect (the close is single-shot once the alias retires, so a dropped
close packet is never retransmitted). That residual, its evidence and its two candidate directions
are owned by the child task `../10-07-tcp-close-drain`; AC1–AC3's strict form (`timeout == 0`) moves
there.

## Out of Scope

- **Symptom 1 (upstream RST fidelity).** The RST is erased by the SOCKS5 hop before WinForward can
  observe it (`research/evidence-notes.md` §5). It needs its own decision — accept the loss through a
  SOCKS5 CONNECT upstream, or change that hop — and its own task.
- **Reworking the E2E harness.** It is a work in progress owned by the user; this task may read it and
  run it, but not restructure it — which is why AC1–AC3 are expressed in its existing metrics. Its
  gaps are recorded in `research/evidence-notes.md` §6 for that rework.
- **Performance work.** No throughput or latency tuning beyond "no regression".

## Constraints

- **Verification is end-to-end on the dev VM**, driven from this host over WinRM: reproduce first,
  then fix, then re-run the acceptance arms (`implement.md` Phase A/D).
- **`C:\wfbench` is read-only for this task.** Binaries may be executed from it; nothing is written
  into it. All VM artifacts live in a sandbox directory created for this task, deleted after the
  results are copied back (`implement.md` Phase E).
