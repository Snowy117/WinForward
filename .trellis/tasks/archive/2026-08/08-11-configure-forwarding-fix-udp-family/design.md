# Bridge Configuration and Cross-Family SOCKS5 UDP Relay

## Configuration Semantics

The delivered configuration keeps one ordered host rule set:

1. Pass listed host processes, including WinForward and the local proxy
   processes, before any proxy rule.
2. Pass listed host-local destination prefixes before any proxy rule.
3. Proxy traffic arriving from the selected Network Bridge adapter, identified
   by stable GUID `{E14A2A2E-F7E2-4428-942E-6D04D1C6D797}`.
4. Proxy all remaining host traffic through `main`.

New forwarded traffic skips rules 1, 2, and 4 because they have no adapter
constraint. It matches rule 3 only when first observed on the selected bridge;
forwarded traffic from every other adapter has no eligible rule and passes
unchanged. The VM's local-network traffic does not traverse the host gateway,
so no bridge-scoped local-CIDR bypass rule is needed.

Existing flow decisions are adapter-agnostic after first claim so the same
routed packet can be recognized at its egress observation. Consequently,
strict isolation for distinct clients on different adapters that happen to use
an identical protocol and endpoint tuple is not claimed by this task. The user
accepted that existing limitation; changing the flow-identity contract is
deferred.

## UDP Association Design

The original-flow address family, SOCKS5 control connection family, relay
transport family, and SOCKS5 UDP payload destination family are separate
concepts. The existing implementation incorrectly couples the first and third.

### Revised Setup Sequence

1. Open and authenticate the SOCKS5 TCP control connection. Its pre-SYN
   loop-prevention registration remains unchanged.
2. Send UDP ASSOCIATE with an all-zero address and port in the control
   connection's family. RFC 1928 requires this when the client does not yet
   know its UDP source endpoint.
3. Normalize the returned relay endpoint using the existing control-peer rules.
4. Create and bind the UDP socket in the returned relay endpoint's address
   family.
5. Register `(Udp, local UDP endpoint, relay endpoint)` in `SelfTrafficRegistry`
   and construct the relay alias. Both endpoints now necessarily have the same
   address family.
6. Send each proxied datagram to the relay. `Socks5UdpCodec.Encode` continues
   to encode the original destination's own IPv4/IPv6 ATYP and address.

For example, an IPv6 original flow can send a datagram with IPv6 ATYP through a
UDP socket bound to IPv4 and directed to an IPv4 `127.0.0.1` relay. The SOCKS5
server decides whether it can reach the IPv6 destination; WinForward does not
rewrite it.

## API and Ownership Boundaries

- Remove the original-flow address-family parameter from
  `IUdpProxyTransportFactory.CreateAsync`, `Socks5UdpTransportFactory`, and
  `Socks5UdpTransport.CreateAsync` to make invalid coupling unrepresentable.
- Replace the endpoint-taking control operation with
  `Socks5ControlConnection.UdpAssociateAsync(CancellationToken)`, which sends
  the correct all-zero endpoint derived from its connected socket family. There
  is no shipped API or other concrete compatibility need for both semantics.
- The control connection owns its TCP loop-prevention token until the transport
  closes. Once the relay is known, the transport owns the UDP socket and UDP
  loop-prevention token. On every failure, dispose resources acquired so far in
  reverse acquisition order.
- `UdpProxyCoordinator` retains its existing flow-keyed session ownership,
  relay-alias collision check, capacity behavior, and fail-closed setup error
  semantics. It no longer selects a UDP socket family from `flow.Local`.

## Compatibility

- No configuration schema change.
- Existing same-family SOCKS5 UDP configurations remain valid.
- A standards-compliant SOCKS5 server receives an all-zero UDP ASSOCIATE
  endpoint instead of a prebound endpoint. A non-compliant server that requires
  a predeclared UDP source port may reject association; that is a server
  compatibility limitation, not a destination-family conversion case.
- SOCKS5 servers still decide whether IPv6 destinations are reachable. Their
  inability to do so must not cause WinForward to silently pass or transform a
  proxy-selected datagram.

## Validation and Rollback

- Unit coverage includes the exact previous cross-family alias failure and a
  real loopback SOCKS5 control/UDP relay exchange proving an IPv6 destination
  header can travel over an IPv4 relay socket.
- The task-local reproduction is pre-fix evidence only. Convert it into product
  regression coverage and delete the throwaway reproduction project before
  completion.
- Regression coverage preserves matching-family IPv4/IPv6 operation,
  loop-prevention ownership, source validation, session reuse, and capacity
  failure behavior.
- The change is localized to SOCKS5 UDP transport creation, coordinator factory
  invocation, tests, README, and task-local recommended configuration. Revert
  those code changes together to restore prior behavior; no data/configuration
  migration is required.
