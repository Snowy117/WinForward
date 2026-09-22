# Teardown split (T leg) — step-2 attribution

Task `09-22-udp-teardown-session-tier-alloc`. Evidence: `research/raw/probe-campaign-run{1,2,3}.log`
(3-batch campaign, all cases), `research/raw/probe-attribution-run{1,2,3}.log` (the E1/E2
attribution micro-cases), parsed with `research/tools/parse-bdn-alloc.py`
(`(alloc@1000 − alloc@1)/999` marginals, means of 3 runs; BDN 0.15.8, `--job short`,
`InvocationCount=1` for the stage cases). Dev box, one process at a time, `/proc/loadavg` per run.

## 1. Measured cases (means of 3 runs, B/session marginal)

| Case | Marginal | Spread | Exceptions (N=1/100/1000) |
|---|---:|---:|---|
| `StageS4_ReadinessTeardownOutsideAsync` (pipeline, teardown out) | 4,355.1 | 224.3 | 0 |
| `StageS5_ReadinessTeardownInsideAsync` (whole teardown, pipeline) | 6,350.6 | 242.5 | **130 / 328 / 2,128** |
| `StageT1_SessionTeardownInsideAsync` (direct fixture, in-window dispose) | 4,845.4 | **0.1** | **2 / 200 / 2,000** |
| `StageT3_ExpiryRetireInsideAsync` (pipeline + in-window expiry sweep) | 6,776.4 | 277.0 | **2 / 200 / 2,000** |
| `StageT5_NonThrowingParkTeardownAsync` (T1, benign park) | 3,549.2 | 1.3 | **0** |
| `ComponentC2_SessionConstructStartAsync` (direct fixture, no disposal) | 2,698.7 | 0.0 | 0 |

Deterministic-row check: S0 488.9, S1 1,317.7, F 104.0, H 356.8, C1 834.7, C2 2,698.7 reproduce the
archived values byte-identically; S2 (1,432.4 vs 1,432.9) and S3 (4,693.5 vs 4,680.5) stay inside
their archived spreads; S4/S5 run ranges overlap the archived ones (4,221.5–4,445.8 vs
4,265.2–4,406.2; 6,212.7–6,455.2 vs 6,325.2–6,500.3).

## 2. Derivations (B/session)

| Component | Value | Derivation |
|---|---:|---|
| **Session teardown body (throwing shape)** | **2,146.7** | T1 − C2 (both direct fixtures; deterministic) |
| — benign-park variant | 850.5 | T5 − C2 |
| — throwing-shape premium (upper bound) | **1,296.2** | T1 − T5 (mixes the two parks' own costs; see §4) |
| Whole teardown delta (pipeline, single-pass) | 1,995.5 | S5 − S4 |
| Expiry retire total | 2,421.3 | T3 − S4 |
| Retire-shape delta (expiry vs wholesale) | 425.8 | T3 − S5 |
| Coordinator machinery (retire path) | ~274.6 | (T3 − S4) − (T1 − C2) |

Shape caveat (carried, not resolved): the direct session-teardown measurement (2,146.7) exceeds
the in-pipeline whole-teardown delta (1,995.5) by ~151 B/session. Both are single-pass windows
with the documented first-pass effects; the direct number is deterministic and is the one used
for component composition, the pipeline delta is the whole-teardown price. The archived docs
carried the same class of window-shape caveat for T (1,968 single-pass vs 1,459 matched).

## 3. Exception attribution (all sources now named)

- **Per-session term = 2 first-chance events = one canceled receive park crossing two await
  sites.** `ComponentE1_CanceledDelayAwaitAsync` (E1) shows a canceled `Task.Delay` await emits
  exactly **1** event per await, so the session's pair is not a doubled single await; T5's
  benign park is 0, so no other throw exists on the teardown path. The two sites: the fake
  transport's own `await Task.Delay(∞, token)` (1) and the session's
  `await _transport.ReceiveAsync(...)` of the now-canceled method (1) — the same two-boundary
  shape a real socket receive has (transport-internal await + session await).
- **Fixed 128 per invocation (S5 only) = `SetupExecutor.Dispose()`**: `_shutdown.Cancel()` makes
  each parked worker's synchronous `_signal.Wait(token)` throw; `ComponentE2_SetupExecutorDisposeAsync`
  (E2) measures exactly **128 = 64 workers × 2** on this box (`max(2 × 32, 16)`). The doubling is
  BCL-inherent to cancelling a parked synchronous `SemaphoreSlim.Wait` (the wait wakes via the
  callback's throw, then the same instance is rethrown where the wait surfaces it); E1 confirms a
  canceled *await* does not double. S0–S4 read 0 because their fixture disposal (executor
  included) runs in `[IterationCleanup]`, outside BDN's counting window.
- **Verdict:** the ≈2/session term is **BCL cancellation mechanics**, not product-thrown control
  flow; the 128 is a fixed executor-shutdown term with no per-session component.

## 4. What is reducible

- The throwing shape's 1,296.2 B/session delta (T1 − T5) is a *shape* delta: it mixes the two
  parks' own allocation (the `Task.Delay` promise versus the TCS + registration + benign-skip
  string + registration disposal, all named in T5's doc) with the exception machinery of two
  throw sites. Removing the outer throw site would require the transport to swallow cancellation
  and return a skip result — a transport/receive-contract change (the skip enum has no benign
  "closed" value; `SkipReason.None` means `HasDatagram = true`) with no measured allocation
  payoff, and the inner BCL throw remains regardless. **Not adopted; recorded as the question a
  future receive-protocol decision would answer.**
- The 128 fixed events are removable only by reworking `SetupExecutor`'s shutdown signaling away
  from `SemaphoreSlim.Wait(token)` (a parked-wait synchronization rewrite with hang risk) for a
  once-per-process, non-per-session saving. **Not adopted; documented.**
- The remaining teardown allocation legs are structural (session object + scope + drain cells +
  state machines), addressed by the C2-split candidates; see `research/c2-split.md` §4.

## 5. Raw and reproduction

```text
dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- --filter '*SessionSetupDecomposition*' --job short
dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- --filter '*ComponentE1*' '*ComponentE2*' --job short
```

Raw logs + loadavg: `research/raw/probe-campaign-*`, `research/raw/probe-attribution-*`.
