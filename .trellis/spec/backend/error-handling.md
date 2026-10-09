# Error Handling

> The project's error *policy*: what fails closed, what is deliberately exempted, and how a failure is
> classified before anything is logged, counted or torn down. The mechanism behind a given rule lives
> in the owning subsystem's document — this one owns the cross-cutting contract and the list of
> bounded exemptions.

## Scope / Trigger

Read before adding a catch, a fallback, a retry, a cooldown, a teardown path, or an exemption to a
fail-closed rule; and before deciding that a failure is "shutdown" rather than a fault.

---

## The Fail-Closed Rule

- **A proxy setup failure is fail-closed.** Proxy-selected packets are never silently downgraded to
  pass.
- **Unknown or ambiguous process attribution does not guess an owner.** Process rules do not match, and
  evaluation continues to the next rule or the fallback.
- **Flow-table capacity exhaustion fails closed** — see the forwarding rules in
  [traffic-policy-lifecycle.md](./traffic-policy-lifecycle.md).
- **Unreadable configuration fails the load.** `ConfigurationLoadOutcome.Invalid` reaches
  `Program.InvalidConfiguration` and the process refuses to run rather than half-applying a document.

## Bounded Exemptions

Every exemption from a fail-closed rule is listed here, with its reason and its end condition. An
exemption that is not on this list is a bug.

- **A pre-existing TCP flow whose SYN was never observed** (2026-08-14). The connection pre-dates
  capture startup, so it was never proxyable — there is no relay it could join retroactively. Its
  packets are passed (`TcpRedirectOutcome.NotRelevant` → reinject) so the connection survives instead
  of hanging. This is **not** a downgrade of an established proxy path: the flow's decision stays
  `proxy`, every packet is re-evaluated, and the exemption ends with the connection's lifetime (the
  next connection has a SYN and is proxied normally). Locked by
  `ExecutorPassesTcpPacketWhenCoordinatorReportsNotRelevant`.
- **Non-flow frames always pass** — the rule and its incident evidence are in
  [traffic-policy-lifecycle.md](./traffic-policy-lifecycle.md).
- **Per-caller cancellation of a shared UDP send does not tear down the session** unless the shared
  setup/send operation itself failed.

## Failure Classification

- **Shutdown is not a failure.** An `OperationCanceledException` raised while the pipeline's scope
  token is cancelled is shutdown: the work item fails closed with **no cooldown and no failure
  counter**. Any other exception is a genuine failure, is counted, and arms whatever cooldown the
  subsystem owns. Getting this backwards either hides real faults or turns every normal shutdown into
  a retry storm.
- **Only a setup failure arms the setup cooldown.** `UdpTeardownReason.SetupFailure` is the single
  reason that arms the 1 s UDP setup cooldown; `UdpTeardownReason.AssociationLost` is counted
  (`udpAssociationLost`), logged rate-limited (`udp.association.lost`) and is deliberately
  cooldown-free, so the flow re-establishes on its next datagram. The mapping is identical on the
  ready-path send and on the setup-queue flush — `UdpProxyCoordinator.TeardownReasonFor` is the single
  decision point. See [udp-session-lifecycle.md](./udp-session-lifecycle.md).
- **Capacity rejection is not an error.** It is explicit capacity management: fail-closed `Blocked`
  with a trace `reason=capacity`, a counter, and a periodic summary. A UDP session-table refusal uses
  the 5 s-throttled `udp.session.capacity-block` warn and arms no cooldown. The TCP budget and the
  RST|ACK a rejected SYN elicits are in
  [tcp-syn-setup-admission.md](./tcp-syn-setup-admission.md).
- **A transient native read error degrades an adapter; it does not exit the process.** The pump
  retries with bounded backoff and then leaves the affected adapter's filter mode restored while the
  process and its sibling adapters keep intercepting. A generation-startup stale-handle fault (native
  87) is absorbed the same way, bounded by a consecutive-recovery streak after which the original
  fault rethrows fail-closed. Both contracts, with their thresholds, live in
  [ndis-capture-refresh.md](./ndis-capture-refresh.md).
- **A fail-closed drop is counted, not thrown.** `UdpProxySession.SendSpanAsync` returns
  `ValueTask<bool>`; `false` is counted (`RuntimeCounters.UdpFailClosedDrop`) with a throttled
  `udp.send.dropped reason=sessionUnavailable`, and the sender never removes the slot — idle expiry
  belongs to the sweeper, a fault to the failure handler.
- **A second `DisposeAsync` on a migrated owner joins instead of throwing.** Every owner moved to
  `QuiescenceScope` makes its teardown a single-flight one-shot whose loser awaits the same drain, so
  no `ObjectDisposedException` escapes from a late `CancelAsync`. A caller entering *after* the seal is
  still refused with `ObjectDisposedException` — the fail-closed direction stays explicit. See
  [async-lifetime.md](./async-lifetime.md), D11.

## Client-Visible Failures

**A failure the client can see must be surfaced, not hung.** A relay setup that fails after a
successful redirect is reported to the client as a crafted in-window RST|ACK from the original server
endpoint (`TcpResetBuilder.TryBuildResetFromSyn`), and an associated flow's fragment teardown does the
same when the tracked sequences permit. The reset degrades to a plain teardown **only** when the SYN
or SYN-ACK sequence numbers were never observed. Injection shapes and their sequencing live in
[tcp-client-close-injection.md](./tcp-client-close-injection.md).

## Never Blocks The Pump

Setup work is admitted, bounded and deferred; the packet path never waits for it. The bounds are
specific to each subsystem and documented there — the UDP setup queue, its global byte budget and its
TTL in [udp-flow-setup.md](./udp-flow-setup.md), the TCP pending-SYN index in
[tcp-syn-setup-admission.md](./tcp-syn-setup-admission.md), and deferred process attribution in
[traffic-policy-lifecycle.md](./traffic-policy-lifecycle.md), "Process Attribution".

## Configuration Diagnostics

- `ConfigurationLoader.TryParse` maps a `JsonException` to the serializer-supplied JSON path
  **re-rooted at the section the document was validated from** — `$.TcpFlowCapacity` becomes
  `WinForward.TcpFlowCapacity`, and a pathless failure becomes `WinForward` — plus a fixed diagnostic
  template. It must not append `JsonException.Message` or raw JSON values: malformed input can carry
  credential-like data.
- `ConfigurationLoader.TryValidate` reports invalid or null collection elements at their indexed field
  paths and returns validation diagnostics. Valid JSON must never escape as a null-reference failure.

## Common Mistakes

- Keying UDP state by PID, DNS transaction ID, or only the local port mixes independent datagrams.
- Returning an existing association solely because its relay alias matches can cross-wire two original
  flows. Alias sharing is rejected for exactly this reason: it makes reverse routing nondeterministic.

## Where The Rest Lives

| Subsystem | Error contract |
|---|---|
| Flow policy, non-flow frames, self-traffic | [traffic-policy-lifecycle.md](./traffic-policy-lifecycle.md) |
| Idle expiry, sweep failure isolation | [idle-expiry-sweep.md](./idle-expiry-sweep.md) |
| TCP redirect, relay, close injection | [tcp-local-redirect.md](./tcp-local-redirect.md) (hub), [tcp-client-close-injection.md](./tcp-client-close-injection.md) |
| UDP relay, setup bounds, session lifetime | [udp-relay.md](./udp-relay.md) (hub), [udp-flow-setup.md](./udp-flow-setup.md), [udp-session-lifecycle.md](./udp-session-lifecycle.md) |
| NDIS capture, degradation, recovery | [windows-ndisapi.md](./windows-ndisapi.md) |
| Lifetime, quiescence, teardown joins | [async-lifetime.md](./async-lifetime.md) |
| Packet path, attribution, allocation | [hot-path.md](./hot-path.md) |
