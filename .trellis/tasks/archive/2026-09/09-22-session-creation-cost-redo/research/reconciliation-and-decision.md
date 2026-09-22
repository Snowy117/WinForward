# Reconciliation and decision (corrected) — per-session session-creation cost after the harness correction

Task `09-22-session-creation-cost-redo`, correcting
`archive/2026-09/09-21-session-creation-cost/research/reconciliation-and-decision.md`.
Evidence: `research/framework-decomposition.md` (R2), `research/churn-measurements.md` (R3),
the re-validated clean instruments (R4, stage batch `research/raw/stages-run{1,2,3}.log`).
Decision unit: managed allocation bytes per session (`GC.GetTotalAllocatedBytes`); ns/µs are
ordinals only on this box.

## 1. Decision summary

- **The recorded framework layer does not exist in the product.** The recorded
  "83,442 B/session isolated path (control connect + handshake 77,448 = 92.8 %)" was measured
  with the loopback SOCKS5 server in the same process; the server allocates ~75.5 KB/session
  of that total (64 KiB relay-loop buffer per accepted control connection +
  per-connection socket/arrays). Out of process the same path is **7,952.0 B/session**, of
  which the actual per-flow TCP dial is **3,792.2 B**.
- **Everything real-dial was inflated by the same ~75 KB/session**: the real probe marginal
  92,260.6 → **17,021.2**, churn 90.8–92.5 KB → **13.2–14.1 KB** (wave) and **13.72–13.87 KB**
  (sustained). The recorded in-process shapes still reproduce (ladder ≤1 B, probe ≤6 B, churn
  sanity cell +0.4 %), so this is a measurement correction, not behavior drift.
- **The bookkeeping ledger is confirmed unchanged**: the stage decomposition re-run reproduces
  every deterministic stage byte-identically (F 104.0, H 356.8, C1 834.7, C2 2,698.7, S0 488.9,
  S1 1,317.7) and the noisy legs within combined spreads; the teardown exception lead
  reproduces exactly (130 / 328 / 2,128 first-chance at N = 1/100/1000, ≈2/session). T1 and T2
  therefore stand as recorded.
- **Decision.** Re-anchor T3 (the real-dial anchor) to the clean values with explicit
  falsification conditions and a harness rule (real-dial instruments must run the server out of
  process); keep the T1/T2 ledger; re-rank the reduction opportunities (§5) — the bookkeeping
  items now carry 42 % of the clean whole-cycle cost (was ~6 % of the recorded one), and the
  control-reuse lever's *allocation* case is ~3.8 KB/session (was quoted as 77 KB), while its
  latency/TTL/port motivations are untouched by this correction.

## 2. Contributor table — clean bookkeeping layer (re-validated)

Values are the recorded 2026-09-22 stage batch (`archive .../noop-decomposition.md`), re-run
this task: deterministic rows byte-identical, noisy rows inside their combined spreads.

| Stage / component | B/session | share of product-shaped Noop | verdict |
|---|---:|---:|---|
| S0: coordinator capacity pre-seed | 488.9 | 8.4 % | partly reducible (entry footprint), structurally bounded |
| admission = S1−S0 | 828.8 | 14.2 % | partly — payload copy is poolable; charge/lease bookkeeping is not |
| setup start = S2−S1 | ≤172 | ≤3.0 % | already minimal (quantized 1 vs ~172, limiter freeze) |
| session tier = S4−S2 (C2 = 2,698.7, 93.4 %) | 2,890.8 | 49.6 % | the largest leg; async/lifetime machinery |
| teardown T = S5−S4 | 1,967.9 (single-pass) / 1,458.5 (matched) | 25–34 % (of the product-shaped total; the archived 25–31 % was of the raw S5) | unattributed internally; ≈2 first-chance exceptions/session (reproduced) |
| **S5 total (single-pass)** | **6,291.5** | — | product-shaped 5,830.7 (raw − 460.8 harness) |
| harness F + H | 460.8 | — | subtracted for product shapes |

## 3. Framework table — clean (out-of-process)

From `research/framework-decomposition.md` §4 (means of 3 runs):

| Component | B/session |
|---|---:|
| control connect + greeting + disposal (the per-flow dial) | 3,792.2 |
| UDP ASSOCIATE | 959.0 |
| relay socket | 576.0 |
| self-traffic register/release | 160.0 |
| transport construction + wiring (residual; 1,536 B send buffer dominates) | 2,464.8 |
| **total (create + dispose)** | **7,952.0** |

Cross-checks: in-process reproduction of the recorded values (83,441.6 / 77,447.3 / 80,319.0
vs recorded 83,441.9 / 77,447.8 / 80,319.8) and a standalone product-client probe
(8,346 B/session, same shape +4.7 %). Harness share of the recorded figure: 75,489.6 B/session.

## 4. Re-anchored budget

Anchors are regression anchors: measured baselines plus guard bands, no reduction target
(the standing directive is "minimize allocation as much as possible"). Enforcement runs on a
documented ≥3-run batch on an unmodified tree, one process at a time; headroom is
anchor-relative (1 − measured ÷ anchor).

| Tier | Anchor | Measured (2026-09-22 redo) | Headroom | Falsification condition |
|---|---|---|---|---|
| **T1. Noop probe** (unchanged) | ≤6,000 B/session marginal raw; ≤5,500 product-shaped | 5,727.0 raw (spread 17.4) / 5,266.2 product-shaped | 4.6 % / 4.2 % | as recorded: a 3-run batch exceeding it in ≥2 runs is drift to fix, never to relax |
| **T2. Setup bookkeeping** (unchanged) | ≤1,500 B/session (capacity + admission + setup start) | 1,432.9 (spread 171.4) | 4.5 % | as recorded; re-map the ledger if a refactor moves work across stages |
| **T3a. Framework path, isolated** (re-anchored) | **≤8,200 B/session** (create + dispose, out-of-process server) | 7,952.0 (spread 0.6) | 3.0 % | a 3-run batch >8,200 while the in-process value stays ~10.5× it → product drift; if both move together, the harness changed and the anchor is re-derived |
| **T3b. Churn whole cycle** (re-anchored) | **≤14,500 B/session** wave shape; **≤14,300 B/session** sustained (48 flows/wave, 30 s) | 14,069.9 worst cell mean; 13,868.6 worst sustained | 3.0 % / 3.0 % | a 3-run batch exceeding it in ≥2 runs on an unmodified tree; or a new cell where the N/delay shape changes the level beyond the recorded ±2 % |
| **T3c. Real probe marginal** (re-anchored; echo-fed shape) | **≤17,500 B/session** | 17,021.2 (spread 15.4) | 2.7 % | same 3-run rule; the anchor is on the echo-fed shape — a discard-fed variant would be a different anchor |

Harness rule (new, part of T3): real-dial instruments must run the loopback SOCKS5 server out
of process (`--socks5-external` / `WINFORWARD_BENCH_EXTERNAL_SERVER=1`); an in-process run is a
*diagnostic* whose number must be reported with the measured harness share (~75.5 KB/session
for the ladder) subtracted or explicitly labelled.

Live-footprint guardrails (`udp-relay.md`: 8 MiB global setup-queue budget, 5 s TTL,
32-pkt/32-KiB slot bounds, capacity 16,384, 8-wide limiter) are unchanged and remain the
contract for held memory; retained per-session working set stays unobservable at the current
scenario granularity (carried gap).

## 5. Ranked reduction opportunities (re-derived against clean magnitudes)

| Rank | Opportunity | Layer | Saving (B/session) | Share of the clean whole cycle (13,679 named) | Confidence | Next step |
|---|---|---|---|---|---|---|
| 1 | **Control-connection reuse / pooling** | framework | 3,792.2 (the per-flow dial). ASSOCIATE (959.0) stays per flow — one association per flow is the SOCKS5 usage model | 27.7 % | size high, feasibility low (product/protocol) | product/protocol ADR (spike); its latency/TTL/port-exhaustion motivations stand independently of allocation |
| 2 | **Teardown path** | bookkeeping | 1,458.5–1,967.9; addressable share unknown; ≈2 first-chance exceptions/session (reproduced) | 10.7–14.4 % | medium — concrete exception lead | teardown component probe (scope drain vs slot/tombstone vs transport dispose + exception source) → remove the exception-shaped control flow |
| 3 | **Session tier (construct/attach/receive start)** | bookkeeping | 2,890.8 leg; C2 = 2,698.7 (93.4 %) | 21.1 % | medium-low; C2 internal split unmeasured | C2 split probe → quiescence-gated managed slab + `TryReset` CTS reuse; .NET 11 trial (§5.2 of the archived design, still sanctioned: the async/lifetime share is a clean measurement) |
| 4 | **Admission + capacity pre-seed** | bookkeeping | 1,317.7 (S0 + admission) | 9.6 % | medium | admission component probe → table/entry footprint + pooled payload slot |

Ordering: the scheduled engineering sequence is unchanged (2 → 3 → 4); item 1 remains gated on
a product decision. What changed is the *weight*: with the harness out, the bookkeeping layer is
42 % of the clean whole cycle (5,727 of ~13,700) instead of ~6 % of the recorded one, so items
2–4 are now first-order work rather than clean-up.

`unsafe` / direct-memory verdict: unchanged — there is no measured buffer materialization to
remove; the managed struct slab is equivalent on the allocation metric and keeps lifetime
safety. Revisit only if a managed variant measurably fails.

## 6. Gaps carried forward

| Gap | Consequence / next probe |
|---|---|
| Teardown internal split (scope drain vs slot/tombstone removal vs transport dispose) + exception source | blocks sizing rank 2; teardown-side component probe (the ≈2 exceptions/session lead is reproduced) |
| Response/retire residue split: ≈0.4 KB/session in the churn (per-wave retire) shape vs ≈3.3 KB/session in the probe (populate-then-drain) shape | the recorded "1.6–3.3 KB" gap is now bounded at both ends but not split; needs a component probe for response-receive vs drain vs retire |
| Retained (post-GC) working set per session | `workingSetDeltaBytes` = 0 again in all 9 footprint rows (100 ms settle, no forced GC) — not observable, not "no cost"; needs longer settle or a forced-GC pass |
| S2 quantization (1 vs ~172 B/session), S3 bracket | the quantization reproduces; the archived +356.9 S3 bracket does **not** (redo bracket +502.8 = S3 4,748.6 − S4 4,245.8, while each leg stays inside its combined run spread) | S4 remains the clean leg; the S3 bracket is shape-produced and should not be quoted across revisions without re-deriving it |
| Fake control connection not expressible (sealed type) | connect+greeting and ASSOCIATE stay protocol-indivisible without a src seam |
| DNS inside the measured dial (address cache null in the benchmark) | production may cache per server; cold-edge option, not a per-session cost |
| TCP churn (deferred) | `LoopbackSocks5TcpServer` carries the same in-process artifact class; measure before optimizing, out of process |
| Probe/churn residue shape difference | named and quantified; no longer a silent mismatch |

## 7. Applied `hot-path.md` §3 replacement (framework bullet)

The recorded §3 framework bullet ("83,442 B/session … 77,448 B = 92.8 % … ≤95,000 churn") is
replaced by:

```markdown
- **Framework socket cost stays outside the bookkeeping ledger but is anchored, measured with
  the loopback SOCKS5 server out of process**: isolated create+dispose 7,952 B/session
  (control connect + greeting 3,792 = 47.7 %; UDP ASSOCIATE 959; relay socket 576; self-traffic
  160; transport ctor + wiring 2,465); churn whole cycle ≤14,500 B/session wave shape /
  ≤14,300 sustained (measured 13,249–14,070 / 13,720–13,869 B, 2026-09-22 redo); real probe
  marginal ≤17,500 B/session (measured 17,021, echo-fed shape). Real-dial instruments must run
  the loopback server out of process (`--socks5-external`, `WINFORWARD_BENCH_EXTERNAL_SERVER=1`);
  the superseded 83,442 / 77,448 / ≤95,000 figures were inflated ~75,500 B/session by the
  in-process harness server's per-connection 64 KiB relay buffer and are not comparable.
  Reusing control connections is the only structural lever in this layer — a product/protocol
  decision whose allocatable share is the ~3.8 KB/session dial.
```

## 8. Reconciled cross-checks

| Quantity | Recorded | This redo | Note |
|---|---:|---:|---|
| framework isolated create+dispose | 83,441.9 | 7,952.0 | harness share 75,489.6 |
| control connect + greeting | 77,447.8 | 3,792.2 | harness share 73,655.1 |
| ASSOCIATE delta | 2,872.0 (reproduced 2,871.7) | 959.0 | the in-process delta carried ~1.9 KB server work |
| real probe marginal | 92,254.8 | 17,021.2 | harness share 75,233.6; residue 3,342.2 vs the named components |
| churn whole cycle (wave) | 90,829–92,451 (all cells) | 13,130–14,147 (all cells) | harness share ≈75–77 KB/session |
| churn sustained | 90,836–91,248 | 13,720.2–13,868.6 | 12.9k–25.5k sessions per run |
| Noop probe marginal | 5,734.6 | 5,727.0 / 5,734.0 (ext / in) | clean instrument; unchanged |
| stage decomposition | S0 488.9 … S5 6,291.5 | byte-identical deterministic rows; noisy legs in spread | clean instrument; unchanged |
| footprint 1000-session allocated | 4,019,880–4,202,592 | 4,044,488–4,131,080 | within the recorded band |
| teardown first-chance exceptions | 130 / 328 / 2,128 | 130 / 328 / 2,128 | byte-identical |

The archived docs' own cross-check note ("the residual ~3.08 KB/session is harness-shaped")
read the 64 KiB buffer as a concurrency effect; it is a per-connection cost, and the clean
probe/churn numbers above show it was carried inside every server-touching figure, not only in
the residual.

## 9. Errata

Errata banners were added to the three superseded archived reports
(`framework-decomposition.md`, `churn-measurements.md`, `reconciliation-and-decision.md`);
`noop-decomposition.md` is unaffected (all its instruments are server-free and were re-validated
byte-identically).
