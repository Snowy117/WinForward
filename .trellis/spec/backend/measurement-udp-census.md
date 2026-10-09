# UDP Source Census

> What `udpSummary.sources[]` and `sourceOverflow` count, and the capacity behind them: the per-interval
> 64-slot table, its reclaim rule, the bounded boundary effect and the claim handshake. Read it before
> changing `Target/SourceCensus.cs`, the summariser's interval, or the `udpSummary` writer. Family hub:
> [measurement-harness.md](./measurement-harness.md).

## What the two readings mean

`udpSummary.sources[]` is what the *interval* saw, keyed by endpoint `(address, port)`, and
`sourceOverflow` is what the interval could not place; both readings are unchanged by T2 (2026-10-08).
`sources` itself is always written: an interval that saw no datagram publishes `[]`.

## Capacity is the interval, not the process

- One fixed table of 64 slots per receive loop (`SourceCensus.SourceCapacity`). A datagram takes the slot
  of its `(address, port)` when its endpoint already holds one, and otherwise the lowest-numbered slot
  **no datagram of the current interval has touched yet** — the idleness clock restarts at every
  `Harvest`, so every slot is claimable at the moment an interval opens.
- A claim that changes hands starts its count over, so the delta `Harvest` publishes is still the
  interval's own. The mechanism is stated at length in `Target/SourceCensus.cs:8-37`; this document owns
  the contract.
- Why: the previous behaviour — "an endpoint keeps its slot for the life of the server" — made the
  table's capacity the process's lifetime distinct endpoints, so a campaign that outlived 64 endpoints
  went blind **permanently** (E5-b's ledger held exactly 64 source endpoints and then published
  `sources: []` with `received == sourceOverflow` for 42 minutes).
- **The trade-off, stated.** A slot the opening interval has not touched yet may be handed to another
  endpoint, so an endpoint that appears once and never again is no longer listed in later intervals (it
  never had a non-zero delta there anyway), and an endpoint that keeps arriving can lose its slot to a
  fresh endpoint that arrives before its own first datagram of the interval. That costs it its running
  count, not its record: its next datagram claims a fresh slot, and that claim's whole count is published
  as this interval's datagrams under the endpoint's own key, because `sources[]` is keyed by endpoint and
  not by slot.
- `sourceOverflow` therefore describes an interval that touched more than 64 endpoints on one loop — the
  slots go to the **first** 64 endpoints of the interval, not to the busiest — rather than a census that
  is dead for the rest of the run. A larger table was rejected: it only postpones the same blindness and
  multiplies the per-datagram scan.

## The bounded boundary effect

A re-claim overwrites the previous claim's count, so a datagram that lands in the summariser's read window
of the closing interval can be lost with it. Measured at campaign shape (8 loops, 60 continuous + 400
historical + 100 probe endpoints, 32,040 datagrams): one datagram, i.e.
`received = censused + sourceOverflow + 1`. That is inside the analyzer's datagram band (1 %, or one
second of the arm's own rate) and is the price of a bounded table. The window scales with how often
`Harvest` runs against how long an endpoint lives, so **the 1 Hz summary interval is part of this
contract, not an implementation detail to shorten casually** (`Target/UdpEchoServer.cs:14`).

## The claim handshake

One receive loop writes a table and the summariser is the only reader. A claim publishes `_claim` last
with a release `Volatile.Write`, so a reader of that value sees the identity and count written before it;
a re-claim writes `_claim = 0`, crosses `Interlocked.MemoryBarrier()`, then moves the identity and
publishes the new claim. `Harvest` reads `_claim`, the identity and the count, then `_claim` again, and
leaves a slot claimed under the read to the next interval. The invariant worth keeping in view: **a torn
claim is never published as one endpoint's address beside another's count**
(`Target/SourceCensus.cs:24-30`). No allocation, delegate or LINQ is on the datagram path; the only added
work is one volatile read of the interval's epoch per datagram.

## Tests Required

`SourceCensusTests` holds all four facts:

- a continuing endpoint is published as the interval's own delta across intervals;
- a wave no datagram touched for an interval is reclaimed whole by the next wave (the fact fails against
  a census that never reclaims);
- a full table reports the interval's unplaced datagrams as `sourceOverflow`;
- a summariser running on another thread beside the receive loop publishes every recorded datagram
  exactly once, under its own port, with nothing unplaced.
