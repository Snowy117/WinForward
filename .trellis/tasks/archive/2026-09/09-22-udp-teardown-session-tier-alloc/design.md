# Design — teardown / session-tier splits, then the no-retained-cost reductions

Scope decision (2026-09-22): probes first; reductions limited to allocation trades that keep
the current memory shape (no pools/slabs/CTS-reuse retaining capacity-sized memory — pooling is
a follow-up decision taken with the split numbers in hand). No behavior change; D1–D11 and the
udp-relay session-lifetime contracts stay locked.

## 1. Basis

- T (teardown leg) = 1,458.5 B/session matched / 1,967.9 single-pass, internal split unmeasured;
  the teardown window carries ≈2 first-chance exceptions/session (130/328/2,128 at N = 1/100/1000,
  byte-identical across batches), zero in S0–S4.
- C2 (session tier) = 2,698.7 B/session, 93.4 % of the 2,890.8 leg; internal split unmeasured.
- Instrument to extend: `benchmarks/WinForward.Benchmarks/Perf/SessionSetupDecompositionBenchmarks.cs`
  (BDN 0.15.8, `[MemoryDiagnoser]`, `--job short`, stage-delta convention, fake transports,
  `// Exceptions:` per case, F = 104.0 / H = 356.8 harness subtraction, `[IterationCleanup]`
  teardown outside the window for every case except S5).

## 2. Architecture and boundaries

- **Benchmark side (additive).** New cases in the existing decomposition class, reusing its
  fixtures, readiness helpers and fakes. No existing case changes shape; the in-process default
  and every recorded command line keep reproducing their numbers.
- **Product side (src).** `src/WinForward.Runtime/UdpProxy/*` and
  `src/WinForward.Runtime/QuiescenceScope.cs` only. No public API or `IUdpProxyTransport`
  interface change is expected; all touched types are internal.
- The probe measures with **fake transports** (deterministic, product-machinery attribution);
  the real-transport whole-cycle stays covered by `udp.churn` (out-of-process rule applies there,
  not here).

## 3. Probe design

All new cases are measured at N ∈ {1,100,1000} with the class's existing conventions
(`[Params(1, 100, 1000)]`, `(alloc@1000 − alloc@1)/999` marginals, spread = max−min of ≥3 runs,
`/proc/loadavg` per run, one process at a time).

### 3.1 Teardown split

| Case | Shape | Measures | Derivation |
|---|---|---|---|
| `StageT1_SessionTeardownInsideAsync` | C2-style direct fixture: construct N sessions (real ctor + `Start`), wait for their receive entries, **dispose them inside the window** (today the C2 fixture disposes in `[IterationCleanup]`) | per-session session teardown: scope cancel + transport dispose (fake no-op) + receive-loop join + scope drain; exception count | direct |
| `StageT3_ExpiryRetireInsideAsync` | pipeline populate (`PopulateAndAwaitReadyAsync`), then `RemoveExpiredAsync(now beyond idle, TimeSpan.Zero)` inside the window | per-session expiry retire: `TryBeginExpiry` (scope cancel) + `RemoveSlotAsync` (gate/dict/association/queue/cooldown) + session dispose + the sweep's own arrays; exception count | direct |
| `StageT5_SessionTeardownNonThrowingParkAsync` | T1 with a fake whose receive parks on a `TaskCompletionSource` **completed benignly** by the token registration (returns `HasDatagram = false`, `SkipReason = None`) so the loop exits through the token check, not an exception | isolates the harness-origin cancel throw's share (attribution aid; not a candidate product shape by itself) | delta vs T1 |

Derivations (documented with caveats, not measured directly):

- **Coordinator machinery** ≈ S5 − T1 (S5 sessions are pipeline-built, T1 direct; the object
  graph and ready state are identical, so the delta prices the coordinator's slot/queue/
  association/cooldown work + the slot-completion awaits).
- **Retire-shape delta** = T3 − S5 (expiry-per-session vs wholesale dispose) — the two retire
  shapes the churn/probe compositions already named (≈0.4 KB per-wave retire vs 3.3 KB
  populate-then-drain residue).
- If T3 − T1 leaves an unattributed coordinator remainder, a `StageT4_ExpiryRetireDirectAsync`
  (T1 fixture + `TryBeginExpiry` + `RemoveSlotAsync` without the sweep) can be added.

### 3.2 C2 split

| Case | Shape | Measures |
|---|---|---|
| `ComponentC2a_ContextRecordOnlyAsync` | `new UdpProxySessionContext(...)` per session, two variants: static lambda and **instance method-group** delegate for `ActivityObserver` (production passes `UdpSessionSetup.OnSessionActivity`; today's C2 passes a static lambda and hides that allocation) | context record allocation; per-session delegate cost |
| `ComponentC2b_SessionConstructOnlyAsync` | `new UdpProxySession(context)`, no `Start`, keep alive | session object + scope construction (linked CTS + parent registration) |
| `ComponentC2c_ScopeConstructOnlyAsync` | `new QuiescenceScope(token)` per session; linked vs unlinked sub-cases | linked-source overhead alone |
| (derivation, no new case) | Start cost = existing `ComponentC2_SessionConstructStartAsync` − C2b (both pass static lambdas; the delta also carries the readiness spin-wait, small and noted) | receive-loop state machines + parked receive + receive-window lease rent |
| `ComponentC2e_*` micro-cases (one method per item, filterable) | `new Lock()` keep-alive, `new TaskCompletionSource(RunContinuationsAsynchronously)` keep-alive, `CreateLinkedTokenSource(token)` with cleanup disposal | sizes for the adoption table (design §4 B/C/E/G) |

Harness discipline: each sub-case keeps its own fake/harness cost constant across variants and
records it with the shape; if a new fake adds a per-session cost not covered by F/H, extend the
subtraction explicitly (never silently absorb it). Deterministic sub-cases must reproduce
within ≤1 B across runs.

### 3.3 Exception attribution rules

- BDN's `// Exceptions:` counts first-chance throws (verified on the recorded batches: caught
  teardown throws are counted; S0–S4 report zero). Per-case deltas attribute a throw to the
  sub-path it was measured in; code reading then assigns the origin (product vs harness/BCL).
- A harness-origin throw (e.g. the fake's `Task.Delay` cancel) must be quantified by T5 before
  any product-side claim, and any fake change is a **new shape** whose numbers are quoted
  alongside the old fake's, never mixed.
- A BCL-inherent throw (e.g. canceling a pending socket receive) is documented as inherent with
  the exact mechanism; it is not counted as a reducible product cost.

## 4. Reduction candidates (adoption table)

Adoption rule: adopt if (a) it removes attributed **product-origin** teardown exceptions, or
(b) its measured size is ≥ ~64 B/session at low/medium risk and it keeps the memory shape;
otherwise record the measured size and defer. Every adopted candidate is re-measured with the
same instruments (before/after, ≥3 runs). Measured sizes and decisions below come from
`research/teardown-split.md` and `research/c2-split.md` (3-batch campaign, 2026-09-22).

| ID | Candidate | Site | Measured | Decision |
|---|---|---|---|---|
| A | Teardown exception removal | receive-loop cancel path | 2 events/session = BCL cancellation across 2 await sites (E1/E2/T5); the 1,296.2 B/session T1−T5 delta is a shape delta with no removable product cost | **not adopted**; documented (redo the question only with a receive-contract change) |
| B | Context record → `readonly record struct` | `UdpProxySessionContext` | 240.0 B/session (C2a1) | **adopt** |
| C | Cache method-group delegates as fields | `UdpSessionSetup` (`OnSessionActivity` observer, `RemoveReceiveFailedSession` handler) | 64.0 measured (C2a2 − C2a1) × 2 sites ≈ 128 | **adopt** |
| D | Merge `ReceiveLoopAsync` + `ReceiveDatagramsAsync` | `UdpProxySession` | not isolated | **adopt**; sized by the re-measure (one SM box + Task) |
| E | Drain cell: skip `_joined` when idle at seal | `QuiescenceScope.DrainAsync` | 88.0 B/session at seal (C2e TCS) | **adopt**; keep cancel → join → dispose-CTS → complete order |
| F | Coordinator sweep trim (`Where/Select` arrays) | `RemoveExpiredAsync` | below threshold — the snapshot array is inherent to an outside-gate sweep; only the LINQ iterators are removable (≈0.1–2 B/session) | **not adopted** (below the rule) |
| G | `Lock` → interlocked protocol (40.0); linked-CTS removal (96.0 + wiring) | `UdpProxySession` / `QuiescenceScope` | sized | **defer** (behavior-sensitive follow-up) |
| H | SetupExecutor worker-shutdown OCE removal | `SetupExecutor.WorkerLoop` / `Dispose` | fixed 128/invocation (E2); no per-session component | **not adopted** (synchronization-primitive rewrite risk) |

Measured effect after the re-measure (`research/remeasure.md`): B 240.0 and D 112.0 land in the
C2 leg (2,698.7 → 2,346.7); C 128.0 surfaces in the pipeline shapes (S4 −476.4 ≈ B+C+D); E 88.0
in the teardown legs (T1 −440.0 = B+D+E); adopted total −565.2 on the Noop probe (5,727.0 →
5,161.8) and −688.6 on the churn N=48/D=0 cell (13,248.9 → 12,560.3, inside the combined run
spreads of the expected −568).

## 5. Compatibility and rollback

- Benchmark additions are additive (new case names only); recorded command lines from the
  archived tasks still run. Rollback: delete the new cases (no other change depends on them).
- src changes are behavior-zero, gated by the existing test suite; each candidate is its own
  reversible commit group. Anchors change only after the re-measure; a candidate that does not
  reproduce its expected saving is reverted.
- No interface or public-API change expected; `IUdpProxyTransport` untouched (a "benign closed
  receive" is expressible inside the existing `Socks5UdpReceiveResult` contract — the
  `HasDatagram = false` skip shape already exists — but is only relevant if attribution shows a
  product-origin benefit).

## 6. Risks

- **Shaping effects**: single-pass windows carry the documented first-pass effect (~0.4–0.6
  KB/session, mostly in teardown); matched `InvocationCount=6` cross-check required for any T
  number quoted as an anchor.
- **Struct copying**: a fat `readonly record struct` context copies on every use; if the
  analyzer gates or readability disagree, fall back to the parameter-passing refactor.
- **Fake/exception confounding**: never let a fake-shape improvement stand in for a product
  saving; T5 + explicit shape naming is the control.
- **Probe additions must not shift existing cases** (S0–S5/C1/C2 reproduce byte-identically
  after the additions — re-verified as part of the probe batch).

## 7. Measurement protocol

```text
# decomposition family (probes), from the repo root, one process at a time, ≥3 runs
dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- --filter '*SessionSetupDecomposition*' --job short
# focused runs while iterating
dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- --filter '*StageT1*' '*StageT3*' '*ComponentC2*' --job short
# end-to-end confirmation after src changes
... --filter '*UdpSession*' --job short                      # Noop probe gate (T1 anchor)
... --stability --scenario udpchurn --burst-flows 48 --dial-delay-ms 0 --churn-waves 1 --socks5-external
```

Raw logs + `/proc/loadavg` per run archived under this task's `research/raw/`; parsing follows
the archived `research/tools/parse-bdn-alloc.py` convention.
