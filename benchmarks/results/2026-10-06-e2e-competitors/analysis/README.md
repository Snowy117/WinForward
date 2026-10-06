# Analysis for the 2026-10-06 end-to-end competitor campaign

`analyze.py` reads a campaign tree, aggregates it **over passes**, and writes three things:

| output | what it is |
|---|---|
| `tables.md` | every table the campaign report needs, with the pass count printed in every cell |
| `verdict.json` | per-row medians, the findings list, the control-block comparison, the dual phase, the ledger cross-check, and a pairwise practical-significance test per headline metric |
| `plots/` | seven PNGs, or `plots/SKIPPED.md` when matplotlib is not importable |

It is Python 3 standard library only. matplotlib is optional, imported lazily, and only used for
the plots — no number in `tables.md` or `verdict.json` comes from a chart.

## Running it

```bash
# the campaign tree the orchestrator writes: <raw>/pass<N>/<row>/...
python3 analyze.py --raw ../raw --out ..

# a one-off flat run whose own directory holds run.json and the arm files
python3 analyze.py --raw /tmp/wf-bench/selftest/out --flat --out /tmp/selftest-analysis

# the same tree with a ledger that is not where the search would look
python3 analyze.py --raw ./raw --ledger ./target-ledger.jsonl --out .
```

| flag | default | meaning |
|---|---|---|
| `--raw` | `../raw` | campaign tree to read |
| `--out` | `..` | directory to write `tables.md`, `verdict.json` and `plots/` into |
| `--ledger` | search | target ledger JSONL; by default the script looks for `target-ledger.jsonl`, `ledger.jsonl` or `*ledger*.jsonl` in each pass directory, in `--raw` itself and in `--raw`'s parent |
| `--flat` | off | treat `--raw` itself (when it holds the run files) or its immediate subdirectories as rows of one implicit pass |
| `--warmup-seconds` | 5 | seconds of each arm excluded from the steady-state memory and CPU cells |
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
  through the deferred queue); destroyed samples, truncated schedules, a missing lane or a
  broken identity are failures.
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
| `measurement-caveat` | a reached in-flight ceiling, `connectAttempts != scheduledAttempts`, a ledger/client count outside its declared tolerance, a missing ledger |
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

- **Attribution.** Every ledger record carries `utc` and `label`. Where the campaign gave each
  run its own label (the orchestrator labels every run, including both dual lanes), the label
  selects the run and the arm's UTC window bounds it; the window is
  derived from the client's `startedUtc` plus the arm's tick offsets, which is the only bridge
  between the client's stopwatch and the ledger's wall clock. Where the ledger carries a single
  label (a one-off run), the window alone attributes, and the script says so in the environment
  table and in `verdict.json`. A connection that opens inside one arm and closes inside the next
  is attributed where it closed. When two runs' windows overlap and the ledger carries no
  per-run label, the counts are reported as unattributable rather than silently merged.
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

## The rule of three

In the UDP accuracy and TCP reliability tables, a cell is printed as `< 3/n` when *every* pass
reports exactly zero, with `n` that row's denominator: zero observed events bound the true rate
near 3/n, they do not prove it is zero. A cell whose median is zero but which has a non-zero pass
keeps its ordinary `0 [0–x]` rendering so the spread stays visible.

## Plots

An ECDF per arm is impossible from the summary histograms the harness writes, so the percentile
curve uses the harness's own p50/p90/p99/p999 instead. The seven plots are

1. `latency-percentiles.png` — percentile curve per row for `LAT` and `LATLOAD` `tcp-rtt`
2. `loss-rates.png` — grouped bars of `LOSS` `lossRate` and `corruptRate` (rows that do not carry UDP are omitted)
3. `rel-outcomes.png` — stacked `REL` outcome distribution per row
4. `mix-private-bytes.png` — private bytes over time per row inside the `MIX` arm
5. `cpu-per-arm.png` — proxy CPU percent of one vCPU per row per arm, per process identity
6. `lat-p99-by-pass.png` — `LAT` p99 against pass index, so run-order drift is visible
7. `dual-direct-lanes.png` — proxied versus direct lane per row

All are written at 150 dpi. Without matplotlib the script writes `plots/SKIPPED.md` instead,
which contains the exact command to regenerate them.

## Judgement calls worth knowing about

- **The loss threshold is declared, not derived.** `window` is the plan's `lossWindowMs`
  (200 ms when the plan declares none). An earlier caption in this analysis claimed
  `max(200 ms, 5 × observed p99 RTT)` capped at 2000 ms; that was wrong and is gone.
- **Not every row runs every arm.** The plan table above is the authority, and the analysis says
  `not measured in this row` rather than treating a design absence as a gap.
- **Proxifier's UDP numbers do not exist.** Every UDP cell reads `not carried (UDP bypassed)`.
- **The port-53 DNS arm is not comparable across products**; `DNSALT` is.
- **Machine-wide CPU is not sampled.** `headroom %` is expressed against logical-processor
  capacity: `100 × (P × 100 − generator %vCPU − proxy %vCPU) / (P × 100)`.
- **The memory slope spans arms with different load shapes**, so it is given twice — over all
  measured arms and over the `MIX` arm alone — and only the CI decides the leak verdict.
- **Fewer than 3 passes is never enough to decide a pair**, so such comparisons are reported as
  `inconclusive` rather than guessed.
- **Only the most-sampled product process is analysed** per row; the provenance table lists every
  non-self process name it saw, so a second name is easy to spot.
- **A ledger arm's datagram count is judged with a one-second band**, because the target
  summarises its UDP census once a second and once more at shutdown.

## Verification

`synthetic/make_tree.py` writes a fabricated multi-pass tree that follows the harness's schema
and deliberately contains every shape the campaign can produce, including the failures — a
`directLeak`, a `foreignConnection`, an identity violation, a zero lane witness, a `samplerError`,
a `readError` sample, a mid-run restart, a `scheduledAttempts` mismatch, a control-block drift and
a per-pass ledger. Run it, then run the analysis over it:

```bash
python3 synthetic/make_tree.py /tmp/wf-synth/raw
python3 analyze.py --raw /tmp/wf-synth/raw --out /tmp/wf-synth/out
```

A real one-off run is read the same way:

```bash
python3 analyze.py --raw /tmp/wf-bench/selftest/out --flat --out /tmp/wf-selftest-out
```

A missing directory, an empty directory, or a tree with no rows exits 2 with a `usage:` line.
