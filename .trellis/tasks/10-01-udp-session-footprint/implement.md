# Implementation plan — F6 UDP per-session footprint: relay receive buffer, pool sizing, adaptive idle TTL

Five code steps plus one instrumentation step that lands first, and the evidence/spec work. Each step
lands its facts **with** its change, so every commit leaves a green, shippable tree; the red readings
(the pool overflow growth at the old capacity, the pre-change series) are captured transiently during
the step and recorded in the artifact README. Run the full gate set at every rollback point.

Prerequisites already true: `dotnet` on PATH via direnv, `rg`, and the recorded baselines under
`benchmarks/results/2026-09-28-udp-reuse/` and `benchmarks/results/2026-09-29-benchmark-coverage/`.

Reference documents: `research/implementation-notes.md` (code anchors, the reproduce-before commands
and the discrepancy table) and `design.md` (mechanisms, trades, acceptance mapping, measurement plan).
Read them beside this checklist; the step numbers below match `design.md` §1.

## Step 0 — Preflight

- [ ] Re-verify the anchors in `research/implementation-notes.md` §2–§6 against the current source
      (`Socks5UdpTransport.cs`, `UdpProxySession.cs`, `UdpProxyCoordinator.cs` + `.Send.cs`,
      `NativeBufferPool.cs`, `UdpAssociationCapability.cs`, `UdpAssociationLease.cs`,
      `IdleExpirySweeper.cs`, `DurableCaptureBundle.cs`, `UdpProxyComposer.cs`,
      `ConfigurationLimits.cs`, `ConfigurationModels.Udp.cs`). Record any drift in the notes.
- [ ] Record the green baseline **before** the first edit: `dotnet test WinForward.slnx -c Release`
      (note the totals: analyzers + `Core.Tests`) and `dotnet build WinForward.slnx -c Release`
      (zero-warning). A green tree must be proven before it is changed.
- [ ] Confirm the container sizes against the 400-effective-line rule
      (`directory-structure.md:55-61`): `UdpProxyCoordinator.cs` **392**, `ConfigurationLimitsTests.cs`
      **350**, `UdpProxySession.cs` **337**, `Socks5UdpTransport.cs` **301**, `IdleExpirySweeper.cs`
      **146** — and two that are **already over**: `SweepAllocationGateTests.cs` **508** (so Step 4's
      new gate goes in a new file) and `UdpSessionBudgetAcceptanceTests.cs` (check before adding rows;
      the Step 4 re-base edits existing literals rather than adding facts). Step 4 must also move
      `RemoveExpiredAsync` into a new partial (`UdpProxyCoordinator.Sweep.cs`) rather than grow the
      main file.
- [ ] Confirm the two UDP exact gates that must not move are green **in isolation**:
      `--filter "FullyQualifiedName~HotPathAllocationGateTests"` and
      `--filter "FullyQualifiedName~Socks5UdpTransportSendTests"`, and the three sweep-area gates:
      `--filter "FullyQualifiedName~SweepAllocationGateTests"`.

## Step 1 — Instruments first: the pool-cycle row, the before-artifacts, the red-before

The existing instruments cannot attribute any of the three changes: `udp.sessionFootprint` drives no
sweep and uses fake transports (`research/implementation-notes.md` §7/D8), and `udp.sessionBudget`
derives its own sweep interval and drives one timeout. Everything that has a **before** state lands
here and is verified before the before-artifact, so before and after are the same instrument. The
adaptive-TTL instrument deliberately does **not** land here (Step 4): it measures a mechanism the
pre-change tree does not have, so it carries its own internal control instead of a before-artifact.

- [ ] `Stability/SessionFootprintScenario.cs`: keep the documented 1/100/1000 row and its three
      fields **exactly** as they are (`benchmarks/README.md:187-190` documents them); after that row's
      existing measurement, run a **second population cycle** over the same pool and emit
      `udp.sessionFootprint.cycle` with `{ sessions, overflowFirstWave, overflowSecondWave }` read
      from the shared pool's `Stats` (internal, friend-assembly visible — `GcSoakScenario` already
      reads it). This row is the pool claim's before/after series, and the second-wave delta is the
      only figure that discriminates (see `design.md` §3). **In this step the scenario's pool stays
      exactly as the pre-change composition builds it** (the literal
      `NativeBufferPool(ReceiveWindowSize(cap))`, default capacity 256), so the before-run reads the
      red: **+744 at 1,000 sessions**, +44 at 300.
- [ ] Capture the **before** artifacts at unmodified product HEAD (the instrumentation is the only
      change on the tree), one run per file:
      ```bash
      mkdir -p benchmarks/results/2026-10-01-udp-session-footprint
      dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- --stability --scenario footprint \
        --output benchmarks/results/2026-10-01-udp-session-footprint/footprint-before.jsonl
      dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- --stability --scenario udpSessionBudget \
        --rate 100 --churn-seconds 90 --drain-seconds 120 --socks5-external --require-pooling \
        --output benchmarks/results/2026-10-01-udp-session-footprint/session-budget-before.jsonl
      dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- --stability --scenario udp \
        --pps 25000 --duration 60 \
        --output benchmarks/results/2026-10-01-udp-session-footprint/udp-loss-before.jsonl
      dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- --stability --scenario udpBurst \
        --burst-flows 48 --dial-delay-ms 100 --duration 60 \
        --output benchmarks/results/2026-10-01-udp-session-footprint/udp-burst-before.jsonl
      dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- --stability --scenario udpChurn \
        --burst-flows 48 --churn-waves 0 --duration 120 --socks5-external \
        --output benchmarks/results/2026-10-01-udp-session-footprint/udp-churn-before.jsonl
      ```
      Expected shapes are in `research/implementation-notes.md` §8; a before-series that misses its
      recorded band stops the step (it means the instrument changed the shape, not the product).
- [ ] Start `benchmarks/results/2026-10-01-udp-session-footprint/README.md` with the host/runtime
      header, the base revision, every command line, the empty before/after tables, the projection
      table (`design.md` §8), and the attribution rules (exact counts vs series; the kernel estimate
      is arithmetic).
- [ ] Write the **red-before** for Step 3 as a test on the unmodified composition: the pool-cycle fact
      with the pre-change capacity (256) and a 300-session population must show **+44** overflow
      growth on the second cycle. Record the number and the command in the README; leave the fact
      skipped/failing locally, not committed red.

Validation for the step: the footprint cycle row appears with two overflow readings and the
second-wave delta is non-zero (the red), the soak still passes its own verdict unchanged, and the
before-artifacts exist. **No product code is touched in this step.**

Rollback: delete the additive scenario row + the before-artifacts (nothing product-visible depends on
either).

## Step 2 — Site 1: relay receive buffer default 128 → 64 KiB — **rollback point A**

- [ ] `src/WinForward.Runtime/Socks5/Socks5UdpTransport.cs:174`:
      `DefaultRelaySocketReceiveBufferSize = 64 * 1024`, and rewrite the XML doc at `:163-173` to state
      the trade (≈44 max-size / ≈128 512-byte responses absorbed per socket; the aggregate is
      per-session bytes × concurrent sessions; the config key restores 128 KiB).
- [ ] `src/WinForward.Configuration/ConfigurationModels.Udp.cs:66`:
      `DefaultUdpRelayReceiveBufferKb = 64` (the bytes constant at `:69` derives it).
- [ ] `tests/WinForward.Core.Tests/ConfigurationLimitsTests.cs`: add the constant-pair guard
      (`DefaultUdpRelayReceiveBufferKb * 1024 == Socks5UdpTransport.DefaultRelaySocketReceiveBufferSize`
      — nothing asserts it today) and an explicit-override row for the **old** value
      (`udpRelayReceiveBufferKb: 128` → 128 KiB, the PRD's "override restores today's value"); re-base
      the two `InlineData` aggregates in `ConfigurationWarnsAboveDefaultUdpSessionCapacityWithoutBlocking`
      (`:171-176`) from 512/2,048 to **256/1,024** MiB, with a comment saying they are derived from the
      default.
- [ ] `tests/WinForward.Core.Tests/UdpSessionBudgetAcceptanceTests.cs:215` — **the third re-based
      row**: the misattribution case hard-codes `relayReceiveBufferBytes: AcceptanceLoadSessions *
      64 * 1_024`, which is exactly the new default, so `UdpSessionBudgetAcceptance.cs:209-211` would
      stop firing and `Assert.False(misattributed.RetentionBounded)` would fail. Re-point it at a
      value that is still misattributed (`32 * 1_024`) and note in the row why 64 KiB no longer
      qualifies. Record it beside the two aggregates in the artifact README.
- [ ] `tests/WinForward.Core.Tests/Socks5UdpTransportLeaseTests.cs` (120 effective lines): add the fact
      that closes the unused `AppliedRelayReceiveBufferSize` seam — a transport built through the
      production `Socks5UdpTransportFactory` with an explicit 128 KiB budget applies at least that to a
      real loopback socket, and the same for the new default; assert `applied >= requested`, record the
      read-back in the artifact, and state why (the Linux kernel doubles `SO_RCVBUF`).
- [ ] Re-run the two must-not-move series that the buffer can actually move (`udpLoss`, `udpBurst`)
      into the `-after` files and compare against `research/implementation-notes.md` §7. If either
      moves across its band, stop and take the `udpRelayReceiveBufferKb: 128` fallback (design §2).

Validation: the touched/added facts in isolation, `FullyQualifiedName~ConfigurationLimits`,
`FullyQualifiedName~Socks5Udp`, then the full gate set.

Rollback: restore the two constants, the two `InlineData` numbers and the misattribution row's 64 KiB
literal; drop the new facts. Nothing else reads the constants except through the configuration.

## Step 3 — Site 2: receive-window pool sized from the session capacity — **rollback point B**

- [ ] `src/WinForward.Runtime/UdpProxy/UdpProxyCoordinator.cs`, beside `ReceiveWindowSize` (`:215`):
      `internal static int ReceiveWindowPoolCapacity(int sessionCapacity)` =
      `checked(sessionCapacity + ReceiveWindowRetireHeadroom(sessionCapacity))` and
      `internal static int ReceiveWindowRetireHeadroom(int sessionCapacity)` =
      `Math.Max(4 * ConfigurationLoader.DefaultUdpAssociationFlowsPerAssociation, sessionCapacity / 16)`,
      with the derivation doc from `design.md` §3: one lease per live session **plus** the
      retire/admit allowance, because `RemoveSlotAsync` removes the slot under `_gate` and only then
      awaits disposal (`UdpProxyCoordinator.cs:512-518`, `:536`), receive-failure teardowns are
      concurrent scope children (`:546-547`) that never take `_sweepGate`, and the lease returns only
      in the loop's `finally` (`UdpProxySession.cs:334`) — no in-code bound, so a documented policy
      number (four association-wide fault bursts at the default fan-out, and at least a sixteenth of
      the capacity), with the attribution-pool precedent named (`DurableCaptureBundle.cs:236-238`).
      Record the allowance table (300 → 64 → 364; 16,384 → 1,024 → 17,408; 96 KiB / 1.5 MiB) and the
      residual (a storm larger than the allowance still allocates transiently).
- [ ] `src/WinForward.Cli/DurableCaptureBundle.cs:233`: pass
      `UdpProxyCoordinator.ReceiveWindowPoolCapacity(configuration.UdpSessionCapacity)` as the pool's
      capacity; keep the comment block that ties the pool size to the coordinator's window size.
- [ ] `benchmarks/WinForward.Benchmarks/Stability/SessionFootprintScenario.cs:31`: construct the
      pool through `UdpProxyCoordinator.ReceiveWindowPoolCapacity(sessions)` so the instrument **mirrors
      the composition** (without this the after-row still reads +744 at 1,000 sessions and contradicts
      the exact gate). The Step 1 before-run keeps the literal default so the red exists; the README
      states that both the pool rule and the soak cadence are composition mirrors that move with the
      product by design.
- [ ] `tests/WinForward.Core.Tests/UdpReceiveWindowPoolTests.cs` (new):
      - `ReceiveWindowPoolSizedFromTheSessionCapacityDoesNotGrowAcrossAPopulationCycle` — capacity
        `ReceiveWindowPoolCapacity(300)` (= 364); rent 300, release 300, rent 300; assert
        `Stats.OverflowAllocations` is **unchanged** across cycle 2, and the balance identity
        (`Rented == Returned`, `Outstanding == 0`, `InPool == 300`) after the final release
        (`quality-guidelines.md:47`). Release every lease in a `finally` **before** asserting, so a
        failing window cannot strand a rental.
      - `ACoordinatorCyclesItsSessionCapacityWithoutOverflowGrowth` — a coordinator at capacity 300
        over fake transports, two full populate/dispose cycles, the shared pool's overflow growth
        asserted 0; the population proven before the second cycle (the fake factory's created count
        cross-checked against `SessionCount`), per the measurement self-check rule.
      - `ReceiveWindowPoolCapacityIsTheSessionCapacityPlusTheRetireAllowance` — the pure rule rows:
        `cap + ReceiveWindowRetireHeadroom(cap)`, the two allowance bands (64 / 1,024), and the shipped
        16,384 → 17,408 value. **Not** `== cap`.
      - the existing `NativeBufferPoolTests` balance/dispose-race facts stay green (the balance
        contract is unchanged; only the composition's argument moves).
- [ ] Record the **red-before** captured in Step 1 (capacity 256 → +44 at 300 sessions; +744 at
      1,000) in the artifact README beside the green row.

Validation: the new class in isolation (Release — the balance assertions are exact), the F3 sweep gate
`--filter "FullyQualifiedName~SweepAllocationGateTests"` unchanged, `FullyQualifiedName~UdpProxy`,
then the full gate set, plus `--stability --scenario footprint` for the cycle row (it must now read
+0 on the second cycle).

Rollback: restore the one-argument pool construction at both sites (composition and the scenario); drop
the rules and the new facts.

## Step 4 — Site 3: the evidence seam, the classification, the two-class sweep, the cadence — **rollback point C** (one commit)

The seam and its only consumer land together: a seam with no reader is a dead internal member, which
the `jb inspectcode` gate can legitimately flag (`design.md` §9).

- [ ] `src/WinForward.Runtime/Socks5/Socks5UdpTransport.cs`, beside `IUdpProxyTransport` (`:76-91`):
      the internal `IUdpExchangeCounters { int DatagramsSent; bool SawResponse; }` with the doc stating
      the null-means-sustained rule; `Socks5UdpTransport` implements it **explicitly** as
      `=> _lease.DatagramsSent;` / `=> _lease.SawResponse;`. The file is at 301 effective lines; keep
      the interface doc tight.
- [ ] `src/WinForward.Runtime/UdpProxy/UdpAssociationLease.cs`: two internal read accessors over the
      existing `_evidence` object. **No writer changes** — nothing in this step touches
      `RecordDatagramSent` / `RecordResponseReceived` or any send/receive line.
- [ ] `src/WinForward.Runtime/UdpProxy/UdpProxySession.cs`:
      - `private const int OneShotDatagramThreshold = 1;` with the derivation doc (`design.md` §4.1);
      - `private readonly IUdpExchangeCounters? _exchange;` assigned in the ctor from
        `context.Transport as IUdpExchangeCounters` (one cast per session, cold);
      - `private bool IsCompletedOneShotExchange => _exchange is { DatagramsSent: <= OneShotDatagramThreshold, SawResponse: true };`
      - `TryBeginExpiry(now, idleTimeout, oneShotIdleTimeout)`: compute
        `effective = IsCompletedOneShotExchange && oneShotIdleTimeout < idleTimeout ? oneShotIdleTimeout : idleTimeout`,
        then the **unchanged** cutoff + gate body. **Keep the two-argument form as a delegating
        overload** (`=> TryBeginExpiry(now, idleTimeout, idleTimeout)`): it has a test caller
        (`UdpSessionRetentionTests` aside, `UdpProxySessionTests.cs:97` passes `TimeSpan.Zero` to drain
        a session), and keeping it is what leaves every F3 caller uniform and needs no test edit.
        Document the cost shape: the predicate is read once per **idle candidate** per tick, before the
        gate; `_scope.IsIdle` plus the bucket cutoff under the gate remain the authority, and
        `TouchActivity` stays lock-free so the datagram path gains no contention.
- [ ] `src/WinForward.Runtime/UdpProxy/UdpProxyCoordinator.Sweep.cs` (new partial, `// Mechanical
      file-size split of UdpProxyCoordinator (behavior-zero): the idle-expiry sweep…`, the
      `UdpProxyCoordinator.Send.cs` precedent): move `RemoveExpiredAsync` verbatim, then
      - keep `public ValueTask<int> RemoveExpiredAsync(DateTimeOffset now, TimeSpan idleTimeout)` as a
        delegating overload (`oneShotIdleTimeout: idleTimeout`) so every existing caller — the F3 exact
        gate included — is unchanged;
      - add the three-argument overload whose pre-filter uses
        `ActivityBucket.Cutoff(now, oneShotIdleTimeout < idleTimeout ? oneShotIdleTimeout : idleTimeout)`
        (the later cutoff ⇒ a superset) and whose re-check passes both timeouts to `TryBeginExpiry`;
      - doc the class-relative never-early bound and the pre-filter's superset property;
      - add `public static readonly TimeSpan OneShotIdleTimeout = TimeSpan.FromSeconds(5);` beside
        `ReceiveWindowPoolCapacity` (the coordinator is the composition-facing home for both constants).
- [ ] `src/WinForward.Runtime/IdleExpirySweeper.cs`: append `TimeSpan? udpOneShotIdleTimeout = null` to
      the ctor (existing call sites keep compiling and behaving identically); add
      `internal static TimeSpan EffectiveUdpRetentionFloor(TimeSpan relayIdleTimeout, TimeSpan? oneShotIdleTimeout)`;
      feed the floor to the **unchanged** `DeriveUdpSweepInterval`; `SweepUdpLegAsync` passes
      `_udpOneShotIdleTimeout ?? _relayIdleTimeout` as the third argument.
- [ ] `src/WinForward.Cli/DurableCaptureBundle.cs:300`: pass
      `udpOneShotIdleTimeout: UdpProxyCoordinator.OneShotIdleTimeout`.
- [ ] `benchmarks/WinForward.Benchmarks/Stability/UdpSessionRetentionScenario.cs` (new,
      `--scenario retention`): a mutable time provider + `ActivityBucketClock` shared through
      `UdpProxyOptions.ActivityClock`, **two identically constructed cohorts** of `2N` sessions over
      fake transports that implement `IUdpExchangeCounters` (each cohort: N driven to
      `DatagramsSent = 1, SawResponse = true`; N to `DatagramsSent = 2`), the clock advanced to an
      instant **past the short TTL and below the long one**, and then **one sweep per cohort at that
      same instant** — which needs **one coordinator per cohort**, because a coordinator sweep is
      population-wide and would retire the control cohort's sessions before the treatment could
      measure them. Both coordinators share the one mutable clock and the one fake factory:
      - **control** cohort, swept with both timeouts equal to the long TTL → nothing is past it, so
        both classes stay resident (`oneShotResident = N, sustainedResident = N`);
      - **treatment** cohort, swept with the long TTL + `OneShotIdleTimeout` → exactly the one-shot
        class retires (`oneShotResident = 0, sustainedResident = N`).
      Rows `udp.sessionRetention.control` / `.treatment` each carry
      `{ oneShot, sustained, shortTtlSeconds, longTtlSeconds, oneShotResident, sustainedResident }`;
      the run aborts unless each cohort's population is proven live before its sweep (the fake
      factory's created count cross-checked against that coordinator's session count) and the
      treatment's two TTLs differ. The control is what makes the attribution exact: at the same
      instant, with identically built cohorts, the **only** difference between the rows is the
      classification — so no before-artifact is needed for this mechanism, and a treatment row that
      moves without the control row standing still is a red flag rather than a result.
- [ ] Register the scenario: `SoakOptions.ParseScenario` (`retention`), `SoakRunner.SelectScenarios`
      (a probe, **excluded from `--scenario all`** — the `scaling`/`sweep`/`residency`/`pump`
      precedent), `benchmarks/README.md`, and a scenario-selection test (`hot-path.md:955-960`
      requires one whenever a scenario is added or deliberately excluded).
- [ ] `Stability/BenchmarkShared.cs`: one fake transport implementing both `IUdpProxyTransport` and
      `IUdpExchangeCounters` (a settable `DatagramsSent` / `SawResponse` pair), and the scenario reads
      the counters back through the interface as part of its population proof (so the seam has a
      reader from the moment it exists).
- [ ] `Stability/UdpSessionBudgetScenario.cs`: derive `sweepInterval` from the effective retention
      floor (the shipped 5 s) and drive both timeouts in `UdpSessionBudgetRun.cs:206`; the warm-up
      drain call at `:97` keeps `TimeSpan.Zero` (its "retire everything" meaning is unchanged). The
      scenario's sweep interval is a *mirror of the production derivation*, so it moves with the
      product by design; the before-artifact (15 s) and the after-artifact (5 s) both state the cadence
      they ran. The scenario's own verdict logic is untouched.
- [ ] **Acceptance re-base, stage 1 (ceiling-derived, so no commit is red).** With a 5 s sweep the
      rate-100 steady-state ceiling is `100 × (30 + 10) + 200 = 4,200`, which is **below** the
      pre-change measured peak `AcceptanceLoadSessions = 4_500` — so `ThePostCapsAcceptanceShapePassesBothHalves`
      and `ASaturatedPopulationSkipsThePoolingHalf…` invert unless the load and every derived row move.
      Re-base in `UdpSessionBudgetAcceptanceTests.cs`, mechanically and completely:
      - `s_sweep` 15 s → 5 s, and document the row's new meaning ("the ceiling the shipped two-class
        retention and 5 s sweep produce; the load's peak is the measured resident set and is re-based
        in Step 5 from the after-artifact");
      - every hard-coded ceiling-derived literal, named: the ceiling expectations **6,200 → 4,200**
        (`:84`), `MinimumChurnSeconds` **63 → 43** (`:86`, `:135`), the refusal sentence
        "above 62" → "above 42" (`:136`, `:139`), the default-rate ceiling **1,240 → 840** (`:150`),
        and the two `6_200L` budget literals **→ `4_200L`** (`:194`, `:201`);
      - `AcceptanceLoadSessions` and the rows derived from it (the `ceil(N/16) + 16` association
        ceiling, the tenth-held-privately row, `SessionsBeyondSharedBudget = N − 256`): for this
        commit use the **bound-derived** value `rate × (short 5 s + 2 × sweep 5 s) + margin 200 =
        1_700` (labelled as a bound, to be replaced by the measurement), which clears the 4,200
        ceiling and keeps `--require-pooling` meaningful;
      - re-check the discrimination precondition at the new ceiling (`9,000 > 4,200`, ratio 2.1× — the
        shipped `--rate 100 --churn-seconds 90` command does not change).
      `UdpSessionBudgetAcceptance.cs` itself is not edited: this is the mirror catching up with the
      product, not a weakened assertion.
- [ ] `tests/WinForward.Core.Tests/UdpSessionRetentionTests.cs` (new):
      - `ACompletedSingleExchangeIsRetiredAtTheShortTtlAndNotOneBucketEarlier` — 0 removed at exactly
        the short TTL, 1 removed one 500 ms bucket later (both edges);
      - `ASustainedExchangeKeepsTheLongTtl` — `DatagramsSent = 2`; 0 removed at short TTL + several
        buckets, 1 removed at long TTL + one bucket;
      - `AnUnansweredSingleExchangeIsNotRetiredOnTheShortTtl` — `SawResponse = false` survives the
        short TTL and retires on the long one (the slow-reply guard);
      - `TheClassificationPromotesOnTheSecondDatagramAndNeverDemotes`;
      - `TheTwoArgumentSweepKeepsUniformRetention` — a completed one-shot retires at the long TTL
        through the legacy overload (proves the F3 callers' meaning is preserved);
      - `TheSweeperRetiresAOneShotSessionOnTheShortCadenceAndKeepsASustainedOne` — the end-to-end leg
        fact over a real `IdleExpirySweeper` with a mutable clock (the leg passes both timeouts and the
        derived 5 s cadence);
      - `EffectiveUdpRetentionFloorFloorsOnTheOneShotClass` — the pure row, plus the degenerate
        `min` case (`configured ≤ short` ⇒ uniform).
- [ ] `tests/WinForward.Core.Tests/UdpAdaptiveSweepAllocationGateTests.cs` (**new file** — the existing
      `SweepAllocationGateTests.cs` is already **508 effective lines**, over the 400 cap, so its facts
      must not grow): `UdpProxyCoordinatorAdaptiveSweepAllocatesNoManagedBytes` — the **no-op tick over
      a populated world** through the **three-argument** overload, with the full window contract
      (`hot-path.md:963-1090`): synchronous completion via `IsCompletedSuccessfully`, unchanged managed
      thread id, exact `Assert.Equal(0, allocated)`, the thread-independent call-count backstop, every
      assertion outside the window, and the exact-zero probe batches before it. Re-prove the gate's
      discrimination by injecting one allocation and recording the failure, then restoring.

Validation: each new fact in isolation in Release; `FullyQualifiedName~UdpProxySession`,
`~UdpProxyCoordinator`, `~IdleExpirySweeper`, `~SweepAllocationGateTests`,
`~UdpAdaptiveSweepAllocationGateTests`, `~UdpSessionBudgetAcceptanceTests`,
`~HotPathAllocationGateTests`, `~Socks5UdpTransportSendTests`; the `udpChurn`/`udp` quick runs for the
retention shape; then the full gate set.

Rollback: revert this commit; the one-line kill switch (`udpOneShotIdleTimeout: null` at composition)
turns the TTL change inert without touching the seam — **but the acceptance re-base must be reverted
with it**, because those ceilings follow the cadence.

## Step 5 — Evidence, spec, and record

- [ ] **After-series** (three runs each, one file per run then concatenated — the runner truncates
      `--output` per process): `footprint-after.jsonl`, `session-budget-after.jsonl`,
      `udp-loss-after.jsonl`, `udp-burst-after.jsonl`, `udp-churn-after.jsonl`, plus the
      `session-retention.jsonl` control/treatment pair (which has no before counterpart — see Step 4).
      Quote the median of three and state the runs. The soak's re-run must
      still print `verdict.retentionBounded: true` and `verdict.poolingCovered: true` with
      `pooling.saturated: false`.
- [ ] **Acceptance re-base, stage 2 (measured, per the amended PRD).** Read the after-artifact's
      `steadyStateSessions` (the measured one-shot-band peak at `--rate 100`) and replace Step 4's
      bound-derived `AcceptanceLoadSessions = 1_700` with it, re-running the rows derived from it (the
      association ceiling, the tenth-held-privately row, `SessionsBeyondSharedBudget`). The constant's
      doc names the artifact row and the run it came from, and the artifact README records both
      numbers (bound → measured) so a reader can see the constant is measured, not invented. If the
      measured peak turns out not to clear the ceiling or to change the pooling verdict, **the load
      moves** (rate/churn-seconds/drain-seconds) and the README says so — a soak that no longer tests
      what it names is not acceptable.
- [ ] Fix the stale cadence prose in the same step (each is a "15 s" that is no longer true, and each
      is a row a future reader measures against): `tests/WinForward.Core.Tests/SweepAllocationGateTests.cs:478`
      ("the tick that repeats every 15 s"), `.trellis/spec/backend/udp-relay.md:216` (the cooldown
      prune's cadence) and `:395` (the sweep-gate bullet), `benchmarks/WinForward.Benchmarks/Stability/SoakOptions.cs:152`
      ("idle 30 s + 2 × sweep 15 s"). `udp-relay.md:569` and the new `udp-relay.md` retention bullet
      are edited in the spec pass below.
- [ ] `benchmarks/results/2026-10-01-udp-session-footprint/README.md`: host/runtime header, base
      revision, every command, the before/after tables, the **red-before** for the pool (capacity 256:
      +44 at 300 sessions, +744 at 1,000) beside the green cycle row, the projection table (MiB/GiB),
      the mixed-retention resident counts with the attribution rule (the mixed arm's control vs
      treatment isolates the classification; the soak bounds the end-to-end effect of the TTL **and**
      the cadence together), the `AppliedRelayReceiveBufferSize` read-back, the **re-based soak
      cadence and ceilings with every moved row listed** (`research/implementation-notes.md` §7),
      the two tightened retention bounds (long class 45.5 s → 35.5 s; F8 pending lifetime
      (5, 20] s → (5, 10] s) with their direction, and the residual risks (sparse-flow
      re-establishment, the fault-storm overlap beyond the retire allowance, kernel-drop invisibility,
      the F8 leg's tripled tick).
- [ ] Gate stability: run the per-gate process proof for every exact gate this task adds or edits
      (`hot-path.md:1176-1310` — own process, N runs, per-class totals string, revision +
      `git write-tree` fingerprint, the accepted-host-event signature predicate
      `168|5216|7384|7448`), and refresh the spec's recorded per-class `totals` string if the fact
      counts moved (`hot-path.md` §4).
- [ ] Spec updates via `trellis-update-spec` (rows in the mapping table below):
      `hot-path.md` §2 signatures + §3 "recorded framework/churn anchors" (buffer, two-class
      retention, 5 s sweep), `udp-relay.md` retention bullet **plus the two stale 15 s rows**
      (`:216`, `:395`), `traffic-policy-lifecycle.md` cadence + never-early bullets (the class-relative
      bound **and** the tightened long-class bound), `error-handling.md:16` (the F8 pending-entry
      lifetime `(5, 20] s → (5, 10] s`), and `quality-guidelines.md` "Bounded Pooled SOCKS5 UDP
      Receive Storage" (the pool-capacity rule, including its retire allowance, + a Tests Required
      row). `async-lifetime.md` needs **no** edit (checked: the receive-loop lease lifetime and the
      `UdpProxySession` scope contract are untouched) — record that as a checked-and-not-needed result
      rather than leaving it unstated.
- [ ] Record the hand-offs: the parent `08-30-proxy-perf-stability` backlog's F6 row (state what
      shipped and what was deferred), and the dead end (shared relay socket) stays recorded.

## Validation commands

```bash
dotnet build WinForward.slnx -c Release                                     # zero-warning
dotnet test WinForward.slnx -c Release                                      # full suite green
dotnet test WinForward.slnx -c Release --filter "FullyQualifiedName~UdpSessionRetentionTests"
dotnet test WinForward.slnx -c Release --filter "FullyQualifiedName~UdpReceiveWindowPoolTests"
dotnet test WinForward.slnx -c Release --filter "FullyQualifiedName~ConfigurationLimitsTests"
dotnet test WinForward.slnx -c Release --filter "FullyQualifiedName~Socks5UdpTransportLeaseTests"
dotnet test WinForward.slnx -c Release --filter "FullyQualifiedName~SweepAllocationGateTests"
dotnet test WinForward.slnx -c Release --filter "FullyQualifiedName~HotPathAllocationGateTests"
dotnet format WinForward.slnx --severity info --verify-no-changes --no-restore   # empty output
jb inspectcode -f=Xml -e=HINT -o=/tmp/jb-inspectcode.xml WinForward.slnx        # zero <Issue>

# BEFORE (pre-change tree, the Step 1 instruments already landed) — see Step 1 for the full block.
# AFTER: the same five commands with -after paths, plus the retention pair (no before counterpart):
for r in 1 2 3; do
  dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- --stability --scenario retention \
    --output /tmp/wf-f6-retention-$r.jsonl
done
cat /tmp/wf-f6-retention-{1,2,3}.jsonl > benchmarks/results/2026-10-01-udp-session-footprint/session-retention.jsonl

dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- --stability --scenario footprint \
  --output benchmarks/results/2026-10-01-udp-session-footprint/footprint-after.jsonl
dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- --stability --scenario udpSessionBudget \
  --rate 100 --churn-seconds 90 --drain-seconds 120 --socks5-external --require-pooling \
  --output benchmarks/results/2026-10-01-udp-session-footprint/session-budget-after.jsonl
dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- --stability --scenario udp \
  --pps 25000 --duration 60 \
  --output benchmarks/results/2026-10-01-udp-session-footprint/udp-loss-after.jsonl
dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- --stability --scenario udpBurst \
  --burst-flows 48 --dial-delay-ms 100 --duration 60 \
  --output benchmarks/results/2026-10-01-udp-session-footprint/udp-burst-after.jsonl
dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- --stability --scenario udpChurn \
  --burst-flows 48 --churn-waves 0 --duration 120 --socks5-external \
  --output benchmarks/results/2026-10-01-udp-session-footprint/udp-churn-after.jsonl

# Must-not-move companions the sweep cadence and the seam can touch (report-only, one run each)
dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- --stability --scenario attribution --quick
dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- --stability --scenario residency --quick --flows 100 --udp-flows 100
dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- --stability --scenario gc-soak --quick
```

## Acceptance-criteria + spec-row → proof mapping

| PRD criterion / spec row | Where it is discharged | Kind |
|---|---|---|
| **AC1** buffer default is the chosen 32–64 KiB value (asserted), the override restores the old value (asserted), the capacity×buffer warning still fires, the trade recorded with the projected kernel change | Step 2: the constant-pair guard, `ConfigurationDefaultsUdpBudgetWhenOmitted`, the new 128 KiB override row, the re-based warning aggregates, the real-socket `AppliedRelayReceiveBufferSize` fact, and `UdpProxyCompositionTests`; README projection table | exact + artifact |
| **AC2** pool sized from the session capacity, zero overflow growth at that capacity, red-before at 256 | Step 3's two cycle facts + the pure rule rows (`cap + allowance` = 364 / 17,408); Step 1's recorded red (+44 at 300, +744 at 1,000) | exact |
| **AC3** one-shot retired on the short TTL / sustained on the long one, both edges, counters from the existing evidence, resident-set series at a mixed population | Step 4's six boundary facts + the end-to-end sweeper leg fact + the three-argument exact sweep gate; Step 4's `udp.sessionRetention` control/treatment arm; Step 5's soak re-record | exact + series |
| **AC4** sustained never retired early; a one-shot reply lands inside its TTL; burst/loss anchors keep their shape; retention/teardown tests green; fail-closed admission untouched | the `SawResponse` conjunct + the boundary facts; the `udp`/`udpBurst`/`udpChurn` before/after bands; the unchanged expiry/teardown/admission suites and the untouched `UdpProxyCoordinator.Send.cs`; both classes' retirement bounds are re-stated with direction (long 45.5 → 35.5 s, F8 pending (5,20] → (5,10] s) | series (anchors) + exact (facts) |
| **AC5** Release zero-warning, full suite green, format empty, inspectcode zero | the validation block at every rollback point; the per-gate process proof for the new gates | exact |
| **AC6** benchmark data recorded and cited before archive | the artifact directory + README; the hand-off note in the parent backlog | artifact |
| PRD requirement 7 / research dead end (no shared relay socket) | not implemented; recorded in `research/implementation-notes.md` §10 and in the README's "deliberately out of scope" | recorded |
| Spec `hot-path.md:782-796` (§2 signatures: the three transport defaults) | Step 5 edit + the Step 2/4 facts | exact |
| Spec `hot-path.md:798-844` (§3 "recorded framework/churn anchors … read with that shape") | Step 5 edit (buffer 64 KiB, two-class retention, 5 s sweep) with the anchor bands re-quoted from the artifact | exact + series |
| Spec `udp-relay.md:569` (retention: buffer default, idle retention, sweeper cadence) | Step 5 edit + the Step 2/4 facts | exact |
| Spec `traffic-policy-lifecycle.md:46` (two cadences, the derivation) and `:49` (bucket stamp, never early) | Step 5 edit naming `EffectiveUdpRetentionFloor`, the class-relative never-early bound, and the tightened long-class bound (45.5 → 35.5 s) | exact |
| Spec `error-handling.md:16` (F8 deferred attribution: "a 5 s retention TTL" on the sweep) | Step 5 edit stating the tightened pending-entry lifetime `(5, 20] s → (5, 10] s` and that the TCP pending-SYN TTL is *not* affected (main leg) | exact |
| Spec `quality-guidelines.md:50-117` (Bounded Pooled SOCKS5 UDP Receive Storage: one window per session, rent/return exactly once, balance regression) | Step 5 edit adding the pool-capacity rule **with its retire allowance** to §2/§3 + a §6 Tests Required row; Step 3's balance assertion in the new cycle fact | exact |
| Spec `hot-path.md:963-1090` (allocation-gate window contract) and `:1176-1310` (per-gate proof) | Step 4's gate shape (new file, since `SweepAllocationGateTests.cs` is over the 400-line cap) + Step 5's gate-stability artifact | exact |
| Spec `hot-path.md:908-960` (measurement self-checks: population-before-sampling, report-only unless exact, prove the gate can fail) | Step 4's scenario population assertions + Step 4's injected-allocation discrimination + the README's exact-vs-series rules | exact |
| Spec `async-lifetime.md` (receive-loop lease lifetime / `UdpProxySession` scope contract) | **no edit required** — the rent/return sites and the scope contract are untouched; recorded as checked-and-not-needed in Step 5 | recorded |
| `directory-structure.md:55-61` (400 effective lines) | Step 4's partial-file split; Step 0's container census | exact |

## Artifacts

| Path | Content |
|---|---|
| `benchmarks/results/2026-10-01-udp-session-footprint/README.md` | host/runtime header, base revision, commands, before/after tables, the pool red-before, the projection table, the mixed-retention attribution, the residual risks |
| `…/footprint-before.jsonl`, `…/footprint-after.jsonl` | `udp.sessionFootprint` 1/100/1000 + the cycle row (overflow first/second wave) |
| `…/session-retention.jsonl` | the mixed one-shot/sustained resident counts, control vs treatment, and the TTLs driven (three runs) |
| `…/session-budget-before.jsonl`, `…/session-budget-after.jsonl` | the acceptance soak: peak population, kernel estimate, descriptors/session, drain, verdict |
| `…/udp-loss-{before,after}.jsonl`, `…/udp-burst-{before,after}.jsonl`, `…/udp-churn-{before,after}.jsonl` | the shape anchors |
| `…/gate-stability.txt`, `…/class-totals.txt` | the per-gate process proof and the per-class fact totals/hashes |

## Risky files

| File | Risk | Rollback |
|---|---|---|
| `src/WinForward.Runtime/UdpProxy/UdpProxySession.cs` | The activity gate and the expiry admission policy (`_expiring` is the owner's policy; the cutoff comparison is the never-early guarantee) | Step 4 (rollback C) |
| `src/WinForward.Runtime/UdpProxy/UdpProxyCoordinator.cs` + new `.Sweep.cs` | The warm datagram gate is untouched, but the sweep's pre-filter and scratch lifetime are load-bearing; a wrong cutoff class retires early | Step 4 (rollback C) |
| `src/WinForward.Runtime/Socks5/Socks5UdpTransport.cs` | The established-datagram path inside the same file as the new interface; any edit below `SendSpanAsync`'s first line breaks the "no new per-datagram cost" proof | Step 4 (rollback C) |
| `src/WinForward.Runtime/IdleExpirySweeper.cs` | The tick period now feeds the UDP leg **and** the F8 attribution TTL leg; a wrong floor shortens every retention | Step 4 (one-line kill switch) |
| `src/WinForward.Cli/DurableCaptureBundle.cs` | Two composition values (pool capacity, one-shot TTL); the load-order/disposal order must not move | Steps 3/4 |
| `benchmarks/…/Stability/UdpSessionRetentionScenario.cs` (new) | The acceptance instrument: a vacuous run (both TTLs equal in the treatment arm, or an unproven population) would green the criterion silently — the row must carry both TTLs, the control/treatment pair, and the population proof | Step 4 (rollback C) |
| `benchmarks/…/Stability/UdpSessionBudgetScenario.cs` + `UdpSessionBudgetAcceptanceTests.cs` | The re-based cadence inverts two acceptance rows unless the ceilings, `MinimumChurnSeconds` **and** `AcceptanceLoadSessions` all move together; the load's meaning must be restated and the final constant must come from the measured after-peak | Step 4 (stage 1) + Step 5 (stage 2), with the README listing every moved row |
| `tests/WinForward.Core.Tests/ConfigurationLimitsTests.cs` | 350 effective lines before this task; new rows can breach the cap | Step 2 (move rows to the new test files) |
| `hot-path.md` / `udp-relay.md` / `traffic-policy-lifecycle.md` / `error-handling.md` / `quality-guidelines.md` rows | Several recorded defaults and every stale "15 s" live only in prose; a stale row makes a future reader measure the wrong shape | Step 5 (the four stale-prose sites are named in Step 5) |
| `tests/**` expectations | The warning aggregates and the misattribution row encode arithmetic derived from the changed constants; a mechanical edit must not weaken the threshold pair (the `>256 KiB ∧ >2,048` predicate is untouched) | Step 2 |

## Commit plan skeleton

```text
0  test(bench): the footprint pool-cycle row + the before-artifacts and the pool red-before [Step 1]
1  perf(udp): relay receive buffer default 128 -> 64 KiB
   + the constant-pair guard, the override row, the real-socket fact                 [Step 2, rollback A]
2  perf(udp): size the receive-window pool from the session capacity + retire allowance
   + the pool/coordinator cycle facts and the composed footprint instrument     [Step 3, rollback B]
3  perf(udp): adaptive one-shot idle retention (evidence seam + 5 s class + cadence),
   the mixed-retention instrument, the acceptance ceiling re-base (stage 1)
   + the boundary/sweep facts and the three-argument exact gate                  [Step 4, rollback C]
4  docs(evidence): before/after artifacts, gate stability, the measured acceptance
   re-base (stage 2), the stale-prose fixes, spec rows for F6                            [Step 5]
```

Each commit is checked with `dotnet format … --verify-no-changes` (empty output),
`jb inspectcode` (zero `<Issue>`), `dotnet test -c Release` (green) and `dotnet build -c Release`
(zero-warning). The three code commits are independent and revertible in any order; commit 3's kill
switch is the composition line named in `design.md` §9.

## Deliberately out of scope / deferred

- The shared relay socket with destination-based demultiplexing (the recorded dead end).
- A config key for the one-shot TTL, and any per-deployment heuristic for the threshold.
- The 32 KiB buffer default (the documented fallback experiment, not the shipped value).
- A second sweep index for the short class, a wall-clock/timing assertion for the classification, or
  any tolerance-based allocation gate.
- The A4 FlowTable rebuild, the F2 lock architecture, and the Windows-only measurement rows
  (`gc-soak` on a real NIC, `TcpThroughputScenario`).
- Changing `NativeBufferPool.DefaultCapacity` itself (composition now always passes a capacity; the
  type stays as it is).

## PRD-versus-code conflicts (recorded for the archive)

All four are precision issues in the PRD's text, not in its requirements; none weakens a criterion.
The full evidence is in `research/implementation-notes.md` §9.

| # | PRD text | Code reality | Resolution |
|---|---|---|---|
| 1 | AC 2: "a session population at that capacity produces **zero** overflow allocations (exact counter gate)" | `OverflowAllocations` counts every rent that misses the free list, so a first fill of N live sessions is N at **any** capacity | the gate asserts **zero growth across a second population cycle**, with the red-before at the old capacity (+44 at N = 300); the design forbids an absolute `== 0` assertion |
| 2 | §Problem: "every session beyond the 256th runs on tracked overflow allocations" | true as *churn* (the free list cannot fill, so each later session allocates and frees one lease per lifetime), not as an absolute counter reading | the artifact states it as per-cycle growth; the qualitative claim stands |
| 3 | §Problem: the receive-window lease is "~1.6 KiB each" | 1,537 B payload + 4 B in-band state word = **1,541 B** | 1,541 B used in the arithmetic, 1.6 KiB kept as the rounded form |
| 4 | §Problem: "at the 16,384-session capacity that projects to ~2 GiB" (and the recorded instruments "both under `benchmarks/results/2026-09-28-udp-reuse/`") | 16,384 × 128 KiB = 2 GiB is exact, but the 1 h artifact's *measured* peak was 4,500 sessions / 500 MiB; and that directory holds no `udp.sessionFootprint` rows (the newest are `2026-08-29-udp-fix/`) | the README records both the capacity projection and the retention-bounded measured figures (500–563 MiB at 4,000–4,500 sessions → 250–281 MiB), and cites the 2026-08-29 footprint rows only as a historical shape |

---

## Review dispositions (independent review, 2026-10-01 — all folded into this plan)

| # | Severity | Finding | Disposition |
|---|---|---|---|
| R1 | BLOCKER | Pool ships zero headroom; the retire/admit overlap is not bounded by 1 (receive-failure teardowns are concurrent and never take `_sweepGate`) | **Fixed** in `design.md` §3: `ReceiveWindowPoolCapacity(cap) = cap + ReceiveWindowRetireHeadroom(cap)`, allowance `max(4 × DefaultUdpAssociationFlowsPerAssociation, cap / 16)` (64 / 1,024; 96 KiB / 1.5 MiB), derivation and residual documented, attribution-pool precedent cited; the pure rule row asserts `cap + allowance` (364 / 17,408), the cycle gate keeps its shape |
| R2 | BLOCKER | The acceptance re-base is incoherent: `SteadyStateSessionCeiling(30 s, 5 s, 200) = 4,200 < AcceptanceLoadSessions 4,500`, so two rows invert and `MinimumChurnSeconds` moves 63 → 43 | **Fixed**: `design.md` §8 + `implement.md` Step 4/5 + `research/implementation-notes.md` §7 enumerate every moved row (ceiling, minimum, refusal sentence, default-rate ceiling 1,240 → 840, `AcceptanceLoadSessions` and its derived rows), restate the load's meaning, and take the final constant from the **measured** after-peak (bound-derived 1,700 only as the intermediate, so no commit is red); if the measured load stops clearing the ceiling or changing the pooling verdict, the load moves and the README says so |
| R3 | MAJOR | "the two-argument `TryBeginExpiry` is removed" is false — `UdpProxySessionTests.cs:97` calls it | **Fixed**: keep a delegating two-argument overload (design §5.2, notes §4, implement Step 4) |
| R4 | MAJOR | Buffer blast radius misses `UdpSessionBudgetAcceptanceTests.cs:215`, whose 64 KiB literal becomes the new default and turns the expected misattribution failure into a pass | **Fixed**: re-point it at 32 KiB and add it to Step 2's re-base list (design §2, notes §9/D19) |
| R5 | MAJOR | The footprint cycle row does not mirror composition (pool built with the literal default 256 → still +744 at 1,000) | **Fixed**: Step 3 constructs the scenario's pool through `ReceiveWindowPoolCapacity(sessions)`; the Step 1 before-run keeps the literal default so the red exists (notes §9/D16) |
| R6 | MAJOR | The two tightened retention bounds are unrecorded (`UdpSessionBudgetAcceptanceTests` aside: long class 45.5 → 35.5 s; F8 pending `(5, 20] → (5, 10]`) | **Fixed**: `design.md` §5.3 + §6 row 4, notes §6 item 2 and §4, the `traffic-policy-lifecycle.md:46/49` and `error-handling.md:16` spec rows, and the README's residual list |
| R7 | MINOR | The sparse-flow cost is per session (a keepalive never promotes) and was not stated | **Fixed**: `design.md` §4.1 + §6 row 3, notes §5 — re-established per sparse datagram for the flow's whole life, bounded at one setup each, with the `SawResponse` conjunct covering the slow-first-reply direction |
| R8 | MINOR | Projection arithmetic mixed decimal MB with MiB | **Fixed**: 24.1 MiB population / 25.6 MiB with allowance, 281 MiB, 62.5 MiB; the kernel range is quoted in one unit (500–563 → 250–281 MiB); "MiB/GiB of the exact product" stated once (design §2/§3/§8, notes §3) |
| R9 | MINOR | The classification cost line understates the sweep-side gate work | **Fixed**: `design.md` §4.2 + §7 AC3-cost — one `_activityGate` entry per idle candidate per tick, bounded by the idle population, while `TouchActivity` stays lock-free so the datagram path gains no contention |
| R10 | MINOR | `SweepAllocationGateTests.cs` is over the 400-line cap, and four "15 s" prose rows are stale | **Fixed**: the new gate goes in `UdpAdaptiveSweepAllocationGateTests.cs`, and Step 5 names `SweepAllocationGateTests.cs:478`, `udp-relay.md:216`, `udp-relay.md:395`, `SoakOptions.cs:152` (the reviewer's `:363` carries the ctor parse, not the sentence) |
| R11 | MINOR | The retention scenario needs two coordinators (a coordinator sweep is population-wide) | **Fixed**: implement Step 4 — one coordinator per cohort, sharing the mutable clock and the fake factory |

Verified-correct review notes folded in without a change of plan: the evidence object is created fresh
per lease, so `SawResponse` can only reflect the session's own reply and no early classification is
possible (notes §5); `TouchActivity` stamps on receives as well as sends, so the short TTL is measured
from the reply (notes §4); reading the predicate outside `_activityGate` is safe because
`_scope.IsIdle` plus the bucket cutoff under the gate are authoritative (design §4.2); the cycle
arithmetic `cycle 1 = N`, `cycle 2 = max(0, N − capacity)` (design §3, notes §3); the seam passes the
layering/analyzer check and a coordinator-supplied delegate would not be simpler (design §4.3); and
`DeriveUdpSweepInterval` returns 5 s when fed the 5 s floor (design §4.4).

---

## Verification dispositions (independent check sub-agent, 2026-10-01)

Checked on the uncommitted tree (working-tree fingerprint `git stash create` tree
`456567417bf0b8214c2616a728b84a5d32ea2dbe` + untracked-file hash `dbc5d5e784544804`): code, tests,
the six boundary cycles (buffer default, pool sizing, adaptive TTL, instrument fix, anchors, projections),
the artifacts and the spec rows. Verified clean without a fix: the 128→64 KiB pair and its override, the
warning pair's re-based aggregates (256/1,024 MiB, derived), the 32 KiB misattribution re-point, the pool
rule (`cap + max(4×16, cap/16)` = 364 / 17,408) and its balance identity, the red-before `+44`
(single-threaded release leaves `InPool` exactly at 256; the concurrent footprint row's 742 = 1,000 − 258,
the racy over-enqueue), both TTL boundary edges and the monotone promotion, the two tightened bounds
(long class 35.5 s; F8 pending `(5, 10]`), the arrival-granular load statistic (the association count
ratchets to the true peak, so a sampled-row load would invert `poolingCovered` — the frozen-clock run
shows exactly that at 62 > 48), every moved acceptance literal (each derived, none hand-tuned), the
must-not-move anchors against the recorded bands, and the projections (except the two arithmetic slips
below). Full suite 9 runs → 8 green (18 analyzers + 1,141 core) + 1 host lump (V9); the per-gate proof
re-derived over 109 own-process runs, every one exit 0 with zero gate failures
(`gate-stability-verify.txt`).

| # | Severity | Finding | Disposition |
|---|---|---|---|
| V1 | MAJOR | The per-gate proof's filter `FullyQualifiedName~SweepAllocationGateTests` also matches the new `UdpAdaptiveSweepAllocationGateTests` (measured `Total: 13` against the expected 12), so the procedure as written stops on a false `VACUOUS MATCH` and cannot complete the sweep arm | **Fixed** in `hot-path.md` §4: the filter is namespace-qualified (`~WinForward.Core.Tests.$gate`) with the measurement recorded; the arm re-ran 10/10 green under the qualified filter |
| V2 | MAJOR | The injected-allocation discrimination proof did not reproduce as recorded: a bare `_ = new byte[64]` is dead and this JIT removes it (2/2 runs of the new gate passed with it) | **Fixed**: re-proven with `GC.KeepAlive(new byte[64])` → new gate `Actual: 88`, pump gate `Actual: 88000` (1,000 × 88 B); the probe-form contract is recorded in `hot-path.md` §2/§4 and in the README's discrimination row |
| V3 | MINOR | The artifact README's `udp.sessionFootprint` after rows (1/100/1000) were left blank although the after-run measured them | **Fixed**: filled from `footprint-after.jsonl` (62,632 / 394,904 / 3,669,360 B allocated; 0 working-set delta and 0 Gen0 in all three) |
| V4 | MINOR | Arithmetic slips in the record: `(16,384 + 1,024) × 1,541 B = 26,824,448 B` (correct 26,825,728 B) in `design.md` §3 and `research/implementation-notes.md` §3; `design.md` §2's "≈ 0.65 s" absorption (correct ≈1.3 s = 128 queued ÷ 98 arrivals/s) | **Fixed** in both files |
| V5 | MINOR | The spec's suite-total figure read `1,140 + 18`, one below the landed `1,141 + 18` (`class-totals.txt` and nine suite runs agree on 1,141) | **Fixed** in `hot-path.md` §4 |
| V6 | MINOR | Stale default in a product XML doc: `ConfigurationLimits.ParseUdpSessionCapacity`'s example still said "the 128 KiB default times a raised capacity" | **Fixed** to "the shipped per-session default" (comment only) |
| V7 | MINOR | The artifact README presented `retentionBounded` as still testing what it names ("4.1× headroom"), and the per-session descriptor range was truncated (1.118–1.121 against a measured max of 1.12195) | **Fixed**: the verdict sentence now states the ceiling's cadence-coupled meaning and points at the new residual; the range is 1.118–1.122 |
| V8 | RECORD | `git write-tree` on an unstaged tree returns the *index* tree (= the base revision's), identical across the before/after arms, so `gate-stability.txt`'s fingerprint does not identify the tree under test | **Recorded**: the re-derivation artifact carries a `git stash create` working-tree fingerprint plus an untracked-file hash, and the README's verification section states the limitation |
| V9 | RECORD | The suite-level host-lump family produced a fifth value on this check: `CapturePumpReadCallTests.CountingReaderIdleIterationsAllocateNoManagedBytes` `Actual: 8120` (= 8,192 − 72; a recorded victim gate with a clean `stabilized` preflight, 3/3 green in isolation, rate 1/9) — outside the documented per-gate signature list `168\|5216\|7384\|7448` | **Recorded, not fixed**: extending the signature list from an in-suite observation would normalize a value the per-gate process proof has not sampled. The operator decides (README, "Independent verification") |
| V10 | RECORD | The soak's `retentionBounded` ceiling is cadence-coupled: it is derived from the configured 30 s retention at the derived 5 s cadence, so at 4,200 against the measured 1,014 it bounds accumulation but is no longer tight to the shipped one-shot band; a retention regression staying under 4,200 would pass it | **Recorded, not fixed**: tightening the ceiling to the one-shot floor changes what the acceptance names (a design decision). The class itself is proven exactly by `UdpSessionRetentionTests` and by the `udp.sessionRetention` control/treatment arm; the soak's before/after series (4,539 → 1,014) carries the end-to-end attribution. README residual added |

Not run here (operator-owned or out of scope): `dotnet format --severity info --verify-no-changes` and
`jb inspectcode` (the two commit gates), and any re-run of the long soaks — the recorded artifacts were
re-read and re-derived instead (`session-budget-{before,after}.jsonl`, the frozen-clock pair, the
footprint pair, `session-retention.jsonl`, and the three anchor series).

### Commit-gate pass (operator's `dotnet format --severity info`, 2026-10-01)

The operator ran `dotnet format WinForward.slnx --severity info --verify-no-changes` on the verified
tree: **10 diagnostics, all info, none an error**, all in this task's surface. Each was fixed in place
(no suppression, no test weakened), then `dotnet build -c Release` (0 warnings) and the affected facts
were re-run green — `Socks5UdpTransportLeaseTests` 8/8, `UdpSessionRetentionTests` 8/8,
`UdpSessionBudgetAcceptanceTests` 11/11, `UdpSessionBudgetScenarioTests` 26/26,
`UdpReceiveWindowPoolTests` 4/4, `UdpAdaptiveSweepAllocationGateTests` 1/1 and
`SweepAllocationGateTests` 12/12 — and the full suite is green at `18 + 1,141`. Tree fingerprint after
the fixes: tracked tree `5c15d9b8971fd24b4cc588b91265dafbd88d9032` + untracked code hash
`df1b2a942a9d589b`.

| # | Rule | Site | Fix |
|---|---|---|---|
| V11 | `MA0154` | `UdpProxyCoordinator.cs:223` | the XML comment's keyword reference uses the langword form (`<see langword="finally"/>`), not `<c>finally</c>` |
| V12 | `MA0076` ×4 | `Socks5UdpTransportLeaseTests.cs:161,162` | both interpolated strings (the read-back line and the assertion message) use `string.Create(CultureInfo.InvariantCulture, $"...")`, the repo-wide pattern; `using System.Globalization;` added |
| V13 | `MA0003` | `UdpSessionRetentionTests.cs:159` | the null argument is named (`oneShotIdleTimeout: null`) |
| V14 | `RCS1118` ×3 | `UdpSessionBudgetAcceptanceTests.cs:118,298,299` | `privateAssociations`, `sampledSessions` and `placedAssociations` are `const` — they are the re-based literals, and `const` also records that they are compile-time constants |
| V15 | `CA1859` | `UdpSessionRetentionTests.cs:222` | **narrowed**, not suppressed: `Flows` is typed `List<FlowKey>`, matching its only producer (`_flows`) and the caller's indexer use — the route the F5 round took on a private helper. No abstraction was relied on |

The two commit gates stay operator-owned: `dotnet format` was not re-run here, and `jb inspectcode` was
not run.

### Inspector pass (operator's `jb inspectcode`, 2026-10-01)

After the operator's `Flows` property → storage conversion cleared the last `dotnet format` diagnostic
(exit 0, empty output), the inspector reported **16 issues** on this surface. Each was fixed in place:
9 redundant usings removed (each verified by a build — the removals include types reachable through the
test namespace itself, so the build is the only authority), 2 ambiguous XML `cref`s given their
signatures, 1 unresolvable `cref` fully qualified, 1 redundant default argument dropped, 1 member
narrowed, 1 dead member deleted, and 1 guard suppression with a documented reason. Then
`dotnet build -c Release` (0 warnings, 0 errors), the affected facts re-run green —
`UdpReceiveWindowPoolTests` 4/4, `UdpSessionRetentionTests` 8/8, `UdpAdaptiveSweepAllocationGateTests`
1/1, `SweepAllocationGateTests` 12/12, `UdpSessionBudgetAcceptanceTests` 11/11,
`UdpSessionBudgetScenarioTests` 26/26, `Socks5UdpTransportLeaseTests` 8/8 — and the full suite green at
`18 + 1,141`. Tree fingerprint after the fixes: tracked tree
`6c580ac8062054b3e51c675f6933777716e0c684` + untracked code hash `42094eaa44e30d4d`.

| # | Rule | Site | Fix |
|---|---|---|---|
| V16 | `InvalidXmlDocComment` ×3 | `UdpChurnScenario.cs:15`, `UdpSessionBudgetRun.cs:222`, `UdpProxyCoordinator.Sweep.cs:18` | the two `cref`s this task's new overloads made **ambiguous** now carry their signatures — `RemoveExpiredAsync(DateTimeOffset, TimeSpan)` (the zero-timeout overload the sentence names) and `UdpProxySession.TryBeginExpiry(DateTimeOffset, TimeSpan, TimeSpan)` (the one the sweep loop calls); the third was **unresolvable** without a using (`ActivityBucketClock` is in a sibling namespace of the benchmarks project), so it is fully qualified as `WinForward.Core.ActivityBucketClock` rather than demoted to `<c>` — all three keep a live `cref` |
| V17 | `RedundantUsingDirective` ×9 | `UdpSessionRetentionScenario.cs:6`; `UdpAdaptiveSweepAllocationGateTests.cs:3,4,6`; `UdpReceiveWindowPoolTests.cs:3`; `UdpSessionRetentionTests.cs:2,3,6,7` | removed (`WinForward.Runtime.Socks5`, `WinForward.Core`, `WinForward.Protocols`, `System.Net.Sockets`, `System.Threading.Channels`). The test files sit **inside** `WinForward.Core.Tests`, so `WinForward.Core` and the TestHelpers types resolve through the enclosing-namespace walk with no using; each removal was confirmed by a clean build, not by inspection |
| V18 | `ConvertIfStatementToReturnStatement` | `UdpSessionRetentionScenario.cs:136` | **suppressed narrowly**, not converted: the guard is the scenario's population proof, so the throw must read as a standalone early exit rather than the false arm of a `return ... : throw ...` expression (the repo's own precedent: `ExternalLoopbackSocks5UdpServer.cs:185`'s latch guard). The suppression carries that reason inline |
| V19 | `RedundantArgumentDefaultValue` | `UdpReceiveWindowPoolTests.cs:99` | the explicit `retiredCapacity` (256) argument is dropped: 256 **is** `NativeBufferPool`'s own default, which is exactly the composition this red-before reproduces, so omitting it makes the fact say what it means. The `retiredCapacity` const stays (it is the asserted expectation), and the comment records why the argument is gone |
| V20 | `MemberCanBePrivate.Local` | `UdpSessionRetentionTests.cs:214` | `Clock` is now `private`: its only reader is `RetentionHarness.Advance`, which ticks it |
| V21 | `UnusedMember.Local` | `UdpSessionRetentionTests.cs:218` | the `Transports` property is **deleted** — it had no reader, only a `<see cref>` in the class doc (a doc reference is not a use). The doc now names the transports `AdmitAsync` returns, which is what the facts actually read |

Both commit gates stay operator-owned: they were not re-run here.
