# Diagnose the UDP association head count flake

## Goal

Make the ten assertions that read the fake SOCKS5 UDP server's ASSOCIATE-reply counter deterministic, and
fix the fixture race that makes them flaky. The failure was **not** a broken sharing invariant: the pool's
own shape assertions passed in the same run.

## Evidence (corrected — the first draft misread the failing line)

- `/tmp/wf-lumps-suite-fail-9.txt:25` reports the failure at **`UdpAssociationHeadTests.cs:110`**:
  `Assert.Equal(282, server.AssociateReplyCount)` → `Expected: 282, Actual: 281`. Line 104
  (`pool.AssociationCount`) is *not* the failure: xunit stops at the first failing assertion, so lines
  103 / 104 / 105 / 109 **passed** — the artifact itself proves the pool held **282 associations,
  4,500 leases and 282 connections**. The sharing invariant was intact; only the **fake's reply counter**
  lagged. (The archive record and journal propagated the misreading and must be corrected by this task's
  record.)
- Cause: `ScriptedSocks5UdpServer.cs:185-186` writes the ASSOCIATE reply and only then increments
  `_associateReplies`, while the client's rent completes when it **reads** that reply
  (`Socks5ControlConnection.UdpAssociateAsync`). Under suite thread-pool load the assertion can run between
  the client's completion and the server's increment. `AssociateReplyCount` is a report of the fake's own
  progress, not a synchronisation point.
- Blast radius — **10 direct assertions in 5 files** depend on that counter (enumerated by direct search,
  not by inheriting a review's list): `UdpAssociationHeadTests.cs:110,140`, `UdpAssociationCapabilityTests.cs:47,148,269,304`, `UdpAssociationRecoveryTests.cs:44,57`, `UdpAssociationEvidenceLifetimeTests.cs:173`, `UdpAssociationPoolTests.cs:276`.
  Two earlier counts in this task's own review chain were wrong (one said 8; the next said 9 in 4 files and
  dropped `UdpAssociationPoolTests.cs:276`); this is the verified list.
- Observed rate: 1 of 13 proof-suite runs of `09-30-exact-gate-residual-lumps` (and 0 isolated runs of this
  test exist anywhere in the records — the flake's habitat is the suite).

## Candidates refuted (do not re-derive)

| Candidate | Refutation |
|---|---|
| Over-capacity association (a 17th flow onto a bound-16 association) | The room test (`UdpControlAssociation.cs:115-116`) and the lease claim (`UdpControlAssociation.StartLease` `:132-138`, reached from the pool's `Attach` `:471-475`) run inside the same `lock (_gate)` as the scan (`UdpAssociationPool.cs:303-320`) and as `AssociationCount` (`:143`); no interleaving admits a 17th flow. |
| Association removed while leased or being placed | `CanRetireFaulted` reads the count (`IsFaulted && leaseCount == 0`, `UdpControlAssociation.cs:190`) and is evaluated with the removal under `_gate` (`UdpAssociationPool.cs:217-226`); the faulted path removes only from `set.Shared` (`:504-509`) while `AssociationCount` counts `set.All` (`:143`); the only leased→`set.All` removal rethrows through `RentAsync` and would surface as an exception. |
| Counting window (association not yet published) | `Create` adds to `set.All` under the gate (`:477-483` via `:316`) **before** `RentAsync` awaits `EnsureAssociatedAsync` (`:184-185`), so no lease can exist before its association is counted. |

## Requirements

- **R1 Reproduce in the flake's habitat and diagnose with the artifact.** The primary reproducer is a loop
  of the **full suite** (the recorded evidence is suite-conditional; an isolated single-test loop is the
  anti-habitat and is not the plan). N runs recorded with raw output per run, the padded summaries, the git
  hash, the tree fingerprint and the exit status; the rate reported with its CI. The diagnosis is the
  fixture ordering above, evidenced by the failing line and the fixture's two lines — no further candidates
  are needed, but any new evidence that contradicts it is followed rather than argued away.
- **R2 Fix the fixture, not the assertions.** Make the reply counter monotone with respect to the reply
  becoming readable — increment **before** the reply write — so every dependent assertion observes a value
  that is at least the readable state. `UdpAssociationHeadTests.cs:104-111` stays **byte-identical**
  (the shape guard is not touched). The alternative (a bounded post-state wait in ten assertions) is rejected:
  it multiplies a retry across five files instead of fixing the one ordering that is wrong.
- **R3 Prove the fix deterministically, not statistically.** Add a test-only seam to the fake server that
  lets a test block the ASSOCIATE-reply write, and a test that starts a rent, waits on the gate's own
  entered signal, and asserts the counter is already incremented **while the write is still blocked**. The
  order inside the fake is fixed and load-bearing: **publish the counter → run the gate hook → perform the
  blocked write**; if the hook ran before the increment, the fail-before run would never fail and the fixed
  fixture would still read 0 at the gate. `ConnectionCount` is not a valid wait signal (incremented at
  accept, `ScriptedSocks5UdpServer.cs:136`). The test lives in its own new class
  (`ScriptedSocks5UdpServerOrderingTests`, new file) so `UdpAssociationHeadTests` keeps its measured **10**
  cases and every loop guards a stable total. With
  the old ordering that test fails (counter 0); with the fix it passes. The full-suite loop is the
  statistical second half, not the proof.
- **R4 Keep every guard honest.** No assertion is weakened, no threshold relaxed, no test skipped or
  deleted. Editing the shared fixture is in scope and its blast radius is the 10 assertions listed above; the
  change must keep each of them meaningful (the counter remains a count of ASSOCIATE replies, now published
  before the reply is readable).
- **R5 Record and retarget the specs.** The invariant (“the pool publishes the association and claims the
  lease before the ASSOCIATE await; the fake publishes its reply counter before the reply is readable”) is
  recorded in the spec that owns the UDP relay contracts — **`.trellis/spec/backend/udp-relay.md` (§522,
  tests §595-613)** — not `windows-ndisapi.md`, which owns no pool content. The task record also corrects
  the archive record's and journal's misattribution of this failure to `pool.AssociationCount`.
- **R6 Prove the loop cannot pass vacuously.** The acceptance loop asserts the **class** total
  (`FullyQualifiedName~UdpAssociationHeadTests`, the **measured** `Total: 10` — 7 Facts + 1 Theory with 3
  InlineData; the sibling classes measure 9 / 7 / 5 — and the new class's own re-derived total) rather than a
  bare `Total: 1`, which would also match the Analyzers summary line; the suite arm additionally asserts the
  post-change totals — the Core total is **measured after the new test class is added** (it becomes ≥982
  against today's 981) and the Analyzers total stays 18, and **both are asserted in the loop** rather than
  quoted from before the change — while the ≥20-suite-run arm's power against a 1/13 rate (~80 %) is stated
  rather than implied.

## Acceptance Criteria

- [x] The fixture ordering is fixed (**publish the counter → gate hook → write**, one increment at
      `ScriptedSocks5UdpServer.cs:199-201`) and `UdpAssociationHeadTests.cs:104-111` is untouched
      (`git diff` empty).
- [x] The deterministic fail-before test (`ScriptedSocks5UdpServerOrderingTests`, its own class, 1 case) failed
      on the old ordering with `Expected: 1, Actual: 0` and passes on the new one; 20/20 green in the proof.
- [x] The full-suite loop is recorded with raw per-run output (26 runs, 22 green) and the rate with its CI
      (4/26 = 15.4 % [6.1, 33.5]); the class loops assert each class's **anchored** totals (100/100 green
      across five classes) and the new class's total (20/20). The four suite failures are the residual host
      family at four gates/three sizes, not this task's assertions.
- [x] `udp-relay.md` §3 carries the pool's publish-before-await invariant and §6 the
      published-before-readable fixture invariant, naming the ten dependent assertions.
- [x] The archived `09-30-exact-gate-residual-lumps/{implement.md,prd.md}` and `journal-1.md` now carry
      correction notes naming `:110` / `server.AssociateReplyCount`, the intact pool shape and the real cause.
- [x] Full gates green: Release build 0 warnings, suite 982 + 18 green, `dotnet format` exit 0 empty,
      `jb inspectcode` **0 issues / 0 `CSharpErrors`**.

## Out of Scope

- The F2–F8 performance pipeline (paused by the operator).
- The residual suite-level host lumps and the health-signal race (closed by
  `09-30-exact-gate-residual-lumps`).
- Any change to the pool's sharing logic, which the artifact proves was correct.

## Notes

- Useful fields if a future failure needs more context (none of these require new seams): the failing line,
  `pool.AssociationCount`, `pool.LeasedFlowCount`, `server.ConnectionCount`, `server.AssociateReplyCount`,
  `server.LiveConnectionCount`, and the distinct relay ports held by the leases. Per-association state is
  **not** reachable: `UdpAssociationLease` exposes no association identity (`UdpAssociationLease.cs:13-66`)
  and the pool exposes no enumeration — a plan must not assume otherwise.
- The sibling bound test is `UdpAssociationHeadTests.cs:119-161` with its relevant assertion at `:140`; the
  pool's maintenance child is `UdpAssociationPool.cs:266-293`, registered at `:116`.
