# Decision record — UDP association sharing is removed

Task `10-05-remove-udp-association-sharing` (parent `10-05-udp-association-sharing-correctness`,
R4 + R5). Decision taken by the user on 2026-10-05; this record states the evidence, the rejected
alternatives, the guarantee the shipped tree provides, and the condition that would reopen the
question. It is the source for the parent's R5 and the reasoning behind the README's UDP section.

Anchors below were read during planning or at implementation time; no protocol behaviour is asserted
here that this repository does not record.

## Decision

**A proxy-decided SOCKS5 UDP flow owns its own authenticated association — its own control
connection, its own `UDP ASSOCIATE`, its own relay socket — and that association lives and dies with
the flow.** Association sharing is deleted outright: the three configuration keys
(`udpAssociationReuse`, `udpAssociationMaxPerServer`, `udpAssociationFlowsPerAssociation`), the pool,
the control association, the lease, the capability sampler, the reuse mode, and the in-place
re-association are gone, and no configuration reaches a sharing path (a config that still sets one of
the keys fails closed at parse through the loader's unknown-property rule; the product has never been
released, so no migration surface exists).

## Evidence

### 1. Sharing misdelivers replies at concurrency, and the harness hid it

The corrected harness records a relay reply as a flow's own first response **only when the reply
arrived on the flow that asked** (`benchmarks/results/2026-10-05-udp-reuse-ownership/README.md`).
Before the correction the sinks keyed the first-response timestamp by the payload's own flow id and
never checked the arriving flow, so a reply the server wrote to a different flow was recorded as the
asking flow's answer.

Measured on the corrected instrument:

| Series | Column | Own replies | Answered by a sibling |
| --- | --- | --- | --- |
| `../2026-10-05-local-target/`, `udp.churn` (48 flows/wave) | `--reuse auto` | 5 / 4 / 7 per wave | 43 / 44 / 41 |
| `../2026-10-05-udp-reuse-ownership/`, `udp.churn` | `--reuse auto` | 9 / 5 / 4 per wave | 39 / 43 / 44 |
| `../2026-10-05-udp-reuse-ownership/`, `udp.churn` | `--reuse always` | 3 / 5 / 3 per wave | 45 / 43 / 45 |
| `../2026-10-05-local-target/`, `udp.burstEstablishment` | `--reuse auto` | 3 of 48 burst | 251 of 256 background flows |
| `../2026-10-05-udp-reuse-ownership/`, `udp.sessionBudget --rate 500` | `--reuse always` | 29,844 of 30,000 | 156 |

Every flow in these rows was answered by *some* reply; the failure is that most answers were not the
asking flow's. Only the 500 flows/s row records it as loss (`datagramsLost` 156), because there the
acceptance's own assertion treats a flow answered by a sibling's echo as unanswered — a flow with no
reply of its own has no answer at all. The `off` column answers 48/48 in every wave and every
scenario, which is the shape the deletion ships.

### 2. The misdelivery is structurally undetectable in the case that matters

The reply-ownership observation compares a reply's declared source with the receiving flow's
destination (`UdpProxySession.TryGetReceiveSource`, counter `udpResponseSourceMismatch`, event
`udp.response.foreign_source`; spec `.trellis/spec/backend/udp-relay.md`, "Reply-ownership
observability"). Two flows that share a destination are indistinguishable by address, so a reply
delivered to the wrong one of them carries the source the receiving flow also expects and never
reaches the comparison. A zero count therefore never proved that a shared association was safe, and
no detector could demote a server on this evidence — the mechanism could not be made trustworthy by
observing it harder.

### 3. The population that motivated sharing no longer needs it

Sharing was introduced to amortize the control connect plus `UDP ASSOCIATE` (≈7 ms) and the per-flow
local ports over the DNS-shaped majority. That population now has a strictly better placement: a
local target answers 48/48 per wave with zero control connections, ≈7.5 KB/session, and a p50 of
3.1–4.1 ms (`../2026-10-05-local-target/`), and it is the recommended placement in the README. The
same directory's `--reuse off` column already priced the post-deletion SOCKS5 shape (48/48 with
96/144/192 handshakes and ≈91 KB/session); the confirmation series re-measured it on the deletion
tree (`../2026-10-05-no-association-sharing/`: 48/48 per churn wave and in the burst on both
surviving columns, exactly one control connection and one ASSOCIATE reply per flow — 96/144/192 and
304/304 cumulative — with the local column at 0/0).

### 4. Every remaining sharing shape needs extra machinery to be safe

- Concurrent sharing is unsafe by construction (evidence 1) and undetectable in the same-destination
  case (evidence 2).
- An exclusive-lease warm pool — one live flow per association, the association kept warm between
  flows — removes the concurrency but keeps a **stale-reply window**: a late reply for a finished flow
  is written to whichever flow has since attached to the warm association, and closing that window
  needs the reply-drop decision the parent PRD deferred (`prd.md` "Out of scope": re-litigating the
  reply-drop posture). It also keeps the pool, the lease, the capability bookkeeping, and a
  configuration surface alive.
- Deleting the mechanism is the only shape that is correct without new machinery.

## Rejected alternatives

| Alternative | Why it was rejected |
| --- | --- |
| Keep concurrent sharing behind an opt-in key (`udpAssociationReuse: always`) | A path that is unsafe for a connection-oriented server — and whose failure is invisible when two flows share a destination — should not be reachable by a configuration key. |
| Default to per-flow but keep the pool/lease/capability machinery | Dead machinery with a config surface is a liability: it must keep passing the gates, the next reader cannot tell whether it is supported, and the unsafe path stays one property away. |
| Exclusive-lease warm pool (`udpAssociationFlowsPerAssociation: 1` plus warm retention) | Removes the concurrency, keeps the stale-reply window (evidence 4) and the machinery. |
| `L1` destination-keyed placement | Fixes routing inheritance only; reply ownership stays ambiguous for two flows to one destination (evidence 2), so it cannot carry the guarantee. |
| `L1`'s payload-inspection clause (inspect payloads to classify flows, e.g. QUIC) | Rejected as specified by the parent R4, and the rejection stands: it requires payload inspection in a transport-layer component, cannot be made reliable across QUIC versions, and encodes a server-side routing configuration into the client. That knowledge belongs in configuration (`routeScope`) or is made unnecessary by structure — which is what per-flow ownership does. It must not be reintroduced later as a "small addition": if payload inspection is proposed again it needs its own PRD and an explicit decision. |

## Shipped guarantee

> **One flow per association, so the reply-ownership ambiguity a shared association has cannot
> occur.**

The guarantee's consequences, all of them exercised by tests and documented in
`.trellis/spec/backend/udp-relay.md` ("UDP association ownership"):

- A flow's relay socket belongs to that flow alone — created, bound, and disposed with it — so a
  reply can only arrive on the socket that sent the request. Cross-flow reply delivery is impossible
  at the proxy, not merely detected.
- The foreign-source counter and its rate-limited warn stay, and they observe something else now: a
  reply whose declared source differs from the flow's destination, which RFC 1928 permits (a
  multi-homed or anycast server may legitimately answer from another endpoint). The counter is an
  observation about the server, not evidence about reply ownership.
- Association death is the flow's death: the control stream ending records
  `UdpAssociationLostException`, the coordinator removes the flow's slot as `AssociationLost` without
  arming the setup cooldown, and the flow re-establishes on its next datagram with a fresh
  association (`Socks5UdpAssociation`, `Socks5UdpTransport`).
- The resource cost is explicit: two local descriptors per live flow (the control connection and the
  relay socket), one relay socket's receive buffer per flow, and one dial + `UDP ASSOCIATE` per flow.

## Reopen condition

**A future need to carry many flows over one control connection is met by an L2 transport — never by
association reuse.** The L2 shape (UoT v2 `CONNECT`, or VLESS + XUDP session ids over one muxed
connection) keeps a single control connection for many flows while giving every flow its own
identity on the wire, which is exactly what the removed mechanism lacked. It plugs into the seam R3
built: `IUdpProxyTransport` / `IUdpProxyTransportFactory` and the `UdpTransportFactory` composite
over the transport-neutral `ProxyTarget` → `PeerEndpoint`/`UdpTransportDatagram` contract
(`src/WinForward.Runtime/UdpProxy/UdpTransportContracts.cs`, `src/WinForward.Configuration/ConfigurationModels.cs`).
`research/l2-readiness.md` in this directory prepares that task.

Reopening *association reuse itself* — attaching several flows to one SOCKS5 association — would
require new evidence that the reply-ownership failure cannot occur (a server-side per-flow framing
guarantee, or a shape this repository has not recorded), and it would need its own PRD. The removed
configuration keys stay removed; the mechanism is not coming back behind a flag.
