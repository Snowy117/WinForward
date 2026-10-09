# TCP Local Redirect Contracts

> How WinForward transparently redirects proxy-selected TCP flows to a local SOCKS5 relay, and how it
> ends them again. This is the family hub: the pipeline, the two wire shapes, and the invariants that
> hold on every redirect path. A redirect is **not** routing and **not** a NAT through the host's
> stack: WinForward captures the SYN, claims the flow, reshapes the frame so it arrives at a listener
> socket WinForward owns, and relays the accepted connection to the original destination. Everything
> the client sees after that — handshake, data, close — is produced by this path, not by the real
> server. Read this first; the child that owns a rule is named at every stage and in the topic map.
> Transport basics are [windows-ndisapi.md](./windows-ndisapi.md)'s, and the deferred-injection lane
> mechanics are [ndis-batched-send.md](./ndis-batched-send.md)'s;
> the quiescence primitive the relay and store are built on is [async-lifetime.md](./async-lifetime.md);
> policy and flow-hold interplay is [traffic-policy-lifecycle.md](./traffic-policy-lifecycle.md).
> Split 2026-08-29 from the former monolithic NDISAPI file; split again 2026-10-09 into this hub plus
> five children.

## The pipeline

| Stage | What happens | Owner |
|---|---|---|
| Policy and claim | A proxy-decided TCP flow reaches `TcpProxyCoordinator`; the SYN claims one association in `TcpRedirectTable` (exactly-once per original key). | [tcp-syn-setup-admission.md](./tcp-syn-setup-admission.md) |
| Rewrite | The forward leg is reshaped in place or into a pooled stage: host shape swaps addresses, forwarded shape moves only the destination. Sequence trackers read the **pre-rewrite** bytes. | [tcp-redirect-transform.md](./tcp-redirect-transform.md) |
| Inject and accept | The rewritten frame is injected toward the listener (immediate on the setup path, deferred to the iteration-end lane flush on the data legs); the accept loop validates the peer and establishes the SOCKS5 relay. | [tcp-redirect-transform.md](./tcp-redirect-transform.md), [tcp-relay-lifecycle.md](./tcp-relay-lifecycle.md) |
| Relay | Two pumps copy bytes in both directions under one scope-owned lifetime and a 30-minute stall window; the reverse leg is reversed back to the client. | [tcp-relay-lifecycle.md](./tcp-relay-lifecycle.md) |
| Close | The relay end delivers the client-visible close: a crafted RST\|ACK for `Stalled`/`Faulted`, and for `CleanEnded` a bounded drain that releases the relay, keeps the alias live until the client acknowledges the stack's own FIN, then retires. | [tcp-client-close-injection.md](./tcp-client-close-injection.md) |
| Retire | The session retires under the store gate: session-dict removal, table-alias removal and tombstone arming in one critical section, then the disposals trail. | [tcp-redirect-teardown-grace.md](./tcp-redirect-teardown-grace.md) |

## The two wire shapes

Shape is selected per association by `TcpRedirectAssociation.ForwardLocalAddress`; the reverse leg
always returns to where the flow came from.

| | Host-originated flow | Forwarded flow (guest/remote client) |
|---|---|---|
| Forward leg | Swap MACs and IPs; `th_dport` (a driver-header field name) → listener port; the client's source port is preserved. | `src` stays client:client-port; only `dst` moves to (adapter-local address L, listener port). No MAC swap. |
| Listener | Binds `0.0.0.0` / `[::]` on an ephemeral port — never loopback. | Same listener, reached through L. |
| Reverse leg | Reversed to the original server:client tuple, injected toward MSTCP. | Same rewrite, injected toward the **origin adapter**. |
| Failure posture | No usable L → fail closed: `tcp.redirect.rejected reason=localAddress`. | Same. |

## Cross-cutting invariants

- **A proxy-selected flow is never silently passed.** Every setup failure fails closed (`Blocked`) or
  is surfaced client-visibly; the reverse hook runs before flow-table lookup and policy so a reverse
  packet is never re-evaluated as a new client flow.
- **A relay end does not retire until its client-visible close has landed.** An abnormal end injects
  the crafted RST|ACK before the retire; a clean end releases the relay, arms a bounded drain on the
  acknowledgement that covers the stack's FIN, and retires when it lands, when the deadline elapses, or
  when another teardown path wins; see [tcp-client-close-injection.md](./tcp-client-close-injection.md).
- **One atomic retire arms the grace window.** Session-dict removal, table-alias removal and tombstone
  arming happen in one store-gate critical section, and every teardown path funnels through the single
  tombstone write point; see [tcp-redirect-teardown-grace.md](./tcp-redirect-teardown-grace.md).
- **The capture pump never waits on setup.** A genuinely new SYN retains a bounded copy and returns
  `SetupPending`; the listener bind, claim, rewrite and injection run on a pooled setup worker; see
  [tcp-syn-setup-admission.md](./tcp-syn-setup-admission.md).
- **Outcomes stay distinguishable.** Grace drops are `TcpRedirectOutcome.Dropped`, never `Blocked` —
  the executor's `Blocked` branch reports a proxy problem and would mislabel a normal grace consume.
- **One owner per lifetime.** Relay pumps, the session lifetime CTS and the store's inflight setups
  live on `QuiescenceScope`; dispose returns only after the tasks it owns have finished.

## Deployment prerequisite: Windows Firewall inbound rule

Every redirect path terminates at a local listener socket, so the injected SYN is an unsolicited
inbound connection from the stack's perspective. On adapters whose profile applies the default
inbound block (typically Public), Windows Firewall silently drops it before TCP: `tcp.redirect.created`
appears, no SYN-ACK ever leaves, `tcp.relay.started` stays absent, and the flow hangs. Diagnose with
`Set-NetFirewallProfile -All -LogBlocked True` plus `pfirewall.log`, and
`Get-NetTCPConnection -State SynReceived` (empty). The remedy is a deployment rule, not code:
`New-NetFirewallRule -Direction Inbound -Action Allow -Program "<path>\WinForward.exe" -Profile Any`.
Forwarded DNAT injections onto Private/unidentified-profile virtual adapters pass by default, which is
why the failure first showed on the public-profile WLAN host path (hardware-verified 2026-08-15).

## Topic map

| Read it when you are changing… | Document |
|---|---|
| The frame shape, the reverse hook, mid-flow data legs, deferred injection, redundant accepts, forwarded DNAT, the IPv6-zone peer rule | [tcp-redirect-transform.md](./tcp-redirect-transform.md) |
| The client-visible close: the abnormal-end RST\|ACK shape, its sequences, the clean-end drain's target and exits, capacity resets, fragment teardown, injection-failure exits | [tcp-client-close-injection.md](./tcp-client-close-injection.md) |
| Admitting a new SYN: pump fast paths, the capacity gate, the bounded pending index, the setup executor and cooldown | [tcp-syn-setup-admission.md](./tcp-syn-setup-admission.md) |
| What happens after a redirect retires: the single tombstone write point, atomic retire, grace consumption, the flow-hold predicate, the warm reverse probe | [tcp-redirect-teardown-grace.md](./tcp-redirect-teardown-grace.md) |
| The relay and store lifetime: pump result classification, `EndKind`, the stall window, dispose ordering and single-flight, setup leases, the accept loop's lifetime | [tcp-relay-lifecycle.md](./tcp-relay-lifecycle.md) |

## Where things moved

This table keeps the numbered and titled citations frozen in `benchmarks/results/**` resolvable after
the 2026-10-09 split. The old numbered sections (`### 1. Scope / Trigger` … `### 7. Wrong vs Correct`)
were template scaffolding; their content is in the child named for the topic.

| Old section (title or number) | Now |
|---|---|
| "WinpkFilter local_redirect transform" (the five-step host transform) | [tcp-redirect-transform.md](./tcp-redirect-transform.md) |
| "Reverse path (the subtle part)" / the X1 prefilter row | [tcp-redirect-transform.md](./tcp-redirect-transform.md) |
| "Mid-flow data" | [tcp-redirect-transform.md](./tcp-redirect-transform.md) |
| "Data-bearing SYNs (TCP Fast Open)" — the `IsTcpSyn`/tolerance half | [tcp-redirect-transform.md](./tcp-redirect-transform.md) |
| "Data-bearing SYNs (TCP Fast Open)" — the sequence-advance/tracker half | [tcp-client-close-injection.md](./tcp-client-close-injection.md) |
| "Redundant accepts" | [tcp-redirect-transform.md](./tcp-redirect-transform.md) |
| "Forwards-direction reverse injection" | [tcp-redirect-transform.md](./tcp-redirect-transform.md) |
| "Forwarded flows use the DNAT-to-local transform" | [tcp-redirect-transform.md](./tcp-redirect-transform.md) |
| "Accept-loop peer identity excludes the IPv6 zone" | [tcp-redirect-transform.md](./tcp-redirect-transform.md) |
| "Relay setup failure resets the client" | [tcp-client-close-injection.md](./tcp-client-close-injection.md) |
| "Client-reset sequence tracking and injection-failure exits" | [tcp-client-close-injection.md](./tcp-client-close-injection.md) |
| "Capacity-rejected SYN → RST\|ACK" — the shape and cooldown | [tcp-client-close-injection.md](./tcp-client-close-injection.md) |
| "Capacity-rejected SYN → RST\|ACK" — the capacity gate that triggers it | [tcp-syn-setup-admission.md](./tcp-syn-setup-admission.md) |
| "Fragments on associated flows" | [tcp-client-close-injection.md](./tcp-client-close-injection.md) |
| "Faulted relay completions must be observed (S3)" | Deleted: the `ContinueWith` observer is gone; the surviving rule is in [tcp-relay-lifecycle.md](./tcp-relay-lifecycle.md). |
| "Mid-flow relay fault/stall must reset the client" | [tcp-client-close-injection.md](./tcp-client-close-injection.md), [tcp-relay-lifecycle.md](./tcp-relay-lifecycle.md) |
| "Every end injects its client-visible close before the retire" | [tcp-client-close-injection.md](./tcp-client-close-injection.md) (its contract now lives in "The abort shape (RST\|ACK)" and "A clean end drains the close handshake instead of injecting a FIN") |
| "New-flow SYN setup never blocks the capture pump" | [tcp-syn-setup-admission.md](./tcp-syn-setup-admission.md) |
| "SOCKS5 control-socket timeout lifecycle" | [tcp-relay-lifecycle.md](./tcp-relay-lifecycle.md) |
| "Deployment: Windows Firewall inbound rule is required" | this hub, "Deployment prerequisite" |
| "Redirect teardown grace and flow-hold contracts" (§1–§7) | [tcp-redirect-teardown-grace.md](./tcp-redirect-teardown-grace.md) |
| "Redirect teardown grace…" §3 (Contracts) — the numbered citation in `benchmarks/results/2026-09-30-warm-path-lock-chain/README.md` | [tcp-redirect-teardown-grace.md](./tcp-redirect-teardown-grace.md) |
| "Relay/redirect quiescence, attach-failure teardown, and setup-fault release" (§1–§7) | [tcp-relay-lifecycle.md](./tcp-relay-lifecycle.md) |
| "Relay pump tracking, scope-owned lifetimes, and intrinsic fault observation" (§1–§7) | [tcp-relay-lifecycle.md](./tcp-relay-lifecycle.md) |
| "UDP is deliberately untouched" (one-line contrast) | Deleted; the UDP side owns its own teardown in [udp-relay.md](./udp-relay.md). |

The frozen citation `benchmarks/results/2026-09-30-flow-key-parse-once/README.md:178` (and
`…/2026-09-30-warm-path-lock-chain/README.md:198`) names "TFO SYN / sequence tracking" without a heading:
the SYN predicate and tolerance half is in [tcp-redirect-transform.md](./tcp-redirect-transform.md), the
tracker and sequence-advance half in [tcp-client-close-injection.md](./tcp-client-close-injection.md).
