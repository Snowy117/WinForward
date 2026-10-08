# Analysis for the 2026-10-06 end-to-end competitor campaign

`WinForward.E2E.Analysis` reads a campaign tree, aggregates it **over passes**, and writes three
things:

| output | what it is |
|---|---|
| `tables.md` | every table the campaign report needs, with the pass count printed in every cell |
| `verdict.json` | per-row medians, the findings list, the control-block comparison, the dual phase, the ledger cross-check, and a pairwise practical-significance test per headline metric |
| `plots/` | `plots/SKIPPED.md`; the charts the reference drew are not rendered (see "Plots") |

It is a .NET console project in this repository, built with the rest of the solution and referencing the
harness's contract project (`WinForward.E2E.Contracts`), so the envelope-level field names it reads —
`run.json`'s keys, the sample counters, the ledger's records — are the same `ArmKeys` constants the
harness writes and a rename there is a compile error rather than an empty cell. The paths *inside*
`metrics` and `parameters` are string literals in this project, because the analysis addresses them
positionally (`metrics/tcp.sent` is one member name, not a nested object) and only the arm's kind knows
which map it should look in: a metric rename is therefore a two-sided change — the harness writes the new
spelling and the analysis has to be taught it — and
`benchmarks/WinForward.E2E/scripts/check-readme-contract.py`
gates the documentation half of that contract. No number in `tables.md` or `verdict.json` comes from a
chart.

## Running it

```bash
# from the campaign's own directory: the tree the orchestrator writes sits beside it
bash <repo>/benchmarks/WinForward.E2E.Analysis/scripts/analyze.sh --raw ../raw --out ..

# a one-off flat run whose own directory holds run.json and the arm files
bash <repo>/benchmarks/WinForward.E2E.Analysis/scripts/analyze.sh --raw /tmp/wf-bench/selftest/out --flat --out /tmp/selftest-analysis

# the same tree with a ledger that is not where the search would look
bash <repo>/benchmarks/WinForward.E2E.Analysis/scripts/analyze.sh --raw ./raw --ledger ./target-ledger.jsonl --out .
```

`scripts/analyze.sh` builds the project and `exec`s the binary, passing every argument through
unchanged and never changing the working directory, so the relative paths above mean what they mean
for the caller. `<repo>` is the checkout that holds this file.

| flag | default | meaning |
|---|---|---|
| `--raw` | `../raw` | campaign tree to read |
| `--out` | `..` | directory to write `tables.md`, `verdict.json` and `plots/` into |
| `--ledger` | search | target ledger JSONL; by default the script looks for `target-ledger.jsonl`, `ledger.jsonl` or `*ledger*.jsonl` in each pass directory, in `--raw` itself and in `--raw`'s parent |
| `--flat` | off | treat `--raw` itself (when it holds the run files) or its immediate subdirectories as rows of one implicit pass |
| `--warmup-seconds` | 5 | seconds of each arm excluded from the steady-state **memory** cells; the CPU cells are not warmed up (see "What the tables cannot see") |
| `--resamples` | 10000 | bootstrap resamples (over passes, never samples) |
| `--seed` | 20261006 | bootstrap seed; the per-pair seeds are derived from it deterministically |

A missing input directory prints a `usage:` line and exits 2, as does a tree with no rows in it.

## What it reads

```
<raw>/pass<N>/<row>/run.json               one object describing that client run
<raw>/pass<N>/<row>/<ARM>.jsonl            JSON Lines: sample / samplerError /
                                           result / armSummary / error records,
                                           plus the REL arm's bounded `attempt` records
<raw>/pass<N>/<row>/proxy-truth.json       {"tcp", "udp", "utcp", "total"}
<raw>/pass<N>/<row>/dual/proxied/…         a second client run (same layout as a row)
<raw>/pass<N>/<row>/dual/direct/…          a second client run, the same workload shape
<raw>/pass<N>/<row>/dual/proxy-truth.json  the row-level shape plus {"directLeak": n}
<raw>/pass<N>/dual/…                       the same phase written once per pass instead
<raw>/pass<N>/<row>/config*                the effective product configuration (hashed, not parsed)
<raw>/pass<N>/target-ledger.jsonl          the target's own ledger (one per target instance)
<raw>/pass<N>/order.txt                    optional within-pass run order
<raw>/environment.json                     optional orchestrator environment block
```

Arms are `IDLE`, `LAT`, `LATLOAD`, `DNS`, `DNSALT`, `LOSS`, `REL`, `THRU`, `MIX`, `PERSIST` and
`BASE`. The stopwatch frequency is derived per run from `(endedTicks - startedTicks) /
wallSeconds`; nothing about it is hardcoded.

A campaign runs **more than one target instance**: the shipped launcher starts a proxied target
(tcp/udp 40010, dns 53, dns-alt 40053) and a separate direct-lane target (tcp/udp 40011, dns
40054), each with its own ledger. The analysis reads every ledger it finds, merges their
records, and attributes each record to a (run, arm) by the ledger's own label and the arm's UTC
window. `--ledger` may be repeated and overrides the search; by default the pass directories,
`--raw` and `--raw`'s parent are searched for `target-ledger.jsonl`, `ledger.jsonl`,
`ledger-main.jsonl`, `ledger-direct.jsonl` or any `*ledger*.jsonl`. A run whose records are
found in the direct ledger is cross-checked against that ledger, so the two lanes are never
mixed.

The **dual phase** may be written per row (`<row>/dual/`) or once per pass (`<pass>/dual/`).
The shipped orchestrator writes one `<pass>/dual/` and rebuilds it for every dual row, so only
the last dual row of a pass survives there; the analysis attaches such a directory to the row
named by its lanes' labels and reports the other dual rows as not analysable rather than
guessing.

## The row table is the design

Every label, exclusion and comparability rule in the analysis is generated from a single table
in the source (`ROW_PROFILES`) that records, per row, the plan it runs, whether it has a dual
phase, what it does with destination-port-53 UDP and what it does with general UDP. Section 1
of `tables.md` prints it.

| row | plan | what it does with UDP/53 | what it does with general UDP |
|---|---|---|---|
| `wf-aot-opt` | full | direct via the local DNS target | proxied (UDP-over-TCP v2) |
| `wf-fdd-opt` | full | direct via the local DNS target | proxied (UDP-over-TCP v2) |
| `wf-aot-nativeudp` | udp only (`LAT`, `LATLOAD`, `LOSS`) | direct via the local DNS target | proxied (native relay) |
| `wf-aot-dnsrelay` | dns only | relayed through the proxy | proxied (UDP-over-TCP v2) |
| `proxifyre` | full | direct via the product's hardcoded port-53 pass-through | proxied (native relay) |
| `proxifier` | full | direct (the product cannot carry UDP) | not carried (UDP bypassed) |
| `proxybridge` | full | relayed through the proxy | proxied (native relay) |
| `control-pre`, `control-post` | BASE only | n/a | n/a |

Two consequences are load-bearing:

- **A partial row is never compared on an arm it did not run.** `wf-aot-nativeudp` runs only
  `LAT`, `LATLOAD` and `LOSS` (`udp-plan.json`, both latency arms over UDP); `wf-aot-dnsrelay`
  runs only `DNS` and `DNSALT` (`dns-plan.json`). Every cell and every
  pairwise comparison involving an arm the row never ran reads `not measured in this row: <arm>
  is not in the <plan> plan`, and that pair is excluded from the Holm family instead of being
  treated as a missing-at-random value or a zero.
- **A row that does not carry UDP has no UDP result.** Every Proxifier UDP cell in every table
  reads `not carried (UDP bypassed)`, and the row is excluded from the UDP-accuracy and
  DNS-latency comparisons. Its TCP results are unaffected and still compare.

## `DNS` versus `DNSALT`

`DNS` is the port-53 arm. On the rows whose DNS local target forwards port-53 UDP to the target
verbatim, and on ProxiFyre, whose source hardcodes destination port 53 to pass through
unredirected, that arm's UDP never reaches the proxy: its latency and answer rate are a
*direct-path* measurement of that row's own wiring. `DNSALT` targets a port no product
special-cases, so **only `DNSALT` is comparable across products**. Section 9 prints the port-53
carriage beside both arms' numbers, and `verdict.json` refuses to compare a port-53 DNS metric
between rows whose carriage differs.

The MIX arm measures a third `dns-rtt`, in its own page loop, on the run's DNS port; it is polled
rather than awaited, so it is comparable between rows but not with either DNS arm's cell. Item 3 of
"What the tables cannot see" has the mechanism.

## Aggregation policy

1. One value is computed per pass: one arm result record, or one statistic of that pass's
   sample stream for that row.
2. The reported number is the **median** of those per-pass values, with the interquartile range
   `[p25–p75]` and the pass count `(n=K)` printed beside it. A cell backed by a single pass is
   flagged `(n=1)` so a lone run never reads as a consensus. When a pass has no value the count
   says so, e.g. `(n=2 of 3; 1 unavailable)`.
3. Percentiles come from the harness's own histograms (`p50Us`, `p99Us`, …) and are never
   recomputed from samples.
4. A cell that cannot be computed prints `n/a (<reason>)`; a rate the harness wrote as JSON
   `null` means its denominator was zero (nothing was sent) and is printed as an **empty cell**,
   never as a zero. Nothing is silently dropped, and no cell averages over a pass count it does
   not print.

## The gates

Section 3.1 is the flow gate table; section 3.2 is the measurement-validity table.

```
tcpAttempts = LAT.tcp.connectAttempts + LATLOAD.tcp.connectAttempts
            + REL.connectAttempts + PERSIST.connectAttempts
            + THRU.streams + DNS.tcpConnections (1 when tcpSent > 0, else 0)
            + MIX.pageConnections + MIX.parameters.desktops
tcpGate     = proxy-truth.tcp / tcpAttempts               >= 0.95 required

udpArms     = UDP sockets the plan is expected to relay: LAT + LATLOAD + LOSS + MIX,
              plus DNSALT, plus DNS only when the row's profile relays port 53
udpGate     = (proxy-truth.udp + proxy-truth.utcp) > 0 required
```

- The TCP gate is the exact one. A ratio slightly under 1 is legitimate: a connection attempt
  that fails before the proxy sees a SYN never produces a proxied flow. Both sides of the ratio
  are printed, never just the ratio.
- `PERSIST` is in the denominator because it opens TCP connections like any other arm, and
  `DNS` contributes **one** connection however many queries it pipelines over it. Counting
  `DNS.tcpSent` queries as flows — which this analysis did before — inflates the denominator and
  can hide a leak.
- The UDP gate is a presence check, and the LOSS arm's own arrived/sent accounting is the real
  evidence. The DNS arms' own UDP sockets are counted only when the row's profile says that port
  is relayed, because a local-target or hardcoded port-53 datagram never reaches the proxy.
- The carriage columns check the wiring: `utcp` / `native` / `none` observed against the row's
  profile. A mismatch is a gate failure.
- The validity gates cover the `LOSS`, `LAT`, `LATLOAD` and `BASE` client-loss counters, the
  latency arms' `backlogDrops`, `scheduleTruncated`, `laneShortfall`, `windowOverflow` and
  `inFlightCeilingMs`, the MIX `idleLanes` and per-desktop lane witnesses, the per-record
  accounting identities, the sampler errors, the rejected samples and the BASE floor from both
  control blocks. A reached in-flight ceiling is a disclosed `warn` (the tail is measured
  through the deferred queue), and it also renders that arm's latency cells in section 5 as
  `n/a (windowOverflow > 0)` — a record whose `windowOverflow` is zero renders exactly as it
  always did; destroyed samples, truncated schedules, a missing lane or a broken identity are
  failures.
- `proxifier` is exempt from the UDP gate only; both control blocks are exempt from the flow
  gates and from the carriage check.

## Findings

Section 0 of `tables.md`, and `verdict.json`'s `findings`, carry everything that is not a
performance number, most serious first:

| severity | examples |
|---|---|
| `correctness-failure` | `directLeak > 0` (the product intercepted traffic configured to go direct), `foreignConnection > 0` (datagrams delivered into the wrong flow), an endpoint overlap between a proxied and a direct-path window, a dirty control block |
| `path-interference` | a dual-phase row whose *direct* lane is worse than the best row's direct lane, beyond the pre-declared threshold |
| `harness-error` | a broken UDP accounting identity, a zero lane witness, a sampler error, a sample carrying `readError`, a ledger that cannot be parsed |
| `measurement-caveat` | a reached in-flight ceiling (the affected arm's latency cells read `n/a (windowOverflow > 0)`), `connectAttempts != scheduledAttempts`, a ledger/client count outside its declared tolerance, a missing ledger |
| `informational` | which rows' port-53 DNS arm is a direct-path measurement, and which rows do not carry UDP |

The accounting identities are asserted per record:
`arrived + late + never + abandonedAtTeardown + corruptDatagrams == sent` for every LOSS, MIX and
BASE record, `answered + servfail + timeout + other == sent` for the DNS arms, and
`connectAttempts == scheduledAttempts` for REL. A UDP identity violation is a harness error and
the affected UDP fields are excluded from every aggregate rather than averaged over — the tables
say `harness error: UDP identity violated (…)` in that cell rather than printing a number. A
`scheduledAttempts` mismatch keeps its values but every cell that carries them says the arm ended
with work in flight.

## Control blocks

`control-pre` and `control-post` bracket the product block inside each pass and run `BASE` only.
Section 13 compares the two blocks against each other over the same pre-declared thresholds as
the rest of the analysis, because the post block is the only instrument in the campaign that can
detect a product that left a driver filtering after it exited. A difference is a
`correctness-failure` finding; the script also checks that the blocks really do bracket the
product block in run order. The BASE floor check in section 3.2 is inherited by every row of the
pass, and says so when it fails.

## The dual phase

Every full-plan row that supports it runs `dual/proxied` and `dual/direct`: the same workload
shape against two targets, with only the path differing. Section 12 reports `directLeak`, both
lanes' latency and loss, whether the two lanes' declared parameters match, and how far the
direct lane is from the best row's direct lane. `directLeak > 0` is reported in section 0.1 as a
correctness failure, not a performance result; a direct lane that is materially worse than the
best row's is `path-interference`, because the direct lane's latency and loss are properties of
the path rather than of the product.

## PERSIST

Section 10 reports the long-lived connection arm: requests, responses, `responseRate`,
`reconnects`, `survivedIdle` per pass, the scheduled and observed idle gaps, and the `tcp-rtt`
percentiles. `survivedIdle=false` or `reconnects>0` reads `BROKE THE IDLE CONNECTION` in the
verdict column — a headline result, not a footnote.

## The target ledger

Section 14 is the independent second opinion: the client counts what it supplied, the target
counts what arrived. The script reads `udpSummary` (cumulative counters plus a per-interval
`source` census and `sourceOverflow`), `tcp` records (one per connection, with its verdict),
`dnsSummary` (per port, written at shutdown), `tcpSummary` and `targetSummary`.

- **Attribution.** Every ledger record carries `utc` and `label`, and attribution runs in two steps.
  First the *ledger*, when the ledger names the target instance that wrote it: the shipped launcher
  starts two long-lived instances and labels each `target:<port>`, and a run is read against the
  ledger of the target its own `run.json` declares — so a record can only be attributed to a run that
  could have produced it, and a run that declared one target is never read against another's ledger.
  Then the *arm*, by the arm's UTC window: the window is derived from the client's `startedUtc` plus
  the arm's tick offsets, which is the only bridge between the client's stopwatch and the ledger's
  wall clock. A ledger that carries a second target's records is never in that run's pool, which is
  what separates the dual phase's two lanes: they overlap in time by construction, but they wrote to
  different instances. Where the campaign gave each run its own label — a target that serves one run,
  the orchestrator's own `--label`, the synthetic tree — the label also selects the run. Where the
  ledger carries a single label (a one-off run), the instance and the window attribute, and the script
  says which of the three paths it took in the environment table and in `verdict.json`. A connection
  that opens inside one arm and closes inside the next is attributed where it closed. When two runs'
  windows overlap *and the two runs share a ledger* and the ledger carries no per-run label, the counts
  are reported as unattributable rather than silently merged.
  The window compares two machines' clocks, so a campaign whose client and target clocks differ by
  seconds mis-attributes every arm's boundary; nothing in the ledger can detect that, and the
  per-arm counts are the symptom.
- **Counting.** Datagram totals come from the `sources` deltas (per-interval), not from the
  cumulative `received` field, and `sourceOverflow` is reported rather than silently dropped.
- **Tolerances** are declared in the source and printed beside every number they judge:
  connections 1 % or two connections, whichever is larger; datagrams 1 %, or one second of the
  arm's own rate, because the target summarises once a second and writes one final summary at
  shutdown, so an arm's own tail can be reported up to one summary interval late.
- **Endpoint checks.** Per row, the endpoints seen on proxied-path windows and on direct-path
  windows must be disjoint; an endpoint serving both means the product did not route that traffic
  where it was configured to. A row with no window on one side of the partition says so.
- **Per-port DNS.** `dnsSummary` is written once per listener at shutdown, so it covers the
  ledger's whole lifetime. The client's DNS and DNSALT queries — plus the MIX arm's DNS class,
  which queries the run's own DNS port — are summed over **every pass** to match, and compared
  per port with a band of one summary interval of the aggregate rate per shutdown summary. A
  port the ledger reports that no client arm used says `no client counterpart`.
- **Verdicts.** The target's per-connection verdicts are tabulated against the client's own
  expectations as a fidelity cross-check.
- **Two target-side totals are disclosed, never attributed.** §14.6 prints the datagrams the target
  could not decode and §14.7 the frames a peer's close cut in half. Neither number carries a
  sequence, a run or an arm, so neither enters any arm-level cell or gate; the captions say so, and
  §14.6's `DecodeNote` names the client's `corruptDatagrams` as the statistic that *cannot* see a
  request-path corruption the target dropped.

## verdict.json

Twenty-one headline metrics, each with the per-pass values, the median, the IQR and every
pairwise comparison, plus the findings, the control-block comparison, the dual-phase summary,
the ledger provenance and the machine-readable row table. Bootstrap resamples **passes** (never
samples), 10000 resamples, seeded.

A pair is:

| verdict | meaning |
|---|---|
| `different` | the 95 % CI excludes both zero and the pre-declared threshold band |
| `same` | the CI lies wholly inside the threshold band (an equivalence claim) |
| `inconclusive` | the CI straddles the band, or fewer than 3 passes contribute |
| `no-threshold-declared` | the metric has no pre-declared threshold (throughput, event counts); the CI is still reported |
| `n/a (not comparable)` | the other row never ran the arm, does not carry UDP, or measured a different port-53 path |

Pre-declared thresholds: latency 5 %, CPU 10 %, memory 10 %, UDP loss 0.5 percentage points, TCP
unexpected rate 0.1 percentage points. `PERSIST responseRate` is judged with the
`tcp-unexpected` family (it is a per-transaction failure rate on TCP) and `PERSIST reconnects`
and `LOSS foreignConnection` are event counts with no pre-declared threshold. Rates are compared
as percentage-point differences, the rest as ratios. Holm–Bonferroni is applied across each
metric's family of **comparable** pairwise comparisons: a pair that makes no claim is reported
as `n/a (not comparable)` outside the family, and `holm_family_size` /
`not_comparable_pairs` record the split. The adjusted verdict requires the adjusted p-value of
the claim actually made to be below 0.05: the no-difference p-value for `different`, the
bootstrap TOST equivalence p-value for `same`. Both raw and adjusted verdicts are recorded.

`REL unexpectedEofRate = metrics.unexpectedEof / metrics.connectAttempts` and
`REL fidelityRate = metrics.fidelityMismatch / metrics.connectAttempts`, both over connect
attempts rather than over completed connections.

## CPU and memory

`proxy %vCPU` and `generator %vCPU` are computed **per process identity** `(pid, startUtc)`:
each identity contributes the delta of its own cumulative `cpuSeconds`, the deltas are summed,
and the sum is divided by the wall-clock span of the arm's sample stream. A first/last
difference over a process *name* — which this analysis did before — is corrupted by a mid-run
restart and hides unreadable processes. A sample whose `readError` is true is rejected from every
CPU and memory cell, because the harness writes `null` (not `0`) for a process it could not read
and a zero there is not a measurement; the rejected count and any `samplerError` records are
printed and reported as findings.

**CPU scope is the sampled process's own time, user mode and its own system calls — nothing else.**
Every CPU cell is the delta of `Process.TotalProcessorTime` for the product process the sampler
matched: the time the kernel charges to that process's threads, its user time plus the privileged
time those threads spend in system calls. Kernel-mode work the product causes *outside* its own
threads is measured nowhere here — interrupt, DPC and ISR time in a kernel data path, packets a
driver serves for other processes, and machine-wide CPU are all outside the number — so a
kernel-heavy product can show a low cell while still costing the machine real CPU. §6 says the same
thing under its table, and `headroom %` is against logical-processor capacity rather than a measured
machine total for the same reason. The **warmup window applies to the memory cells only**: §6 reads
every readable sample of the arm, because a CPU rate wants the whole arm as its denominator.
`generator %vCPU` is the sampler's own cost, read from the same per-identity accumulation but from
the `self` series, so the cost of generating the load is never charged to the product.

One consequence is worth stating plainly: the row-level `proxy CPU` in the headline matrix
concatenates **every non-IDLE arm's samples for the pass** into one first-to-last span, so both its
numerator and its denominator include the idle stretches between arms, while every §6 cell is one
arm alone. The two are different populations; the headline is the run-level cost, not the sum of the
section's cells.

## What the tables cannot see

Eight properties of the instrument change how a cell in this file has to be read. Each can invert a
conclusion that the numbers alone appear to support, and each is a property of the harness, the
ledger or the analysis rather than of any product. Items 1–2 concern the latency table, 3–4 the
histogram columns, and 5–8 the ledger, the resource cells and the throughput column.

1. **`tcp-connect`'s population is successful connects only.** A probe that fails to connect
   contributes no sample to that histogram. `metrics.tcp.connectFailures` counts the failures and
   **no gate reads that counter and no table prints it** — the `connectFail %` column in §11 is the
   REL arm's own outcome, a different population — so a product that hangs or resets a connect
   contributes nothing to the column while a product that answers slowly contributes a large sample:
   the column can invert a connect ranking by itself. Read `metrics.tcp.connectFailures` and
   `metrics.tcp.connectAttempts` from the record before drawing anything from the histogram.
2. **`tcp-connect` and `metrics.meanConnectMs` are two statistics, never one pair.** For `LAT` and
   `LATLOAD` the histogram holds only the 1 Hz connect probe, measured from each probe's *intended*
   instant (so pacing lateness is inside the sample); `metrics.meanConnectMs` is the mean over every
   *successful* connect the arm made — lane connects and probes together, each from the start of its
   own connect — and `REL` and `PERSIST` publish their own over their own attempts. They may both be
   printed, but they must never be cited side by side as one measurement of the same thing.
3. **The MIX arm's `dns-rtt` is polled, not awaited.** The MIX page loop asks `Socket.Available` and
   waits `Task.Delay(1)` between asks, so its sample carries up to one timer tick that has nothing to
   do with the path; on Windows that tick is up to 15.6 ms on top of a sub-millisecond round trip.
   The `DNS` and `DNSALT` arms measure the same statistic from a dedicated receive loop and do not pay
   it, so the MIX cell is comparable between rows (every row pays the same poll) but not with the DNS
   arms' cells.
4. **The latency histograms saturate at 17.18 seconds.** Their buckets clamp into `[1 ns, 2³⁴ − 1 ns]`,
   there is no overflow counter, and a percentile reports the exclusive upper bound of its bucket: a
   printed `maxUs` of `17179869.183` means "17.18 s or more", and every longer hang is
   indistinguishable from a 17.18 s one. Any tail drawn from these histograms is a tail up to that
   ceiling.
5. **The ledger attributes runs by label and window, and identifies no packet.** A ledger record
   carries `utc` and `label` and nothing else that names a run: the analysis joins the label to a run
   and bounds it with the arm's UTC window, derived from the client's `startedUtc` plus the arm's tick
   offsets, and where a ledger carries a single label the window alone attributes (§14.1 prints which
   case applied). Its UDP census is written once a second, and datagram totals come from the `sources`
   deltas of that interval rather than from the cumulative `received`; `dnsSummary` is written once
   per listener at shutdown and covers the ledger's whole lifetime. Nothing in it identifies a
   datagram, so `undecodable` — the only witness of corruption on the request path — is a target-side
   total with no sequence number: it belongs to no run and no arm, no cell anywhere in this file
   includes it, and §14.6 discloses it as such. A frame the peer's close cut in half is the same kind
   of number and is disclosed the same way in §14.7.
6. **The 5-second warmup is a memory window, not a CPU window.** The CPU cells read every readable
   sample of the arm, and the row-level `proxy CPU` in the headline matrix concatenates every non-IDLE
   arm of a pass into one first-to-last span, so its denominator contains the idle stretches between
   arms while its numerator contains the CPU the product burned during them. "IDLE is excluded" is
   therefore true of the arms that are summed and not of the interval that is divided by. One footnote
   still says otherwise: §2's `steady-state warmup` row reads "N s of every arm excluded from the
   memory and CPU steady-state cells". The memory half is the measured behaviour; the CPU half is a
   wording this analysis inherited from the frozen reference, and correcting it would change a compared
   table cell and therefore require refreezing `verification/` (see `FROZEN.md`). This document and
   §6's own caption are the authority until that refreeze happens.
7. **The memory slope is fitted over arms concatenated in load order.** The time series behind the
   leak slope appends one arm's post-warmup samples after another's in the analysis's fixed
   `ArmRecords.LoadOrder`, not in the order the pass actually ran them, and it fits private bytes
   against that concatenated elapsed time. When a pass's real order differs — the orchestrator seeds
   it per pass — the curve jumps between arms and back, so the slope and the leak verdict describe the
   shape of the concatenation rather than a product's memory over wall-clock time. The `MIX`-only
   slope is the one series that is a single arm's own.
8. **`THRU` measures up to its own declared ceiling, and nothing above it.** The arm paces every
   stream to an aggregate `targetBytesPerSecond` and sends one 32 KiB frame at a time per stream, so a
   path that can carry more than the declared rate is reported *at* the declared rate: rows that
   differ by a factor of two above the ceiling print the same goodput, and nothing in the table marks
   the ceiling — `metrics.budgetReached` says only whether the arm stopped on its byte budget or on
   time, and an arm that keeps up with its pacer stops on the budget. The metric carries no
   pre-declared threshold (`no-threshold-declared` in `verdict.json`), so only its confidence interval
   separates rows — and at the ceiling there is nothing for it to separate. Read the row's goodput
   against its plan's declared rate before calling a tie a result.

Each item also has its counterpart in the harness's own document,
[../WinForward.E2E/README.md](../WinForward.E2E/README.md) ("Non-obvious properties"); item 5 is
stated under the tables it qualifies as well (§14.6 and §14.7), and item 6 records the one footnote
that still disagrees with it.

## The rule of three

In the UDP accuracy and TCP reliability tables, a cell is printed as `< 3/n` when *every* pass
reports exactly zero, with `n` that row's denominator: zero observed events bound the true rate
near 3/n, they do not prove it is zero. A cell whose median is zero but which has a non-zero pass
keeps its ordinary `0 [0–x]` rendering so the spread stays visible.

## Plots

An ECDF per arm is impossible from the summary histograms the harness writes, so the percentile
curve uses the harness's own p50/p90/p99/p999 instead. The seven plots the reference drew are

1. `latency-percentiles.png` — percentile curve per row for `LAT` and `LATLOAD` `tcp-rtt`
2. `loss-rates.png` — grouped bars of `LOSS` `lossRate` and `corruptRate` (rows that do not carry UDP are omitted)
3. `rel-outcomes.png` — stacked `REL` outcome distribution per row
4. `mix-private-bytes.png` — private bytes over time per row inside the `MIX` arm
5. `cpu-per-arm.png` — proxy CPU percent of one vCPU per row per arm, per process identity
6. `lat-p99-by-pass.png` — `LAT` p99 against pass index, so run-order drift is visible
7. `dual-direct-lanes.png` — proxied versus direct lane per row

None of them is drawn: the analysis writes `plots/SKIPPED.md` unconditionally instead, and states
there that the plots are not reproduced and that their inputs are all still in `tables.md`.

## Judgement calls worth knowing about

- **The loss threshold is declared, not derived.** `window` is the plan's `lossWindowMs`
  (200 ms when the plan declares none). An earlier caption in this analysis claimed
  `max(200 ms, 5 × observed p99 RTT)` capped at 2000 ms; that was wrong and is gone.
- **Not every row runs every arm.** The plan table above is the authority, and the analysis says
  `not measured in this row` rather than treating a design absence as a gap.
- **Proxifier's UDP numbers do not exist.** Every UDP cell reads `not carried (UDP bypassed)`.
- **The port-53 DNS arm is not comparable across products**; `DNSALT` is.
- **Machine-wide CPU is not sampled.** `headroom %` is expressed against logical-processor
  capacity: `100 × (P × 100 − generator %vCPU − proxy %vCPU) / (P × 100)`. The CPU columns are also
  the sampled process's own time only; see "CPU and memory" for the full scope and for what the
  warmup does and does not cover.
- **The memory slope spans arms with different load shapes**, so it is given twice — over all
  measured arms and over the `MIX` arm alone — and only the CI decides the leak verdict. The
  all-arms series is concatenated in the analysis's fixed load order, not in the pass's real run
  order; see item 7 of "What the tables cannot see".
- **Fewer than 3 passes is never enough to decide a pair**, so such comparisons are reported as
  `inconclusive` rather than guessed.
- **Only the most-sampled product process name is analysed** per row, with ties broken by ordinal name
  comparison — and every tick writes one record per name, so a tie is the normal case rather than an
  edge one. A product whose work is split across two images has only one of them counted, and two
  processes that share one name (a service and its GUI, say) are summed into one series without being
  flagged. §2's sampling table is the provenance: it prints the configured names, the one that won,
  and every other name it saw with its record count (`name xCount`), which shows the split but not how
  many processes carried a name.
- **A ledger arm's datagram count is judged with a one-second band**, because the target
  summarises its UDP census once a second and once more at shutdown.

## Verification

`verification/synthetic/make_tree.py` writes a fabricated multi-pass tree that follows the harness's schema
and deliberately contains every shape the campaign can produce, including the failures — a
`directLeak`, a `foreignConnection`, an identity violation, a zero lane witness, a `samplerError`,
a `readError` sample, a mid-run restart, a `scheduledAttempts` mismatch, a control-block drift and
a per-pass ledger. Run it, then run the analysis over it:

```bash
python3 benchmarks/WinForward.E2E.Analysis/verification/synthetic/make_tree.py /tmp/wf-synth/raw
bash benchmarks/WinForward.E2E.Analysis/scripts/analyze.sh --raw /tmp/wf-synth/raw --out /tmp/wf-synth/out
```

A real one-off run is read the same way:

```bash
bash benchmarks/WinForward.E2E.Analysis/scripts/analyze.sh --raw /tmp/wf-bench/selftest/out --flat --out /tmp/wf-selftest-out
```

A missing directory, an empty directory, or a tree with no rows exits 2 with a `usage:` line.

The tree and the documents it is judged against are frozen under
`benchmarks/WinForward.E2E.Analysis/verification/`; `FROZEN.md` there records what is frozen, how it
was produced, and what the boundary trees are. `scripts/check-fairness.py` and
`verification/check-boundary-trees.py` assert the disclosures this document describes.
