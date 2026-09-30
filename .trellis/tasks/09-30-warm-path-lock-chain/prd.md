# F2 warm-path lock chain: lock-free resolve, self-traffic reorder, UDP ready path

Parent: `08-30-proxy-perf-stability`. Finding F2 of the archived research
`09-29-tcp-udp-path-structural-perf` (`research.md` §F2; addendum §A2 and §A4 items 1–4). This is step 3
of the operator's F2–F8 pipeline; its predecessor F3 (`09-30-expiry-sweep-bounded-pause`) landed the
bounded-hold sweeps and deferred the activity-bucket representation to this task.

## Problem

One warm packet on an established flow pays a chain of process-wide locks and dictionary probes:

| # | Site | Cost today |
|---|------|-----------|
| 1 | `SelfTrafficRegistry.IsOwned` (`src/WinForward.Runtime/SelfTrafficRegistry.cs`) — called **before** the flow lookup on the warm entry (`FlowDispatcher.cs:159`) | 1 global lock + up to 4 dictionary probes |
| 2 | `FlowTable.TryResolve` (`src/WinForward.Core/FlowTable.cs`) | 1 global lock + 1–2 probes + `Touch`, which calls `TimeProvider.GetUtcNow()` on **every** hit |
| 3 | TCP: `TcpRedirectTable.IsReverseCandidate` then `TryResolveByOriginal` (`src/WinForward.Runtime/TcpRedirect/TcpRedirectTable.cs`) | the same `_gate` entered **twice** per packet |
| 4 | UDP: coordinator `_gate` (cooldown + session probe) → session `_activityGate` → transport `_sendGate` semaphore (`src/WinForward.Runtime/UdpProxy/*`) | lock + lock + semaphore per datagram |

Measured by the benchmark-coverage task (`benchmarks/results/2026-09-29-benchmark-coverage/scaling-contention.jsonl`):
throughput **falls** as workers are added — scaling ratio 0.158 (fake self-traffic guard) and 0.203 (real
registry) at four threads, against 1.0 at one thread. The real guard costs ~2× the fake one
(3.33 M vs 6.77 M resolutions/s ≈ +150 ns per lookup), so every existing dispatcher row built on the fake
guard reports a constant zero delta for the self-traffic reorder.

## Requirements

1. **The warm resolve takes no process-wide lock.** On a hit, a TCP redirect packet and a UDP datagram must
   resolve their flow without acquiring the flow table's, the registry's or the redirect table's global
   gate. Per-slot/per-stripe synchronisation is allowed; a global lock is not. Proof is structural and
   exact (a concurrent warm resolve completing while the global gate is held by another thread), not a
   timing series. The mechanism is a **concurrent index** (`ConcurrentDictionary` or better): reading a
   plain `Dictionary` concurrently with a `Resize` is provably unsafe — the runtime publishes `_buckets`
   before `_entries` and `FindValue` throws `InvalidOperationException` on the torn chain, which on a pump
   thread tears capture down process-wide (evidence recorded in the task's research notes).
2. **Self-traffic leaves the warm path without weakening loop prevention.** The warm hit must not run the
   **exact-tuple** half of `IsOwned`: the full check runs once at claim time and a claimed state is the
   "proven not self" record. The **wildcard relay-socket half stays on the warm path**, answered lock-free
   (registry writes are cold, so a concurrent-index probe satisfies requirement 1's no-global-lock clause):
   dropping it breaks the loop-prevention contract in `.trellis/spec/backend/traffic-policy-lifecycle.md`
   — a host flow's ephemeral port recycled to the relay's control socket is registered as a wildcard
   `(Tcp, Any:P, proxyEndpoint)` tuple *before* its SYN (`TcpProxyRelay.cs:40-44`), and a warm hit on a
   stale state for that port would redirect WinForward's own control connection recursively. The design
   must state which half is deleted, which half stays, and prove the retained half with a red-before /
   green-after fact (a relay wildcard tuple is never proxied on a warm hit).
3. **Activity time is lock-free and clock-call-free.** `Touch` becomes one volatile store of a bucketed
   value; the per-hit clock call disappears. This lands the bucket representation F3 deferred, honouring
   the seven-item contract in
   `.trellis/tasks/archive/2026-09/09-30-expiry-sweep-bounded-pause/research/implementation-notes.md` §8,
   and it unifies the `FlowState.Reset` clock source (defect D9 recorded there). The quantised consumers are
   named explicitly: the flow table's warm touch and sweep, the TCP redirect association's activity stamp
   (which also removes its per-packet clock read), and the UDP session's activity stamp (F3 contract item 5
   had the UDP session unchanged; this task changes it, and `.trellis/spec/backend/traffic-policy-lifecycle.md`
   is updated accordingly). **Sanctioned resolution of that contract's contradiction**: its two halves ("≥ 1 s" and "≤ idleTimeout/8") cannot both hold once
   the 5 s UDP session floor is the denominator (5/8 = 0.625 s), so the bucket is **500 ms** and the
   binding half is "at least eight buckets per retention window". Bucket granularity shifts retirement
   **never early and by at most one bucket (500 ms) late** — the retention contract in requirement 6 is
   read with that tolerance.
4. **TCP pays one gate entry per packet** for the reverse-candidate + resolve pair; the reverse alias and
   the cross-adapter aliasing semantics stay exactly as they are.
5. **UDP ready path is wait-free**: the session lookup + ready check takes at most one `TryEnter`-style
   attempt (no unbounded wait) and cooldown reads are lock-free; admission of a new session may still
   serialise.
6. **Semantics preserved**: exactly-once claim, fail-closed capacity, expiry/retention semantics,
   reverse/cross-adapter aliasing, self-traffic correctness (a self tuple must never be routed as a
   proxied flow), and the 0 B steady-state allocation contract on the warm path (dispatcher warm-path
   gate included).
7. **Existing gates stay green**: the full suite, `HotPathAllocationGateTests`, the F3 sweep gate matrix
   (its chunked round and `_liveStates` registry must survive the resolve-path change), gc-soak shape
   anchors, and the UDP retention/burst scenarios.

## Acceptance Criteria

- [ ] **Scaling (series, one line).** The `scaling` scenario gains a **warm arm** — the post-reorder
      production shape, i.e. per-lookup unit = concurrent-index resolve + the lock-free wildcard guard, with
      the registry populated but no per-lookup exact-tuple `IsOwned` call. Acceptance: the warm arm's
      **self-normalised** four-thread ratio is ≥ 0.6 over 3 runs with its one-thread arm ≥ 3.00 M/s; the
      ratio against the recorded one-thread baseline (3,331,758/s ⇒ ≥ 7,996,220/s at four threads) is
      recorded beside it as the comparability reading. The scenario's arm-count arithmetic (`windowSeconds`
      assumes two arms) must be corrected so `--quick --duration 45` still means what it says.
- [ ] **No global lock on the warm resolve (exact).** A test proves a warm TCP redirect resolve and a warm
      UDP ready-path resolve both complete while another thread holds the corresponding global gate; the
      same test fails against the pre-change code (recorded red).
- [ ] **Self-traffic reorder (exact).** An instrumented counter shows zero **exact-tuple** guard probes on
      warm hits and exactly the claim-time probes on misses, **plus** the retained wildcard half's own
      fact: `ARelayWildcardTupleIsNeverProxiedOnAWarmHit`, red before the change and green after.
- [ ] **Clock call gone (exact + series).** `Touch` performs no clock read (proven by an injected clock that
      throws when read on the warm path) and the `ReadActivityClock` benchmark row is retired or recorded
      as obsolete.
- [ ] **UDP ready path (series, one line).** `udp-ready-path-contention`'s four-worker `ReadySend` mean is
      ≤ 261.7 ns (≥ 2× the recorded 523.3 ns) over 3 runs, with the one- and two-worker rows recorded
      beside it. The added "4/1-worker ratio ≤ 1.2" condition is **dropped**: it is not in this PRD, and the
      ratio is a process-level BenchmarkDotNet host metric whose recorded value is already 1.45.
- [ ] Release build zero-warning, full suite green, `dotnet format --severity info --verify-no-changes`
      empty output, `jb inspectcode` zero `<Issue>`.
- [ ] Benchmark data supporting each claim recorded under `benchmarks/results/2026-09-30-warm-path-lock-chain/`
      and cited in the task record before archive.

## Notes

- **Sanctioned semantic delta (requirement 2)**: with the warm-entry `IsOwned` gone, a tuple that is
  registered as self *after* its flow was claimed keeps being proxied until that flow expires, where today
  the next packet would divert. The claim-time check still runs before every claim, so a self tuple can
  never be *admitted* as a proxied flow; the window only affects an already-claimed flow. The design
  records the registration-ordering contract that bounds it.
- Deliberately **out of scope**: the sharded FlowTable rebuild of addendum §A4 items 2/6 (grow-only slab +
      per-shard open-addressing index, incremental rehash). That is roadmap item 7, a data-structure
      rebuild, and this task must reach a lock-free *read* path without it. If the design concludes the
      read path cannot be made lock-free within the existing index shape, that conclusion and its evidence
      are the deliverable, and the PRD's requirement 1 is restated rather than quietly dropped.
- The F3 sweep's `_liveStates` registry, `_gate` and `SweepHoldProbe` are part of the contract this task
      must keep working; F3's minimal-granularity round and its exact work-per-hold gate must stay green.
- Dead-end candidates to check before designing: lock-free reads over a plain `Dictionary` (torn reads are
      a real hazard); caching "not self" without the claim-time check (the F3/research A4 note says self
      tuples must not be cached across claims); per-stripe locks without a direction-normalised hash
      (reverse packets must land on the same stripe).
