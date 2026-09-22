# C2 split — step-2 attribution

Task `09-22-udp-teardown-session-tier-alloc`. Evidence: `research/raw/probe-campaign-run{1,2,3}.log`,
marginals `(alloc@1000 − alloc@1)/999`, means of 3 runs (`research/tools/parse-bdn-alloc.py`).

## 1. Measured cases (B/session, spread across 3 runs)

| Case | Marginal | Spread | Notes |
|---|---:|---:|---|
| `ComponentC2_SessionConstructStartAsync` (existing) | 2,698.7 | 0.0 | reproduced byte-identically |
| `ComponentC2a1_ContextRecordStaticLambda` | 240.0 | 0.0 | context record only (shared collaborators, `GC.KeepAlive` sink) |
| `ComponentC2a2_ContextRecordMethodGroup` | 304.0 | 0.0 | + instance method-group observer |
| `ComponentC2b_SessionConstructOnly` | 1,770.7 | 0.0 | context + claim + fake transport + session ctor (no `Start`) |
| `ComponentC2c1_ScopeConstructLinked` | 216.0 | 0.0 | `new QuiescenceScope(token)` |
| `ComponentC2c2_ScopeConstructUnlinked` | 120.0 | 0.0 | `new QuiescenceScope()` |
| `ComponentC2e_LockConstruction` | 40.0 | 0.0 | `new Lock()` |
| `ComponentC2e_TaskCompletionSourceConstruction` | 88.0 | 0.0 | `new TaskCompletionSource(RunContinuationsAsynchronously)` |
| `ComponentC2e_LinkedTokenSourceConstruction` | 152.0 | 0.0 | raw `CreateLinkedTokenSource(token)` |

## 2. Derivations (B/session)

| Component | Value | Derivation |
|---|---:|---|
| Start leg (receive-loop state machines + parked receive + lease rent + readiness wait) | 928.0 | C2 − C2b |
| Per-session instance-method-group delegate (production pays; the static-lambda probes hide it) | **64.0** | C2a2 − C2a1 (not compiler-cached — measured) |
| Linked-source premium inside the session scope | 96.0 | C2c1 − C2c2 |
| **Production-shaped C2** | ≈ **2,826.7** | C2 + 2 × 64 (the `OnSessionActivity` observer and the `Start(host.RemoveReceiveFailedSession)` handler are both method groups at their call sites in `UdpSessionSetup.CreateSessionAsync`) |

The session object itself is not isolated (it sits inside C2b together with the context, the
association claim ≈ C1's 834.7, the fake transport and the linked scope); no decision depends on
that remainder.

## 3. Reading

- The C2 leg is dominated by async/lifetime machinery: the session ctor block (scope + object,
  incl. the 216 B linked scope) and Start (928.0) — consistent with the redo's "session + context
  + quiescence scope/CTS + receive-loop start" description of C2.
- Two product-side per-session delegate allocations (64 B each) were previously invisible because
  the probe passes static lambdas; `hot-path.md` §3 already prescribes constructor-cached
  method-group delegates, so caching them restores the probe↔production parity and saves ~128
  B/session.
- The context record is a plain record **class** (12 positional members); its 240 B/session is
  pure heap cost that a `readonly record struct` removes without moving memory shape.

## 4. Candidate sizing (feeds design §4)

| Candidate | Measured size | Decision |
|---|---:|---|
| B — `UdpProxySessionContext` → `readonly record struct` | 240.0 | adopt |
| C — cache the two method-group delegates as fields | 64.0 measured + ~64 analogous = ~128 | adopt |
| D — merge `ReceiveLoopAsync` + `ReceiveDatagramsAsync` (one boxed SM + Task less) | not isolated; sized by the re-measure | adopt, measured after |
| E — skip the `_joined` TCS when idle at seal | 88.0 (one TCS; the common teardown path is idle at seal) | adopt (keep the D-contract order) |
| F — `RemoveExpiredAsync` sweep LINQ → hand-rolled snapshot | below threshold: the tuple snapshot array (~16–24 B/session) is inherent to any sweep that runs outside the gate; only the two LINQ iterators are removable (fixed per sweep ≈ 0.1–2 B/session) | **not adopted** (below the 64 B/session rule) |
| G — `Lock` → interlocked protocol (40.0); linked-CTS removal (96.0 + wiring); any protocol change | sized | defer (behavior-sensitive; follow-up) |
| N/A — teardown exception shape (see `teardown-split.md` §3/§4) | 1,296.2 shape delta | not adopted (BCL-inherent) |

Adopted set expected effect (before re-measure): ≈ 368 B/session on the C2 leg (B 240 + C 128;
13 % of the production-shaped 2,826.7) plus ≈ 88 B/session on the retire path (E), with D sized
by the re-measure.
