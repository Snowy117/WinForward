# Implementation Plan: UDP allocation zero-out + jumbo buffer sizing

Design: `design.md` (D1-D5). Evidence inventory: `research/current-state.md`.
Work through the steps in order; the tree must compile + full suite green at
every `[gate]`.

## Validation commands

```bash
dotnet build WinForward.slnx -c Release          # zero warnings
dotnet test                                      # all projects (459+ after new tests)
dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- --filter '*UdpSession*' --memory
```

Benchmark numbers (session populate 1/100/1000 + NoopTransport variant) go into
the task notes / journal as the recorded evidence, matching prior children.

## Steps

1. **Protocols: decode to `IPAddressValue`** (D2, compiles standalone in Protocols)
   - `src/WinForward.Protocols/Socks5Udp.cs`: `Socks5UdpDatagram` field type
     `IPAddress?` → `IPAddressValue?`; `TryReadAddress` returns `IPAddressValue`
     via `FromIPv4`/`FromIPv6(bytes, checked((uint)scopeId))`; drop the now-
     unreachable `ArgumentException` catch; keep type/length cross-check.
   - Migrate assertions: `UdpPacketParsingTests.cs` (incl. scope 9 →
     `IPAddressValue.ScopeId`), `UdpRelayTests.cs`, `Socks5UdpAssociateTests.cs`,
     `Socks5UdpTransportSendTests.cs`, `UdpReceiveResilienceTests.cs`,
     `Socks5ControlTimeoutTests.cs`.
   - `[gate]` Protocols + Core.Tests compile; suite green except Runtime
     consumers (fix in step 2) — if preferred, land steps 1+2 as one compile
     unit.

2. **Runtime: transport interface + session** (D1, D3, D4)
   - `Socks5UdpTransport.cs`: `SendAsync(Endpoint destination, ...)`;
     `_receiveSenderTemplate` field (ctor-computed); `maximumFrameSize`
     internal-seam parameter → `_sendBuffer = new byte[6 + 16 + _maximumFrameSize]`;
     encode via `destination.Address`/`destination.Port`; `ReceiveAsync` uses
     the template. XML-doc the oversized boundary (D5).
   - `Socks5UdpTransportFactory`: required `int maximumFrameSize` ctor param.
   - `UdpProxySession.cs:115` pass-through; `:193/:200` `is not { } address`
     pattern + `Endpoint.From(address, port)`.
   - Do NOT touch: `SendOnReadySessionAsync` OCE filter
     (`UdpProxyCoordinator.cs:195-200`), teardown/`RemoveSlotAsync`, setup-queue
     charge/credit sites, `_sendGate` contract comments.
   - Migrate fakes/callers: `UdpTransportFakes.cs`, `UdpReceiveResilienceTests.cs`
     (`ConnectionResetOnceTransport`), `IdleExpirySweeperFailureTests.cs`
     (`ParkedTransport`), `Socks5UdpAssociateTests.cs:229`,
     `benchmarks/.../Perf/BenchmarkShared.cs`, `LoopbackSocks5UdpServer.cs`
     (D2 echo path).
   - `[gate]` full build zero-warning; `dotnet test` green.

3. **Cli composition** (D4)
   - `Program.cs CreateUdpCoordinator`: hoist `maximumFrameSize =
     NdisApiAbi.MaximumEthernetFrame`, pass to `Socks5UdpTransportFactory`,
     `UdpResponseReinjector`, and `UdpProxyCoordinator`; single-source-of-truth
     comment (send buffer / receive buffer / reinjector / native ABI agree;
     only the ABI constant changes).
   - `[gate]` build; CLI tests (if any construct the factory) green.

4. **New tests** (red→green where practical; all listed in PRD ACs)
   - Session→transport endpoint fidelity: `UdpProxySessionTests` — fake records
     the `Endpoint`; assert address/port/family match what the session was
     given (covers AC1).
   - Jumbo send buffer: `Socks5UdpTransportSendTests` (existing seam pattern) —
     internal `CreateAsync` with `maximumFrameSize: 9014` encodes a 2000B
     payload without `IOException`; also assert the default seam still rejects
     a payload over `6 + 16 + 1514` fail-closed (AC4).
   - Decode scope/value coverage via step-1 migrations (AC2) — add an explicit
     IPv6-scope test if the migrated set lacks one.
   - Production cap plumb: not unit-testable (CLI composition); verified by
     review + the Program.cs diff (AC5).
   - `[gate]` suite green (459 + new).

5. **Full verification** (last-iteration full scope)
   - `dotnet build` zero warnings; `dotnet test` all green.
   - Benchmark run (`*UdpSession*` + a dispatcher/touch point for sanity);
     record numbers; confirm no allocation regression on the populate paths
     (forward-leg gains show up in soak/stability scenarios, not required as a
     gate here).

6. **Spec update** (Phase 3.3, `trellis-update-spec` flow)
   - `udp-relay.md`: endpoint zero-allocation contract (forward `Endpoint`
     pass-through, `IPAddressValue?` decode with scope propagation, cached
     sender template), frame-cap-derived send-buffer rule + composition
     single-source-of-truth, oversized-response boundary (1472B @ 1514 ABI).
   - `hot-path.md`: only if the checker finds a convention worth noting (raw
     addresses now extend to SOCKS5 datagram decode).

7. **Parent bookkeeping**
   - Parent `prd.md` backlog row: `udp-alloc-jumbo` → completed (date).
   - Journal entry; commit `feat(udp): zero-allocation endpoints + frame-cap
     send buffer (X6, R5)`; `task.py` archive per finish-work flow.

## Review gates / rollback

- Checker (`trellis-check`) runs after step 5 and again on the final iteration
  (full scope): spec compliance, invariants from current-state.md §Invariants,
  charge/credit sites untouched, OCE filter byte-identical.
- Single commit; `git revert` is the rollback. No partial-landing.
