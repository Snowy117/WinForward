# Implement: UDP association reuse and session resource budget

> Task `09-28-udp-association-reuse`. Order matters: Step 1 (budget/retention/observability) is
> independently shippable and is the mitigation for the reported degradation; Step 2 (pool) is the
> structural fix; Step 3 (detection) makes default-on safe. Every step ends green and committed.

## Pre-flight (before Step 1)

- [ ] Re-validate every `file:line` anchor in `prd.md` against HEAD (`git log -1`); the tree moved
      since the diagnosis session.
- [ ] Record the baseline for the acceptance run: diagnosis probe (20 flows/s, 60 s) +
      `--stability --scenario udp --pps 25000 --duration 60` + `--stability --scenario burst`
      (N=8/48/128, dial-delay 0/100/4000) into `benchmarks/results/2026-09-28-udp-reuse/baseline.jsonl`.
- [ ] Confirm no other in-flight edit touches `Socks5UdpTransport`, `IdleExpirySweeper`,
      `DurableCaptureBundle`, `ConfigurationModels`.

## Step 1 — Budget, retention, observability (R4/R5; no reuse yet)

- [ ] `ConfigurationModels.cs` + `ConfigurationLoader`: add and validate `udpSessionCapacity`
      (default 16384, 1..16384, warn > 4096 with the 2-ports/flow rationale), `udpRelayReceiveBufferKb`
      (default 128, 16..1024, warn > 256 when capacity > 2048), `udpSessionIdleSeconds`
      (default 30, 5..600). Wire into `ValidationLimits`/`ValidatedConfiguration`.
- [ ] `UdpProxyOptions.cs`: `Capacity`, `RelayReceiveBufferBytes`, `SessionIdleSeconds`;
      `UdpProxyComposer.Create` passes them; `Socks5UdpTransport.CreateAsync` uses the configured
      buffer instead of the 512 KiB constant (update the "not a config knob" comment).
- [ ] `IdleExpirySweeper.cs`: sweeper interval becomes `max(5 s, udpSessionIdleSeconds / 2)` for the
      UDP leg (keep the existing constructor default for other legs); UDP idle timeout from options.
- [ ] `RuntimeCounters.cs` + rejection paths: add `UdpCapacityRejections`, `UdpSetupFailures`,
      `UdpRelayBufferBytes`-style aggregates; promote `udp.session.rejected` (capacity/setupRing) and
      setup-failure logging from trace to a rate-limited warn (reuse `RuntimeLogThrottle`).
- [ ] `RuntimeHeartbeat.cs` + `Cli/Program.cs`: `udpSessions`, `udpCapacity`, `udpRelaySockets`,
      `udpRelayBufferMB`, `udpSetupFailures` deltas.
- [ ] Tests: `ConfigurationLimitsTests` (new keys, boundaries, warnings), `UdpSetupQueueTests`
      unaffected, a transport test asserting the configured buffer size is applied.
- [ ] Validation runs (must be green before Step 2):
      `dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- --stability --scenario udp --pps 25000 --duration 60 --output benchmarks/results/2026-09-28-udp-reuse/step1-udp.jsonl`
      `… --stability --scenario udpBurst --burst-flows 48 --dial-delay-ms 100 --duration 60`
      `… --stability --scenario churn --burst-flows 48 --churn-waves 0 --duration 120 --socks5-external`
      Gate: loss rate 0 and no burst first-response regression beyond the recorded band; if the
      128 KiB buffer loses responses, raise the default and re-run (record the number that passed).

## Step 2 — Association pool (R1/R3/R6/R7; `off` + `always` modes)

- [ ] New `UdpProxy/UdpAssociationLease.cs`, `UdpProxy/UdpAssociation.cs`,
      `UdpProxy/UdpAssociationPool.cs` (≤ 400 effective lines each, split on the cap).
- [ ] `UdpAssociation`: dial (reuse the `createControl` seam), ASSOCIATE once, relay endpoint +
      family, refcount, `FlowsPerAssociation` cap, watchdog (`scope.Run`, 1-byte read + 5 s sampling
      tick), in-place re-association (I7), fault fan-out (I4), D11 one-shot teardown.
- [ ] `UdpAssociationPool`: per-server warm set, `RentAsync`, `MaxAssociationsPerServer = 16`,
      local-port hashing, `off` mode = one private association per lease, `always` mode = share,
      drain nesting, capability field placeholder.
- [ ] `Socks5UdpTransport`: take `(lease, socket, relayEndpoint, selfTrafficToken, options)`;
      ownership inversion; mutable relay `SocketAddress`; `DatagramsSent`/`SawResponse` counters;
      `associationLost` callback; `SendSpanAsync` fail-fast once the lease is faulted.
- [ ] `UdpProxySession`: register the fault callback (construction-time delegate, no per-datagram
      cost) → `_scope.RecordFault` + the existing synchronous receive-failure signal.
- [ ] `UdpTeardownReason.AssociationLost`; `UdpProxyCoordinator` needs no gate change — assert I5
      (no cooldown armed) by test.
- [ ] `Cli/DurableCaptureBundle.cs`: create the pool before the coordinator, dispose it after the
      coordinator (before/after the native pools per design §2), keep `off` mode identical.
- [ ] Tests: `UdpAssociationPoolTests`, `UdpAssociationRecoveryTests`, `Socks5UdpTransportLeaseTests`,
      plus `UdpRelayTests`/`UdpProxyCoordinatorTests` green; new TestHelpers fakes for the drop case.

## Step 3 — Capability detection and sticky fallback (R2, default `auto`)

- [ ] `UdpAssociation.SampleCapability()` (5 s tick) implementing design §5; `ServerCapability`
      sticky per pool; `udp.association.fallback` warn + `UdpAssociationFallbacks` counter; existing
      shared associations drain, sessions survive the flip.
- [ ] Tests: `UdpAssociationCapabilityTests` (pinning detected, permissive server never flips,
      single-attach stays `Unknown`, `always`/`off` overrides, sticky for the run).

## Step 4 — Soak scenario and acceptance

- [ ] New `benchmarks/.../Stability/UdpSessionBudgetScenario.cs` + `SoakScenario.SessionBudget`:
      `--rate`, `--capacity`, `--churn-seconds`, `--drain-seconds`; asserts flat sockets/fds/buffer
      estimates, reports per-phase series (port the diagnosis probe into the harness).
- [ ] Acceptance run: `--stability --scenario sessionBudget --rate 100 --duration 3600` with the
      default configuration; plus `udp.churn`, `udp.burstEstablishment`, `udp.lossRate` matrices;
      store under `benchmarks/results/2026-09-28-udp-reuse/`.

## Step 5 — Docs, spec, wrap-up

- [ ] `README.md` config table (4 new keys, default-on statement, fallback behaviour).
- [ ] Spec updates (Phase 3.3): `udp-relay.md` (pooling contract, retention/buffer/budget policy,
      `AssociationLost`), `hot-path.md` (two counters, allocation claim unchanged),
      `error-handling.md` (association-loss fail-closed + no-cooldown).
- [ ] Journal + `task.py archive` per the finish-work flow.

## Validation commands (every step)

```
dotnet build WinForward.slnx -c Release
dotnet test WinForward.slnx -c Release
dotnet format WinForward.slnx --severity info --verify-no-changes --no-restore
jb inspectcode -f=Xml -e=HINT -o=/tmp/jb-inspectcode.xml WinForward.slnx    # zero <Issue>
```

## Risky files and rollback points

| Area | Risk | Guard / rollback |
|---|---|---|
| `Socks5UdpTransport` send path | hot-path regression | allocation gate + `udp.lossRate`/`churn` matrices; revert Step 2 only if the gate fails |
| `UdpProxySession` fault wiring | teardown-vs-signal deadlock (the F1 precedent) | synchronous callback only, mirroring `RemoveReceiveFailedSession`; `DisposeAsyncWaitsForAnOutstandingSendLease` and the receive-failure suites stay green |
| `DurableCaptureBundle` disposal order | lease outliving the pool | `UdpAssociationPoolTests` drain-join test + coordinator dispose suite |
| Default-on sharing | server incompatibility in the field | `udpAssociationReuse: off` (R7) is a first-class tested mode; sticky fallback |
| Retention/buffer retune | response loss under bursts | Step 1 validation gate before Step 2; recorded matrices |

## Follow-up checks before `task.py start`

- [x] design §9 change boundary matches the actual edit list; no file crosses 400 effective lines
      (re-verified after Step 1: max 374).
- [x] `implement.jsonl` / `check.jsonl` curated with the specs this task must respect.
- [x] Planning summary approved by the user (D1 settled by the user; D2/D3 approved with the plan).

---

## Verification log

### Phase A — Step 1 (budget / retention / observability)

| Round | Agent | Outcome |
|---|---|---|
| Implement | `trellis-implement` (subagent) | 3 config keys + validated wiring, 128 KiB default buffer (from 512 KiB), 30 s UDP retention + fast UDP sweep cadence, 4 counters + rate-limited warns, heartbeat estimate. Build 0 warnings, 840 tests (baseline 807), format empty. |
| Check | `trellis-check` (subagent) | **PASS-WITH-FIXES**: 25 `jb inspectcode` issues (all in the new files), duplicated event names, untested composition `int` seams, missing baseline dir, `--scenario burst` typo. |
| Fix | `trellis-implement` (subagent) | Visibility/suppression fixes, leg isolation, event-name split (`udp.session.capacity-block`), seam tests, baseline recorded. |

**Parent rebuttal (recorded so it is not "fixed" again):** the check's "a failing UDP leg starves the
main expiry legs forever" claim is **not reproducible**. `IdleExpirySweeper.RunAsync` runs the gated
main-leg group *before* the UDP leg and stamps `lastMainSweepUtc` before running it, so a UDP-leg
fault aborts only that tick's debug log while the main legs keep their 60 s clock. The fix round adds
explicit per-group `try`/`catch` isolation as a defensive improvement, not as a bug fix.

**Parent finding folded into the fix round:** the aggregate kernel-buffer warning had a blind spot —
the (default 128 KiB × raised `udpSessionCapacity`) combination, e.g. 128 KiB × 16384 = 2 GiB, was
silent. The aggregate arithmetic now rides the existing `udpSessionCapacity` warning (which fires
only above the 4096 default, so defaults stay silent).

**Step 1 acceptance evidence** (recorded under `benchmarks/results/2026-09-28-udp-reuse/`):
`udp` 25k pps × 60 s → 1,495,689 sent = received, loss 0, 24,925 achieved pps; `udpBurst` 48 × 100 ms
→ 48/48 first responses, loss 0, p50 304 ms (2026-09-06 baseline 325 ms); `churn` 48 flows × 120 s
external server → 77,328 sessions, 0 rejected, loss 0, 13,066 B/session (anchor ≤14,500).

### Phase B — Step 2 (association pool)

| Round | Agent | Outcome |
|---|---|---|
| Implement | `trellis-implement` (subagent) | `UdpControlAssociation` + `UdpAssociationPool` + `UdpAssociationLease`; transport ownership inverted (lease instead of owning the control connection); `udpAssociationReuse` key (`auto` off-equivalent until Step 3); `AssociationLost` teardown reason + `udpAssociationLost`/`udpAssociationRecovered`; heartbeat `udpAssociations`/`udpLeasedFlows`. Build 0 warnings, 872 tests (baseline 843, +29), format empty, acceptance with sharing ON zero loss and **churn 13,066 → 7,557 B/session (−42 %)**, first-response p50 12.5 → 3.2 ms. |
| Check | `trellis-check` (subagent) | **PASS-WITH-FIXES**: 19 `jb inspectcode` issues; the setup-flush path misclassified association loss as `SetupFailure` (armed the 1 s cooldown); watchdog had no catch-all; relay endpoint/address could pair across two publications; a tautological test and three missing behaviour-pinning tests. Independently reproduced both headline numbers (7,556.7 B/session, p50 3.241 ms) and confirmed the interfaces are byte-identical to HEAD. |
| Fix | `trellis-implement` (subagent) | jb → 0, flush-path mapping, watchdog catch-all, single immutable relay-target publication, restored/added tests, plus the two parent findings below. |

**Parent findings folded into the fix round:** (P1) the pool's maintenance `PeriodicTimer(period,
TimeProvider)` silently faulted for providers without `CreateTimer` (the test fake), killing
retention — the timer now uses the system clock while the idle comparison keeps the injected clock;
(P2) a *shared* association that faults with zero leases was never disposed (its last release had
already run) and lingered in `All` — the maintenance sweep now retires faulted zero-lease
associations, never from inside the watchdog (that would self-drain).

**Naming deviation:** the pooled owner is `UdpControlAssociation` because
`WinForward.Runtime.UdpProxy.UdpAssociation` already exists (the flow↔relay-alias record used by
`UdpAssociationTable`); `UdpAssociationPool`/`UdpAssociationLease` keep their planned names.

**Deferred to Step 3:** `auto` becomes share + passive detection (so the default-mode acceptance run
lands there); the harness scenarios switch from the hard-coded `always` to the production default;
`GcSoakScenario` (pre-existing over-cap file, now hard-coded `always`) is revisited then.
**Flagged:** `ConfigurationModels.cs` is at 397/400 effective lines — the next configuration key must
split the file first.

