# Batch 5c-1: raw-socket relay seam

Parent: `.trellis/tasks/10-25-kernel-backend-integration`

## Goal

Add a public way to establish the existing WinForward TCP relay from an already-accepted `Socket`
(the Kernel backend owns its listener and gets a connected socket from WFP local redirect, not a
`TcpRedirectListener` association), and expose the relay end kind so that backend can set linger 0
on a faulted/stalled relay. The packet-path relay behavior is unchanged.

## Requirements

- **R5c1-1 Raw-socket entry.** `TcpProxyRelayFactory` gets a public
  `EstablishAsync(Endpoint originalDestination, Socket acceptedSocket, Socks5Server server,
  CancellationToken)` overload. It wraps the socket in the existing internal accepted-connection
  type (same assembly), uses the existing SOCKS5 control/connect path, and transfers socket
  ownership to the relay. A missing remote endpoint is a clear startup error.
- **R5c1-2 End kind.** Make the relay end-kind surface public (`CleanEnded`/`Stalled`/`Faulted`)
  so a consumer outside the Runtime assembly can distinguish a clean close from a stall/fault after
  `Completion`. The packet-path `TcpRedirectAcceptor` keeps using it; no behavior change.
- **R5c1-3 Tests.** Add a test that establishes the relay through the raw-socket overload against
  the existing fake SOCKS5 upstream and pumps both directions; assert the end kind for a clean end
  and a fault. Do not add a 30-minute stall test; the stall window is unchanged.
- **R5c1-4 Boundaries.** No KernelBackend/device/listener/accept/redirect-context code, no
  host TCP/UDP/forwarded, no ABI/Kernel contracts/driver/NdisApi changes. Existing packet-path
  tests and behavior remain green.
- **R5c1-5 Quality.** Root build/test/format, Kernel fast gate; project-level jb for Runtime if
  feasible; the full-solution jb gap stays recorded.

## Acceptance Criteria

- [x] R5c1-AC1: the raw-socket overload returns a working relay; the socket is owned/disposed
      exactly once and the existing accepted-connection path is unchanged.
- [x] R5c1-AC2: the end kind is readable after `Completion` and distinguishes clean/fault; no
      existing relay test changes meaning.
- [x] R5c1-AC3: root build/test/format and the Kernel fast gate pass; no packet-path behavior or
      unrelated changes.

## Out of Scope

- `KernelBackend`, WFP device configuration, host listener/accept loop, redirect-context parsing,
  target table wiring, Windows E2E.
