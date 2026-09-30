# F8 process attribution off the pump thread with an owner-table snapshot cache

Parent: `08-30-proxy-perf-stability`. Finding F8 of the archived research
`09-29-tcp-udp-path-structural-perf` (`research.md` addendum §A3, roadmap item 8). This is step 6 of the
operator's F2–F8 pipeline; F3, F2, F4 and F5 are archived and their contracts are the regression surface.

## Problem

With any process rule configured, every new **host** flow pays, **synchronously on the capture pump
thread** (`WindowsProcessAttributor.FindAsync`, `src/WinForward.Windows/ProcessAttribution.cs`):

1. `GetExtendedTcpTable`/`GetExtendedUdpTable` — a **system-wide connection-table enumeration** (thousands
   of rows on a busy desktop), preceded by a size probe and followed by a full managed copy
   (`new TcpOwner[rowCount]`, two `new IPAddress` per TCP row) and a LINQ `Where/Select/Distinct/ToArray`;
2. on a miss, `await Task.Delay(2 ms)` and **the whole scan again**;
3. on a hit, `Process.GetProcessById` + `OpenProcess` + `QueryFullProcessImageName`.

The pump's `InvokeHandler` blocks on the handler's `ValueTask` (`NdisCapturePump.cs`), so the entire
sequence — including the retry — stalls **every** flow on that adapter: 0.5–7 ms per new flow, at tens of
new flows per second during browsing bursts. Measured (`benchmarks/results/2026-09-29-benchmark-coverage/`):
a 50 ms per-flow stall moves the pooled p99 from 8.3 ms to **~52 ms** while the stable-class mean does not
move, and a new flow allocates **~91 KB**.

## Requirements

1. **Attribution never runs on the pump thread.** On a flow miss that requires attribution, the pump hands
   the packet to a bounded per-flow pending structure and returns; **attribution** (and, where the design
   decides, policy evaluation) runs on a setup worker, and the flow's pending packets are drained in order
   once the verdict lands. The **claim itself stays on the pump thread at the delivery point**: moving it
   would require a warm-path pending marker and a thread-safe pass-lane flush, i.e. it would reopen F1's
   batched-lane contract and F2's warm path — the two things this task must not regress. The existing
   pending-SYN index and UDP setup queue are the templates (proven shape, bounded budgets).
2. **Order is preserved and nothing is replayed twice.** A flow's first packet and every packet that follows
   it are delivered in arrival order after the decision; a packet must never be delivered twice, and a flow
   whose setup fails must not leave its packets stranded (fail-closed: the packets are released under the
   documented budget/refusal rules).
3. **The pending structure is bounded and fail-closed.** A burst beyond its budget refuses exactly as the
   existing setup queues do (a counted refusal, no unbounded growth, no silent drop), and the refusal is
   observable.
4. **One system-wide scan serves a burst — by coalescing, not by TTL.** The owner tables get a short-lived
   snapshot (~250–500 ms) **and** a single-flight refresh that concurrent misses coalesce onto; planning
   established that a TTL window alone cannot serve a burst's *own* new flows (an owner-table row appears at
   socket bind, after any snapshot taken before the burst), so coalescing is the mechanism that delivers the
   "one scan per page load" property. A miss falls back to today's fresh scan and refreshes the snapshot, so
   the worst case is unchanged. Caching is **safe per table**: TCP (full four-tuple, TIME_WAIT) may serve
   from the snapshot; **UDP may not** — its lookup matches on the local port alone, so a recycled port inside
   the window would attribute a flow to the previous process (fail-open) — until a creation timestamp makes
   the match unique, UDP lookups coalesce but never answer from cache.
5. **The pump stall is gone (the acceptance series).** The `tcpChurn` scenario with a per-flow delay shows the
   pumped p99 back near its no-attribution baseline, and the exact counters show zero attributions on the
   pump thread.
6. **Semantics preserved**: identical attribution results and policy decisions for the same inputs, the
   existing attribution-miss behaviour (policy continues without a process match) unchanged, no change to
   non-attributed flows' path, the 0 B pump-loop allocation contract intact (the setup path keeps its
   documented budgets), and no change to fail-closed or teardown semantics.
7. **Existing gates stay green**: full suite, the F2/F3/F4/F5 facts, the dispatcher warm-path gate, gc-soak
   anchors, and the UDP/TCP scenarios.
8. **Windows-only paths stay behind their seam**: the real table enumeration cannot run on this host, so
   every fact is built against the injectable attribution seam and the plan says what a seam-level fact does
   and does not prove.

## Acceptance Criteria

- [ ] **Off-pump (exact).** An instrumented counter proves zero attributions on the pump thread and exactly
      one per **admitted pending entry** on a setup worker — a failed claim or a second key for the same
      transport tuple attributes again, and each such re-admission is counted rather than hidden; the
      red-before shows the pump-thread call on the pre-change path.
- [ ] **Order and exactly-once (exact).** A test drives a new flow's several packets through the deferred
      path and asserts in-order delivery after the decision, no duplicate delivery, and that a failing
      setup releases the pending packets under the refusal rules.
- [ ] **Bounded (exact).** The pending budget refuses with a counted refusal beyond its limit, and the
      structure never grows past it.
- [ ] **Snapshot (exact + series).** With a fake table provider, a coalesced refresh **epoch** performs
      exactly one shared system-wide scan, plus exactly one forced rescan for a flow whose socket bound
      after that read — `ownerTableScansPerBurst` is a **recorded series, not a threshold**, because a
      snapshot can never answer a burst's own newly-bound sockets. A miss falls back to a fresh scan and
      refreshes, and UDP is proven **not** to serve from cache. The movement series comes from a **new
      `attribution` scenario arm** (exact thread counts plus a `pumpBlockedMs` series) under
      `benchmarks/results/2026-10-01-attribution-off-pump/`, because planning established that `tcpChurn`
      never runs the dispatcher, the pump or the attributor — its `--attribution-delay-ms` is a client-side
      `Task.Delay` (`TcpChurnScenario.cs:114`). `tcpChurn` is kept as **calibration and must-not-move**, at
      the **5 % delayed arm** (the pooled-p99 movement only exists there; at 1 % the delayed flows fall back
      into the stable class and the reading is a host outlier — the archived "1 % → ~52 ms" reading must not
      be used as an acceptance arm), and the pooled percentile is recorded explicitly rather than re-derived
      from per-class summaries.
- [ ] **Allocation.** The pump-loop 0 B gates stay green, and the **attribution path's own** allocation is
      measured and gated separately: the recorded ~91 KB per new connection belongs to the churn path's own
      bookkeeping (planning reproduced it at 91,088/91,066/91,031 B on a tree with no attributor), so it is
      the churn ceiling, not attribution's cost.
- [ ] **Regression surface includes F1.** The batched-injection facts
      (`NdisPacketActionExecutorBatchingTests`, `TcpRedirectInjectionBatchingTests`) stay green alongside the
      F2/F3/F4/F5 facts — the deferred pump delivery must not reopen the lane contract they pin.
- [ ] Release build zero-warning, full suite green, `dotnet format --severity info --verify-no-changes`
      empty output, `jb inspectcode` zero `<Issue>`.
- [ ] Benchmark data supporting each claim recorded and cited in the task record before archive.

## Notes

- The research's A3 proposals (off-pump via the pending structure; the ~250–500 ms owner-table snapshot) are
  the input; the design decides whether both land and may defer one with reasons.
- This host cannot run the real enumeration; every fact is seam-level, and the on-Windows verification items
  are recorded with the experiment that would close them.
- Out of scope: changing what attribution *means* (the policy semantics, the process-name/path fields, the
  miss path), the Windows API surface itself, and the F6/F7 findings.
