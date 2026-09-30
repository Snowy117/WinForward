# F4 flow keys and parsing: interned adapter slot, parse-once view, atomic sequence trackers

Parent: `08-30-proxy-perf-stability`. Finding F4 of the archived research
`09-29-tcp-udp-path-structural-perf` (`research.md` §F4). This is step 4 of the operator's F2–F8 pipeline;
its predecessors F3 (`09-30-expiry-sweep-bounded-pause`) and F2 (`09-30-warm-path-lock-chain`) are archived
and their contracts are the regression surface here.

## Problem

Per redirected packet, the data path pays for data structures that were built for convenience rather than
for the packet:

| # | Site | Cost today |
|---|------|-----------|
| 1 | `FlowKey` (`src/WinForward.Core/Domain.cs`) | ~100+ bytes: two `Endpoint`s, family/protocol/origin, a **`string? OriginAdapterId`**, and a generation. `Equals` runs `string.Equals(..., Ordinal)` on **every** dictionary probe, and the struct spans several cache lines |
| 2 | Header parsing | the same frame is walked up to **four times**: the processor's `IPTcpUdpPacket.TryParse`, `TcpFrameRewriter.IsTcpSyn`, `TcpSequenceObservation.TryReadTcpSequenceAdvance`, and the checksum rewriter's full revalidation |
| 3 | `TcpRedirectAssociation._sequenceGate` (`src/WinForward.Runtime/TcpRedirect/TcpRedirectTable.cs`) | two lock entries per packet guarding two `uint?` fields |
| 4 | `FlowContext` / `CapturedFlowPacket` | stacks more strings (`AdapterId`, `AdapterName`) and is copied by value several times per packet (`packet with { ... }` at dispatch) |

Measured by the benchmark-coverage task: the IPv6 reverse forwarded leg is a reproducible **2.0×** the IPv4
leg (`benchmarks/results/2026-09-29-benchmark-coverage/tcp-redirect-data-path.csv`), and the composed
data-path rows are the per-packet baseline the change must move. F2 has since removed the per-hit clock
read and the warm-path gates, so the before-numbers for this task must be re-taken on the current tree,
not quoted from the F2-era artifacts alone.

## Requirements

1. **Adapter identity is interned.** A capture-scope adapter gets a small integer slot at composition (a
   side table mapping slot ↔ stable id / generation / friendly name, used by logging and policy only), and
   `FlowKey` stores that slot instead of a string: no string comparison on any lookup path, and the key
   fits one cache line (≤64 B) with all-integer equality.
2. **A frame is parsed once.** The classifier's parse produces a small view (IP header length, transport
   offset, protocol, flags-byte position) carried on the packet; `IsTcpSyn`, the sequence observation and
   the rewriter consume the view instead of re-walking headers, and no path re-validates what the view
   already proves. The view must be a value type carried by the packet, adding no allocation.
3. **Sequence trackers are atomic.** `uint?` → a single `long` per leg (−1 = unobserved) with a CAS-max
   writer and `Volatile.Read` readers; the RST builders are cold and tolerate the weakly-consistent read.
   The two `_sequenceGate` entries per packet disappear.
4. **The per-packet context is slim.** Process name/path and adapter names move behind an interned
   metadata reference created at claim time, so the per-packet struct copies shrink and no string is
   copied per packet.
5. **Semantics preserved**: identical classification, redirect/reverse decisions, SYN detection, sequence
   tracking and RST behaviour for IPv4 and IPv6; identical policy evaluation; the 0 B steady-state
   allocation contract; fail-closed behaviour; and the F2/F3 contracts (warm cache validation uses the
   key, so a key-shape change must keep its exactness — F2's `FlowHash.CombineCanonical` and the
   transport-tuple corroboration are part of this task's regression surface).
6. **Existing gates stay green**: full suite, `HotPathAllocationGateTests`, the F3 sweep matrix, the F2
   warm-path facts, gc-soak anchors, and the UDP/TCP scenarios.

## Acceptance Criteria

- [ ] **Key shape (exact).** A test asserts `FlowKey`'s size (≤64 B) and that no `string` comparison runs on a
      lookup (`OriginAdapterSlot`-based equality); the interning table round-trips slot ↔ stable id and
      refuses an unregistered adapter; and the packed key **round-trips its endpoints** (`Local`/`Remote`
      and the scope ids, IPv4 and IPv6) so a self-consistent low/high swap cannot pass the hash-agreement
      fact — plus the reverse-swap fact.
- [ ] **Parse-once (exact).** A **thread-scoped** instrumented counter, driven through the real
      processor → dispatcher → coordinator path (the processor's `TryParse` is the first of today's four
      walks, so a harness that starts at the coordinator cannot record the red), proves one header walk
      per redirected packet on the warm path, with the same parse results asserted for IPv4 and IPv6,
      including malformed/truncated frames taking the same rejection path.
- [ ] **Sequence trackers (exact).** Zero `_sequenceGate` entries per packet, with the CAS-max semantics
      pinned by a concurrent test (two threads advancing, the larger value wins) and the RST paths green.
- [ ] **IPv6 forwarded-leg series (one line, recorded reading).** `tcp-redirect-data-path.csv` re-run: the
      IPv6 reverse forwarded row's 3-run median improves against the re-taken before-series **with the
      mechanism named**, and no other row regresses beyond noise; discharge is **per-leg no-regression**,
      with the improvement figure and the IPv6:IPv4 ratio recorded as readings, **not** as pass/fail
      gates — this host's recorded noise floor makes a 10 % delta unmeasurable (`hot-path.md` contract 9).
      Planning measured that the recorded 2.0× is not in parsing (family-neutral: 13.4 ns vs 12.2–15.0 ns)
      but the review showed the row also contains `TrackServerSequence`'s lock (22.9–29.0 ns uncontended),
      so the series is re-taken **after requirements 1–4 land** and the evidence-gated IPv6 checksum step
      (single-pass wide-load delta, bit-identical proof, optional commit) is adopted **only if the re-run
      shows a real win**. Before/after artifacts recorded under
      `benchmarks/results/2026-09-30-flow-key-parse-once/`.
- [ ] **Allocation.** The per-packet 0 B gates (including the dispatcher warm-path gate and F2's
      `FlowTableWarmResolveAllocatesNoManagedBytes`) stay green.
- [ ] Release build zero-warning, full suite green, `dotnet format --severity info --verify-no-changes`
      empty output, `jb inspectcode` zero `<Issue>`.
- [ ] Benchmark data supporting each claim recorded and cited in the task record before archive.

## Notes

- The research's F4.1–F4.4 are the input; the design decides the order and may defer one with reasons
  (the roadmap itself says "intern first" because the interned slot is the prerequisite for the key shape).
- **Operator decisions on the planning conflicts** (all recorded in the design's dispositions):
  - `OriginAdapterGeneration` **stays a key field** with integer equality. Only the stable id is interned,
    so the side table is bounded by the adapter count and no per-generation slot can be exhausted; the
    design's alternative (intern the `(StableId, Generation)` pair and drop the generation from equality)
    is rejected because it weakens the adapter-recreation semantics the field exists for. The ≤64 B key
    contract is met with the generation kept.
  - The "no string comparison" requirement is satisfied **structurally** (no reference-typed key field, no
    string `Equals` on a probe path); no per-packet timing claim is attributed to it, because Ordinal
    `Equals` already short-circuits on reference equality.
  - Two interned metadata references (adapter identity + per-flow process metadata) are sanctioned instead
    of one: adapter identity is known at classification and is logged from warm packets too.
  - The 16-byte `PacketLayout` carried on the packet is sanctioned over carrying the 80-byte `PacketView`,
    which would grow a struct copied 2–3× per packet.
  - Layering: `PacketLayout` lives in `WinForward.Protocols` (Core has no project references) and the
    process metadata in Core.
- Anything that changes the key's bit layout must keep F2's exact-validation invariant intact: the warm
  cache compares the full key and corroborates the transport tuple, so a key change is only safe if that
  path is re-proven, not merely recompiled.
- Out of scope: the sharded FlowTable rebuild (A4) and anything F2/F3 already landed; this task changes the
  key, the parse and the trackers, not the tables' concurrency architecture.
