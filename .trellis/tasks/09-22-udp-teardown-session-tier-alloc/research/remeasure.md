# Re-measure after the adopted reductions (Step 4)

Task `09-22-udp-teardown-session-tier-alloc`. Same instruments, ≥3 runs each, one process at a
time. Raw: `research/raw/remeasure-decomp-run{1,2,3}.log`, `remeasure-noop-run{1,2,3}.log`,
`remeasure-churn-run{1,2,3}.jsonl` (+ `.load`). All runs exit 0. Parsing: `research/tools/parse-bdn-alloc.py`
conventions; churn rows read from the scenario's JSONL `result.metrics`.

## 1. Adoption table — expected vs actual

| Candidate | Expected | Actual | Confirmed by |
|---|---:|---:|---|
| B — context → `readonly record struct` | 240.0 | **240.0** | C2b −240.0, C2 −240 of −352, C2a1 240.0 → 0.0 |
| C — cached delegate fields | ~128 | **128** (visible in pipeline shapes) | S4 −476.4 ≈ B 240 + C 128 + D 112 |
| D — merged receive async methods | not isolated (one boxed SM + Task) | **112.0** | C2's extra −112 beyond B |
| E — drain idle fast path | 88.0 | **88.0** | T1's extra −88 beyond B + D |
| **adopted total** | **~568** | **−565.2** on the Noop probe; −586.8 on S5; −688.6 on the churn cell | Noop 5,727.0 → 5,161.8 |

## 2. Deterministic decomposition cases (3-run means, spread 0.0–0.1)

| Case | Before | After | Δ |
|---|---:|---:|---:|
| `ComponentC2a1_ContextRecordStaticLambda` | 240.0 | 0.0 | −240.0 (B; case re-shaped per its doc) |
| `ComponentC2a2_ContextRecordMethodGroup` | 304.0 | 64.0 | −240.0 (the 240 was the boxed keep-alive; 64 is the delegate guard) |
| `ComponentC2b_SessionConstructOnly` | 1,770.7 | 1,530.7 | −240.0 (B) |
| `ComponentC2_SessionConstructStartAsync` | 2,698.7 | 2,346.7 | **−352.0** (B 240 + D 112) |
| `StageT1_SessionTeardownInsideAsync` | 4,845.4 | 4,405.4 | **−440.0** (B 240 + D 112 + E 88) |
| `StageT5_NonThrowingParkTeardownAsync` | 3,549.2 | 3,109.5 | −439.7 (same set) |
| F / H / C1 / S0 / S1 / C2c\* / C2e\* | unchanged | unchanged | byte-identical (untouched paths) |

## 3. Pipeline-shaped and probe instruments (means of 3 runs)

| Instrument | Before | After | Δ |
|---|---:|---:|---:|
| S3 pipeline-no-payload | 4,693.5 (spread 283.5) | 4,312.4 (216.9) | −381.1 |
| S4 full readiness | 4,355.1 (224.3) | 3,878.7 (270.6) | −476.4 (≈ B+C+D) |
| S5 whole teardown (single-pass) | 6,350.6 (242.5) | 5,763.8 (217.3) | −586.8 (≈ B+C+D+E) |
| T3 expiry retire | 6,776.4 (277.0) | 6,239.8 (201.1) | −536.6 |
| **Noop probe marginal (T1 anchor)** | **5,727.0 (spread 17.4, redo)** | **5,161.8 (spread 14.9)** | **−565.2** |
| Noop product-shaped (−460.8 harness) | 5,266.2 | 4,701.0 | −565.2 |
| Noop N=100 row (`alloc@100 / 100`) | ≈6,500 | ≈5,872 | −628 |
| **churn N=48 D=0 wave cell (out-of-process)** | **13,248.9 (spread 210.5, redo)** | **12,560.3 (spread 182.8)** | **−688.6** |

Churn cell details (all 3 runs): accepted/rejected/firstResponses = 48/0/48, establishment loss 0,
gen0/1/2 = 0/0/0, retireMs 8.39–8.66, p50 9.28–10.27 ms — the shape is intact and lossless.

## 4. Derived legs after the change

| Component | Before | After |
|---|---:|---:|
| Session teardown body (throwing shape) = T1 − C2 | 2,146.7 | 2,058.7 (−88 = E) |
| Benign-park teardown = T5 − C2 | 850.5 | 762.8 (−87.7) |
| Throwing-shape premium = T1 − T5 | 1,296.2 | 1,295.9 (unchanged) |
| Whole teardown delta = S5 − S4 | 1,995.5 | 1,885.1 |
| Expiry retire = T3 − S4 | 2,421.3 | 2,361.1 |
| Retire-shape delta = T3 − S5 | 425.8 | 476.0 |
| Session tier = S4 − S2 | 2,890.8 | 2,503.0 |

## 5. Attribution re-verified (unchanged by the reductions)

Exception counts are byte-identical to the pre-change campaign: S5 = 2/session + fixed 128
(E2: `SetupExecutor.Dispose`, 64 parked workers × 2 BCL events); T1/T3 = 2/session (one canceled
receive across two await sites; E1 = 1 per canceled await); T5/E1/C-series = 0. The reductions did
not alter the exception shape — as designed (candidate A was not adopted).

## 6. Notes

- The churn delta (−688.6) exceeds the adopted set (−568) by ~121 B/session, inside the combined
  run spreads (cell spread 182.8; the redo baseline spread 210.5). The single cell is a
  confirmation, not a re-derived matrix; the full wave matrix / sustained / real-probe shapes were
  not re-run.
- The pipeline legs' spreads (200–280 B/session) bracket their expected deltas; the deterministic
  cases carry the exact confirmation of each candidate.
