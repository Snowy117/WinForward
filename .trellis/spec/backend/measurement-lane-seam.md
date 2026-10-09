# Measurement Lane Seam (engine / policy / transport)

> Who owns each counter between the engine, the policy and the transport, the receive-kind vocabulary,
> and the thread contract that decides when a count may move. Read it before adding a counter to an
> arm's send or receive loop, changing a lane's deferral or settlement path, or changing what a
> transport reports. Family hub: [measurement-harness.md](./measurement-harness.md). The keys these
> counters publish are declared by [the record contract](./measurement-record-contract.md).

## The three collaborators

An arm's send and receive loops live behind three collaborators (`Client/Lanes/`):

| Collaborator | Owns | Must not |
|---|---|---|
| `ILaneTransport` (adapter) | connect (`OpenAsync` returns a result, never throws), one send, one receive | judge wire validity, dispose the policy/engine |
| `ILanePolicy` | window admission + in-flight, frame building, reply classification, every receive-side counter | count on the receive thread, block, allocate in `BuildRequest` |
| `LaneEngine<TTransport>` | pacing, the offer loop, the bounded defer queue, the grace drain, the **send-side** counters (`LaneCounts`) | keep a second copy of the window or of any policy counter |

`LaneReceiveKind ∈ {Payload, EndOfStream, Malformed, IOError}` describes what arrived, not whether it is
valid. The TCP adapter maps the frame reader's statuses (`Client/Lanes/TcpLaneTransport.cs:110-152`):

- `Payload` → `Payload`; `EndOfStream` → `EndOfStream`;
- `BadChecksum` → `Malformed` (counted, keep reading: the boundary was still readable, so the next frame
  can be found);
- `BadMagic`, `BadLength` and a peer close inside a frame (`Truncated`) → `IOError` (the frame boundary
  is gone and no later message can be framed, so the lane stops);
- an oversized datagram becomes `Malformed` with `FrameDecodeError.Truncated`
  (`Client/Lanes/UdpLaneTransport.cs:113`), never a silent "corrupt".

## The thread contract

1. the **receive** thread decodes, classifies and enqueues a settlement carrying `ReceivedTicks` — no
   counters;
2. the **send** thread calls `Settle(now)` after `Pacer.WaitUntil` and before the next `BuildRequest`,
   and that is the only place pending removal, RTT (from `ReceivedTicks`, never from the settle time),
   histograms, `inFlight--` and receive-side counters change;
3. at arm end: stop offering → bounded grace (keep receiving and settling until the book is empty or the
   drain limit) → cancel and join the receive loop → a final `Settle` → compute gates and metrics.

## Per-slot sequence, deferral and counters

- Per-slot sequence is strictly serial (`BuildRequest → SendAsync → OnSent`, `OnSent` before the next
  `BuildRequest`), and `BuildRequest` is idempotent: a deferred slot is retried with its original
  sequence and intended instant, once per slot in `Supplied`.
- `WouldBlock` is sampled from `IsCompleted` **before** the await, and a would-block that ends in an
  asynchronous failure increments both `SendWouldBlock` and `SendFailures`.
- `DeferredQueued` counts a slot once — the retry of an already queued intent is not a new deferral —
  and `DeferredDropped` is a subset of it. `DeferredPending` (queue occupancy at teardown) is what
  completes `outstandingAtTeardown`; never derive it by subtracting the other counters, which only holds
  when a policy never returns `Skip`. `ScheduleTruncated` says the offer loop stopped before its
  deadline — cancelled, failed, or never connected.
- A policy states a count as a property, never under a `LaneCounts` member's name: the send-side
  counters are the engine's, and a policy that kept a second copy of one would be two truths about one
  number (`Client/Lanes/ILanePolicy.cs`).

#### Wrong — the seam copies the bookkeeping
```csharp
if (++_inFlight > window) { _deferred.Enqueue(frame); }   // the engine keeps a window counter the policy also owns
policy.OnReply(payload, Clock.Now);                       // receive thread books RTT and counters
```
One writer per quantity: the window is the policy's, the send-side counters are the engine's, and the
receive thread settles nothing (`policy.Settle(Clock.Now)` is the only settlement point, and it runs on
the send thread).

## Tests Required

- **With a fake transport** (`tests/WinForward.E2E.Tests/Lanes/*`): send outcomes, would-block sampled
  before the await (two facts, one per mutation), defer FIFO with a retry that does not re-count
  `Supplied`, the three grace exits, settlement carrying the receive time, a concurrency identity
  (`delivered == sent == settled`, each sequence exactly once) and a zero-allocation gate whose
  counter-proof fails when `BuildRequest` allocates.
- **With a real transport**: the same gate on the sending path, and the send path must keep using the
  `ReadOnlyMemory` overload — `byte[]`/`ArraySegment` binds an overload that allocates per call, which
  the fake gate cannot see.
- **Ownership and mapping**: `LaneCountsDisjointnessTests` holds the one-truth-per-number rule against
  both the fake and the latency arm's real policies, and `LaneTransportTests` holds the receive-kind
  mapping above (an oversized datagram is `Malformed` with `FrameDecodeError.Truncated`, a bad checksum
  is not terminal, a lost frame boundary is).
