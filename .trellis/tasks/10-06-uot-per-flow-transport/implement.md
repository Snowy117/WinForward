# Implement — UoT v2 per-flow UDP transport

Execution plan for `prd.md` + `design.md`. Requirements are R1–R9; design sections are §1–§11.

## Ground rules for every batch

- Implement only the batch's scope; do not refactor neighbouring code and do not start other agents.
- Read before writing: `prd.md`, `design.md`, this file, plus `.trellis/spec/backend/udp-relay.md`,
  `.trellis/spec/backend/async-lifetime.md`, `.trellis/spec/backend/hot-path.md`, and
  `.trellis/spec/backend/quality-guidelines.md`.
- Every batch ends with `dotnet build WinForward.slnx -c Release` (zero warnings) and its focused
  tests green. Never pipe a gate command in a way that hides its exit code.
- `ConfigureAwait(false)` on every await, `_camelCase` fields, narrow reason-carrying pragmas,
  nested `try/finally` release chains — the conventions the neighbouring SOCKS5 files already show.
- No commit from a batch: commits happen once, at the end of the task, after the full gates.

## Batch 1 — protocol, configuration, observability (R3, R6, R5-observability)

Files: `src/WinForward.Protocols/UotCodec.cs` (new), `src/WinForward.Protocols/Socks5State.cs`,
`src/WinForward.Configuration/ConfigurationModels.cs`, `src/WinForward.Configuration/ConfigurationTargets.cs`,
`src/WinForward.Runtime/UdpProxy/UdpProxyLogging.cs`, tests.

1. `UotCodec`: `MagicAddress = "sp.v2.udp-over-tcp.arpa"`, `Version = 2`, `MaximumRequestHeaderLength`,
   `RequestHeaderLength(AddressFamilyKind)`, `TryWriteRequestHeader(bool isConnect, IPAddressValue, ushort, Span<byte>, out int)`,
   `FrameHeaderSize = 2`, `TryWriteFrameHeader(ushort payloadLength, Span<byte>, out int)`.
   **Corrected in batch 6b: the request header's ATYP values are the ordinary SOCKS set** (`0x01`
   IPv4, `0x04` IPv6; the domain form is never emitted) and are shared from `Socks5Messages`
   (`AddressTypeIPv4`/`AddressTypeIPv6`); the `0x00`/`0x01`/`0x02` values belong to the
   v1/non-connect per-datagram stream format and are not carried by `UotCodec` (design §4,
   `research/r7-server-verification.md` §2). The port is `u16be`; the payload length is `u16be`. No
   existing encoder emits this framing (design §4).
2. `Socks5Messages`: add `RequestLength(string domain)` and
   `WriteRequest(Socks5Command command, string domain, ushort port, Span<byte> destination)`
   (ATYP 3, length-prefixed, same `VER/CMD/RSV` shape as the IP overload) — the magic CONNECT needs it.
3. Config: `Socks5ServerDto.UdpOverTcp` (`bool?`), `Socks5Server(..., bool UdpOverTcp = false)` as the
   last positional member (defaulted so existing construction sites compile), validator passthrough in
   `ValidateServer`. The source-gen context uses camelCase + `UnmappedMemberHandling.Disallow`, so the
   DTO member is what makes `udpOverTcp` parse at all — add a test that an unknown spelling still fails closed.
4. `UdpProxyLogging`: a `udpTransport` field (`"uot"` when the target is a SOCKS5 server with
   `UdpOverTcp`, `"native"` for any other SOCKS5 server, null otherwise) beside `targetKind`; keep
   `targetKind` as `local|socks5` (design §8).

Tests: `Socks5ProtocolTests` additions (domain request bytes/length; UoT header + frame prefix bytes,
both address families, round-trip against a hand-decoded expectation), `ConfigurationValidationTests`
(+ the local-target test file's conventions) for parse/absent/unknown-spelling, and a logging-field
assertion in the cheapest existing host of `LogDebug`-with-target.

## Batch 2 — fault vocabulary and teardown classification (R4)

Files: `src/WinForward.Runtime/UdpProxy/UdpTransportContracts.cs`, `UdpProxyCoordinator.Send.cs`,
`UdpProxyCoordinator.cs`, `UdpProxySession.cs`, tests in `tests/WinForward.Runtime.UdpProxy.Tests/`.

1. `UdpTransportHandshakeRejectedException : IOException` beside `UdpAssociationLostException`
   (same no-serialization-ctor pragma the neighbour carries).
2. `UdpProxyCoordinator.TeardownReasonFor` gains the branch →
   `UdpTeardownReason.SetupFailure`; `UdpAssociationLostException` keeps mapping to `AssociationLost`.
3. `RemoveReceiveFailedSessionCoreAsync` classifies the session's recorded fault through the same
   helper instead of hard-coding `Fault`; expose the recorded exception from the session (the scope
   stores it at `UdpProxySession.cs:369`).
4. Prove the native delta is nil: the existing suite must stay green, and a test must pin that a
   receive-side fault still lands as `Fault` for a non-typed exception (design §7).

## Batch 3 — deferred handshake in the control connection (R2)

Files: `src/WinForward.Runtime/Socks5/Socks5ControlConnection.cs`, tests in
`tests/WinForward.Runtime.Socks5.Tests/`.

1. Split `AuthenticateAsync` (`:349-362`) into a write half (greeting, `[+ username/password]`) and a
   read/validate half (method selection, `[+ credential reply]`), keeping the existing
   `ConnectAsync` behaviour byte-identical by calling both in sequence.
2. Add the deferred variant: dial (self-traffic callback intact, `NoDelay`, timeouts, attempt loop,
   address cache) → write the greeting `[+ auth]` → **return without reading any reply**; plus a
   completion step that reads and validates those replies, callable once, and a way to obtain the raw
   stream for the transport's own framing. Cancellation/deadline semantics must stay in the
   `QuiescenceScope` shape the class already uses, including dispose-joins-in-flight-op.
3. Tests: the deferred variant writes the exact bytes and reads nothing; completion accepts a valid
   sequence, rejects a method refusal and a credential refusal; a closed connection before completion
   surfaces as a typed failure; the existing quiescence/timeout tests stay green.

## Batch 4 — UoT transport, factory branch, test fixture (R1, R2, R3, R5)

Files: `src/WinForward.Runtime/Socks5/Socks5UotTransport.cs` (new),
`src/WinForward.Runtime/Socks5/Socks5UdpTransport.cs` (factory branch only),
`tests/WinForward.TestSupport/ScriptedSocks5UotServer.cs` (new), tests in
`tests/WinForward.Runtime.Socks5.Tests/` and `tests/WinForward.Runtime.UdpProxy.Tests/`.

1. Fixture first: `ScriptedSocks5UotServer` — greeting `[+ auth]`, read the CONNECT request
   (assert the magic FQDN), then read the UoT request header and framed datagrams; hooks for a
   deferred CONNECT reply (proving pipelining), injected frames, and connection drop; counters for
   connections/connect replies/frames. Mirror `ScriptedSocks5UdpServer`'s style and ordering guarantees.
2. Transport: create = deferred-handshake dial + publish endpoints; first send = one buffer
   (CONNECT + UoT header + frame) with the destination captured; later sends = frames only; receive =
   completion step (method/auth/CONNECT replies) then frames. Send gate + non-blocking fast path +
   copy-and-await slow path; `IUdpExchangeCounters`; typed faults per design §6; source synthesis from
   the captured destination; failure translations that never let a raw `ConnectionReset` reach the
   session (it would be treated as a skip and spin).
3. Factory branch on `target.Socks5.UdpOverTcp` with the same release-on-construction-failure catch the
   native path has.
4. Tests: frame-before-CONNECT-reply (the R2 acceptance observation); echo round trip; split frame
   reassembly; oversized frame consumed + reported; zero-length frame; destination mismatch fails
   closed; payload above `ushort.MaxValue` fails closed; CONNECT refusal → `SetupFailure` + cooldown
   (coordinator-level); mid-flow drop → re-establish on the next datagram, no cooldown; one-shot
   retention via `IUdpExchangeCounters`; concurrent sends never interleave; zero managed allocations
   on the warm send; disposal releases the connection exactly once; self-traffic tuple registered
   before the SYN.

## Batch 5 — harness column and evidence (R8)

Files: `benchmarks/WinForward.Benchmarks/Stability/LoopbackSocks5UotServer.cs` (new),
`Stability/SoakOptions.cs`, `Stability/UdpChurnScenario.cs`, `Stability/UdpBurstScenario.cs`,
`Stability/UdpSessionBudgetScenario.cs`, `benchmarks/README.md`, `benchmarks/results/2026-MM-DD-uot-per-flow/`.

1. `--target uot` in `SoakTargetKind` + parse; lift the `--target` refusal for `udp.sessionBudget`.
2. `LoopbackSocks5UotServer`: the benchmark sibling of the batch-4 fixture, bridging frames to a
   per-connection upstream UDP socket toward `EchoReceiver`; the CONNECT reply is deferred until after
   the first frame is read; counters `{ connections, connectReplies, frames }`.
3. The three scenarios: start the UoT fixture for the `uot` column, build the same
   `Socks5UdpTransportFactory` with `UdpOverTcp = true`, and emit the UoT handshake counters beside the
   existing `socks5Handshakes` field (design §9).
4. Run the columns and write the results README in the established convention (commands block,
   accounting identity, `## Superseded / Still standing`):
   `--target socks5` vs `--target uot` for `udp.churn` and `udp.burstEstablishment`, plus
   `udp.sessionBudget --target uot` for the descriptor/session number. Record the delta honestly,
   including "not worth recommending" if that is what the numbers say.

## Batch 6 — server verification, docs, full gates (R7, R9)

1. **R7 memo** under `research/`: pinned build (`sing` version / sing-box release), socks-inbound UoT
   support, connect-mode semantics, the reply write path for a destination-bound connection, and the
   pipelined flight (greeting+auth+CONNECT+UoT header+first datagram written without awaiting the
   replies). Where the exact build cannot be exercised from this host, record the probe used, the
   observed transcript, and what remains unverified — the repository's `[recorded]`/`[to confirm]`
   convention applies.
   **Record two residuals carried from earlier batches** rather than silently fixing them: the magic
   CONNECT is 30 bytes (FQDN 23, not the 22 the first design estimate assumed — the send buffer is
   `52 + maximumFrameSize`), and a receive-path rejection arms the cooldown through the classification
   without incrementing `udpSetupFailures` or emitting `udp.setup.failed` (those stay in the setup
   pipeline; the flow still traces `udp.setup.cooldown` on its next datagram). README's debug-event
   field list must gain `udpTransport` (the event now carries `target`, `targetKind`, `udpTransport`).
2. README: the mode, how to enable it, and the TCP-carriage caveat (per-flow head-of-line,
   QUIC-over-TCP congestion stacking; native stays the recommendation on lossy legs).
   `.trellis/spec/backend/udp-relay.md`: the transport's ownership section, extending the shipped
   guarantee to one flow per connection.
3. Full gates, in order: `dotnet build WinForward.slnx -c Release`,
   `dotnet test WinForward.slnx -c Release`,
   `dotnet format WinForward.slnx --severity info --verify-no-changes --no-restore` (empty output),
   `jb inspectcode -f=Xml -e=HINT -o=/tmp/jb-inspectcode.xml WinForward.slnx` (parse the XML: zero
   `<Issue>` entries — the exit code is not the gate).

## Risky files and rollback points

| Point | Risk | Rollback |
| --- | --- | --- |
| `Socks5ControlConnection` (batch 3) | a security-sensitive handshake path; the deferred variant must not weaken the existing one | the existing `ConnectAsync` keeps its exact behaviour; batch 3 is a pure addition until the transport calls it |
| `UdpProxyCoordinator` classification (batch 2) | shared by every transport | additive branch + a test that the native path is unchanged; revert the two hunks |
| packet-path send gate (batch 4) | frame interleaving or allocation regressions | the gate/allocation tests mirror the native transport's; revert the branch |
| whole task | any batch | the mode is one config field, default off; deleting the UoT transport + codec + branch + config/log members reverts it completely |

## Follow-up checks before `task.py start`

- `prd.md`, `design.md`, `implement.md` present and consistent (R2/R4/R7 wording matches design §3/§6/§7).
- The design decisions the user owns are settled: opt-in mode, native default, per-flow connect mode,
  no sharing/pooling, harness column decides the recommendation (not whether it ships).
