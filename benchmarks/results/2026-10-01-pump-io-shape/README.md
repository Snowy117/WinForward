# F5 pump I/O shape — evidence

Task `.trellis/tasks/10-01-pump-io-shape` (finding F5 of the archived `09-29-tcp-udp-path-structural-perf`).
Host: AMD Ryzen 9 9955HX, NixOS 26.11 (Zokor), .NET 10.0.12, x64, Release. Base revision `56dd85f`.
The change under test is uncommitted, so it is identified by the **measured-surface content
fingerprint** `da99a967abddb3ccf9666812fb4dc168886e235a12879bd8d83a13d7a2591075` — the SHA-256 over the
sorted `sha256sum` output of every tracked and untracked file under `src/`, `tests/`,
`benchmarks/WinForward.Benchmarks/`, `.trellis/spec/`, `analyzers/` and the solution/props files.
`git write-tree` cannot identify this tree: with the change uncommitted it returns the *index* tree
`6599093d8033ce2b624a0b110c4fb2a65aa41d5f`, which is HEAD's tree and contains none of F5.

Fingerprint history, so each reading stays attributable. `c9c40c81d1137ad8cc58456e904674bd902be0ff2c5ae5db70a1eac44c996cf8`
is the tree the regenerated after-series and the check round's 140/140 per-gate loop ran on.
`eccd8a9c95721daa3cce1d23c9e8e747bfaac42ca90a34fccf752e6ad96e22b3` is that tree after the operator's
`dotnet format` round (doc comments, invariant-culture assert messages, one private-helper parameter
narrowed, and deconstruction/UTF-8-literal rewrites in tests — no gate window and no executed product
path changed); that round re-ran the two gate classes whose files changed (40/40) and the full suite.
The value above is the tree after the operator's `jb inspectcode` round, which removed the redundant
double dispose in the two event rows (a stop seam instead of a manual dispose of an `await using`
pump), completed the loop constructor's parameter docs, applied the `field` keyword to
`ReadShapeMismatchSink` and dropped one redundant using; it re-ran the `pump` scenario end to end, the
affected classes and the full suite. `gate-stability.txt` records each round's runs.

**This host has no Windows driver and no `ndisapi.dll`.** Everything below is proven at an injectable
seam. §5 names every on-Windows question this change depends on, with the experiment that would close
it and the fallback if the observation contradicts the expectation. Nothing here fakes a hardware
number.

## Readings

**Counts and bytes are gates; timing is a series.** Every timing row carries `gated: false` and a named
control; every exact claim is an assertion over a recorded call sequence or a byte delta, and is
re-derivable from a file in this directory. Attribution rules (design §7), applied to every number
below:

- The idle-CPU drop is attributed to **cadence**, not to pump work: the `pump.idleEvent` row reports
  `waitsPerSecond` and `emptyReadsPerSecond` beside the CPU number, so a CPU change without a cadence
  change would be visible as unattributed.
- The read-shape counts are attributed to the **recorded call sequence of the seam**, never to
  "IOCTLs saved": the only claim is about which calls the driver makes, in what order.
- The wake-latency series is attributed to the **pump's wait**, not to the driver event (the event on
  this host is an OS event). Only a Windows run can establish the real event's cost.
- The idle-IOCTL **rate** claim is attributed to the wait cadence, not to the read shape: ~10 waits/s
  replacing ~886 polls/s is the ~88× factor, and the read shape contributes at most a factor of 2
  within a poll. The two factors are reported separately.
- Nothing here is credited with a throughput change.

## Files

| File | Content |
|---|---|
| `read-shape-counts.txt` | Per-drain native call sequences before/after, both recorded reds (the pre-change `[Query, Read]` counts, and the unguarded read-first degradation with its retry count), the guard's post-arming shape and counter read-out |
| `pump-idle-wake-before.jsonl` | The comparator series: the archived `benchmarks/results/2026-09-29-benchmark-coverage/pump-idle-wake.jsonl`, 3 `--quick` runs on this host. See "before-series provenance" below |
| `pump-idle-wake-after.jsonl` | 3 landed `--quick` runs: `pump.idle`, `pump.idleEvent`, `pump.idleWake`, `pump.idleWakeEvent`, `pump.idleWake.verdict` |
| `class-totals.txt` | Per-class test totals, gate totals string, revision, tree fingerprint |
| `gate-stability.txt` | The per-gate proof loop for the allocation-gate classes plus the two new exact gates, with the injected-allocation discrimination re-proof; the check round's independent 140/140 re-derivation (which adds `MultiAdapterCaptureLoopArrivalSignalTests`), its repeated discrimination probe, and the full-suite lump attribution against the pre-change baseline |

## Commands

```bash
# after-series: 3 runs, one file per run, then concatenated (the runner truncates --output per process)
for r in 1 2 3; do
  dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- \
    --stability --scenario pump --quick --output /tmp/wf-f5-after-$r.jsonl
done
cat /tmp/wf-f5-after-{1,2,3}.jsonl > benchmarks/results/2026-10-01-pump-io-shape/pump-idle-wake-after.jsonl

# exact gates
dotnet test WinForward.slnx -c Release --filter "FullyQualifiedName~NdisApiReadShapeTests"
dotnet test WinForward.slnx -c Release --filter "FullyQualifiedName~NdisCapturePumpIdleWaitTests"
dotnet test WinForward.slnx -c Release --filter "FullyQualifiedName~CapturePumpReadCallTests"
dotnet test WinForward.slnx -c Release --filter "FullyQualifiedName~NdisCapturePumpTests"
dotnet test WinForward.slnx -c Release --filter "FullyQualifiedName~NdisApiAbiTests"
dotnet test WinForward.slnx -c Release --filter "FullyQualifiedName~MultiAdapterCaptureLoopArrivalSignalTests"
dotnet test WinForward.slnx -c Release --filter "FullyQualifiedName~NdisCaptureResilienceTests"

# the two recorded reds (temporary bodies, never committed)
dotnet test WinForward.slnx -c Release -p:TreatWarningsAsErrors=false --filter "FullyQualifiedName~NdisApiReadShapeTests"
dotnet test WinForward.slnx -c Release -p:TreatWarningsAsErrors=false --filter "FullyQualifiedName~TheMismatchNeverReachesTheTransientRetryBudget|FullyQualifiedName~AGuardedProbeThatAlsoFailsThrowsTheReadErrorLikeToday"
```

### before-series provenance (recorded deviation)

`implement.md` Step 3 asks for the before-series to be captured from a frozen Step-2 tree. It is instead
the **archived 2026-09-29 series** (`benchmarks/results/2026-09-29-benchmark-coverage/`), copied
byte-for-byte into `pump-idle-wake-before.jsonl`:

- the two comparator rows (`pump.idle`, `pump.idleWake`) are unchanged code paths — `pump.idle` installs
  no arrival signal, so it runs the identical `Thread.Sleep` branch it ran before this task, and
  `pump.idleWake`'s reader-side proxy is byte-identical — and the after-series re-measures both on the
  landed tree, which is the direct proof that they did not move;
- the two new rows (`pump.idleEvent`, `pump.idleWakeEvent`) did not exist before Step 3, so a
  pre-change run of them would be a different program, not a comparator.

No reconstructed pre-change tree was measured, so no number here is presented as a before/after pair it
is not.

## Series — before / after

Three `--quick` runs each. `before` = the archived 2026-09-29 series (see provenance below);
`after` = `pump-idle-wake-after.jsonl`.

### Idle cadence and CPU (`--duration 15`, window ≈ 15.00 s)

| Reading | before (3 runs) | after, `pump.idle` (no signal) | after, `pump.idleEvent` (100 ms wait) |
|---|---|---|---|
| polls/s | 877.6 / 885.6 / 885.8 | 895.0 / 897.1 / 897.4 | 10.0 / 10.0 / 10.0 |
| waits/s | — (none) | — (sleep pacing) | 10.0 / 10.0 / 10.0 |
| empty reads/s | ≈ polls/s (one query per poll at the driver) | 895.0 / 897.1 / 897.4 | 10.0 / 10.0 / 10.0 |
| CPU s per idle s | 0.017798 / 0.018501 / 0.017826 | 0.016704 / 0.016528 / 0.016503 | **0.000540 / 0.000539 / 0.000542** |
| `allocatedBytes` (row's own gate) | **0** | **0** | **0** |
| `readCallsPerPoll` (pump seam) | 1.0 | 1.0 | 1.0 |
| waits in the window | — | — | 150 / 150 / 150 |

Readings, not gates: the CPU seconds per idle second is a **~31× drop** between `pump.idle` and
`pump.idleEvent` on the landed tree (30.9 / 30.7 / 30.4), and it is attributed to the **cadence**
(897 polls/s → 10 waits/s, ~90×), not to pump work — the two rows run the same read path and the same
zero-allocation loop body.
The exact half of those rows is the `allocatedBytes = 0` throw, enforced inside each row.

`pump.idle`'s polls/s sits ~1.5 % above the archived series and its CPU ~8 % below it; the row's code
path is **unchanged** (no arrival signal ⇒ the identical `Thread.Sleep` branch, byte-for-byte), so the
difference is session-to-session host variation, not a moved comparator. The row is a series, not a
pass line.

### Wake latency (arrival → dispatch, 5,000 measured wakes each)

| Row | p50 (ms) | p95 (ms) | p99 (ms) | max (ms) | exact counts |
|---|---|---|---|---|---|
| `pump.idleWake` before (reader-side proxy) | 0.0779 / 0.0783 / 0.0791 | 0.1268 / 0.1239 / 0.1221 | 0.1890 / 0.1877 / 0.1662 | 0.5844 / 0.8211 / 0.7125 | `readsPerWake` 1.0 |
| `pump.idleWake` after (unchanged row) | 0.0685 / 0.0680 / 0.0673 | 0.1302 / 0.1174 / 0.1356 | 0.1812 / 0.1594 / 0.1769 | 0.3519 / 0.3853 / 0.4599 | `readsPerWake` 1.0, `readCallsPerPacket` 1.0 |
| `pump.idleWakeEvent` after (pump-side wait) | **0.0660 / 0.0661 / 0.0664** | 0.1130 / 0.1143 / 0.1169 | 0.1578 / 0.1563 / 0.1583 | 1.2134 / 0.3704 / 0.4132 | `frameReadCallsPerWake` 1.0, `emptyReadsPerWake` 1.0002, `parkConfirmationsPerWake` 1.0002, `signalDrivenReturns` 5000/5000 |

The pump-side row is the same order as the reader-side proxy it replaces, and both are ~17× below the
~1.13 ms poll cadence the old shape paid per idle→active transition. The **park-confirmation contract**
is what makes the row's samples real wakes: the harness waits for one more `Wait` entry than the
previous wake (1.0002 entries per wake over 5,500 wakes — the extra entry is the terminating one),
sleeps the park delay, then stamps arrival, arms the frame and sets the event; a missing entry fails the
row, and every measured wake was a signal-driven return (5,000/5,000, never the 100 ms timeout).

**Accounting change, stated rather than compared.** `pump.idleWakeEvent` counts one extra *seam-level*
read per wake relative to the proxy's accounting: the row reports `frameReadCallsPerWake` 1.0 (the
frame-delivering read) **plus** `emptyReadsPerWake` 1.0002 (the empty read that parks the pump), while
the old row counted only frame-delivering reads because its parked `TryReadPackets` hid the empty one —
so its `readsPerWake` of 1.0 and this row's 2.0002 describe different things. `readCallsPerPacket` is
therefore **not** an acceptance figure in this task; one read IOCTL replaces ~886 empty-queue queries per
second, which is the actual win.

**After-series re-derivation (independent check, 2026-10-01).** The three after-runs were regenerated on
the landed tree with the documented command after `emptyReadsPerWake` was corrected to a
measured-window delta: it had divided the whole run's empty reads — the 500 warmup parks included — by
the measured wake count, reporting 1.1006 where the per-wake rate is 1.0002 (5001 empty reads for 5000
measured wakes). Only that metric's definition changed; the other rows were re-measured and sit inside
session noise (see `implement.md`'s check-round dispositions). The runs were taken immediately before
the check round's doc-only edits to `PumpIdleWakeScenario.cs`/`NdisApiDriver.cs`/`NdisApiAbi.cs`; no
executable statement differs, so the series describes the fingerprinted tree.

## Read shape (counts are gates)

Per-drain native call sequences recorded at the `INdisReadPacketCalls` seam by
`NdisApiReadShapeTests`, driven through `NdisApiDriver.CreateForTests` — full detail and both recorded
reds in `read-shape-counts.txt`:

| Drain | Before (derived from the query-first body, notes §7) | After (recorded) |
|---|---|---|
| non-empty, read succeeds | `[Query, Read]` = 2 native calls | `[Read]`, `count == buffers.Length` = 1 |
| empty, read succeeds with 0 (hypothesis B) | `[Query]`, read never issued | `[Read]`, 0 packets, no error, 0 queries |
| empty, read fails, query 0 (hypothesis A) | `[Query]` | `[Read, Query]`, 0 packets, no error |
| non-empty, first read failure for the handle | `[Query, Read]` then throw | `[Read, Query, Read]` — heals the shape once per handle, delivers the packets |
| same handle afterwards | `[Query, Read]` then throw | `[Query, Read]` then throw — byte-identical |
| 1,000 conforming drains | 1,000 × query + read | 1,000 × `[Read]`, 0 queries, `ReadShapeMismatchCount` 0 |

## Windows open items

Copied verbatim from `design.md` §9. Row 1 is **acceptance-relevant** (PRD requirement 7): its failure
is the query-first rollback of Step 2, not a tuning knob.

| # | Question this host cannot answer | Experiment (on Windows, real NIC + real driver) | Expected observation | Fallback if it fails |
|---|---|---|---|---|
| **1** | **Does a read requesting the full batch capacity succeed with a short count** when the queue holds fewer packets than requested? (PRD requirement 7; **acceptance-relevant**) | (a) leave the adapter idle ≥ 60 s **first** — the §3.5 guard cannot arm on an empty queue, so this half still reads the clean A/B counters of row 2; (b) then send 1..31 packets in bursts while the pump requests 32, and watch for the `adapter.readShape.mismatch` warn (the internal counter is readable in tests/benchmarks) | either **no** mismatch line and every short request returns its short count (the ABI conforms, 1 IOCTL per drain), or exactly one line per adapter carrying `requested=32`, `queued=n`, `nativeError=…`, the pump keeps running with **no** `adapter.retry`/`adapter.degraded`, and the drain shape is `[Query, Read]` from then on | **the guard handles it — that is its purpose; the experiment only confirms which shape the hardware takes.** If the guard itself ever fails (a second mismatch warn for the same handle, a degradation, or a drain shape that is neither), the query-first rollback of `implement.md` Step 2 applies and the reverted counts are recorded — never a tuning knob |
| 2 | Does `ReadPackets` return `TRUE`+0 or `FALSE` on an empty queue? | leave the adapter idle ≥ 60 s with the pump in capture mode, then read `NdisReadDiagnostics` — through the stability/benchmark host (`WinForward.Benchmarks` is a friend assembly, `NdisApiAbi.cs:6-7`) or a temporary log line, because the counters are `internal`; the product run's evidence is the log | exactly one of: `EmptyReads` grows with flat `FailedReads`/`QueueSizeQueries` (hypothesis B, 1 read/poll, 0 queries) or `FailedReads` ≈ `QueueSizeQueries` both grow (hypothesis A) and **no** `adapter.retry` / `adapter.degraded` event | hypothesis A is already safe; if `FailedReads` grows *without* a matching query outcome of 0 (i.e. the query reports a stale non-zero), revert to the query-first shape (`implement.md` Step 2 rollback) and record the reverted counts |
| 3 | Is the empty read's last-error inside `IsTransientReadError`? | same run, `LastFailedReadNativeError` | a single stable code; if it is 31/170/995, hypothesis A | confirms the disambiguating query is load-bearing; if the code is *not* in the table, the pure speculative read becomes viable and is recorded as a follow-up (not adopted without the fact) |
| 4 | Does the event fire for every arrival, or can a signal be lost between an empty read and the wait? | register the event, idle ≥ 60 s, send exactly one packet, measure arrival→dispatch; repeat N = 200 | N/N packets dispatched within a few ms; no packet waits the full 100 ms | lower `IdleWaitTimeout` (e.g. 25 ms) and re-measure; if loss persists, drop the event and keep the sleep fallback (the feature is an optimization) |
| 5 | Is the event level-ish (a spin) or one token per signal? | same run's idle CPU + `emptyReadsPerSecond` with no traffic | ~1/timeout empty reads per second, CPU ≈ 0 | revert the event (auto-reset already protects; a spin here would mean the driver holds the event set regardless of the object's reset mode) |
| 6 | What does the wake cost on hardware (`KeSetEvent` → dispatch), and what is arrival→dispatch on a real NIC? | same experiment as row 4, reporting the p50/p95/p99 | one order of magnitude below the ~1.13 ms poll cadence | none — this is a reported series; a bad number argues for a shorter timeout, not for a revert |
| 7 | Generation-stop latency with a parked pump, on both disposal orderings | (a) production: trigger a refresh (NIC disable/enable) while the link is idle, time `adapter.refresh` → pumps-stopped; (b) direct: dispose a `MultiAdapterCaptureLoop` with N parked pumps and no cancel, time it | (a) ≈ one timeout total (the cancel at `CaptureLifecycle.cs:126` precedes the awaits, so the pumps wake concurrently); (b) N × timeout, with no production caller | if (a) exceeds the refresh budget, adopt the deferred stop-handle variant (`research/implementation-notes.md` §5): a pump-owned `EventWaitHandle` + `WaitHandle.WaitAny([arrival, stop], timeout)`, which returns immediately on dispose. (b) is controlled, not fixed |
| 8 | Does the real DLL expose `SetPacketEvent` with the pinned 3-arg signature? | `dumpbin /exports ndisapi.dll` (already recorded 2026-08-27: present) + one live registration | registration returns `TRUE`; **no** `capture.packetEvent.unavailable` warn for that adapter (a success emits no line of its own), and that adapter's idle pump shows the arrival cadence — ~1 timeout of waits per second instead of ~886 polls/s, and a wake that returns on the event rather than on the poll | if registration fails, the design *is* the fallback: `capture.packetEvent.unavailable` warn + today's poll shape |
| 9 | `CreateEvent`/`KeSetEvent` on an **auto-reset** event (the vendor samples use manual-reset) | experiment 4 with `EventResetMode.AutoReset` | works; the pump does not spin (experiment 5) | switch to the sample's manual-reset + reset-after-wait, accepting its documented lost-wakeup window; re-run experiment 4 to bound it |

## Residuals — what a seam-level proof does not establish

From `research/implementation-notes.md` §9 and `design.md` §3.2. Each is a thing this task does **not**
claim:

1. **Real IOCTL counts.** The instrument counts *call shape* (which call, in what order, with what
   requested count), never kernel round trips. The query/read split is unobservable below
   `NdisApiNative` on this host, and §1's experiments 2–3 are what would settle it.
2. **Which empty-queue ABI the real driver implements.** Both hypotheses are implemented and proven at
   the seam; only hardware picks one.
3. **Whether a full-capacity request fills a short count** (open item 1). The guard exists precisely
   because this cannot be established here.
4. **The real event's signal semantics and the wake cost.** The recorded wake percentiles are this
   host's `EventWaitHandle`; the driver's `KeSetEvent` path and a real NIC's arrival→dispatch are open
   items 4–6.
5. **The guard's factory wiring.** `NdisCaptureGenerationFactory.Create`'s registration loop and its
   `capture.packetEvent.unavailable` branch need a real driver (or a DLL without the export); the pump
   level is pinned instead (`WithoutAnArrivalSignalTheIdlePathKeepsThePollDelaySleep`), and the wiring
   loop is a recorded Windows-only residual, never a passing fact.
6. **Generation-stop latency** with a parked pump on a real adapter set (open item 7).
7. **The guard's false-positive cost on real hardware** — one extra read at the occurrence, then
   query-first for that handle until the next generation — is a priced residual, not a measured one.
8. **The park-confirmation's residual window** (added by the check round). The decorator counts a `Wait`
   *entry* before delegating, so an arm+set landing while the pump thread is preempted between the entry
   increment and the `WaitOne` call would be retained by the auto-reset token and returned immediately —
   `signalDrivenReturns` alone cannot separate that from a real wake. The 1 ms park delay makes it
   negligible rather than impossible, so the row's guarantee is exactly "a park was entered, and the
   return was signal-driven", which is what 1.0002 park confirmations per wake and 5,000/5,000 driven
   returns record.
