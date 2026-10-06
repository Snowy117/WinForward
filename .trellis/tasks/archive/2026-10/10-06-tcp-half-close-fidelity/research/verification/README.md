# Verification record — TCP half-close fidelity

All arms: E2E harness client (current tree publish, `win-x64`) on the VM (`WinLTSC`), target on
`192.168.100.4:40020`, sing-box 1.14.2 on `127.0.0.1:1080`, sandbox product instance in
`C:\wf-hcfix`, 30 s @ 20 conn/s unless stated. Raw `result` records are the sibling `.jsonl` files.

## Baseline (before any fix)

| arm | product | config | expected | observed |
|---|---|---|---|---|
| `base-aot-halfclose` | AOT `C:\wfbench\wf-aot` (the artifact behind the symptom report) | legacy `config.json` | `clean=601` | `clean=48, timeout=553, halfCloseViolation=0` |
| `base-aot-clean` | same | same | `clean=601` | `clean=47, timeout=554, halfCloseViolation=0` |
| `base-aot-rel` | same | same | `clean=601, reset=300, unexpectedEof=300` | `clean=55, reset=0, unexpectedEof=590, timeout=556, halfCloseViolation=0`, `achievedRate=17.156` |

The REL arm reproduces the published symptom numbers (report: `clean=56, reset=0,
unexpectedEof=595, timeout=550`, `achievedRate=17.15`) on the same artifact, so the sandbox is a
faithful reproduction of the reported environment.

Two findings from the single-mode arms:

- `clean` and `halfClose` fail identically (≈92 % of attempts hang). The trailers are therefore not
  the fault; the missing piece is the *close event*.
- `halfCloseViolation=0` and `reset=0` in both arms: the clients that hang receive neither a FIN nor
  an RST — they observe no end of stream at all.

## Fix iteration 1 — inject the FIN|ACK at a clean relay end

| arm | product | expected | observed |
|---|---|---|---|
| `ctrl-fdd-halfclose` | FDD, unfixed, current config format | `clean=601` | `clean=53, timeout=548, halfCloseViolation=0` |
| `fix2-halfclose` | FDD, **fixed**, current config format | `clean=601` | `clean=556, timeout=45, halfCloseViolation=0` |

Matched pair: same build flavor, same config, same sandbox; only the fix differs. 548 → 45 hangs.

Residual 45: the injected FIN's sequence was taken from the packet path's tracker, which can already
have counted the client-facing socket's own FIN (+1). A FIN one byte past the client's receive
sequence is queued forever, so the client still hangs. Confirmed by the shape: no truncation and no
reset, only timeouts.

## Fix iteration 2 — sequence the crafted FIN from the relay's delivered byte count

The relay now reports the server-to-client stream bytes it wrote to the client-facing socket
(`ITcpRelayEndInfo.ServerStreamBytes`), and the close frame's sequence is
`server-ISN + 1 + delivered` — exact, independent of capture-path timing and of the socket's own FIN.

| arm | product | expected | observed |
|---|---|---|---|
| `fix3-halfclose` | FDD, iteration-2 fix | `clean=601` | `clean=551, timeout=50, halfCloseViolation=0` |
| `fix3-clean` | same | `clean=601` | `clean=571, timeout=30, halfCloseViolation=0` |
| `fix3-rel` | same | `clean=601, unexpectedEof=300` | `clean=592, unexpectedEof=586, timeout=23, halfCloseViolation=0` |
| `dbg-halfclose` | same, Debug log level | `clean=601` | `clean=562, timeout=39, halfCloseViolation=0` |

Iteration 2 did **not** move the residual (45 → 50 on `halfClose`, within run-to-run noise). It is
kept because it removes a real hazard (a tracker that already counted the socket's own FIN yields a
FIN one byte past the client's receive sequence, which hangs exactly like the original defect), but
the measured gain came from iteration 1.

## What the residual is, and what it is not

Debug-level run of the same arm (`dbg-halfclose`, 601 attempts): five distinct per-connection events
each fired exactly 601 times (relay start/end, session close, close injection, and the SYN injection
pair), and **no warning or error** came from the injection path — no `batch-failed`, no
`deferred-overflow`, no `tcp.redirect.failed`, no relay-end close failure. The remaining warnings are
WinRM traffic (port 5985, passed through) and one pre-existing driver read-shape warning.

So the 23–50 hangs per arm are connections where the close **was** injected, once, and did not land:

- Nothing is delivered twice on that path: the crafted FIN is a single packet, and the socket's own
  FIN is emitted at relay completion, after which the session retires and the reverse alias is gone.
  A close packet dropped at the client (transient zero window, capture latency) has no retransmission.
- The same is true for the *tail of the data*: once the relay ends, a segment the client never
  received can no longer be retransmitted, and the injected FIN then sits behind a permanent gap —
  which is also a hang, with no truncation and no reset, matching the observed shape.

Both readings point at one property rather than at any remaining sequencing bug: **the relay stops
being able to retransmit at the moment the client-visible close is needed.** Two directions to close
it, both out of this task's scope as planned:

1. Keep the client-facing socket and the association alive after the upstream ends, until the
   client's acknowledgement covers the close (a bounded close drain), so the operating system's own
   TCP retransmission delivers both the tail and the FIN.
2. Repeat the crafted close a bounded number of times (needs the association's SYN template copied
   before the retire, because retiring releases the pooled template).

\* `reset=0` in every arm is expected: the upstream RST is erased by the sing-box hop (see
`.trellis/tasks/10-06-e2e-competitor-benchmark/research/singbox-rst-to-fin.md`), which is a separate,
non-WinForward issue.

