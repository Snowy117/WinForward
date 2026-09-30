# F5 pump I/O shape: speculative batched read and event-driven idle wake

Parent: `08-30-proxy-perf-stability`. Finding F5 of the archived research
`09-29-tcp-udp-path-structural-perf` (`research.md` §F5). This is step 5 of the operator's F2–F8 pipeline;
F3, F2 and F4 are archived and their contracts are the regression surface.

## Problem

| # | Site | Shape today |
|---|------|-------------|
| 1 | `NdisApiDriver.TryReadPackets` (`src/WinForward.NdisApi/NdisApiDriver.cs:142-166`) | `GetAdapterPacketQueueSize` (IOCTL #1) **then**, only when non-empty, the batched `ReadPackets` (IOCTL #2). Under load every batch pays the query; at idle every adapter pays one query per poll cycle |
| 2 | `NdisCapturePump` idle pacing (`src/WinForward.NdisApi/NdisCapture.cs`) | a ~1 ms `Thread.Sleep` poll loop, so idle CPU is spent and per-packet tail latency carries up to a poll period of jitter |

Measured by the benchmark-coverage task (`benchmarks/results/2026-09-29-benchmark-coverage/pump-idle-wake.jsonl`):
about **1.8 % of a core per idle second** and a **0.078 ms median wake**. The read side has **no IOCTL
counting** behind the driver↔native boundary, and the driver exposes **no counters at all** (the
`windows-ndisapi.md` row claiming batched-send telemetry only is stale — planning corrected it), so nothing
existing can stand in for a read-call proof. This task must therefore build the read-call instrument it
needs before it changes the read shape.

## Requirements

1. **One read IOCTL per drain under load, not two.** The drain issues the batched read **first**, so the
   queue-size query disappears from the loaded path (it was IOCTL #1 before every batch). The empty-queue
   case is recognised from the read result **when the pinned ABI carries the answer** (a successful read
   returning zero packets); when the read does not succeed, the query is retained **only** as the
   disambiguator between "empty" and "driver error", reproducing today's fail-closed classification
   state-for-state. Whether the real driver returns success-plus-zero or failure on an empty queue cannot be
   established on this host, so **both branches are implemented and proven at the seam**, and the
   on-Windows experiment that decides which one the hardware takes is recorded as an open item. If neither
   branch can be made safe, the query-first shape stays and that negative result is the deliverable.
2. **Read-call counting exists and is exact, at the driver↔native boundary.** The pump↔driver counting seam
   already exists and proves nothing new; the missing instrument is one layer below it, counting the native
   read and query calls together with the classified outcome. The idle path is judged as an **idle-IOCTL-rate**
   property, not a raw call count: under the read-first shape the idle poll count drops ~88× (877–886 polls/s
   → ~10 waits/s), so even the worst ABI hypothesis (one query per empty read) cuts the idle IOCTL rate ~44×.
3. **Idle wake is event-driven.** The pump waits on the driver's packet-arrival signal (the ndisapi event
   binding) with a bounded timeout instead of sleeping a full poll period, so idle CPU drops and wake
   latency becomes arrival-driven. The wait must be injectable so the pump's idle behaviour is testable
   without a Windows driver, and the disposal/cancellation contract must stay bounded by the timeout.
4. **Burst behaviour unchanged**: the drain-till-empty loop, the batch size, the fail-closed behaviour on
   driver errors, and the single-pump degradation stay as they are.
5. **0 B allocation** on the pump loop (including the idle path) and no new per-poll allocation; the
   existing `IdlePollIterationsAllocateNoManagedBytes` gate and the read-call gates stay green.
6. **Existing gates stay green**: full suite, the F2/F3/F4 facts (warm path, sweeps, key/parse), the
   dispatcher warm-path gate, gc-soak anchors, and the UDP/TCP scenarios.
7. **The Windows-ABI question is recorded, not assumed.** Whatever cannot be verified on this host (the
   real driver's empty-queue semantics, the event binding's behaviour, wake latency on a real NIC) is named
   as an explicit open item for the Windows program with the exact experiment that would close it. One of
   those rows is **acceptance-relevant and must appear in the artifact README**: whether a read requesting
   the **full batch capacity** succeeds with a short count when the queue holds fewer packets than
   requested. If it fails instead, the read-first shape would throw a transient read error, retry, and
   degrade a perfectly working adapter — so that row's fallback is the query-first rollback of requirement 1,
   not a tuning knob.

## Acceptance Criteria

- [ ] **Read shape (exact, seam-level).** A test proves **one batched read call per drain** where the
      pre-change shape issued `[Query, Read]` (red-before recorded on the instrumented pre-change body), and
      that the empty-queue case still yields zero packets with no error and the same classification. The
      residual query is permitted **only** on the non-success path, where the fail-closed equivalence table
      must hold state-for-state; the driver↔native read counters are the instrument, and the existing
      pump↔driver read-call gates stay green.
- [ ] **Idle wake (exact + series).** A test proves the idle wait is a single bounded event wait per idle
      iteration (not a sleep), and the `pumpIdleWake` scenario re-run records idle CPU and wake latency
      against the baseline series under `benchmarks/results/2026-10-01-pump-io-shape/`.
- [ ] **Allocation.** The idle poll gate and the drain paths allocate 0 B.
- [ ] **Windows items recorded.** The artifact README names every on-Windows verification this change
      depends on (with the experiment, the expected observation and the fallback if it fails) — including the
      **partial-batch** row of requirement 7, whose failure means the query-first rollback rather than a
      retry-and-degrade — and the PRD's requirement 1 fallback is honoured if the ABI cannot support the
      speculative read.
- [ ] Release build zero-warning, full suite green, `dotnet format --severity info --verify-no-changes`
      empty output, `jb inspectcode` zero `<Issue>`.
- [ ] Benchmark data supporting each claim recorded and cited in the task record before archive.

## Notes

- **Sanctioned trade (requirement 3)**: an event-driven idle wait bounded by a timeout raises the cost of
  stopping a *parked* pump from ≤1 ms today to the wait's timeout (~100 ms at the design's default). The
  research accepts timeout-bounded stop latency as part of the pump's disposal contract; the design records
  the measured figure, and a signal-on-stop variant that removes it is deferred rather than silently
  invented. A pump with no arrival signal keeps the current sleep-poll shape byte-identically.
- The research's F5.1/F5.2 are the input; the design decides whether both land, and in what order, and may
  defer one with reasons. The research itself flags the speculative read as needing on-Windows ABI
  validation before adoption.
- This host cannot exercise the real driver; every fact must therefore be built at the injectable seam, and
  the plan must be explicit about what a seam-level fact does and does not prove.
- Out of scope: the driver itself (the pinned ndisapi ABI is not modified), the batched-send telemetry, and
  the Windows-only measurement program's rows (they are recorded as the follow-up, not faked here).
