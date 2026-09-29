# Implementation plan — stabilize the flaky gate and the hanging run

Order: probe the premise → reproduce → diagnose → fix A → attack B through its race → hunt → **then** the
stability proof (B's test-tree changes would invalidate an earlier one) → spec → record → commit → archive.

## Step 0 — Preflight

- [ ] Read the recorded evidence again and keep it as the baseline: `1/25` isolated runs and `2/13`
      full-suite runs, pooled 3/38 = 7.9 %, Wilson 95 % CI [2.7 %, 20.8 %]; the affected test is
      `HotPathAllocationGateTests.ReverseRewriteAndInjectAllocatesNoManagedBytes`
      (`HotPathAllocationGateTests.cs:112`, unchanged since `1a56eec`) and it drives a reverse SYN-ACK,
      not a mid-flow packet.
- [ ] Note the two facts that shape everything: **the 7520 figure is recorded once** (treat determinism as
      something to establish), and CI runs **no tests**, so the proof procedure lives with the spec.
- [ ] Confirm the current totals (`979` Core.Tests + `18` Analyzers) and that the affected filter selects
      exactly **11** tests, so a vacuous match cannot pass for a green loop.
- [ ] Read `hot-path.md:302-304,316-317,369-370,415-431` — the per-thread counter caveat, the repo's
      negative tiering result, and the gate shape this task must adopt.

## Step 1 — Probe the tier-up premise (before spending the diagnosis budget)

- [ ] Trivial one-thread loop: a method called ~200×, per-call `GC.GetAllocatedBytesForCurrentThread()`
      deltas, with and without `DOTNET_TieredCompilation=0` / `DOTNET_TieredPGO=0`.
- [ ] Record the outcome in the task record with the `hot-path.md:316-317` citation. If (as expected) a
      compile-time event cannot land on the calling thread's counter, say so and move on; do not spend
      further time on the tiering family.

## Step 2 — Reproduce defect A on demand, and settle single-vs-sum

- [ ] Reproducer: loop the **exact** measured body, per-iteration deltas, **no excluded iterations**,
      reporting every firing iteration index and its delta.
- [ ] Answer the arithmetic question first: one firing carrying the whole delta ⇒ a single object; repeated
      equal small deltas ⇒ a sum. (7520 ÷ 40 = 188, ÷ 64 = 117.5 — neither matches a per-iteration shape.)
- [ ] Raise the iteration count until it fires; record the observed firing rate of the reproducer itself,
      so its sensitivity is known. If it fires once and never again in-process while the real gate still
      fails ~1/25 runs, fall back to looping the real class filter and recording the firing iteration and
      delta from the gate's failure path — that fallback is what keeps the diagnosis alive when the
      reproducer's own process state is the trigger.

## Step 3 — Attribute defect A

- [ ] Bisect **only over sub-steps the gate drives**: sequence tracking / reverse endpoint rewrite /
      injector call / fake collaborator / whole body. The reset-injection tail is out (a failure path the
      gate never enters).
- [ ] Enumerate the plausible product-side allocations on the winning sub-step and check each against the
      measured size before believing any of them (`new NdisPacketBuffer` ≈ 40 B on a shared-pool miss is
      the only one the current path can reach).
- [ ] Write the diagnosis into the task record **before** fixing: the site, the condition, the evidence.

## Step 4 — Fix defect A and harden the gate

- [ ] Apply the fix the diagnosis calls for (product fix with a regression test, or test fix with the
      missing precondition explained).
- [ ] Harden the gate to the spec's landed shape: `IsCompletedSuccessfully` asserted,
      `Environment.CurrentManagedThreadId` captured and unchanged, exact `Assert.Equal(0, allocated)`
      kept, call-count backstop kept. No warm-up tuning.
- [ ] Re-prove discrimination: inject a managed allocation into the measured region → exact failure →
      restore → green.
- [ ] Commit: `fix(test): …` or `fix(<layer>): …` with the task slug.

## Step 5 — Attack defect B through its race, then hunt

- [ ] Write the `SetupExecutor` race test: `Dispose()` on a background thread racing an enqueue; require
      either a completed completion or an explicit refusal (today the swallow at `:203-208` plus the
      drain-then-dispose order at `:293-306` can leave the completion uncompleted forever). If the natural
      interleaving will not fire, add a minimal internal test seam to force it: reaching the window by
      construction beats concluding the fix is unjustifiable.
- [ ] If it fires, fix it with the repository's existing post-enqueue-recheck pattern
      (`NdisPacketBufferPool.cs:119-141`, `NativeBufferPool.cs:96-121`), with the race test as the
      regression test.
- [ ] Hunt: `--blame-hang --blame-hang-timeout 90s --blame-hang-dump-type mini --results-directory
      /tmp/wf-blame` over as many full-suite runs as the wall clock allows; keep any `Sequence_*.xml` and
      name the test it points at. Demonstrate the mechanism once by injecting a deliberate hang into a
      scratch test, recording the sequence file that names it, then restoring.
- [ ] Report "not reproduced in N runs, 95 % upper bound p < 3/N" if it does not fire. Do **not** assert on
      the setup thread count (64 workers parked is the designed state) and do **not** add `Fact(Timeout)`.

## Step 6 — Stability proof (after every test-tree change has landed)

- [ ] ≥100 consecutive green runs of the **class** filter `FullyQualifiedName~HotPathAllocationGateTests`
      (the same selection as the recorded 1/25 baseline), each recording the padded summary line with
      `Total: 11`, the git hash and the process exit status. No `--no-build`, no discarded output.
- [ ] ≥40 consecutive green full-suite runs with the same record shape (20 runs is only ~81 % power
      against the observed 7.9 %; 40 reaches ~96 %).
- [ ] A failure anywhere stops the loop and returns to diagnosis; it is not re-run away.
- [ ] Record the loop commands and the summary of results in the task record.

## Step 7 — Spec, gates, record, archive

- [ ] `hot-path.md`: the gate-shape rule, the repeat-run procedure as a named section, the corrected suite
      figure (the stale "678 tests green"), and the per-thread-counter caveat referenced from the gate rule.
- [ ] Full gates: Release build zero-warning, full suite green, `dotnet format` empty, `jb inspectcode`
      zero `<Issue>` (parse the XML; the exit code is meaningless).
- [ ] Record: diagnosis A, the discrimination proof, the race-test outcome, the hunt bound, both loop
      results, and the pre-fix baseline with its CI.
- [ ] Commit the spec and the task record, archive, journal.

## Validation commands

```bash
dotnet build WinForward.slnx -c Release
dotnet test WinForward.slnx -c Release
dotnet format WinForward.slnx --severity info --verify-no-changes --no-restore
jb inspectcode -f=Xml -e=HINT -o=/tmp/jb-flake.xml WinForward.slnx     # parse for <Issue

# premise probe
DOTNET_TieredCompilation=0 dotnet test WinForward.slnx -c Release --filter 'FullyQualifiedName~TieringProbe'   # scratch, removed afterwards

# stability proof — every run recorded, never discarded
log=/tmp/flake-proof.txt; : > "$log"; rev=$(git rev-parse --short HEAD)
summary='Failed: *[0-9]+, Passed: *[0-9]+, Skipped: *[0-9]+, Total: *[0-9]+'
for i in $(seq 1 100); do
  out=$(dotnet test WinForward.slnx -c Release --filter 'FullyQualifiedName~HotPathAllocationGateTests' 2>&1); rc=$?
  echo "filter $i $rev rc=$rc $(echo "$out" | rg -o "$summary" | tail -1)" >> "$log"
  [ "$rc" -eq 0 ] || { echo "NON-ZERO EXIT at filter run $i"; break; }
  echo "$out" | rg -q 'Total: *11' || { echo "VACUOUS MATCH at $i"; break; }
  echo "$out" | rg -q 'Failed: *0,' || { echo "FAILED at $i"; break; }
done
for i in $(seq 1 40); do
  out=$(dotnet test WinForward.slnx -c Release 2>&1); rc=$?
  echo "suite $i $rev rc=$rc $(echo "$out" | rg -o "$summary" | tail -1)" >> "$log"
  [ "$rc" -eq 0 ] || { echo "NON-ZERO EXIT at suite run $i"; break; }
  echo "$out" | rg -q 'Failed: *0,' || { echo "SUITE FAILED at $i"; break; }
done

# hang hunt — bounded by wall clock, dumps under /tmp (never multi-GB inside the repo)
deadline=$(( $(date +%s) + 1800 )); runs=0
while [ "$(date +%s)" -lt "$deadline" ]; do
  runs=$((runs + 1))
  /usr/bin/time -f "%e s" dotnet test WinForward.slnx -c Release --blame-hang --blame-hang-timeout 90s \
    --blame-hang-dump-type mini --results-directory /tmp/wf-blame || { echo "HANG at run $runs"; break; }
done
echo "hunt runs: $runs (sequence files under /tmp/wf-blame)"
```

## Completion record (implement sub-agent, verified in the parent session)

**Change set** (8 files, +357/−17): `WinForward.Core.Tests.csproj` (host contract
`<TieredCompilation>false</TieredCompilation>` with the evidence and the cost in a comment),
`HotPathAllocationGateTests.cs` + `SweepAllocationGateTests.cs` + `CapturePumpReadCallTests.cs` +
`NdisCapturePumpTests.cs` (the landed gate shape and the bounded exactly-zero probe preflight),
`SetupExecutor.cs` (the pool family's post-enqueue recheck + the worker guard), `SetupExecutorTests.cs`
(the natural race test), and `hot-path.md` (the new section, the reconciliation, the corrected figure).

**Step outcomes.**

| Step | Outcome |
|---|---|
| 1 premise probe | A first-call/tier-up event **can** land on the calling thread's counter (136 B at call 0 of a fresh `NoInlining` method) — `design.md` §0.1's mechanism claim was wrong; the toggle matrices below settle the family |
| 2 reproduce | Reproducer fired once at iteration 0 (128 B / 40 B) — the reverse body is steady-state clean; the lump lives in the **counter-read-only control** (7,336–7,360 B, 12/40 and 14/20 process runs) |
| 3 attribute | No driven sub-step carries it; sizes 168 / 1,880 / 5,216 / 7,336 / 7,360 / 7,384 / 7,448 / 8,008 B; the archived 7520 = 4 × 1,880 is a **sum**; the failing test here is `DispatcherWarmFastPathAllocatesNoManagedBytes` |
| 4 fix A | Host contract (tiering off, 0/20) + the gate shape; product config untouched; **cost ~7 s → ~10.5 s per suite run**, on the record |
| 4 discrimination | `new byte[64]`/iteration → 5,632 B and 22,528 B exact; restored green; the `Assert.Equal`-in-a-window trap found and specced |
| 5 defect B | Race fired naturally (no seam): pre-fix 3/3 host aborts and 1,997/1,998 stranded; fixed with the post-enqueue recheck + worker guard; 2,000-attempt run `stranded=0`; the ODE half has no assertion-based test (pre-fix behaviour is a process abort) — recorded as an explicit gap |
| 5 hunt | 47 runs, 0 hangs, 0 sequence files, **p < 3/47 = 6.4 %**; the naming mechanism demonstrated once with an injected hang, then restored |
| 6 stability | Filter **100/100 green** (verified from `/tmp/wf-proof-filter.log`: 100 lines with `rc=0` and `Total: 11`, none non-green); full suite **not** 40 consecutive — recorded as the unmet arm with its evidence |
| 7 spec | `hot-path.md`: gate-shape rule, repeat-run procedure, tiering host contract + cost, reconciliation of the older contradictory note, the assertion trap, the corrected 998 figure, the ODE gap, the two follow-up candidates |

**Verified independently in the parent session**: build 0 warnings; suite **980 + 18** green; `dotnet format`
exit 0 empty; no `[Fact]`/`[Theory]` removed (the suite count grew by one); the proof log's 100 green runs;
the residual failures' names and sizes from the implementer's own capture files; `git diff -- src/`
contains only the justified `SetupExecutor.cs` change.

**Deviation, stated plainly**: acceptance criterion "full suite in ≥40 consecutive runs" is **unmet**.
The residual is a different family — host-level lumps that survive the tiering-off contract, landing in
other exact gates, plus one unrelated timing race — and is carried by the follow-up task
`09-30-exact-gate-residual-lumps`. Nothing was declared fixed that was not: the affected class has zero
full-suite failures after the fix, and the residual's evidence is recorded for its own diagnosis.
