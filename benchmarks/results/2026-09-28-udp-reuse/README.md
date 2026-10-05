# 2026-09-28 UDP association reuse — Step 1 acceptance baseline

> **Superseded in part, 2026-10-05 (task `10-05-harness-response-ownership`).** The **per-flow success
> columns** of this directory's `udp.churn` and `udpBurstEstablishment` rows — `firstResponses`,
> `establishmentLossRate`, and the `firstResponseMs` distribution — are not measured per flow: the
> sinks that produced them chose a first-response timestamp by the payload's own flow id without
> checking which flow the reply arrived on, and the harness server answers every reply to the last
> sender of its association. On the same binary, only 7.9 % of the burst's replies arrived on the
> flow that asked while the rows reported 48/48 with loss 0. The corrected baseline and its
> commands are in [`../2026-10-05-udp-reuse-ownership/`](../2026-10-05-udp-reuse-ownership/README.md);
> the *resource* columns here (`bytesPerSession`, sessions, waves, allocations, GC counts) and the
> `udp.lossRate` row — a forward-direction measurement — **still stand**.

Task `09-28-udp-association-reuse`, Step 1 (budget / retention / observability) — recorded against
the **uncommitted Step 1 tree**: the three validated config keys (`udpSessionCapacity`,
`udpRelayReceiveBufferKb`, `udpSessionIdleSeconds`), the 128 KiB relay receive-buffer default
(down from 512 KiB), the 30 s UDP idle retention with the fast UDP sweep cadence, the four
`udpCapacityRejections` / `udpSetupFailures` / `udpAssociationLost` / `udpAssociationRecovered`
counters, and the now rate-limited capacity/setup warns. No association pool exists yet (Step 2).

The three JSONL files were captured to `/tmp/step1-*.jsonl` and are copied here verbatim.

## Commands

```text
dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- \
  --stability --scenario udp --pps 25000 --duration 60 \
  --output benchmarks/results/2026-09-28-udp-reuse/step1-udp.jsonl

dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- \
  --stability --scenario udpBurst --burst-flows 48 --dial-delay-ms 100 --duration 60 \
  --output benchmarks/results/2026-09-28-udp-reuse/step1-burst.jsonl

dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- \
  --stability --scenario churn --burst-flows 48 --churn-waves 0 --duration 120 --socks5-external \
  --output benchmarks/results/2026-09-28-udp-reuse/step1-churn.jsonl
```

All other options are defaults (256 flows, 512-byte payload, seed 42). Host: NixOS 26.11, .NET
10.0.12, X64; one run per scenario (not a ≥3-run batch).

## `udp.lossRate` — 25 kpps × 60 s (256 flows)

| Metric | Value |
|---|---|
| sent / destination received | 1,495,689 / 1,495,689 |
| lossRate | 0 |
| outOfOrder / duplicates | 2 / 0 |
| responsesInjected | 1,495,689 |
| achievedPps | 24,925.1 |
| relay hops | received 1,495,945, decodeDropped 0, forwarded 1,495,945, replies 1,495,945, sendFaults 0 |
| sendLoopOverflows | 117 |

## `udp.burstEstablishment` — 48 flows × 100 ms dial delay (25 kpps background)

| Metric | Value |
|---|---|
| accepted / rejected | 48 / 0 |
| firstResponses | 48 |
| establishmentLossRate | 0 |
| firstResponseMs min / p50 / p95 / p99 / max | 102.0 / 304.4 / 605.6 / 605.7 / 605.7 |
| timeToFirst / timeToLast / timeToIssue | 102.0 / 605.7 / 4.9 ms |
| background (control / burst / post) loss | 0 / 0 / 0 (send p95 ≈ 0.06 ms) |
| unattributedResponses | 0 |

Baseline `../2026-09-06-udp-burst-ttl-fix/` (same 48 × 100 point, `--flows 16 --pps 4000 --payload-bytes 512 --seed 42`):
firstResponses 48 → 48, loss 0, p50 309.0 → 325.0 ms, max 624.7 → 628.4 ms. This run's p50 304.4 ms
and max 605.7 ms sit inside that band.

## `udp.churn` — 48 flows × 120 s sustained, out-of-process SOCKS5 server

| Metric | Value |
|---|---|
| waves / sessions / accepted / rejected | 1,611 / 77,328 / 77,328 / 0 |
| establishmentLossRate | 0 |
| allocatedBytes | 1,010,408,608 |
| **bytesPerSession** | **13,066.5** |
| bytesPerSecond / sessions per second | 7,560,655 / 578.6 |
| gen0 / gen1 / gen2 collections | 62 / 35 / 1 |
| wave bytesPerSession min / p50 / p95 / max | 5,709.5 / 12,847.5 / 15,233 / 22,537.3 |
| firstResponseMs min / p50 / p95 / p99 / max | 1.1 / 12.5 / 29.2 / 44.0 / 121.2 |
| wallSeconds | 133.6 |

Comparison sources:

- `hot-path.md` §3 ledger (relay/churn family): churn whole cycle **≤14,500 B/session** wave shape
  and **≤14,300 B/session** sustained (measured 13,249–14,070 / 13,720–13,869); the N=48/D=0 wave
  cell was re-measured at 12,560.3 B/session after the 09-22 teardown-tier reductions. §6 states the
  same bar as "churn ≤14,500 B/session" for out-of-process real-dial runs.
- 09-22 churn measurement (`.trellis/tasks/archive/2026-09/09-22-udp-admission-capacity-alloc/research/raw/confirm-churn.jsonl`,
  wave shape, N=48/D=0, 60 s, `--socks5-external`): 48 accepted / 0 rejected / 48 first responses /
  loss 0 / 12,591.2 B/session.
- This run's 13,066.5 B/session is +4.0 % over the 12,560.3 wave cell and +3.8 % over the 09-22
  single-wave measurement, and stays inside the ≤14,300 sustained band. It is a sustained (120 s,
  1,611 waves) shape, not the single-wave cell.

## Socket shape and retention caveat

`udpRelayReceiveBufferKb` now defaults to **128 KiB** per relay socket (was a hard-coded 512 KiB)
and `udpSessionIdleSeconds` to **30 s** retentions (the sweep tail shrank from ~2.5 min), so the
socket shape and the retention window differ from every pre-Step-1 framework/churn number. Managed
B/session stays comparable in shape, but kernel receive-buffer totals and any measurement whose
harness carried a per-connection relay buffer (the superseded in-process-server figures) must be
read with that stated. `--socks5-external` keeps the harness server's own relay buffer out of this
number; it is a managed-allocation measurement, not a kernel-memory one.

---

# Step 2 — association pool acceptance (sharing ON)

Recorded against the uncommitted Step 2 tree (association pool; the harness scenarios hard-code
`always` because `udpAssociationReuse: auto` is off-equivalent until Step 3). `step2-always-*.jsonl`
are the implementer's final-tree runs, `step2-check-churn.jsonl` the independent check's re-run; the
two agree, so both headline claims below reproduce.

| Scenario | Step 1 | Step 2 (`always`) |
|---|---|---|
| `udp` 25 kpps × 60 s | 1,495,689 sent = received, loss 0, 24,925 pps, 117 overflows | 1,498,696 = 1,498,696, loss 0, 24,976 pps, 16 overflows |
| `udpBurst` 48 × 100 ms | 48/48, loss 0, p50 304.4 ms | 48/48, loss 0, p50 305.3 ms (check) / 308.8 ms (implementer) |
| `churn` 48 × 120 s `--socks5-external` | 77,328 sessions, 13,066.5 B/session, firstResponse p50 12.5 ms | 222,192 sessions, **7,556.7 B/session (−42.2 %)**, **p50 3.241 ms (−74 %)**, 0 rejected, loss 0 |

The churn gain is the point of the task: one authenticated control connection and one ASSOCIATE now
serve 16 flows, so the per-flow framework cost (control connect + greeting + ASSOCIATE — 93 % of the
measured per-session framework path) is amortized instead of paid per flow. The churn still lands
inside the `hot-path.md` §3 anchors (≤14,500 wave / ≤14,300 sustained).

---

# Step 3 — capability detection on the production default (`auto`)

Recorded against the uncommitted Step 3 tree **after the check's fix round** (per-lease evidence held
as the association's live attached set, so a sample can only read the leases attached at that
instant; a warn-level product-event census carried by every stability row). The three harness
scenarios construct the pool with `UdpAssociationReuseMode.Auto` — the production default — instead
of the previous hard-coded `always`; `step3fix-*.jsonl` are the runs below.

| Scenario | Step 1 (per-flow) | Step 2 (`always`) | Step 3 (`auto`) |
|---|---|---|---|
| `udp` 25 kpps × 60 s | 1,495,689 = received, loss 0, 24,925 pps, 117 overflows | 1,498,696 = 1,498,696, loss 0, 24,976 pps, 16 overflows | 1,499,000 = 1,499,000, loss 0, **24,982.2 pps**, 15 overflows |
| `udpBurst` 48 × 100 ms | 48/48, loss 0, p50 304.4 ms | 48/48, loss 0, p50 305.3 / 308.8 ms | 48/48, loss 0, p50 **305.5 ms**, max 613.6 ms |
| `churn` 48 × 120 s `--socks5-external` | 77,328 sessions, 13,066.5 B/session, p50 12.513 ms | 222,768 sessions, **7,564.7 B/session**, p50 3.203 ms | 222,720 sessions, **7,577.5 B/session**, p50 3.246 ms, 0 rejected, loss 0 |

**Fallback evidence.** Every row now carries the `productEvents` census and
`udp.association.fallback` is one of its names, so "`auto` never flipped the server" is a recorded
number instead of an unasserted claim: all three rows report **`udp.association.fallback: 0`**
(alongside `udp.session.capacity-block: 0` and `udp.setup.failed: 0`). The census enables only
`Warn`-level product events — every product logging site guards on `IsEnabled`, so the per-datagram
trace/debug events stay out of the send path and the rows remain throughput/latency-comparable with
the Step 1/2 runs; the scenarios' compile-time `CaptureProductEvents` switch still adds the verbose
census for a loss-localization session.

`auto` **shared** in all three runs: the churn B/session is the discriminator (≈7.6 KB shared vs
≈13 KB per-flow), and zero fallback events confirm the loopback server's permissive response pattern
was settled `SharedOk` rather than flipped to per-flow. Step 3's churn is +0.2 % over Step 2 and
−42.0 % versus Step 1, inside the `hot-path.md` §3 anchors (≤14,500 wave / ≤14,300 sustained); the
burst p50 sits in the same band as Steps 1–2.

`step3-*.jsonl` are the pre-fix Step 3 runs the check reviewed and are kept for comparison: their
Step 3 column was `udp` 1,497,001 / 24,947.2 pps / 35 overflows, `udpBurst` 48/48 / p50 306.0 ms, and
`churn` 221,664 sessions / 7,582.9 B/session — the fix round's re-run above reproduces them within
run-to-run noise, so the evidence model change moved no headline number. Those rows predate the
census change and carry no `productEvents` field, which is exactly the gap the fix round closed.

## File-size exception (dated, explicit)

`directory-structure.md` caps every `.cs` file at 400 effective lines. A repo-wide scan (excluding
`obj/`/`bin/`) finds exactly **two** files above the cap, both benchmarks and both predating this
task: `benchmarks/WinForward.Benchmarks/Stability/GcSoakScenario.cs` (685 effective lines; the Step 3
change is the one-token `UdpAssociationReuseMode.Always` → `Auto` switch, net 0 effective lines) and
`benchmarks/WinForward.Benchmarks/Perf/SessionSetupDecompositionBenchmarks.cs` (909 effective lines,
untouched). They are recorded here (2026-09-28) as an explicit exception with splitting as a
follow-up rather than silently inherited; this task added no lines to either file. Every file this
task touched is ≤400 effective lines (largest: `UdpChurnScenario.cs` 326,
`UdpAssociationPool.cs` 316, `UdpAssociationCapabilityTests.cs` 288).


## Framework instruments stay on `off`

`UdpSessionBenchmarks` and `FrameworkSetupBenchmarks` still construct the pool with
`UdpAssociationReuseMode.Off`, deliberately. They are per-session framework/allocation instruments
whose recorded anchors (`hot-path.md` §3: the ≤5,400 B/session Noop probe, the ≤8,200 B/session
framework ladder, the ≤14,500/≤14,300/≤17,500 churn anchors) were all measured against the per-flow
shape. Pooling control connections removes the ~3.8 KB/session dial that those anchors bracket, so
running them on `auto` would silently re-base every recorded number instead of comparing against it.
The `auto` sharing claim is carried by the `udp.churn` scenario above and by the §3 ledger's
"reusing control connections is the only structural lever there".

---

# Step 4 — session-budget soak (`--stability --scenario udpSessionBudget`)

Two generations of this instrument are recorded below. The **pre-caps** runs (A/B/C) measured the
pool's original 16 × 16 = 256-flow shared head and are kept verbatim as the historical record that
motivated the caps round; everything the pre-caps runs claimed about pooling is **superseded**. The
**post-caps** runs (`step4-session-budget-caps.jsonl`, `step4-session-budget-caps-lowrate.jsonl`)
re-measure the same two load shapes against the shipped default head of 16 × 1,024 = 16,384 flows per
server. The instrument was hardened in the same round: the shared-placement slack no longer scales
with the per-server ceiling, a churn window that cannot discriminate retention from accumulation is
refused before the load, `--require-pooling` makes the pooling half mandatory, the estimated kernel
receive buffer is asserted as the byte form of the retention ceiling, and every row carries the
baselines its per-session ratios are computed from.

The acceptance instrument for PRD acceptance 1 and for R4/R5, recorded against the uncommitted Step 4
tree (Steps 1–3 committed at `a60363a`). One new UDP flow arrives every `1 / --rate` seconds for
`--churn-seconds`, each sending exactly one datagram whose echo is the flow's establishment proof;
then a `--drain-seconds` window runs with no new flows. The arrival schedule is anchored at the churn
window's own start, so the warm-up duration is never emitted as a burst of catch-up arrivals. The
coordinator's idle expiry is driven for the whole run — warm-up included — on the **production**
sweeper cadence, taken from `IdleExpirySweeper.DeriveUdpSweepInterval` for the validated 30 s UDP
retention (`max(5 s, idle / 2)` capped by the 60 s main-leg interval = **15 s**; every row states
`sweepIntervalSeconds` 15 and `retentionSeconds` 45). `--capacity` defaults to the validated
configuration default (`ConfigurationLoader.DefaultUdpSessionCapacity`, 16,384) and is rejected at
parse time outside the product's own `1..16384` range, so a capacity the product would refuse cannot
make the "strictly below `--capacity`" assertion vacuous.

Every ~5 s one row reports elapsed/phase, live sessions, the process's descriptors, the pool's
`AssociationCount`/`LeasedFlowCount`, the accepted/rejected/expired counters, datagrams
sent/received/lost, the estimated kernel receive buffer, and the per-session descriptor/byte/buffer
ratios; the final row is the run's verdict. The two per-session measurements are deltas against the
run's **post-warm-up baselines** — `baselineFileDescriptors` and `baselineManagedBytes`, both carried
by every row — so either ratio is recomputable from a row alone; `managedBytes` itself is a raw
process total (the in-process harness's own transport buffers included) and is not a per-session
figure. `relayReceiveBufferBytes` is `live sessions × the configured relay receive buffer`, the same
product the product's heartbeat reports (`Cli/Program.cs`), and `relayReceiveBufferBytesPerSession`
must reproduce the configured buffer for a non-empty population.

The descriptor series is the **proxy's own**: Linux `/proc/self/fd` (Windows `Process.HandleCount`
fallback) minus the two sockets the in-process harness SOCKS5 server owns per accepted control
connection (`harnessServerConnections`, 2 per connection); `--socks5-external` moves those into a
child process, which is why the external runs are the clean byte/descriptor measurement.

The run fails (after writing the verdict row) on any of: a churn window whose cumulative flow count
does not exceed the retention ceiling (refused *before* the load, with "raise `--churn-seconds` above
X"); a refused flow or capacity/setup rejection during churn; a lost datagram; a live-session count at
or above `--capacity`; a steady-state population above the retention ceiling
`rate × (idle + 2 × sweep) + margin`; an estimated receive buffer above that ceiling in bytes or a
per-session buffer that is not the configured one; a descriptor delta above
`1.25 × sessions + associations + 32`; an association count above the shared fan-out ceiling while
the population fits the pool's shared budget (and, under `--require-pooling`, a saturated population
that skips that check at all); or a drain that does not return to zero sessions, zero leases, zero
associations, and the pre-churn descriptor baseline. The summary row names the two halves
independently: `verdict.retentionBounded` and `verdict.poolingCovered`, alongside
`retentionDiscriminating`, `minimumDiscriminatingChurnSeconds`, and the `pooling` block — a reader
does not have to infer which term held from the prose.

## Commands

```text
# --- PRE-CAPS (historical record: the 256-flow shared head) --------------------------------
# A — the short acceptance run (harness server in this process, the parent's command verbatim)
dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- \
  --stability --scenario udpSessionBudget --rate 100 --churn-seconds 60 --drain-seconds 120 \
  --output benchmarks/results/2026-09-28-udp-reuse/step4-session-budget.jsonl

# C — the same load with the harness server out of process (clean descriptor and managed-byte series)
… --stability --scenario udpSessionBudget --rate 100 --churn-seconds 60 --drain-seconds 120 \
  --socks5-external --output benchmarks/results/2026-09-28-udp-reuse/step4-session-budget-external.jsonl

# B — the pooled regime: the population stays inside the pool's then-256-flow shared budget
… --stability --scenario udpSessionBudget --rate 5 --churn-seconds 120 --drain-seconds 120 \
  --socks5-external --output benchmarks/results/2026-09-28-udp-reuse/step4-pooled-budget.jsonl

# --- POST-CAPS (default head 16,384 flows per server) -------------------------------------
# D — the acceptance load at the post-caps default head
dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- \
  --stability --scenario udpSessionBudget --rate 100 --churn-seconds 90 --drain-seconds 120 \
  --socks5-external --require-pooling \
  --output benchmarks/results/2026-09-28-udp-reuse/step4-session-budget-caps.jsonl

# E — the low-rate pooled regime, unchanged load shape
… --stability --scenario udpSessionBudget --rate 5 --churn-seconds 120 --drain-seconds 120 \
  --socks5-external --require-pooling \
  --output benchmarks/results/2026-09-28-udp-reuse/step4-session-budget-caps-lowrate.jsonl
```

All other options are defaults (512-byte payload, seed 42, `auto` association reuse, 30 s UDP
retention / 15 s sweep). `--churn-seconds` must exceed the ceiling's discrimination minimum — at
`--rate 100` that is 63 s, at the default `--rate 20` it is 63 s too (`20 × 63 = 1,260 > 1,240`) —
otherwise the run is refused before the load with "raise `--churn-seconds` above X". The post-caps
rate-100 run uses **90 s** rather than the pre-caps 60 s because 60 s is arithmetically
non-discriminating (6,000 flows ≤ the 6,200 ceiling): the load shape is identical, the window is
longer so the ceiling can actually reject an implementation that never expires a session.
`--require-pooling` fails the run if the pooling half was not evaluated, so a pass cannot come from a
saturated sample that skipped it. Host: NixOS 26.11, .NET 10.0.12, X64; one run per cell.

## Measured — pre-caps (historical, superseded)

> The pooling column of these runs is the pre-caps finding, not the shipped shape: the pool's shared
> head was 16 × 16 = 256 flows per server, so at the acceptance load 94 % of flows were served by
> private associations. Both rate-100 runs predate the churn-window precondition (their 60 s window
> is not discriminating) and predate the receive-buffer/managed-baseline fields; they are kept exactly
> as recorded. The post-caps table below is the current shape.

| Metric | A — rate 100 (in-process) | C — rate 100 (`--socks5-external`) | B — rate 5 (pooled regime) |
|---|---|---|---|
| new flows accepted / rejected | 6,000 / 0 | 6,000 / 0 | 600 / 0 |
| datagrams sent / received / lost | 6,000 / 6,000 / **0** | 6,000 / 6,000 / **0** | 600 / 600 / **0** |
| first response min / p50 / p95 / p99 / max (ms) | 0.51 / 1.4 / 1.87 / 7.77 / 33.15 | 0.76 / 2.05 / 2.49 / 4.7 / 48.51 | 0.93 / 1.08 / 1.6 / 3.15 / 4.65 |
| peak churn sessions (capacity 16,384) | 4,500 | 4,001 | 224 |
| steady-state sessions vs retention ceiling | 4,500 ≤ 6,200 | 3,999 ≤ 6,200 | 224 ≤ 332 |
| expired sessions (warm-up + churn) | 6,016 | 6,016 | 605 |
| **descriptors per live session** | **1.95** | **1.94** | **1.06** |
| **associations per live session** | **0.95** | **0.94** | **0.067 (≈ 1/15)** |
| descriptor-budget headroom (worst sample) | +784 | +783 | +71 |
| managed bytes per live session | 107 KB (harness relay buffers in-process) | **16.0 KB** | 44.1 KB |
| pooling coverage assertion | reported saturated (4,244 sessions beyond the shared budget) | reported saturated (3,743 sessions beyond the shared budget) | **evaluated and held** |
| drain: sessions zero at | t = 90.4 s | t = 90.6 s | t = 150.1 s |
| drain: associations / leases / descriptor delta | 0 / 0 / +1 | 0 / 0 / +1 | 0 / 0 / +1 |
| verdict | **passed** | **passed** | **passed** |
| `udp.association.fallback` / capacity-block / setup.failed | 0 / 0 / 0 | 0 / 0 / 0 | 0 / 0 / 0 |

## Measured — post-caps (current)

Run D is the acceptance load with the shipped caps and `--require-pooling`; run E is the low-rate
pooled regime. Both are `--socks5-external`, one run per cell, and both verdicts carry
`retentionBounded: true` and `poolingCovered: true` — the two terms PRD acceptance 1 states, each
recorded rather than inferred.

| Metric | D — rate 100, 90 s churn | E — rate 5, 120 s churn |
|---|---|---|
| new flows accepted / rejected | 9,000 / 0 | 600 / 0 |
| datagrams sent / received / lost | 9,000 / 9,000 / **0** | 600 / 600 / **0** |
| first response min / p50 / p95 / p99 / max (ms) | 0.61 / 0.90 / 1.31 / 2.38 / 39.27 | 0.92 / 1.07 / 1.70 / 2.93 / 6.66 |
| steady-state peak sessions vs retention ceiling | **4,500** ≤ 6,200 | **225** ≤ 332 |
| steady-state sampled sessions (5 s rows) | 4,000 (the rows alias the 3,000–4,500 sawtooth) | 200 |
| **descriptors per live session** (worst row) | **1.07** | **1.065** |
| **associations per session** (at the steady peak) | **0.0627 (≈ 1/16)** | **0.067 (≈ 1/15)** |
| steady-state peak associations vs shared-placement ceiling | **282 ≤ 296** (`ceil(4,500/16) + 16`) | **15 ≤ 31** (`ceil(225/16) + 16`) |
| estimated kernel receive buffer at the worst row | 500 MiB (4,000 × 128 KiB) | 25 MiB (200 × 128 KiB) |
| retention bound on that estimate | 775 MiB (6,200 × 128 KiB) | 41.5 MiB (332 × 128 KiB) |
| descriptor-budget headroom (worst row) | +783 | +71 |
| managed bytes per live session (above the post-warm-up baseline) | **9.3 KB** | **19.1 KB** |
| pooled (`pooling.saturated`) | **false** — `poolingCovered: true` | **false** — `poolingCovered: true` |
| drain: sessions zero at | t = 120.3 s (30.3 s into the drain) | t = 150.3 s (30.3 s into the drain) |
| drain: associations / leases / descriptor delta | 0 / 0 / +1 | 0 / 0 / +1 |
| verdict | **passed** (`retentionBounded: true`, `poolingCovered: true`) | **passed** (`retentionBounded: true`, `poolingCovered: true`) |
| `udp.association.fallback` / capacity-block / setup.failed | 0 / 0 / 0 | 0 / 0 / 0 |

### Why the steady peak and the worst sampled row differ

The live population is a sawtooth, not a level: every 15 s the sweep releases the cohort that has been
idle for 30 s, so the population steps down and climbs again between ticks — measured at rate 100,
between ≈3,000 and ≈4,500. The 5 s rows are snapshots and can miss the crest by up to a sample interval
of arrivals, so the summary reports both: `steadyStateSessions` (the arrival-granular peak, which the
retention ceiling is asserted against) and `steadyStateSampledSessions` (the worst row, the alias the
pre-caps tables quote — which is why run C's peak reads 4,001 there while run D's sampled value is
4,000 and its true peak 4,500). The pooling check reads the peak pair
(`steadyStateSessions` / `steadyStatePeakAssociations`), because the association count ratchets with the
true peak while the row is a snapshot: run D's worst row shows 4,000 sessions with 280 associations
(1.07/session) against a true peak of 4,500 with 282 (0.0627/session). The pooled fan-out
`1/16 = 0.0625` is visible only against the peak.

## Retention bounds the population, not the flow count

Runs A/C create 6,000 flows in 60 s while 6,016 sessions are expired by the same 15 s sweeper
cadence, and the live population oscillates between 3,000 and 4,500 instead of growing linearly; the
drain then reclaims everything (sessions zero 30 s into the drain, associations zero after their 60 s
warm-retention, descriptors back to baseline +1). Post-caps run D repeats the shape with a 90 s churn
window — 9,000 cumulative flows against the same 6,200 ceiling — so the assertion can actually reject
an implementation that never expires a session.

That discriminance is now enforced rather than left to the reader. A churn window whose
`rate × churn-seconds` does not exceed the ceiling is **refused before the load** with "raise
`--churn-seconds` above X": such a window would let a no-expiry implementation end the churn inside
the bound, so the run must not record it as retention evidence. At `--rate 100` the ceiling is 6,200
and X = 62 (the minimum is 63 s); at the pre-caps 60 s the two run-A/C commands would now be rejected.
The scenario's default `--churn-seconds` is **90** for exactly that reason: at the default `--rate 20`
the ceiling is `20 × (30 + 2 × 15) + 40 = 1,240`, and 20 × 63 = 1,260 is the first discriminating
window, so 90 clears it with headroom for the 5 s sample cadence. The one-hour acceptance below is the
strongest case: 360,000 cumulative flows against the same 6,200 ceiling.

## The pooling ratio — pre-caps finding (superseded)

**Superseded by the caps round; kept as the historical record that motivated it.** With the pool's
original shared placement budget of **256 concurrent flows per server** (`MaxAssociationsPerServer` 16
× `FlowsPerAssociation` 16, design I6), the pool served every flow beyond it from a *private*
association, never refusing it. At ≥ 100 new flows/s the steady state is `rate × (idle + sweep)`
≈ 3,000–4,500 live sessions, so 4,244 of them sat outside the shared budget: the control connection
was effectively per-flow again, and the measured shape was one relay socket plus one control
connection per session — **1.94–1.95 descriptors/session**, the same cost as
`udpAssociationReuse: off`. Raising `udpAssociationMaxPerServer` to 1,024 (head 16,384 flows per
server = the default capacity) removed it; the post-caps table above is the shipped shape.

The descriptor budget assertion (`fds − baseline ≤ 1.25 × sessions + associations + 32`) is the
pooling *accounting* — it charges one descriptor per association, not per session — so it passes in
both shapes; the discriminating numbers are the reported ratios and the **pooling coverage
assertion**. Its ceiling is `ceil(sessions / flowsPerAssociation) + min(cap, 16)`: at the acceptance
load that is `ceil(4,500 / 16) + 16 = 298`, against 282 at the ideal fan-out. The slack is a clamped
constant, so raising the per-server ceiling can no longer widen it (the pre-caps formula charged the
whole ceiling and would have allowed ≈1,100 private control connections); a shape with 10 % of flows
held privately (704 associations) fails it, and the 16-association slack is the honest residual for
cold and warm associations kept beyond the ideal fan-out. Run B exercises the pooled regime with the
assertion evaluated (`pooling.saturated: false`) and held: **1.06 descriptors/session** and
**0.067 associations/session**, i.e. the pooling claim is asserted, not inferred from `B/session`.
Under `--require-pooling` a population that saturates the shared head fails the run instead of
skipping the check, which is what the one-hour artifact uses.

For PRD acceptance 1 the two terms are therefore separate and separately recorded: the population (relay
sockets, descriptors, ephemeral ports and the estimated kernel receive buffer) is **flat over time**
because retention bounds it rather than the cumulative flow count, and the control-connection half is
**sub-linear in the concurrent flow count** because one authenticated association serves 16 flows —
`associations ≈ sessions / 16` — up to the shipped head of 16,384 flows per server.

## Harness notes (benchmarks only)

- `LoopbackSocks5UdpServer.MaximumControlConnections` 4096 → **16,384** (the product's default/max
  session capacity). The pre-caps rate-100 shape held one control connection per concurrent
  association beyond the shared budget (~4,250 at peak), which the old cap would have refused,
  surfacing as datagram loss inside the measured process; post-caps the same load needs ≈ 282–290, so
  the raise is headroom rather than a requirement (it also covers the `udpAssociationReuse: off`
  shapes and a saturated pool at the 1,024-association ceiling).
- `LoopbackSocks5UdpServer.ConnectionCount` exposes the harness's live control connections so the
  session-budget scenario can subtract its two sockets per connection when the server runs in-process.
  Pre-caps in-process the rate-100 run peaked at 17,370 raw descriptors (8,850 of them the proxy's)
  and 562 MB working set; `--socks5-external` cut that to 7,855 raw descriptors and 152 MB, which is
  why the external series is the one to quote for managed bytes. The post-caps external rate-100 run
  peaks at ≈ 4,900 proxy descriptors with the harness's sockets out of the process.

## The 1-hour acceptance command

```text
dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- \
  --stability --scenario udpSessionBudget --rate 100 --churn-seconds 3600 --drain-seconds 300 \
  --socks5-external --require-pooling \
  --output benchmarks/results/2026-09-28-udp-reuse/step4-session-budget-1h.jsonl
```

360,000 flows at 100/s against the 6,200 ceiling — 5,700× the discrimination minimum of 63 s, so the
retention assertion is as sharp as this scenario gets — with `--require-pooling` so the verdict row
states which half held. Expected shape, from the post-caps short runs: the live population sawtooths
between ≈3,000 (just after each sweep) and ≈4,500 (just before it), so `steadyStateSessions` ≈ 4,500
and `steadyStateSampledSessions` somewhere in that band; ≈ 1.07 descriptors per live session in the
rows (one relay socket plus one shared control connection per 16 flows; 0.0627 associations per session
at the peak); estimated kernel receive buffer ≈ 4,000–4,500 × 128 KiB ≈ 500–563 MiB against the
retention bound 6,200 × 128 KiB ≈ 776 MiB; 0 loss / 0 rejections; `verdict.retentionBounded: true` and
`verdict.poolingCovered: true` with `pooling.saturated: false`.
The drain must reach zero sessions, zero leases and zero associations inside 300 s
(idle + 2 × sweep + the 60 s association warm-retire ≈ 165 s). The pool's shared head is 16,384 flows
per server, so the acceptance load must **not** saturate: a `poolingCovered: false` in this artifact is
a product regression, not an instrument problem. `--socks5-external` matters more here than in the
short runs: the in-process harness's echo tracker keeps a 64-entry sequence ring per *observed* flow
(≈0.6 KB each), so an hour of 360,000 distinct flows would add ≈200 MB of managed growth to the
**measured** process and skew `managedBytesPerSession`; out of process it accrues in the child, whose
memory the parent's series does not describe. The parent records the 1 h row here after running it.

## 1-hour acceptance run (measured 2026-09-29)

`step4-session-budget-1h.jsonl` — the command above, unchanged, on a quiet host (NixOS 26.11,
.NET 10.0.12, 32 cores; 3,902 s wall). The verdict row is
`{"passed":true,"retentionBounded":true,"poolingCovered":true,"requirePooling":true,"failures":[]}`.

| Metric | Value |
|---|---|
| flows / rejected / expired | 360,000 / 0 / 360,016 |
| datagrams sent = received / lost / lossRate | 360,000 = 360,000 / **0** / **0** |
| firstResponseMs min / p50 / p95 / p99 / max | 0.41 / **0.878** / 1.158 / 1.372 / 84.18 |
| steady-state sessions: true peak / worst sampled row vs ceiling | 4,500 / 4,000 vs 6,200 (`retentionDiscriminating: true`, minimum 63 s) |
| peak shared associations vs ceiling | 282 ≤ 296 (`associationsPerSession` 0.0627) |
| descriptors: baseline → final (+Δ), per live session, headroom | 94 → 95 (+1), **1.070**, +781 |
| estimated kernel receive buffer: total (per live session) vs bound | 500 MiB (131,072 B = the configured 128 KiB) ≤ 775 MiB |
| managed bytes per live session above baseline | 9.5 KB (no forced GC, so this includes uncollected Gen2) |
| drain: sessions zero at / final sessions, associations, leases | t = 3,630.3 s (30 s into the drain) / 0, 0, 0 |
| product events (capacity-block / setup.failed / association.fallback) | 0 / 0 / 0 |
| working set at the end | 174 MB (flat through the run) |

How to read it: the live population follows `rate × retention` (4,500 = 100/s × 45 s) rather than the
360,000 cumulative flows, so per-flow resources are **bounded over time** instead of accumulating; and
the control-connection half of the per-flow cost is amortized (one authenticated association per 16
flows), so descriptors are 1.07 per live session rather than ≈2. What still scales with the live
population is the R1/I2 floor — one relay socket per live flow — and the kernel receive-buffer estimate
that follows from it; both are bounded by retention. `poolingCovered: true` is meaningful here because
`--require-pooling` fails a saturated population: the shared head (16,384 flows/server) is above the
admission capacity, so a `false` in a future run means the caps or placement regressed.



