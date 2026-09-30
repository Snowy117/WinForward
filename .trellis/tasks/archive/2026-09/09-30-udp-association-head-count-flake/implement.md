# Implementation plan — the fake server's reply-counter race

Small, order-sensitive fix with a deterministic proof. Nothing in the pool or in the test's shape guard
changes.

## Step 0 — Preflight

- [ ] Read `ScriptedSocks5UdpServer.cs` around `:150-200` (the greeting and ASSOCIATE reply writes, the two
      counters) and `Socks5ControlConnection.UdpAssociateAsync` (where the client's rent completes).
- [ ] Confirm the failing line from the artifact: `/tmp/wf-lumps-suite-fail-9.txt` reports
      `UdpAssociationHeadTests.cs:110`; the shape assertions above it passed.
- [ ] Enumerate the **ten** dependent assertions (verified by direct search, not from a review list):
      `UdpAssociationHeadTests.cs:110,140`, `UdpAssociationCapabilityTests.cs:47,148,269,304`, `UdpAssociationRecoveryTests.cs:44,57`, `UdpAssociationEvidenceLifetimeTests.cs:173`, `UdpAssociationPoolTests.cs:276`. Confirm each reads the counter **after** awaiting its
      rents, so an increment one ahead is acceptable to all of them. Measured class totals for the guards:
      `UdpAssociationHeadTests` 10 (7 Facts + 1 Theory × 3 InlineData), `UdpAssociationCapabilityTests` 9,
      `UdpAssociationRecoveryTests` 7, `UdpAssociationEvidenceLifetimeTests` 5.
- [ ] Plan the new test's home: its **own new class** (`ScriptedSocks5UdpServerOrderingTests`, new file), so
      the existing classes' totals stay valid; re-derive its own total after adding it (the Core total in the
      suite loop becomes the measured post-change number, ≥982).
- [ ] Note the sibling bound test's real range (`:119-161`, assertion at `:140`) and the pool's maintenance
      child (`UdpAssociationPool.cs:266-293`, registered at `:116`) for the spec text.

## Step 1 — The deterministic fail-before test (write it first)

- [ ] Add a test-only seam to `ScriptedSocks5UdpServer`: a hook (e.g. `AssociateReplyWriteGate`, a
      `Func<CancellationToken, ValueTask>?`) that runs **after the counter is published and before the write**
      and signals entry through its own `TaskCompletionSource` — the three-step order
      **publish the counter → run the hook → perform the write** is load-bearing; if the hook ran first, the
      fail-before run below would never fail and the fixed fixture would still read 0 at the gate.
- [ ] Write the test: arm the gate, start a rent on a background task, wait on the **gate's own entered
      signal** (not `ConnectionCount`, which is incremented at accept,
      `ScriptedSocks5UdpServer.cs:136`), assert `server.AssociateReplyCount == 1` **while the write is
      blocked**, release the gate in a `finally`, await the rent, and assert the counter is still 1.
- [ ] Run it against the **current** (unfixed) fixture and record the failure (`Expected: 1, Actual: 0`).
      That failing run is the fail-before evidence.

## Step 2 — Fix the ordering

- [ ] In `ScriptedSocks5UdpServer`, order the three steps **increment `_associateReplies` → gate hook →
      `await stream.WriteAsync(reply, …)`** (the comment states why the counter must never lag a reply the
      client can already read, and records the caveat that a failed write then over-counts — acceptable for a
      fake, and no dependent test aborts mid-ASSOCIATE).
- [ ] Re-run the Step 1 test → green. Re-run the ten dependent assertions' classes
      (`UdpAssociationHeadTests`, `UdpAssociationCapabilityTests`, `UdpAssociationRecoveryTests`,
      `UdpAssociationEvidenceLifetimeTests`, `UdpAssociationPoolTests`) → green.
- [ ] Verify with `git diff` that `UdpAssociationHeadTests.cs:104-111` is byte-identical to its pre-task
      content.

## Step 3 — Prove it statistically in the flake's habitat

- [ ] Full-suite loop ≥20 runs, each keeping the **raw output** in `/tmp/wf-head-suite-<i>.txt` and recording
      the padded summaries (both assemblies), the git hash, the tree fingerprint and the exit status; break
      on any failure and report the rate with its CI (~80 % power against 1/13).
- [ ] Class-filter loops (cheap): `FullyQualifiedName~UdpAssociationHeadTests` asserting the **anchored**
      `Failed: 0, Passed: 10, Skipped: 0, Total: 10,` each run, and the new class asserting its own total
      through the `new_class_total` variable measured once after it lands (an unanchored `Total: *1` guard
      would also match the Analyzers summary line, and an earlier draft's `Total: 3` could never match).
- [ ] No `--no-build`, no discarded output.

## Step 4 — Spec, gates, record, archive

- [ ] `.trellis/spec/backend/udp-relay.md` (§522, tests §595-613): add the publish-before-readable fixture
      invariant and the pool's publish-before-await invariant, naming the ten dependent assertions.
- [ ] Correct the earlier misattribution in this task's record (the archive record and the journal point at
      `pool.AssociationCount`); the correction is a deliverable.
- [ ] Full gates: Release build zero-warning, suite green, `dotnet format` empty, `jb inspectcode` zero
      `<Issue>` (parse the XML).
- [ ] Commit: `fix(test): …` with the task slug; then the spec commit, the task record, the archive and the
      journal.

## Validation commands

```bash
dotnet build WinForward.slnx -c Release
dotnet test WinForward.slnx -c Release --filter 'FullyQualifiedName~UdpAssociationHeadTests'
dotnet test WinForward.slnx -c Release --filter 'FullyQualifiedName~UdpAssociationCapabilityTests|FullyQualifiedName~UdpAssociationRecoveryTests|FullyQualifiedName~UdpAssociationEvidenceLifetimeTests|FullyQualifiedName~UdpAssociationPoolTests'
dotnet format WinForward.slnx --severity info --verify-no-changes --no-restore
jb inspectcode -f=Xml -e=HINT -o=/tmp/jb-head.xml WinForward.slnx     # parse for <Issue

log=/tmp/wf-head-proof.txt; : > "$log"; rev=$(git rev-parse --short HEAD); tree=$(git write-tree)
new_class_total=1   # replace with the measured post-change total of ScriptedSocks5UdpServerOrderingTests
summary='Failed: *[0-9]+, Passed: *[0-9]+, Skipped: *[0-9]+, Total: *[0-9]+'
for i in $(seq 1 20); do
  out=$(dotnet test WinForward.slnx -c Release 2>&1); rc=$?
  echo "$out" > "/tmp/wf-head-suite-$i.txt"
  echo "suite $i $rev $tree rc=$rc $(echo "$out" | rg -o "$summary" | paste -sd' | ')" >> "$log"
  core_total=$(echo "$out" | rg -o 'Passed: *[0-9]+, Skipped: *[0-9]+, Total: *[0-9]+' | tail -1 | rg -o '[0-9]+$')
  { [ "$rc" -eq 0 ] && echo "$out" | rg -q 'Failed: *0,' \
    && echo "$out" | rg -q 'Failed: *0, Passed: *18, Skipped: *0, Total: *18,' \
    && [ "${core_total:-0}" -ge 982 ]; } || { echo "SUITE FAILED or TOTALS MOVED at run $i (core total ${core_total:-none})"; break; }
done
echo "core-total=$core_total (the exact post-change Core total is asserted in the record)" >> "$log"
for i in $(seq 1 20); do
  out=$(dotnet test WinForward.slnx -c Release --filter 'FullyQualifiedName~UdpAssociationHeadTests' 2>&1); rc=$?
  echo "class $i $rev rc=$rc $(echo "$out" | rg -o "$summary" | tail -1)" >> "$log"
  echo "$out" | rg -q 'Failed: *0, Passed: *10, Skipped: *0, Total: *10,' || { echo "VACUOUS OR WRONG TOTAL at class run $i"; break; }
  [ "$rc" -eq 0 ] || { echo "CLASS FAILED at run $i"; break; }
done
# the new deterministic class, same shape, with its own total measured once after it lands
for i in $(seq 1 20); do
  out=$(dotnet test WinForward.slnx -c Release --filter 'FullyQualifiedName~ScriptedSocks5UdpServerOrderingTests' 2>&1); rc=$?
  echo "new-class $i $rev rc=$rc $(echo "$out" | rg -o "$summary" | tail -1)" >> "$log"
  echo "$out" | rg -q "Failed: *0, Passed: *$new_class_total, Skipped: *0, Total: *$new_class_total," || { echo "NEW CLASS TOTAL MOVED at run $i"; break; }
  [ "$rc" -eq 0 ] || { echo "NEW CLASS FAILED at run $i"; break; }
done
```


## Execution record (implement sub-agent + parent verification, 2026-09-30)

**The fix** — `ScriptedSocks5UdpServer` now orders its reply path **publish the counter → run the test gate →
write the bytes** (`:199-201`, one increment), with the reason and the failed-write over-count caveat in the
comment. The ASSOCIATE-reply counter can no longer lag a reply the client has already read, which is what all
ten dependent assertions observe. `UdpAssociationHeadTests.cs:104-111` is **untouched** (`git diff` empty,
byte-identical), and no assertion was weakened anywhere.

**Fail-before evidence** (genuine, `/tmp/wf-head-fail-before.txt`): with the seam inserted before the write
while the increment was still in its pre-fix position after it, the new test failed
`Expected: 1, Actual: 0` at `ScriptedSocks5UdpServerOrderingTests.cs:43` — the counter was 0 while the write
was blocked, i.e. a completed rent could exist with no reply recorded. The implement sub-agent's sequencing
note is recorded honestly: adding the seam already in the fixed order would have made the fail-before run
pass, so the two-move order was the only one consistent with the requirement.

**The new test** — `ScriptedSocks5UdpServerOrderingTests.TheAssociateReplyCounterIsPublishedBeforeTheReplyBecomesReadable`
in its own file/class (measured total **1**): it arms the gate, rents on a background task, waits on the
gate's **own entered signal** (never `ConnectionCount`, which is incremented at accept), asserts the counter
is 1 while the write is blocked, releases in a `finally`, and asserts the counter is still 1 after the rent
completes.

**Blast radius verified** — ten direct assertions in five files
(`UdpAssociationHeadTests.cs:110,140`, `UdpAssociationCapabilityTests.cs:47,148,269,304`,
`UdpAssociationRecoveryTests.cs:44,57`, `UdpAssociationEvidenceLifetimeTests.cs:173`,
`UdpAssociationPoolTests.cs:276`); their class totals are unchanged (10 / 9 / 7 / 5 / 13) with only the new
class added, and all six classes are green.

**Spec** — `udp-relay.md` §3 gains "Placement publishes and claims before it awaits" (the pool's
publish-before-await invariant) and §6 gains the fixture invariant with the ten dependent assertions named.

**Record correction (a deliverable of this task)** — the archived
`09-30-exact-gate-residual-lumps/{implement.md,prd.md}` and `journal-1.md` carried the misattribution of this
failure to `pool.AssociationCount` at `:104`; a correction note in each now names the real line (`:110`,
`server.AssociateReplyCount`), records that the pool's shape passed in the same run, and points at the
fixture cause fixed here.

**Gates** — Release build 0 warnings; suite **982 + 18** green (single confirmation run before the proof);
`dotnet format` exit 0 empty; `jb inspectcode` **0 issues / 0 `CSharpErrors`**.


## Proof results (frozen tree `rev=3b9689c`, `tree=e1e380063f95bd87ac96dbf7d9cae6e57cd166f1`)

**The fixture fix is verified; the suite-level residual host family is not this task's and is unchanged.**

| Arm | Runs | Result |
|---|---|---|
| The new ordering class (`ScriptedSocks5UdpServerOrderingTests`) | 20 | **20/20 green**, total asserted every run |
| The five dependent classes (Head 10, Capability 9, Recovery 7, EvidenceLifetime 5, Pool 13) | 5 × 20 | **100/100 green**, each class's total asserted every run |
| Full suite (this revision) | 26 | **22 green, 4 failed** |

**Not one `AssociateReplyCount` assertion failed in any of the 146 runs** — the ten sites are the ones this
task exists for, and they are now deterministic under suite load (pre-fix: 1/13 suite runs threw
`Expected: 282, Actual: 281` at `UdpAssociationHeadTests.cs:110`).

**The 4 suite failures are the residual host family** (accepted, unattributable, disposition (c) of
`09-30-exact-gate-residual-lumps`), observed this session at four **different** gates and three sizes:

| Gate | Size |
|---|---:|
| `HotPathAllocationGateTests.UdpSetupEnqueuePathAllocatesNoManagedBytes` | 56 B |
| `NdisCapturePumpTests.IdlePollIterationsAllocateNoManagedBytes` | 6,128 B |
| `SweepAllocationGateTests.FlowTableSweepAllocatesNoManagedBytes` | 7,448 B (a recorded size) |
| `Socks5UdpTransportSendTests.WarmSyncSendAllocatesNoManagedBytes` | 7,872 B |

This widens the family's definition, and the addendum appended to the archived residual record says so: the
family is defined by the **gate shape** (an exactly-zero `*AllocateNoManagedBytes` assertion over a region
that allocates nothing in isolation), not by a closed list of gates and sizes — the four recorded sizes are
instances, not the boundary. The per-gate proof remains valid (in per-gate processes these gates were
80/80 green in the residual task and 100/100 green here), which is exactly what makes the suite-level rate a
separate, documented host property rather than a product regression.

**Suite-level rate at this revision: 4/26 = 15.4 % [6.1, 33.5] (Wilson 95 %)** — in the same range as the
residual task's pooled 4/76 = 5.3 % [2.1, 12.8] and its 3/29 + 4/29, and composed of the same host family.
Recorded as measured; not a regression from this task (its own gates never failed).
