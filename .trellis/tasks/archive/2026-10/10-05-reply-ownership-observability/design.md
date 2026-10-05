# Design — reply-ownership observability

## Boundary

The check lives in `UdpProxySession`'s receive classification, the only place holding both operands. It
must not move into `Socks5UdpTransport`, the association pool or the relay: those produce the delivery
behaviour under observation, and a component that both causes and judges an event cannot report it
honestly.

Nothing downstream changes. `InjectResponseAsync` receives the same source and payload, and
`UdpResponseReinjector` builds the same frame.

## Data flow

```
ReceiveLoopAsync
└─ _transport.ReceiveAsync             → Socks5UdpReceiveResult (decoded relay datagram)
   └─ TryGetReceiveSource               → Endpoint source = the reply's declared server address
      ├─ NEW: source != Flow.Remote ?
      │        ├─ RuntimeCounters.Shared.Increment(udpResponseSourceMismatch)  ← unconditional
      │        └─ rate-limited `udp.response.foreign_source` warn              ← throttled only
      └─ InjectResponseAsync(source, …) → unchanged; the reply is still delivered
```

`TryGetReceiveSource` already returns the decoded `Endpoint` (`UdpProxySession.cs:405-412`) and `Flow` is
a session field, so the comparison needs no new plumbing.

## Contracts

- **Counter**: `RuntimeCounters.UdpResponseSourceMismatch = "udpResponseSourceMismatch"`, cumulative, in
  the same family as `udpOriginUnresolved` / `udpFailClosedDrop`. It reaches operators automatically
  through `RuntimeCounters.Snapshot()` (`RuntimeCounters.cs:113`) and the heartbeat; there is no
  registration list to forget.
- **Event**: `udp.response.foreign_source` at Warn with `destination` (the flow's own), `source` (the
  reply's declared), `origin`, `originKind` and the association generation. Throttled with the same
  CAS-on-ticks shape as `MaybeLogSkipSummary` (`UdpProxySession.cs:472-479`).
- **Cumulative, not windowed**: the log path must not reset this counter. The skip counters are window
  deltas that `MaybeLogSkipSummary` deliberately drains; this one is a lifetime total read from the
  heartbeat.

## Why count-only

- `RuntimeCounters` states its own contract: counters "must never influence packet disposition,
  fail-closed, relay, or shutdown decisions" (`RuntimeCounters.cs:6-14`). Dropping on a counter's verdict
  would contradict the type's purpose.
- A mismatch is not proof of an invalid reply. `udp-relay.md:11` refuses exact source equality on the
  relay side precisely because RFC 1928 does not pin the reply source, and some protocols continue on a
  new endpoint by design (TFTP's transfer port is the classic case).
- The count is the measurement that makes the drop question decidable. Shipping the drop first would
  remove the evidence and change behaviour in the same step.

**Consequence to guard against**: do not route this through `RecordSkippedDatagram` or add it to
`Socks5UdpReceiveSkipReason`. That path means "not delivered", and reusing it would silently convert the
observation into a drop.

## Blind spot

Two flows targeting the same destination are indistinguishable by address: a reply delivered to the wrong
one carries the source the receiving flow also expects. The counter therefore observes cross-destination
misdelivery only. **A zero does not mean sharing is safe**, and `README.md` plus `udp-relay.md` must say
so rather than letting a reader infer safety from silence.

## Cost

One `Endpoint` comparison — a `readonly struct` implementing `IEquatable<Endpoint>` (`Domain.cs:32-56`),
so no allocation and no boxing — plus one `Interlocked.Increment` on an existing key. The receive loop
already builds that `Endpoint` today. The warn is off the steady path: when the window has not elapsed,
the only per-reply work is a ticks read and compare.

## Compatibility and rollback

Additive: one new counter key and one new log event. No existing field, semantic or configuration key
changes, so existing configuration files and log consumers are unaffected. Reverting the commit removes
the counter and the event with no other effect; the key simply stops appearing in snapshots.
