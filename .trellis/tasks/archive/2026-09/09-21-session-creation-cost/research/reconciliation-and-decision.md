# Reconciliation and decision — per-session session-creation cost (task 09-21-session-creation-cost, R4/R5)

> **ERRATUM (2026-09-22, task `09-22-session-creation-cost-redo`).** §3 (framework table), the
> T3 anchors (≤95,000 / ≤84,000) and the rank-1 "up to 77,448 B/session" saving are
> harness-inflated: the loopback SOCKS5 server ran in the same process as the measured client,
> and its per-connection 64 KiB relay buffer + socket/arrays account for ~75.5 KB/session of
> every real-dial figure. Clean values (server out of process): framework path 7,952.0 B/session
> (control dial 3,792.2, ASSOCIATE 959.0), real probe marginal 17,021.2, churn whole cycle
> 13,248.9–14,069.9 (wave) / 13,720.2–13,868.6 (sustained). §2's bookkeeping contributor table,
> T1/T2 and the teardown/exception findings are unaffected and were re-validated byte-identically.
> Corrected report with the re-anchored T3 and the re-ranked list (repo-root-relative):
> `.trellis/tasks/09-22-session-creation-cost-redo/research/reconciliation-and-decision.md`.

Evidence base: `research/noop-decomposition.md` (R1), `research/framework-decomposition.md` (R2),
`research/churn-measurements.md` (R3), all against HEAD `feb5955` (2026-09-21/22). This document
reconciles the measured reality with the documented budget, proposes the re-anchored budget and its
unapplied `hot-path.md` §3 text, and ranks the reduction opportunities. Decision unit: managed
allocation bytes per session (`GC.GetTotalAllocatedBytes`); ns/µs are ordinals only — the
2026-09-21 A/B established this dev box cannot resolve sub-2× latency deltas on short runs (up to
2.8× same-binary drift), so no latency claim is made here.

No `src/**` change was made on this task; the §8 spec re-anchor was applied to `hot-path.md`
(docs only) on 2026-09-22 with user approval.

## 1. Decision summary

- **What the cost is.** Per-session managed allocation is two largely independent layers: the
  **framework dial** — per-session control TCP connect + SOCKS5 handshake 77,448 B (93 % of the
  83,442 B/session framework path) — and the **bookkeeping stack** — capacity pre-seed 488.9,
  admission 828.8, background setup start ≤172, session tier 2,890.8, teardown 1,458.5–1,967.9
  B/session (product-shaped total 5,276–5,831 B/session depending on window shape).
- **Churn reality.** Per-session cost is flat and never amortized: the churn matrices measure
  90.8–92.5 KB/session (spread ≤0.9 %) across wave sizes 48/128/256, dial delays 0/5/20 ms and the
  30 s sustained shape (12k–23k sessions/run), ≈11.5–11.9 MB per gen0, no establishment loss or
  rejects at any design-bound load. Because UDP flows carry 1–2 datagrams, this is a rate cost:
  24–65 MB/s allocated at the 264–716 sessions/s the dev box achieved (rate differences across
  runs are scheduling noise, not dial-delay effects).
- **Documented contract is stale.** The `hot-path.md` §3 ledger (≤1 KB/session; "measured 2.1 →
  ~0.4 KB", 2026-08-29) and the ≈3.9 KB Noop record (2026-08-30) are both superseded: the
  present-day product-shaped probe total is 5.28 KB/session and the stages attribute it to 100 %.
  No gate failed on the drift because none of the older numbers were ever re-measured against the
  current stage set.
- **Decision.** Re-anchor the budget in three tiers tied to the instruments that measure them
  (§4), keep the framework cost explicitly out of the bookkeeping ledger but anchored (§4, §8),
  and schedule reduction work in the bookkeeping layer only (§5): teardown first (unattributed,
  concrete exception lead), then the session-tier async/lifetime machinery, then
  admission/capacity. Control-connection reuse is recorded as the largest single lever (~77 KB)
  but is a product/protocol decision, not an allocation micro-fix. A .NET 11 SDK trial is
  sanctioned on the async evidence, with a measurement-first protocol (§5.2).

## 2. Contributor table — Noop layer (design §5.1)

All values are mean B/session of the 2026-09-22 batches (`decomposition-cur-run{1,2,3}` +
`decomposition-stages345-run{1,2,3}`), single-pass `InvocationCount=1` shape unless stated.

| Stage / component | B/session | % of S5 | spread | Reducibility verdict |
|---|---:|---:|---:|---|
| S0: coordinator capacity pre-seed (slot + association + cooldown tables) | 488.9 | 7.8 % | 0 | Partly — per-session share is table growth (pre-seed clamped at `min(capacity, 1024)`); entry-footprint work is possible, structurally bounded |
| Admission = S1−S0 (slot claim, setup-queue enqueue + payload copy + charge/lease) | 828.8 | 13.2 % | 0 | Partly — the original ledger's core; payload copy is a real buffer materialization (poolable), charge/lease bookkeeping is not payload |
| Setup start = S2−S1 (executor enqueue/worker, setup state machine, limiter queue, dial re-stamp) | 115.2 | 1.8 % | 171.4 (quantized 1 vs ~172) | Already minimal; read as "≤0.2 KB/session" (S2 is limiter-frozen) |
| Session tier = S4−S2 (association claim, `UdpProxySession` + context + quiescence scope/CTS, attach, receive-loop start + receive-window rent, flush) | 2,890.8 | 45.9 % | 294.9 | Partly — the largest leg; C2 (2,698.7, 93.4 % of the leg) is async/lifetime machinery: reuse-able only with a quiescence-gated pool; receive-window rent and flush are load-bearing |
| — bracket: S3 variant of the same leg (probe-driven readiness) | 3,247.7 | (51.6 %) | 781.2 | Not an extra stage: S3 waits by repeated admission probes (+356.9 vs S4, spread 486); S4 is the clean leg, S3 is its bracket |
| Teardown T = S5−S4 (compensation inside the measured window) | 1,967.9 | 31.3 % | 311.3 | Partly — **unattributed internally** (scope drain vs slot/tombstone removal vs transport dispose not split); carries ≈2 first-chance exceptions/session, S0–S4 report none |
| **S5 total (today's probe shape, single pass)** | **6,291.5** | 100 % | 175.2 | — |
| harness F (`BenchmarkUdpTransport` + 2 `IPEndPoint`s) + H (flow-key construction) | 460.8 | — | 0 | Not product cost; must be subtracted (F 104.0, H 356.8) |
| **Product-shaped S5** | **5,830.7** | — | — | — |
| Component probes: C1 association claim/release (upper bound; overlaps S0's table share) | 834.7 | — | 0 | Reference measurement |
| Component probes: C2 session construct/start | 2,698.7 | — | 0 | Reference measurement (93.4 % of the session-tier leg) |

Shape ladder (the same work measured three ways — the anchors in §4 must name the shape):

| Shape | Raw total (B/session) | Product-shaped (raw − F − H) | Note |
|---|---:|---:|---|
| Recorded Noop probe marginal (1→1000 sweep) | 5,737 (recomputed 5,739) | 5,276.2 | the gate's own shape; spread 10 |
| Matched `InvocationCount=6` (probe ≡ S5 to 0.09 %) | 5,807.4 | 5,346.6 | teardown share 25.1 % here |
| Single-pass `InvocationCount=1` (stage diag) | 6,291.5 | 5,830.7 | first-pass-in-window effect (~0.4–0.6 KB) sits in teardown |

Teardown rows: T(probe/matched shape) = 1,458.5 (spread 28.3), T(single-pass) = 1,967.9, T(confirmation
batch) = 2,029.7, T(V_A window) = 2,126.8. Attribution coverage: 109.7 % of the recorded Noop probe
at the single-pass shape, 101.2 % at the matched shape — the surplus is the quantified BDN
window-shape artifact, not an unattributed code path.

## 3. Framework table — real-transport layer (design §5.2)

Isolated per-session components, 1,000 sequential create+dispose, HEAD `feb5955` (3 runs; the
isolated-path per-session spread is 3.5 B, i.e. 0.004 %).

| Component | B/session | share | Reuse/pool feasibility |
|---|---:|---:|---|
| Control connect + greeting handshake (+ socket, `NetworkStream`, deadline CTS, 513 B scratch, disposal) | 77,448 | 92.8 % | **Only via control-connection reuse** (shared/pooled connection, per-flow ASSOCIATE): a product/protocol decision with a security/attribution trade-off. No within-dial allocation is removable — the objects are the BCL socket/stream/CTS graph |
| UDP ASSOCIATE (request write + reply parse + relay-endpoint materialization + linked CTS) | 2,872 | 3.4 % | Not poolable without reuse of the control connection; per-flow protocol traffic |
| Relay socket (create, 512 KiB receive buffer, bind, non-blocking, close) | 576 | 0.7 % | Small; the socket must exist per session under the current design (one relay endpoint per flow) |
| Self-traffic register/release | 160 | 0.2 % | Small; registry entry + token; bounded |
| Residual: transport construction + wiring (1,536 B send buffer dominated, `SocketAddress`, `IPEndPoint`, `SemaphoreSlim`, transport instance) | 2,386 | 2.9 % | Partly — the send buffer's lifetime equals the session's; not reusable without session-structure changes |
| **Framework path total** | **83,442** | 100 % | — |
| Real-transport probe marginal (both layers, 1→1000 sweep) | 92,255 / 92,265 / 92,244 | — | 96.4 % of the probe-derived difference (86,516 B) is the isolated path; the residual ~3,000 B/session is harness-shaped (loopback server concurrency + real-socket receive interaction) |
| Churn whole-cycle (fire → first responses → retire, `udp.churn`) | 90,829–92,451 | — | ≈ framework 83,442 B + bookkeeping ~5,800 B + 1,600–3,300 B unseparated response/retire residue |

No framework component is removable by a WinForward-side allocation micro-fix: the control
connection is the SOCKS5 protocol's per-session dial, and the ASSOCIATE share is protocol traffic
on that socket.

## 4. Proposed re-anchored budget (design §5.3)

The tiering matches the instruments that measure each number, so a drift points at one
instrument and one layer. Anchors are stated on the measurement shape they are enforced on, and
there is no numeric churn *target* (user directive): they are measured baselines plus guard bands
against the guiding principle "minimize allocation as much as possible" — §5 ranks by achievable
bytes, not by the size of a target gap.

Units: all research-doc figures are bytes/session. BDN's own "KB" is 1,024 B (its report
explicitly notes it), so a B/session value divided by 1,000 is a *decimal* KB; the anchors below
are given in both forms because the stale text's unqualified "KB" is part of how the drift went
unnoticed (a 5.74-decimal-KB figure is 5.61 KiB, and the PRD-era anchors "~5.3/~89.4 KB" are KiB
readings of 5,446 / 91,537 B/session at `865228f`).

| Tier | Anchor | Measured (2026-09-22) | Rationale | Falsification condition |
|---|---|---|---|---|
| **T1. Noop probe product-shaped budget** | **≤5,500 B/session** product-shaped, i.e. raw probe **≤6,000 B/session marginal** (1→1000 sweep) | raw 5,737 B (5.74 decimal KB; spread 10 B; matched-shape probe 5,802 B); product-shaped 5,276 B (matched 5,347 B) | Product-shaped = probe total minus the probe's own harness (F + H = 460.8 B). Guard bands: 4.6 % on the raw anchor (3.4 % against the matched-shape probe), 4.2 % on the product-shaped anchor (2.9 % matched); the recorded spreads are ≤0.9 %. The stage diagnostic runs at a different window shape (single-pass raw 6,291.5 B, product-shaped 5,830.7 B) and is compared against its own recorded values, not against these anchors | A documented 3-run batch of the probe instrument on an unmodified tree exceeding 6,000 B raw (or 5,500 B product-shaped) in ≥2 runs with F/H unchanged — that is product drift to fix or an anchor to re-derive, never to relax. Also falsified if the stages stop summing to the probe within the recorded spread (attribution broken → re-derive from fresh stage data before enforcing) |
| **T2. Setup bookkeeping (admission/capacity) sub-budget** | **≤1,500 B/session** | 1,432.9 B (S0 488.9 + admission 828.8 + setup start ≤172); spread 0 on S0/admission, quantization bound 1,489.7 B | This is the original §3 ledger's scope (slot, setup queue, task machinery, payload/MAC copy, tombstone/tracking) re-anchored to its present-day measured value: the ≤1 KB claim was never re-measured and is 43 % low. Components sum with the quantization upper bound ≤1,500 B | Same 3-run rule; plus: if a refactor moves queue/charge/table work into the session tier or teardown, the mapping — not the code — is wrong and the ledger must be re-mapped before it can fail anything |
| **T3. Real-transport / churn anchor** (regression anchor, not a reduction target) | **≤95,000 B/session** whole cycle; framework sub-anchor **≤84,000 B/session** isolated | churn 90,829–92,451 B; isolated framework path 83,442 B (control 92.8 % of it); probe-derived difference 86,516 B | Records the framework layer that the ledger explicitly excludes, so drift is visible instead of silent (the failure mode that produced this task). Headroom ≈2.8 % over the worst churn cell; the 84,000 B sub-anchor is 0.7 % over the isolated measurement (spread 3.5 B) | Churn rows >95,000 B/session on an unmodified tree; or the isolated framework path >84,000 B while the control share stays ~93 % (would point at a non-control component); or the 1.6–3.3 KB residue being attributed to a removable product path (then the anchor's composition changes and is re-derived) |

Enforcement notes:

- A documented run = the command lines in the three research docs (BDN `--job short`, one process
  at a time, ≥3 runs; the churn campaign script for T3). Stage attribution is the localization
  tool when T1 moves: `SessionSetupDecompositionBenchmarks` + `FrameworkSetupBenchmarks`.
- The re-anchor is about **allocation**, not live memory. The existing live-footprint guardrails
  (`udp-relay.md`: 8 MiB global setup-queue budget, 5 s datagram TTL, 32-pkt/32-KiB slot bounds,
  `Capacity` 16,384, 8-wide setup limiter) are unchanged and remain the contract for held memory;
  the retained per-session working set is not observable at the current scenario granularity
  (§7) and is carried as a gap.
- **Fate of the stale §3 text.** "≤1 KB per session" survives only as the re-anchored ≤1,500 B
  ledger (T2) with an explicit component mapping; "Measured 2.1 KB → ~0.4 KB" is deleted — it
  describes a revision whose numbers cannot be reproduced and whose scope predates the
  session-tier/teardown attribution that now dominates. The mechanisms it listed (single-slot
  setup-queue fast path, no `registered` TCS, inlined setup-failure handling, cached method-group
  delegates, clamped dictionary pre-sizing) are retained as still-valid design guarantees. The
  framework-cost scoping decision is kept explicit — outside the ledger budget, now anchored (T3).
- **Second stale gate found (same file, not part of the §3 proposal).** `hot-path.md` §6 "Tests
  Required" still says "UdpSession Noop probe ≤ ~4 KB @100 sessions"; the recorded N=100 row is
  642,223 B/invocation ÷ 100 sessions = 6,422 B/session (the N=100 shape carries ≈685 B/session of
  amortized per-invocation fixed cost). It should be re-anchored in the same edit to
  "≤6,000 B/session marginal (1→1000 sweep); N=100 row ≈6,400 B/session".

## 5. Ranked reduction opportunities (design §5.4)

Ranking criterion (user directive): allocation minimization first, performance second. Solutions
that merely move cost elsewhere say so in-line.

| Rank | Opportunity | Layer | Expected savings (B/session) | Confidence | Blast radius | Compat / risk | Next step |
|---|---|---|---|---|---|---|---|
| 1 | **Control-connection reuse / pooling** | framework | **up to 77,448** per flow (93 % of the 83,442 B/session framework path, ~85 % of the churn total) | High on the size; low on feasibility (protocol/product) | UDP + TCP control lifecycle, self-traffic registry, relay-endpoint binding, failure isolation | **Product/protocol decision**, not an allocation fix: one association per control connection is the SOCKS5 usage model; shared connections change upstream attribution (all flows = one authenticated client), change failure domains, and need server-capability detection + fallback; kernel socket state is retained, not removed | Product/protocol ADR (spike), not an optimization task |
| 2 | **Teardown path (T)** | bookkeeping | 1,458.5–1,967.9 (25–31 % of the Noop total); addressable share unknown | Medium — concrete lead: ≈2 first-chance exceptions/session on the disposal path (0 in S0–S4) plus drain/join-cell allocations | `UdpProxySession`/`UdpProxyCoordinator` disposal, quiescence drain, tombstone/slot removal, transport dispose | Medium-high: teardown semantics are contract-locked (drain/join cells, D7 CTS ownership, tombstones/cooldowns/counters); packet path untouched; no protocol change | Teardown component probe (scope drain vs slot/tombstone removal vs transport dispose + exception source) → then remove the exception-shaped control flow |
| 3 | **Session tier (construct/attach/receive start)** | bookkeeping | 2,890.8 leg (45.9 %); C2 covers 2,698.7 (93.4 %); removable share unknown | Medium-low — C2's internal split (context/session vs CTS vs scope vs receive-window rent) is not measured | Session lifetime + cancellation semantics; the established-session send path shares these objects (the warm 0-B gates must stay green) | High: token identity/linked-CTS correctness, reuse requires a quiescence proof (no stragglers); pooling/slab retains capacity-sized memory | C2 internal split probe → quiescence-gated reuse (managed slab) + `TryReset`-style CTS reuse (P1 precedent); .NET 11 trial (§5.2) |
| 4 | **Admission + capacity pre-seed** | bookkeeping | 1,317.7 total (S0 488.9 + admission 828.8) = 21 % of the Noop total | Medium — components known; per-entry split needs a probe | Coordinator admission path + pre-seeded tables only | Low-moderate: behavior locked by admission/cooldown/capacity tests and the 8 MiB charge/credit exactly-once contract; pooling the payload copy moves memory to a pool (retained) | Admission component probe → table/entry footprint + pooled payload slot |

Ordering rationale: item 1 is first on absolute allocation (85 % of the churn total) but is gated
on a product decision, so the scheduled engineering sequence starts at item 2. Items 2–4 cover
essentially all attributable bookkeeping cost: 6,176 B/session of the 6,292 B/session single-pass
total (98 %), leaving only the ≤172 B setup-start leg and the 460.8 B harness outside them.

### 5.1 `unsafe` / direct-memory verdict (user directive)

- **No target among the measured allocations.** The per-session allocations are managed control
  objects (CTS, quiescence scope, session/context records, slot + dictionary entries, state
  machines, exception objects) in the bookkeeping layer and BCL socket/stream/CTS objects in the
  framework layer. The only buffer materializations are the 1,536 B send buffer (framework
  residual), the setup-queue payload copy (inside admission) and the receive window (already
  rent-based from the native pool). A direct-memory rewrite has nothing to delete there.
- **Where a real variant could exist:** the session-tier reuse of item 3 — pooled/arena session
  state, i.e. struct-ified per-session control objects in a pre-allocated slab of cells replacing
  the per-session object graph (session + context + scope + CTS). Honest assessment: on the
  allocation-first metric the *managed* slab and the *unmanaged* (`NativeMemory`) slab are
  equivalent (one allocation for the slab, then ~0/session until a pool miss); `unsafe`/direct
  memory buys no additional allocation reduction here, only removal of GC scanning — while
  forfeiting the runtime's lifetime safety net for objects handed to concurrent async work.
  Therefore, on this evidence, **`unsafe` is not the recommended variant**; the managed
  struct/object slab is preferred, with `unsafe` revisited only if the managed variant
  measurably fails on GC pause/allocation in a later measurement.
- **Safety argument and fallback** (for the managed variant that is recommended): reuse is valid
  only when no straggler can still touch a cell — exactly the invariant the quiescence
  scope/drain-join contract already proves at teardown (`async-lifetime.md` D7). The pool's
  release gate must therefore be that proof, with `NativeLease`-grade rigour (idempotent release,
  no stale-copy use after return, no reference held across an await); CTS reuse must handle
  cancelled-source semantics (a reset source must never be observable by the old session's
  stragglers). Fallback if reuse cannot be made safe: keep per-session allocation and shrink
  object cost (CTS pooling via `TryReset`, trimmed context record, fewer wrapper objects) — the
  C2 split decides how much that can buy.
- **Cost-moved disclosures:** slab/pool variants convert allocation rate into a retained
  footprint sized by capacity (16,384) — visible as a *retained* (not allocated) cost that the
  current footprint scenario cannot observe (§7). Control-connection pooling converts per-flow
  TCP/socket allocation into long-lived kernel socket state (retained, not removed).

### 5.2 Runtime lever: .NET 11 (user directive)

- **Evidence that async machinery is a material contributor:** the session-tier leg is 45.9 % of
  the Noop total and C2 shows 93.4 % of it is the session object + context + quiescence
  scope/CTS + receive-loop start — the design's D7 lifetime machinery, not protocol work; the
  teardown quarter (25–31 %, 1,458.5–1,967.9 B/session) runs on exception-shaped control flow
  (≈2 first-chance exceptions/session, byte-identical across batches, zero in S0–S4). Together
  those legs are ~77 % of the Noop total and are the async/lifetime machinery.
- **Recommendation (sanctioned): trial .NET 11**, with a measurement-first protocol — the current
  probes attribute at object-type level, not at runtime-infrastructure level, so the upgrade's
  *expected effect* cannot be quoted from this data. Bound: ≤ the async/lifetime share
  (~4.3–4.9 KB/session) and plausibly ~0 for product-owned objects (CTS, scope, session record),
  which a runtime upgrade does not remove. Protocol: install the .NET 11 preview SDK on a branch,
  re-run the exact documented batches (stage decomposition, framework split, baseline probe,
  `udp.churn`), and compare against the recorded spreads — S0/S1/F/H/C1/C2 are byte-identical
  today, so any per-session movement is attributable to the runtime.
- **Migration risk (medium):** SDK/TFM bump across every project; the analyzer/format/inspectcode
  gates must be re-green; BCL behavior changes on the Windows capture/NDIS paths must be
  re-verified on a guest; publish shape and the runtimeconfig GC fuses (`WinForward.Cli.csproj`)
  must be re-checked; preview-runtime risk for a product. Falsification: if the trial moves the
  session tier + teardown by less than the run spreads (≤0.3 KB/session), the lever is dead for
  these layers and the recommendation reverts to the code-level pooling in §5 item 3.

## 6. TCP-transfer notes (deferred layer)

Transfers directly to per-TCP-session dials (`TcpProxyRelayFactory.EstablishAsync`,
`TcpProxyRelay.cs:40` dials `Socks5ControlConnection.ConnectAsync` per TCP session):

1. **The dial dominates.** A TCP session pays the same control connect + greeting component
   (77,448 B/session, 92.8 % of the framework path); TCP replaces UDP ASSOCIATE (2,872) with the
   CONNECT request/reply — a comparable small protocol share. Rank-1 control-connection reuse is
   the same lever in the same code, and if pursued should be decided once for both layers.
2. **The control-reuse decision, the self-traffic registration, the address-cache cold edge** and
   the `Socks5ControlConnection` failure/retry machinery are shared.
3. **Method transfer:** the stage-bisect method (fake seams, harness subtraction, shape-matched
   windows) and the allocation-vs-latency discipline transfer unchanged; TCP has fewer existing
   seams (accepted connection + upstream stream), so it needs its own fake surface.

Differs (do not carry the numbers over):

1. **No analogue for the UDP bookkeeping stages.** TCP has no receive window, no setup
   queue/TTL/8-wide limiter, no association/cooldown tables; it has an accept path (client
   socket + SYN retention/RST injection) and two pump directions whose 64 KiB buffers are
   already `NativeBufferPool`-backed and whose stall windows already reuse a CTS
   (`TryReset`, P1). Its per-session managed allocation is therefore expected to be dominated by
   the control dial plus socket/stream wrapper objects — unmeasured.
2. **Amortization.** TCP connections are long-lived relative to DNS-shaped UDP flows, so the
   per-session cost is amortized over the connection's payload bytes; the "never amortized" hazard
   that drives this task is UDP-shaped. Short-lived TCP churn (abort/RST waves, modelled in the
   soak abort mix) still pays the dial per session.
3. **Teardown.** The UDP teardown exception lead (RST/half-close semantics differ) and the
   response/retire residue have no confirmed TCP equivalent — a TCP teardown probe is separate.
4. **Retained footprint.** TCP's relay sockets are stream sockets (no 512 KiB receive buffer);
   its pooling surface is the existing pump-buffer pool.

Recommendation: keep TCP as a follow-up measurement task after the UDP reductions land; measure
before optimizing (its steady-state per-session shape is already better pooled than UDP's).

## 7. Gaps carried forward

| Gap | Source | Consequence / next probe |
|---|---|---|
| T internal split (scope drain vs slot/tombstone removal vs transport dispose) and the ≈2 first-chance exceptions/session source | noop-decomposition | Blocks sizing rank-2 work; teardown-side component probe |
| Churn residue split (1.6–3.3 KB/session: real-socket receive path vs response sink vs per-wave retirement) | churn-measurements, framework-decomposition | Blocks closing the 91 KB whole-cycle decomposition to 100 % |
| Retained (post-GC) working set per session | churn-measurements, framework-decomposition | `workingSetDeltaBytes` = 0 in all 9 rows (100 ms settle, no forced GC): "not observable", not "no cost"; needs longer settle or a forced-GC pass — also the missing metric for any slab/pool variant (§5.1) |
| S2 quantization (1 vs ~172 B/session, limiter freeze) | noop-decomposition | Needs a seam between executor dequeue and dial start |
| S3 bracket (+356.9 B/session vs S4, spread 486) | noop-decomposition | S4 is the clean leg; S3 is reported as its bracket only |
| Fake control connection not expressible (sealed type) | framework-decomposition | connect+handshake and ASSOCIATE are protocol-indivisible without a `src/**` seam |
| DNS resolution inside the control path (address cache null in the benchmark) | framework-decomposition | Production may cache per server; a cold-edge option, not this decision |
| ~3.0 KB/session anchor gap not split (loopback server concurrency vs real-socket receive interaction) | framework-decomposition | Needs a server-side counter or fake server (new harness surface) |
| Coverage 109.7 % (single-pass) / 101.2 % (matched) vs the recorded probe | noop-decomposition | Quantified window-shape artifact (matched-window evidence); not a code difference |
| TCP churn not measured | churn-measurements, §6 here | Separate follow-up task |

## 8. Applied `hot-path.md` §3 replacement (user-approved 2026-09-22)

> Replaced the former bullet "**UDP session cold-path bookkeeping budget: ≤1 KB per session** …"
> and re-anchored the §6 "≤ ~4 KB @100 sessions" line to "≤6,000 B/session marginal
> (1→1000 sweep; N=100 row ≈6,400 B/session)" in the same edit (§4 enforcement notes). The text
> below is what now lives in `hot-path.md` §3.

```markdown
- **UDP session setup bookkeeping budget: ≤1,500 B per session** (capacity pre-seed, slot claim,
  setup queue + payload copy, task machinery, tombstone/tracking structures). Measured
  1,433 B/session, spread ≤172 B (task 09-21-session-creation-cost, 2026-09-22). The design
  guarantees from the 2026-08-29 contract stand (single-slot setup queue fast path, no
  `registered` TCS, inlined setup-failure handling, method-group delegates cached in the
  constructor, dictionary pre-sizing clamped to `min(capacity, 1024)`); its ≤1 KB and
  "measured 2.1 KB → ~0.4 KB" figures predate the session-tier and teardown attribution and are
  superseded.
- **Noop probe budget: `UdpSessionBenchmarks` Noop probe ≤6,000 B/session marginal** (1→1000
  sweep; measured 5,737 B, spread 10 B). The probe's window contains its own fake transport +
  flow-key harness (460.8 B/session), so product-shaped cost is ≤5,500 B/session
  (measured 5,276 B). When the probe moves, `SessionSetupDecompositionBenchmarks` localizes the
  move: capacity 489 / admission 829 / setup start ≤172 / session tier 2,891 / teardown
  1,459 (probe shape) – 1,968 (single-pass) B per session.
- **Framework socket cost stays outside this budget** (control TCP connect + SOCKS5 handshake,
  UDP ASSOCIATE, relay socket) but is anchored instead of untracked: isolated create+dispose path
  83,442 B/session (control connect + handshake 77,448 B = 92.8 %; ASSOCIATE 2,872, relay socket
  576, transport ctor 2,386), probe-derived difference ~86,500 B/session, churn anchor (whole
  fire→retire cycle) ≤95,000 B/session (measured 90,829–92,451 B). Reusing control connections is
  the only structural lever there — a product/protocol decision, not an allocation micro-fix.
```

## 9. Reconciled cross-checks and discrepancies found

Baseline reconciliation (all HEAD `feb5955`, 2026-09-21/22; PRD-era anchors quoted in BDN's KiB,
this task's measurements in bytes/session):

| Quantity | PRD-era / A/B anchor | This task's measurement | Note |
|---|---|---|---|
| Noop probe marginal | 5.32 KiB (865228f) → 5.61 KiB (feb5955) = 5,446 → 5,742 B | 5,737 / 5,737 / 5,727 B (recomputed 5,739) | A/B delta +296 B reproduced; this task's probe agrees with the A/B after-run (5,742) within 5 B |
| Real probe marginal | 89.39 KiB (865228f) → 90.11 KiB (feb5955) = 91,537 → 92,269 B | 92,255 / 92,265 / 92,244 B | agrees with the A/B after-run (92,269) within 14 B; recomputed A/B delta +733 B vs the PRD's +715 B |
| Framework anchor | ~84 KiB derived (89.4 − 5.3) = 86,000 B | 83,442 B isolated (81.5 KiB); 86,516 B probe-derived difference (84.5 KiB) | The probe-derived difference reproduces the old ~84 KiB anchor; ~3,000 B/session of it is harness-shaped (framework-decomposition §Cross-checks). The isolated path (83,442 B) is the reproducible sub-anchor |

Inconsistencies to keep visible:

- `framework-decomposition.md` quoted the run-1 real-probe marginal as **92,205** B/session;
  recomputation from the same log (summary `Allocated` 159.6 KB → 90,162.28 KB; GC lines
  1,797,704/11 → 92,326,176/1) gives **92,255** B/session — a 50 B (0.05 %) slip, below the run
  spread. The derived difference row (86,478/86,528/86,506) recomputes to 86,516/86,528/86,517
  (≤11 B). Run-2/run-3 cells match exactly.
- Independent check pass (2026-09-22) corrections, applied in the research docs: the
  `framework-decomposition.md` run-1 marginal and its derived difference row (above) plus the
  residual (3.05 → 3.08 KB/session) and the loopback relay buffer's unit (65 → 64 KiB = 65,536 B);
  `churn-measurements.md`'s quotation of the same 92,205 marginal, the churn floor cell range
  (90,836 → 90,829, the wave-mode minimum; §3/§4/§8 here now carry the corrected range), the
  sustained per-wave p95 range (98.0–98.5 → 98.0–98.8 KB) and the delay-effect percentages
  (D5 ≈ 0.4–1.5 % above D0; D20 lies between D0 and D5 only at N = 48 and below D0 at N = 128/256);
  `noop-decomposition.md`'s invariance-count phrasing (the six runs that measured S0/S1/F/H/C1/C2,
  not nine). No measured value or anchor changed.
- `noop-decomposition.md`'s recorded Noop marginals (5,737/5,737/5,727) are the floor of the
  recomputed values (5,738.8/5,737.3/5,727.8) — self-documented as ≤2 B apart.
- `hot-path.md` §6 "UdpSession Noop probe ≤ ~4 KB @100 sessions" is stale alongside §3 (measured
  6.42 KB/session at N=100; §4).
- The ≈3.9 KB Noop record (2026-08-30) and the ≤1 KB ledger cannot be re-derived from the current
  tree: no stage-level evidence exists from those revisions. The present-day stage set attributes
  100 % of the present total, so future drift is localizable; the historical drift itself stays
  unattributed.
