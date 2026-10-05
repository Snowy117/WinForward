# Reply-ownership observability: count replies whose declared source is not the flow's destination

Parent: `10-05-udp-association-sharing-correctness` (R2).

## Goal

Make a misdelivered relay reply visible in the shipped product.

`UdpProxySession.TryGetReceiveSource` decodes a reply's declared source address
(`UdpProxySession.cs:395-414`) and `InjectResponseAsync` hands it straight to the reinjector as the
injected frame's source (`:417-421`). Nothing compares that source to `Flow.Remote`. A reply the server
delivered to the wrong flow is therefore injected toward that wrong flow's client as a legitimate
frame, and no counter, log line or metric records that it happened.

The harness work (`10-05-harness-response-ownership`) measured how large that event stream is on the
test stand-in — 92.1 % of burst replies arrived on a flow other than their sender on the pre-change
build — and closed the harness's own blind spot. This task closes the product-side one: it makes the
same class of event countable in production, which is also the evidence an operator needs to decide
whether their association-sharing configuration is safe for their traffic.

## Confirmed facts

- The comparison's two operands are already in hand at the receive path: the reply's declared source is
  decoded into an `Endpoint` (`UdpProxySession.cs:405-412`) and the flow's own destination is
  `Flow.Remote`. Both are `Endpoint`, a `readonly struct` implementing `IEquatable<Endpoint>`
  (`src/WinForward.Core/Domain.cs:32-56`), so the comparison adds no allocation.
- The counter mechanism is `RuntimeCounters.Shared.Increment(...)` with product-level names, and the
  UDP family already has same-shaped members: `udpFailClosedDrop`, `udpOriginUnresolved`,
  `udpAssociationFallbacks` (`src/WinForward.Runtime/RuntimeCounters.cs:20-80`).
- **A mismatch is not proof of an invalid reply.** `udp-relay.md:11` already establishes that UDP reply
  sources are not pinned — the relay-side validation is deliberately port + address-family rather than
  exact `IPEndPoint`, because RFC 1928 does not pin the reply source and multi-homed/anycast relays
  answer from a different address. Some protocols also legitimately continue on a new endpoint (TFTP's
  transfer port is the classic case). So the *count* is sound; treating the mismatch as an error is a
  separate, riskier decision.
- **The check is blind to same-destination misdelivery.** When two flows share an association and target
  the same destination, a reply delivered to the wrong one carries the source the receiving flow also
  expects, so no address comparison can separate them. The counter detects cross-destination
  misdelivery only, and that boundary has to be stated wherever the counter is documented.
- Rate-limited structured events have local precedent in the same file: `RuntimeLogThrottle` plus an
  `_logger.Event(..., "udp.reinject.drop", ...)` shape (`UdpResponseReinjector.cs:273-284`).
- The receive loop already classifies and counts per-datagram anomalies without tearing a session down
  (`Socks5UdpReceiveSkipReason`, `UdpProxySession.RecordSkippedDatagram`), so a new counted condition
  fits the existing shape rather than inventing one.

## Requirements

- R1 — Count every reply whose declared source does not equal the flow's own destination, in the
  product, under a product-level counter name alongside the existing UDP counters.
- R2 — Do not change what is delivered. A mismatched reply is still injected, exactly as today; turning
  the count into a drop is a separate decision that needs its own evidence and its own task.
- R3 — Emit a rate-limited structured warning carrying enough context to act on: the flow's destination,
  the reply's declared source, the association generation, and the origin kind. The counter is
  unconditional; only the log line is throttled.
- R4 — Record the boundary in the docs and in the spec: the counter observes cross-destination
  misdelivery and cannot observe same-destination misdelivery. A reader must not conclude from a zero
  that sharing is safe.
- R5 — Keep the per-reply cost to one comparison and one interlocked increment. No allocation, no lock,
  no logging on the steady path unless the counter is read.

## Constraints

- The check belongs in the observer (the session's receive classification), never in the relay or the
  association pool: those produce the behaviour being observed.
- No change to the reinjector's frame building, to the injected frame's source, or to session lifetime.
- The counter must not fire on the reply of a flow whose destination is the flow's own — including the
  DNS case where a spoofed source is the flow's original destination by construction.
- Follow the existing counters' naming and registration pattern, including wherever the counter set is
  enumerated for diagnostics.

## Acceptance criteria

- [ ] A unit test injects a reply whose declared source differs from the flow's destination and asserts
      the counter advances, the session survives, and the reply is still delivered.
- [ ] A unit test injects a matching reply and asserts the counter does not move.
- [ ] The warning is rate-limited: a burst of mismatched replies produces one log line per window with
      the counter still counting every one.
- [ ] A test pins the documented boundary — two flows to the same destination with a cross-delivered
      reply do not advance the counter, because the two are indistinguishable by address.
- [ ] The hot-path allocation gate stays green (`tests/WinForward.Performance.Tests`).
- [ ] `README.md` and `.trellis/spec/backend/udp-relay.md` state what the counter observes and what it
      cannot, and that a zero does not prove sharing is safe.
- [ ] `dotnet build WinForward.slnx -c Release` is zero-warning and
      `dotnet test WinForward.slnx -c Release` stays green.

## Open question

- Should the mismatch also *drop* the reply (behind a configuration key, default off), or stay
  count-only? **Recommendation: count-only.** A mismatch is not proof of an invalid reply — the
  repository already refuses exact source equality on the relay side for the same reason, and TFTP-style
  endpoint changes would break silently. Count-only is zero-risk and is exactly what is needed to decide
  the drop question later with data; a drop switch would add a configuration key and a delivery-affecting
  branch to a task whose value is that it changes nothing observable.
