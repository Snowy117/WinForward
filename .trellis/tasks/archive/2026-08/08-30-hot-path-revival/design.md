# Design: Hot dispatch path revival

Baseline evidence: research `socks5-capture-dispatch.md` B.2.1 (X1, spot-verified) +
PRD. Re-verified against current source 2026-08-30 (post-fast-hardening): all line
numbers below are current.

## Current state (verified)

- `FlowDispatcher.cs:129`: `if (_logger.IsEnabled(RuntimeLogLevel.Trace) || _reverseHandler
  is not null) return DispatchSlowAsync(...)`. Production (`WinForward.Cli/Program.cs:271`)
  always wires `reverseHandler: tcpCoordinator.HandleReverseIfApplicableAsync`, so the
  non-async warm entry (lines 131–153) is dead for 100% of production packets.
- The reverse handler is wired as a bare `Func<CapturedFlowPacket, CancellationToken,
  ValueTask<TcpRedirectOutcome>>`; its TCP-only gate lives inside the handler
  (`TcpProxyCoordinator.cs:265-269`, H1/M5).
- Reverse candidacy is keyed by the full endpoint pair:
  `TcpRedirectTable.IsReverseCandidate(local, remote)` probes
  `_byReverse[ReverseRedirectTuple(ReverseSourceEndpoint, ReverseDestinationEndpoint)]`
  (`TcpRedirectTable.cs:219-222`). `ReverseSource` carries the listener port; a reverse
  candidate's `key.Local.Port` (source port) is always an active (or tombstoned) listener
  port.
- Warm entry already carries the UDP-reverse special case (`:142`, `IsReverseOf` → slow
  path) — UDP is fully safe on the warm entry today.
- Benchmarks: `DispatcherBenchmarks` warm shapes are measured in handler-less
  compositions — they prove nothing about the production path (this is how X1 stayed
  invisible).

## D1. `ITcpReverseHandler` — move the diversion knowledge into the handler

The dispatcher must not learn reverse-handler internals (protocol gate, port
prefilter). Replace the bare `Func` with an internal interface:

```csharp
internal interface ITcpReverseHandler
{
    /// True when the packet MIGHT belong to a redirect reverse leg, so the dispatcher
    /// should divert it to the slow path where the handler runs. MUST be conservative:
    /// a false answer skips only the proactive diversion; miss-fallthrough packets
    /// still reach the handler on the slow path, so false must imply the full handler
    /// (IsReverseCandidate + tombstone) would find the packet NotRelevant **on the
    /// resolved-flow shapes** (see the fall-through theorem D3).
    bool WantsPacket(in CapturedFlowPacket packet);

    ValueTask<TcpRedirectOutcome> HandleReverseIfApplicableAsync(
        CapturedFlowPacket packet, CancellationToken cancellationToken);
}
```

- `TcpProxyCoordinator` implements the interface (the method already exists with the
  exact signature; only `WantsPacket` is new).
- `FlowDispatcher._reverseHandler` becomes `ITcpReverseHandler?`; `TryHandleReverseAsync`
  calls `HandleReverseIfApplicableAsync` on it; the constructor parameter changes type
  (Program.cs wiring is unchanged — the coordinator instance already satisfies it; test
  fakes that passed lambdas get a small fake object instead).
- Warm entry:

```csharp
if (_logger.IsEnabled(RuntimeLogLevel.Trace)) return DispatchSlowAsync(packet, ct);
if (_reverseHandler is { } handler && handler.WantsPacket(packet))
    return DispatchSlowAsync(packet, ct);
```

### WantsPacket staged implementations

- **Stage (a)**: `key.Protocol == TransportProtocol.Tcp` — immediate relief for UDP and
  all non-TCP; no correctness argument needed beyond the handler's own H1/M5 gate
  (a false answer is exactly what the handler's first line already proves).
- **Stage (b)**: `key.Protocol == TransportProtocol.Tcp &&
  _table.IsReverseCandidatePort(key.Local.Port)` — the prefilter (D2). With no live
  listeners the answer is false for every TCP packet, restoring the warm entry fully.

`TryHandleReverseAsync` keeps calling the FULL handler (protocol gate +
`IsReverseCandidate` + tombstone check) — `WantsPacket` is never used there. The slow
path must stay complete for miss-fallthrough packets (tombstone stragglers rely on it).

## D2. The prefilter: reference-count array on `TcpRedirectTable`

- `TcpRedirectTable` grows `internal bool IsReverseCandidatePort(ushort port)` backed
  by `int[65536] _candidatePorts`:
  - `TryClaim` success (association added, still under `_gate`, before the caller
    rewrites+injects the SYN): `Interlocked.Increment(ref _candidatePorts[listenerPort])`.
  - `TryRemove` removal (under `_gate`): `Interlocked.Decrement`.
  - Query: `Volatile.Read(ref _candidatePorts[port]) != 0`.
  - Deviation from the PRD's "65536-bit bitmap, atomically swapped": a count array has
    no snapshot copies, no CAS loops, and no torn-read surface; 256 KiB resident is
    negligible. The AC3 behavioral contract (no torn reads, no missed candidates during
    swap) holds by construction.
- Registration ordering: the increment completes inside `TryClaim` before
  `TcpRedirectSetup` injects the rewritten SYN — the listener's SYN-ACK (first reverse
  packet) can never arrive before the port is visible. Decrement happens in `TryRemove`;
  post-teardown stragglers rely on the fall-through theorem (D3).
- Query direction: src port only — `ReverseRedirectTuple.Source` carries the listener
  port, so `key.Local.Port ∈ listener set` is the exact necessary condition for
  candidacy.

## D3. Safety theorem (why misses cannot misroute)

A reverse-candidate packet has `key.Local.Port` = listener port. If a TCP packet's src
port is NOT in the active set, it cannot match `_byReverse`. Even when the prefilter
misses a true candidate (tombstone window: port decremented, grace window open), the
warm entry's `_flows.TryResolve` cannot resolve the listener-shaped tuple
`(listener-ip:port -> client:port)` — exact/reverse/origin-flipped/adapter-agnostic
modes all key on the original flow's endpoint pair — so the packet falls through to
`DispatchSlowAsync`, where `TryHandleReverseAsync` runs the full handler
(`IsReverseCandidate` miss → tombstone `TryHit` → `Dropped`). **Prefilter misses degrade
to today's behavior (slow path + grace), never to wrong routing.** Known ignorable
corner: a stored flow whose real server endpoint (IP and port) equals a listener
endpoint could resolve via the reverse mode — requires the original server on-host with
a colliding ephemeral port; not a proxy-able shape.

## D4. Benchmarks must measure the production path (user requirement, do FIRST)

- `DispatcherBenchmarks` gains production-composition variants: dispatcher constructed
  with a wired `ITcpReverseHandler` whose `WantsPacket` runs the REAL predicate logic
  (stage (b): protocol + `TcpRedirectTable`-backed port array — `TcpRedirectTable` is
  pure managed, usable directly in benchmarks) and whose
  `HandleReverseIfApplicableAsync` returns `NotRelevant` (warm packets never call it).
- Variants: `WarmPassProduction` / `WarmProxyProduction` (non-candidate TCP + UDP
  shapes) — these MUST show the warm 160 B figure after the fix, and will show the
  slow-path figure before it (record both); optional `ReverseCandidateSlowPath`
  (candidate src port) as the diverted control.
- Execution order: land the benchmark variants FIRST (they compile against the current
  Func-based composition via a small fake — or land them together with the interface
  change if the fake is simpler that way), record the pre-fix numbers as the evidence
  baseline, then land (a)/(b) and flip the numbers.

## What must NOT change

- `DispatchSlowAsync` ordering: self-traffic → reverse → flow resolve → attribute →
  claim; `TryHandleReverseAsync` calls the full handler (never `WantsPacket`).
- The executor seams (`ProxyAsync` routes to `TcpProxyCoordinator.HandlePacketAsync`,
  which retains its own internal `IsReverseCandidate` check at `:299` — an independent
  second net for proxy-decided flows).
- UDP `IsReverseOf` special case on the warm entry (`:142`).
- Trace-on → everything slow (diagnostics).

## Tests

- Interface: fake handler recording `WantsPacket`/`Handle` calls; coordinator
  `WantsPacket` unit tests (stage a and b semantics, port visibility before injection).
- Prefilter (`TcpRedirectTable`): Inc on claim / Dec on remove / concurrent claims /
  `Volatile` read transitions.
- Dispatcher integration: candidate src port TCP → diverted, handler invoked;
  non-candidate TCP + UDP with handler wired → warm decision executed, handler NOT
  invoked; handler-null composition unchanged; tombstone-straggler shape (listener
  tuple, port removed) falls through to slow path → `Dropped` (pins the theorem).
- Regression: existing dispatcher/coordinator suites stay green (slow-path shapes
  behavior-zero); production-composition benchmark = warm allocation (AC4).

## Tradeoffs / alternatives

- **Keep the bare `Func` + separate predicate parameter**: rejected (user direction) —
  two loose delegates scatter the handler's contract; the interface keeps protocol +
  prefilter knowledge inside the handler.
- **Use `WantsPacket` inside `TryHandleReverseAsync` too**: rejected — would skip the
  tombstone check for miss-fallthrough packets and change straggler behavior.
- **Delete the diversion entirely** (rely on the fall-through theorem): rejected — makes
  warm-entry safety depend on FlowTable resolution-mode details; the prefilter keeps
  the argument local to the reverse table.
- **Port bitmap with atomic snapshot swap** (PRD wording): rejected for the count array
  (no copies, no CAS); same contract strength.

## Rollout

Benchmark variants → (a) → (b), separate commits, each independently production-safe.
Rollback = revert. No config surface.
