# 配置网卡转发并修复 UDP 地址族错误

## Goal

Provide a correct, testable configuration for proxying VM traffic from
`vEthernet (Network Bridge)` while keeping unselected forwarded adapters out of
the proxy, and eliminate the observed UDP proxy failure:

```text
[warn] UDP proxy handling failed: ArgumentException: Flow endpoints must use the same address family. (Parameter 'remote')
```

The user should be able to proxy selected forwarded traffic and normal host
traffic through the configured local SOCKS5 server without an IPv4/IPv6 relay
endpoint mismatch aborting UDP session setup.

## Confirmed Facts

- Since `b90fce7`, new `Forwarded` traffic evaluates only adapter-qualified
  rules. Adapter-unqualified process, CIDR, and catch-all rules do not affect
  it; unmatched forwarded traffic passes unchanged (`README.md:82-97`).
- Host traffic evaluates every rule in declared order. In the supplied
  configuration, the bridge proxy rule precedes the host process/CIDR pass
  exemptions, so a host flow using that adapter can be proxied before its pass
  exemption is considered.
- On the actual target machine, `WinForward adapters` reports the bridge as
  stable adapter ID `{E14A2A2E-F7E2-4428-942E-6D04D1C6D797}` with friendly
  name `vEthernet (Network Bridge)`. The configuration should use this GUID so
  a friendly-name change does not silently retarget or invalidate the rule.
- The user's intent is to proxy forwarded Internet traffic originating from
  this bridge and leave forwarded traffic from every other adapter untouched.
- The VM's local-network traffic does not traverse the host gateway. No
  bridge-adapter-qualified local-CIDR pass rule is required for VM traffic.
  The existing adapter-unqualified `remoteCidr` pass rule remains a Host-only
  exemption and must precede the host catch-all proxy rule.
- `UdpProxyCoordinator.CreateSessionAsync` creates a UDP socket family from
  the original flow, then constructs a relay alias from the socket's local
  endpoint and the SOCKS5 UDP ASSOCIATE reply (`src/WinForward.Runtime/UdpProxyCoordinator.cs:163-176`).
  `FlowKey.Create` rejects endpoints from different address families
  (`src/WinForward.Core/Domain.cs:78-81`).
- `Socks5UdpTransport.CreateAsync` creates the UDP socket before UDP ASSOCIATE,
  but `Socks5ControlConnection` may connect to a SOCKS5 server address of a
  different family and use that IPv4/IPv6 control peer to normalize a wildcard
  UDP relay reply (`src/WinForward.Runtime/Socks5Client.cs:396-416`,
  `src/WinForward.Runtime/Socks5Client.cs:289-294`).
- With `host: "127.0.0.1"`, an IPv6 original flow can therefore have an IPv6
  local UDP transport endpoint and an IPv4 relay endpoint, producing the exact
  observed `FlowKey.Create` exception. This is an internal transport-family
  contract failure, not a SOCKS5 UDP protocol failure.
- Before the fix, a task-local deterministic reproduction constructed an IPv6
  original flow and an IPv6-local/IPv4-relay transport, printed the exact
  observed exception, and exited 0 after asserting it. Its scenario is now
  covered by product tests and the throwaway project was removed.
- RFC 1928 requires the client to send UDP payloads to the relay endpoint
  returned by UDP ASSOCIATE, while each payload independently includes an ATYP
  and destination address. An IPv4 relay transport can therefore carry a UDP
  payload whose destination ATYP is IPv6; actual IPv6 reachability remains the
  SOCKS5 server's responsibility.

## Requirements

- Give the user an updated configuration that preserves host pass exemptions
  before host proxy rules, proxies the selected bridge adapter's forwarded
  traffic, and allows other forwarded adapters to pass unchanged.
- Select the bridge by `adapterId` using
  `{E14A2A2E-F7E2-4428-942E-6D04D1C6D797}`, not by friendly name.
- Establish UDP ASSOCIATE before creating the UDP socket, using an all-zero
  endpoint in the control connection's address family as RFC 1928 prescribes
  when the client does not yet know its UDP source port.
- Create the UDP socket in the returned relay endpoint's address family, so
  relay aliases and loop-prevention tuples always use same-family endpoints.
- Keep the original flow's destination address family in the SOCKS5 UDP header;
  do not translate IPv6 destinations to IPv4 or otherwise alter destinations.
- Preserve current IPv4 and IPv6 UDP relay behavior when the SOCKS5 server and
  relay already use the original flow's address family.
- Keep configuration schema and forwarded-adapter policy semantics unchanged.

## Acceptance Criteria

- [x] The delivered configuration proxies new forwarded traffic from
      `{E14A2A2E-F7E2-4428-942E-6D04D1C6D797}` (`vEthernet (Network Bridge)`)
      while a new forwarded flow from an unselected adapter passes unchanged.
- [x] Host process and local-network pass rules take precedence over host
      adapter and catch-all proxy rules.
- [x] The task research records a deterministic pre-fix reproduction of the
      exact IPv6-local/IPv4-relay `FlowKey.Create` family mismatch.
- [x] A product regression test using that cross-family scenario fails before
      the fix and succeeds afterward without weakening `FlowKey` invariants.
- [x] The corrected UDP path handles the selected IPv6-flow/IPv4-relay policy
      without emitting the observed `Flow endpoints must use the same address
      family` failure.
- [x] A loopback integration test proves an IPv4 SOCKS5 UDP relay receives a
      correctly encoded IPv6-destination UDP request after an all-zero UDP
      ASSOCIATE request.
- [x] Existing matching-family IPv4 and IPv6 UDP relay tests remain green.
- [x] The recommended configuration validates successfully and maintains the
      intended host exemptions, selected bridge proxying, and unselected
      forwarded-adapter pass behavior.

## Out of Scope

- Changing the policy JSON schema, Windows routing/NAT configuration, or the
  existing direction-derived `Forwarded` definition.
- Supporting SOCKS5 UDP relays that cannot transport the requested destination
  address family; the selected behavior must fail clearly rather than silently
  alter the destination.
- Redesigning cross-adapter flow identity. Existing-flow lookup intentionally
  reuses a decision across adapter observations so routed packets are not
  re-evaluated as Host traffic. Distinct clients on different adapters that use
  an identical protocol and endpoint tuple can therefore theoretically share a
  decision; strict per-adapter tenant isolation requires a separate flow-table
  design task.

## Selected Decisions

- Use automatic cross-family relay compatibility. An IPv4 SOCKS5 relay may
  carry a SOCKS5 UDP payload addressed to an IPv6 destination, and vice versa.
- Use the target bridge's GUID `{E14A2A2E-F7E2-4428-942E-6D04D1C6D797}` in the
  delivered configuration.
- Do not add bridge-local-CIDR bypass rules because those VM flows do not
  traverse the host gateway.
- Accept the existing cross-adapter same-tuple reuse limitation for this task.
  Ordinary new flows entering an unselected adapter still default to pass and
  do not reach the Host catch-all proxy rule; strict multi-tenant adapter
  isolation is deferred to a separate flow-identity task.
