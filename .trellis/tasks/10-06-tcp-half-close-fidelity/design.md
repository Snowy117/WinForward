# Technical design — TCP half-close fidelity

## 1. Where the fix lives

Everything happens on the client-facing close path of one session:

```
client socket ──(NDIS capture + local_redirect rewrite)──> local listener socket
                                                                    │  local→upstream pump
                                                                    ▼
                                                              SOCKS5 upstream ──> sing-box ──> target
client socket <──(reverse-leg rewrite, TcpRedirectTable._byReverse)── local listener socket
                                                                    ▲  upstream→local pump
```

The relay (`src/WinForward.Runtime/TcpRedirect/TcpProxyRelay.cs`) owns both pumps and both ends of
the client-facing socket. The reverse-leg rewrite owns the delivery of every frame the local socket
emits; it resolves the association through `TcpRedirectTable._byReverse`
(`src/WinForward.Runtime/TcpRedirect/TcpRedirectTable.cs:455`).

## 2. Current behaviour, and why half-close breaks it

`TcpProxyRelay.RunPumpAsync` (`TcpProxyRelay.cs:143-195`) shuts down **one** destination, the
destination of whichever pump finished first:

```csharp
if (first == localToUpstream) ShutdownSend(upstream);   // client finished sending
else ShutdownSend(_localSocket);                        // upstream finished sending
```

The client-facing FIN is not limited to that call, though: `RunPumpAsync` opens the client side as
`await using var localStream = new NetworkStream(_localSocket, ownsSocket: true)`
(`TcpProxyRelay.cs:145`), so returning from the method **closes the socket** and emits a FIN in both
orderings. What differs between the orderings is *when* that happens relative to the session's
retire:

- **Upstream first** (`partialFin`, `resetAfterN`, any server-initiated close): the explicit
  `ShutdownSend(_localSocket)` runs inside the pump body and the method then blocks in
  `await Task.WhenAll(...)` until the *client* reacts (it has to receive EOF and close). That window
  is milliseconds wide, so the captured FIN reaches the reverse leg while the association is alive.
  Measured as the working case: `unexpectedEof=595`.
- **Client first** (`clean`, `halfClose` — the client calls `shutdown(SD_SEND)`): the trailers cannot
  exist until the target has seen that FIN, so `localToUpstream` always finishes first, the relay
  takes the `ShutdownSend(upstream)` branch, and both pumps are already done. The method returns and
  closes the socket **in the same instant that `Completion` completes**, so the client-visible FIN is
  emitted exactly when the teardown starts:

  - `TcpRedirectSessionStore.RetireSessionBodyUnderGate` (`TcpRedirectSessionStore.cs:262-275`)
    removes the session and calls `RemoveAssociationFromTable` **inside the store gate**, releasing
    every table index — `_byReverse` included — and arming the tombstone.
  - The relay's disposal (`ReleaseRetiredAsync`, `TcpRedirectSessionStore.cs:277-308`) trails that.

  The FIN segment therefore has to travel *socket close → Windows send path → capture queue →
  capture pump thread → dispatcher → reverse lookup* before the retire runs on its own continuation.
  It normally loses that race, and a frame that arrives after the retire resolves to nothing and is
  consumed as a late packet. The client waits for an end of stream that never arrives and dies on its
  own 10 s budget — `timeout=550`, no `halfCloseViolation`, no `truncated`, exactly the measured
  shape. The 56 successes out of 601 are the races that were won.

The spec already states the invariant this violates for the *abnormal* end: "every relay end that is
not a clean FIN-propagated end is surfaced to the client as an in-window RST|ACK ... injected ...
**before** `_tearDownSession`" (`.trellis/spec/backend/tcp-local-redirect.md:126`), and the acceptor
skips the reset for a clean end precisely because "a clean end already propagated FINs"
(`TcpRedirectAcceptor.cs:219-234`). In the client-first ordering that premise does not hold: the FIN
exists, but it is delivered by a race the retire usually wins.

### What is confirmed, and what still needs the VM

Confirmed by code: the ordering above, the retire-before-relay-disposal sequence, and that the
existing relay unit test `HalfClosePropagatesFinAndAllowsReverseResponse`
(`tests/WinForward.Runtime.TcpRedirect.Tests/TcpProxyRelayTests.cs:54`) cannot see any of it — it has
no capture path and no association table, and its `ReceiveWithTimeoutAsync` helper throws on timeout,
so its EOF assertion is genuine but satisfiable by the socket close alone.

Still to measure on the VM (Phase A/B): whether the client-first arms show *nothing* (pure
`timeout`), which pins "emitted but not delivered"; the alternatives would show up as
`halfCloseViolation` (a reset arrived instead of a FIN) or as truncated echoes (the return data went
missing earlier).

## 3. Fix design

**Primary — inject the client-visible close before teardown, symmetric to the existing RST.**
The acceptor already branches on the relay's end kind: `EndKind != CleanEnded` → inject an RST|ACK
via `ClientResetInjector.TryInjectClientResetAsync`, then tear down. A clean end takes no such step
because the FIN was assumed to have propagated. Make that assumption true by construction: for a
clean end, inject a crafted **FIN|ACK** toward the client *before* `_tearDownSession`, using the same
inputs the RST path already relies on:

- `association.OriginalSynTemplate` and `OriginAdapterHandle` (already used by the injector),
- `ServerNextSeq` for the FIN's sequence number — after all delivered data this is exactly the
  client's `RCV.NXT`, so a modern Windows stack accepts it (the RFC 5961 exact-match rule the RST
  path already documents at `tcp-local-redirect.md:95`),
- `ClientNextSeq` for the acknowledgement, and the existing injection lanes with the same
  `towardMstcp` direction choice.

Properties that make this safe:

- **No interleaving hazard.** If the injected FIN overtakes the last data segments still sitting in
  the local socket's send queue, the client's stack queues it as out-of-order and only reports EOF
  after the gap fills. TCP sequencing already orders it correctly.
- **The socket's own FIN becomes a harmless duplicate.** The local socket is still closed by the
  relay (it must be); its FIN|ACK carries the same sequence and is treated as a retransmission.
- **No alias resurrection.** The injected frame does not need the redirect table: it is a crafted
  packet on the wire, exactly like the RST path, so the atomic-retire invariant
  (`tcp-local-redirect.md:172`) is untouched.
- **One packet-construction path.** `TcpResetBuilder.TryBuildReset` already builds an RST|ACK from
  the SYN template plus seq/ack; the FIN flavor shares that builder (flags parameterized) rather than
  adding a second packet builder.

Rejected alternatives:

- *Grace window before the retire* (delay `tearDownSession` for clean ends so the captured FIN wins).
  A timing hack: it weakens the port budget this project treats as a first-class resource, is not
  deterministic under load, and leaves the defect in place whenever the capture path is slow.
- *Keep the reverse alias resolvable after the retire* so the late FIN is rewritten and delivered.
  This is precisely what the tombstone exists to prevent: ephemeral tuples get reused, and a
  post-retire reverse frame cannot be told apart from a new flow's traffic.
- *Reset a half-closed client instead of closing it cleanly.* Wrong semantics for a legitimate
  pattern (request-then-FIN, streaming responses): the client must observe EOF, not ECONNRESET.
- *Only add an explicit `ShutdownSend(_localSocket)` earlier in `RunPumpAsync`.* Insufficient on its
  own: it moves the FIN by microseconds inside the same completion instant, so it does not change
  which side wins the race. Kept out unless the VM measurement shows the socket close is not the
  frame that races.

**Observability (R4).** `TcpRedirectAcceptor.ObserveRelayCompletionAsync` logs every end as
`outcome=completed` (`TcpRedirectAcceptor.cs:235`). Pass the relay's `EndKind`
(`CleanEnded` / `Stalled` / `Faulted`, already exposed through `ITcpRelayEndInfo`) into that existing
structured event's `outcome` field. No new event, no schema change, and a future run of this class is
diagnosable from the product log alone.

**Performance (R5).** One extra crafted packet per relay (not per packet), no allocation on the
packet path, no new per-chunk delegate. The injection lanes already handle an extra frame per flow.

## 4. Compatibility, risk, rollback

- No configuration, protocol, or ABI change. Non-half-closing flows keep today's behaviour: their
  FIN already propagates and the injected one is a duplicate.
- Risk: an injected FIN for a client that has already fully closed its connection. Harmless — the
  client's stack answers with an RST that the tombstone consumes as a late packet.
- Risk: sequences unknown (`ServerNextSeq`/`ClientNextSeq` unobserved, or no SYN template). Degrade
  exactly like the RST path: skip the injection, the socket close remains as the only FIN source.
- Risk: double close signals to a client that is mid-read. TCP treats the injected FIN as the
  authoritative end of stream; the duplicate is dropped by sequence.
- Rollback: the change is confined to the acceptor's clean-end branch, the injector, and the packet
  builder. Reverting those restores today's behaviour; nothing else depends on them.

## 5. Verification design

Three levels, cheapest first:

1. **Acceptor/injector regression test (must fail before the fix).** Mirror `TcpRelayEndResetTests`:
   drive a relay that ends cleanly and assert that a client-visible FIN|ACK is injected *before*
   the session teardown, from the tracked sequences (`ServerNextSeq`, not ISN+1), and that a relay
   whose sequences were never observed degrades to no injection. Today nothing is injected for a
   clean end, so the test fails for the right reason. A relay-level socket-pair test cannot
   discriminate: `RunPumpAsync`'s own `await using` closes the socket in both variants, so the peer
   observes EOF either way.
2. **Single-mode E2E arms on the VM** (existing harness, no harness change):
   `modeMix: "clean=100"` and `modeMix: "halfClose=100"` — `observed.clean == connectAttempts`,
   `observed.timeout == 0`. The *baseline* run of the same arms also discriminates the mechanism:
   pure `timeout` confirms "emitted but not delivered"; `halfCloseViolation` would mean a reset
   reached the client instead, and would send the investigation back to the return data path.
3. **Full four-mode REL arm** as the regression gate (`clean ~ 601`, `timeout ~ 0`, existing
   `partialFin`/`resetAfterN` shape unchanged).

## 6. Measurement environment

Driven from this host over WinRM into the dev VM (`WinLTSC`). Hard constraints learned on
2026-10-06:

- **`C:\wfbench` is read-only for this task** (binaries may be executed from it, nothing written);
  all artifacts live in a sandbox directory that is deleted after the results are copied back.
- **Test traffic uses the `192.168.100.0/24` path**: the host firewall admits only that subnet for
  the target port range, so the target binds `192.168.100.4` (`benchmarks/WinForward.E2E/AGENTS.local.md`).
  The VM's source address on the external network is silently dropped.
- **Never run arms while the E2E orchestrator is live.** `scripts/orchestrator.ps1` stops product
  processes *by name* between rows, so a sandbox `sing-box.exe`/`WinForward.exe` both corrupts the
  campaign's rows (it intercepts the campaign's own client, which has the same process name) and gets
  killed by it. Check for the orchestrator process and a fresh `C:\wfbench\heartbeat.txt` before any
  arm.
- **The watchdog task `wfbench-watchdog` is enabled**: a stale heartbeat makes it kill product
  processes and re-enable the firewall. Keep the heartbeat fresh during a run, or run while the
  campaign is not active and re-check the firewall state before each arm.
- **Use `benchmarks/WinForward.E2E/scripts/wf.sh`** (tmux-backed) to drive the VM: piped stdin loses
  output, which is what made the first local attempts look like silent failures.
- The current tree reads **`appsettings.json`** (`WinForward` section, PascalCase keys), not the
  legacy `config.json` the campaign artifact uses; a current-tree build launched with the legacy file
  exits without intercepting, and the arm then measures a direct connection.

## 7. Measured outcome (2026-10-06)

Numbers and raw records: `research/verification/`. Baseline on the AOT artifact behind the symptom
report reproduces the published figures exactly (`clean=55, timeout=556` of 1201; report: 56/550).

- The single-mode arms confirmed the mechanism family: `clean` and `halfClose` fail identically
  (≈92 % hangs) with `halfCloseViolation=0`, so the trailers are not the fault and the missing piece
  is the close event.
- Injecting the client-visible FIN|ACK at a clean relay end removes ~92 % of the hangs
  (matched pair: 548 → 45 timeouts). Sequencing that FIN from the relay's delivered byte count did
  not improve the residual further, but removes a real hazard and is therefore kept.
- **Residual: 23–50 hangs per arm (2–8 %), all of them pure timeouts.** Debug-level evidence shows
  the close was injected exactly once per connection with no injection-path warning, so the residual
  is not a sequencing bug: it is the fact that the relay can no longer retransmit once the
  client-visible close is needed (a single crafted FIN; the socket's own FIN and any unacknowledged
  tail data die with the retired alias). Two candidate directions are recorded in the verification
  notes and are deliberately **not** part of this task: a bounded close drain that keeps the socket
  and alias alive until the client's acknowledgement covers the close, or a bounded repeat of the
  crafted close with a pre-copied SYN template.
