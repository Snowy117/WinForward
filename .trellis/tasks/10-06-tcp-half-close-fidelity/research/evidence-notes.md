# Evidence notes — TCP half-close fidelity (symptom 2)

Collected 2026-10-06 while reviewing `research/symptom-report.md`. Every statement below is either a
measured fact (with the artifact it came from and its anchor) or explicitly labelled a hypothesis.

## 1. What the measurement proves

- The 2026-10-06 REL arm (60 s @ 20 conn/s, `clean=25,resetAfterN=25,partialFin=25,halfClose=25`)
  scheduled 1201 attempts. Expected `clean=601` (301 `clean` + 300 `halfClose`), observed
  `clean=56`; 545 of the 601 half-closing attempts did not finish.
- The forward path works. For `halfClose` the target sends its three trailer frames **only after** it
  reads the client's FIN (`benchmarks/WinForward.E2E/Target/TcpTargetServer.cs:264-268`), and its
  ledger rows carry `verdict=halfClose`, `bytesEchoed=8192`. The client's FIN was therefore forwarded
  end to end and the trailers were written to the wire by the target.
- The relay already implements half-close and is unit-tested green
  (`src/WinForward.Runtime/TcpRedirect/TcpProxyRelay.cs:176-179` — on the local FIN it shuts down only
  the upstream send side and keeps the other pump running;
  `tests/WinForward.Runtime.TcpRedirect.Tests/TcpProxyRelayTests.cs:54`).

## 2. What the measurement does NOT prove

`timeout` in the harness means "the client never observed the end of the stream", **not** "no bytes
arrived":

- `ReliabilityAttempt.Truncated` and the outcome classification are computed only after a completed
  receive phase; an attempt whose 10 s budget expires returns at `ExchangeStatus.Timeout` first
  (`benchmarks/WinForward.E2E/Client/Arms/ReliabilityArm.cs:355`) and the classification at
  `:364-365` is never reached. The bytes it did receive (`ExchangeResult.Echoed`,
  `TrailerBytes`) are discarded with it.
- So `timeout=550`, `truncated=0`, `halfCloseViolation=0` are equally consistent with "the echo and
  all 768 trailer bytes arrived, but no close ever followed".

The symptom is therefore stated in `prd.md` as **the client does not observe the upstream close**;
whether the trailers also failed is a second, independently verifiable question. AC1/AC2 cover both.

## 3. Fault localisation

The client-facing return path is `relay → local listener socket → reverse-leg rewrite → client`.
Evidence that the loss is WinForward's and not the environment's:

- Same campaign, same topology (`client process → intercept → 127.0.0.1:1080 sing-box direct →
  target`, `benchmarks/research/2026-10-06-proxifier-comparison-design.md` §Scope): Proxifier
  observed `clean=46` against `expected clean=46` with `truncated=0`
  (`/tmp/wf-bench/val/pass1/proxifier/REL.jsonl`). The sing-box hop delivers trailers and close when
  the client-side product handles them correctly.
- WinForward's own rows in that campaign hang instead (`wf-aot-native`: `clean=7`, `timeout=106`,
  `expected clean=111`). The defect is in WinForward's client-facing close path — not in the target,
  the client, or sing-box.
- Campaign caveat: those rows predate the harness statistics fix recorded in
  `research/symptom-report.md` §"需要留意的测量前提"; they are used here for the qualitative
  contrast only, never for rates.

## 4. Candidate mechanism (hypothesis — prove before fixing)

`TcpProxyRelay.RunPumpAsync` shuts down the destination of the pump that finished **first**, and only
that one (`src/WinForward.Runtime/TcpRedirect/TcpProxyRelay.cs:176-177`). In every half-closing
attempt the local→upstream pump finishes first — the trailers cannot exist until the target has seen
the client's FIN — so the relay takes the `ShutdownSend(upstream)` branch and never calls
`ShutdownSend(_localSocket)`. The branch that produces the FIN the client is waiting for is reachable
only when the upstream→local pump finishes first, i.e. in the non-half-closing case.

When the relay completes without that explicit shutdown, the client-visible FIN depends on the
implicit close inside teardown (`TcpProxyRelay.DisposeAsync` → `_localSocket.Dispose()`). That frame
must then survive the capture path and still resolve to a live association. If teardown retires or
tombstones the association before the captured FIN reaches the reverse handler, the FIN is dropped
and the client hangs — the observed 545/601.

Why the unit test cannot see it: `HalfClosePropagatesFinAndAllowsReverseResponse` uses a socket pair,
so the peer observes EOF from the socket close no matter which code path produced it, and that test
has no capture path and no association table.

**Cheapest falsification, before any product edit:** count what the relay actually did with the
client half-closing — per-direction byte counts at relay end, whether the local send shutdown ran,
and the relay end kind — and have the client report the bytes it received when its budget expires.
If the FIN is dropped by the capture path while the data arrived, this shows up immediately.

## 5. Out of scope: symptom 1 (upstream RST)

The report attributes the missing resets to the relay. The measurement chain contains an
uninstrumented third-party hop whose close semantics erase the distinction: sing-box v1.14.2 (tree at
`/tmp/wf-bench/singfix/sing-box`, `git describe` = `v1.14.2`) relays the SOCKS5 CONNECT through
`CopyConn` (`sing/common/bufio/copy.go:221`), and on any non-EOF read error it closes the
client-facing connection with a plain `Close()` (`sing/common/cond.go:353`) — no `SetLinger(0)`
anywhere on that path. A SOCKS5 CONNECT hop cannot distinguish "the peer reset" from "the peer
finished", so the reset is gone before WinForward's upstream socket sees anything. Corroborated by
Proxifier's `reset=0` in the same topology. Needs its own decision and task; deliberately excluded
here so the half-close fix is not entangled with it.

## 6. Harness gaps

Recorded for the owner's harness rework (2026-10-06: the harness is a work in progress and this task
does not restructure it):

- `design.md:112-117` promises a `connId`-joined `fidelityDiscrepancyRate` between client and target
  verdicts. The implementation compares the client's observation against the *mode expectation* only
  (`benchmarks/WinForward.E2E/Client/Arms/ReliabilityArm.cs:214-219`), and the client persists no
  per-attempt record at all. The join key exists on both sides: the client's connection id is
  `0x52450000 + index` (`ReliabilityArm.cs:86`) and the target ledger records it (e.g. `1380253697`
  = base + 1 = `resetAfterN`).
- Timed-out attempts lose `Echoed`/`TrailerBytes` (§2).
- `/tmp/wf-bench/rel-ledger.jsonl` is append-only across runs and shared by the direct baseline
  (peer `192.168.77.4`) and the proxied runs (peer `192.168.77.2`). The report quotes a snapshot
  (`clean 904 / reset 900 / partialFin 900 / halfClose 900 / clientClosedEarly 116`) that no longer
  matches the file (`906 / 900 / 900 / 900 / 162`). The 900s are 3 runs x 300, while the client
  numbers are one 1201-attempt run — the two sides are not the same connections.
- The 162 `mode=unknown` / `clientClosedEarly` ledger rows are connect-only probes from other arms
  (`benchmarks/WinForward.E2E/Client/Arms/LatencyArm.cs:223`), not relay failures.

## 7. Verification recipe with the existing harness (no harness changes)

Single-mode plans from `research/repro.md` isolate the acceptance numbers:

- `modeMix: "halfClose=100"` → after the fix `observed.clean == connectAttempts`, `timeout == 0`.
- `modeMix: "clean=100"` → after the fix `observed.clean == connectAttempts`, `timeout == 0`.
- The full four-mode REL arm is the regression gate (`clean ~ 601`, `timeout ~ 0`).

The proxied arm needs the VM (WinForward + sing-box) and the Linux target; `research/repro.md` has
the exact commands.
