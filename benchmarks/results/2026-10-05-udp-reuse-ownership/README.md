# 2026-10-05 UDP association reuse — response-ownership-corrected baseline

> **Removed feature, 2026-10-05 (task `10-05-remove-udp-association-sharing`, R6).** Every column in
> this directory — `off`, `auto` and `always`, across churn, burst and the session-budget soaks — is
> a measurement of UDP association sharing, which no longer exists: the three configuration keys were
> removed, the pool / lease / control-association / capability machinery was deleted, and a
> proxy-decided flow now owns its own authenticated association. The `reuse` parameter on every row
> and the `reuseMode` metadata field name the removed knob, and the commands below are
> **historical**: `--reuse` is gone from the harness, and a command line that still passes it fails
> at parse with the harness's unknown-argument error. The `off` column recorded the placement the
> tree now ships (one association per flow, reached then through the pool's `Off` branch), so its
> resource and latency figures still describe the surviving shape; `auto` and `always` describe a
> path that is no longer reachable. Confirmation series for the surviving columns:
> [`../2026-10-05-no-association-sharing/`](../2026-10-05-no-association-sharing/README.md).
> **No number in this directory was edited or deleted** — this note is the only change.

Task `10-05-harness-response-ownership` (child of `10-05-udp-association-sharing-correctness`, R1).
This directory is the corrected baseline for the sharing comparison: the harness now records a relay
reply as a flow's first response **only when the reply arrived on the flow that asked for it**, and
every affected row reports the per-flow accounting (`firstResponses` / `misdelivered` /
`noResponse`, summing to the flow count) plus the `reuse` mode it ran under.

Before this change the three per-flow sinks chose a first-response timestamp by the payload's own
flow id and never checked the arriving flow. The loopback SOCKS5 server in front of them reproduces
the connection-oriented server's write path faithfully — one peer address per association, every
reply written to the last sender (`LoopbackSocks5UdpServer.SendReplyAsync`) — so a reply the server
delivered to the wrong flow was recorded as that flow's own answer. The measured result below is
what that hid.

Host: NixOS 26.11, .NET 10.0.12, X64. One run per cell (not a ≥3-run batch). All columns were
produced from **one binary**; the placement mode is the `--reuse` argument, the production default
being `auto`.

## Commands

```text
# churn, three columns (48 flows/wave, 3 waves, 50 ms dial delay)
dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- \
  --stability --scenario udpChurn --burst-flows 48 --churn-waves 3 --dial-delay-ms 50 \
  --reuse off --output benchmarks/results/2026-10-05-udp-reuse-ownership/churn-off.jsonl
dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- \
  --stability --scenario udpChurn --burst-flows 48 --churn-waves 3 --dial-delay-ms 50 \
  --reuse auto --output benchmarks/results/2026-10-05-udp-reuse-ownership/churn-auto.jsonl
dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- \
  --stability --scenario udpChurn --burst-flows 48 --churn-waves 3 --dial-delay-ms 50 \
  --reuse always --output benchmarks/results/2026-10-05-udp-reuse-ownership/churn-always.jsonl

# burst establishment, three columns (48 burst flows, 256 background at 25 kpps, 50 ms dial delay)
dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- \
  --stability --scenario udpBurst --burst-flows 48 --dial-delay-ms 50 \
  --reuse <off|auto|always> --output benchmarks/results/2026-10-05-udp-reuse-ownership/burst-<mode>.jsonl

# session budget, three columns at the shipped default rate
dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- \
  --stability --scenario udpSessionBudget \
  --reuse <off|auto|always> --output benchmarks/results/2026-10-05-udp-reuse-ownership/session-<mode>.jsonl

# session budget, the overlapping-arrival variant (see "Rate" below)
dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- \
  --stability --scenario udpSessionBudget --rate 500 --churn-seconds 60 --drain-seconds 30 \
  --reuse <off|always> --output benchmarks/results/2026-10-05-udp-reuse-ownership/session-<mode>-rate500.jsonl
```

Every run's `metadata.options` carries `reuseMode` and every result row's `parameters` carries
`reuse`, so a column is self-describing once the file is separated from its command line. Every
command below was run once per mode (the burst and session-budget blocks abbreviate it as
`<mode>`); the files were captured to `/tmp/acc/<name>.jsonl` (the path each file's
`metadata.options.outputPath` records) and copied here, with the `--output` paths above written as
the canonical destination.

## The headline: `udp.churn` (48 flows/wave, 3 waves)

| Wave | `--reuse off` | `--reuse auto` | `--reuse always` |
|---|---|---|---|
| own / misdelivered / noResponse | 48 / 0 / 0 | 9 / 39 / 0 | 3 / 45 / 0 |
| own / misdelivered / noResponse | 48 / 0 / 0 | 5 / 43 / 0 | 5 / 43 / 0 |
| own / misdelivered / noResponse | 48 / 0 / 0 | 4 / 44 / 0 | 3 / 45 / 0 |
| `establishmentLossRate` | 0.000 | 0.812 / 0.896 / 0.917 | 0.938 / 0.896 / 0.938 |
| firstResponse p50 (ms) | 157.0 / 155.9 / 164.7 | 3.4 / 4.1 / 3.9 | 5.4 / 4.8 / 4.7 |
| bytesPerSession | 93,792 / 92,916 / 86,243 | 13,562 / 13,129 / 13,093 | 13,782 / 13,257 / 12,966 |

`own + misdelivered + noResponse == 48` in every row of every column. Under `off` the identity
reduces to `own + noResponse == flows`.

Reading the columns: the resource win is real and survives — sharing still removes ~86 % of the
per-session framework cost (93.0 kB → 13.3 kB per session). The latency win also survives, but it is
smaller and differently sourced than published: p50 158 ms → 4.1 ms is the amortized connect +
ASSOCIATE against the relay socket, and the ~158 ms `off` figure is the serialized 8-wide setup
limiter at a 50 ms dial delay (`ceil(48/8) × 50 ms × 3 ≈ 900 ms` budget), not a per-flow sharing
cost. What does **not** survive is the per-flow success claim: under sharing only 3–9 of 48 flows
in a wave receive their own reply, and 81–94 % of the wave is answered by a sibling's echo.

## `udp.burstEstablishment` (48 burst flows, 256 background flows at 25 kpps)

| Metric | `off` | `auto` | `always` |
|---|---|---|---|
| burst own / misdelivered / noResponse | 48 / 0 / 0 | 3 / 45 / 0 | 3 / 45 / 0 |
| `establishmentLossRate` | 0.000 | 0.938 | 0.938 |
| firstResponse p50 (ms) | 161.3 | 104.5 | 102.6 |
| background `misdeliveredFlows` (of 256) | 0 | 252 | 250 |
| background control `injected / sent` | 124,250 / 124,250 | 9,691 / 124,000 | 10,414 / 123,751 |
| `unattributedResponses` | 0 | 50,644 | 53,607 |

Identity holds: `3 + 45 + 0 == 48`. The burst's own p50 figure under sharing is *lower* than the
unshared one for the same reason the churn figure is: a new flow attaches to an already-established
association instead of paying the serialized dial + ASSOCIATE chain the unshared column pays. The
timestamp itself is only ever written by the flow's own echo. The two figures also cover different
populations — 3 own answers against 48, because only a flow answered by its own echo contributes a
sample — so their percentiles are not comparable. The background windows show the same effect on a
large sample: 252 of the 256 background flows had at least one reply delivered to a different flow,
and the in-flight attribution tracker (which matches `(flowId, sequence)` stamps) refuses the
replies that pass the ownership check but match no live stamp — those are counted in
`unattributedResponses` (50,644 / 53,607 under sharing, 0 under `off`).

## `udp.sessionBudget`

At the shipped default rate (20 flows/s) an arrival is answered long before the next one arrives, so
only one flow is ever in flight on an association and there is nothing for the last-sender write path
to confuse. Both shared columns are clean, and the identity is exact:

| Rate | Column | accepted | own | misdelivered | noResponse | `datagramsLost` | verdict |
|---|---|---|---|---|---|---|---|
| 20 /s | `--reuse off` | 1,800 | 1,800 | 0 | 0 | 0 | fails *pooling only* (200 associations for 200 live sessions, as `off` must) |
| 20 /s | `--reuse auto` | 1,800 | 1,800 | 0 | 0 | 0 | passes |
| 20 /s | `--reuse always` | 1,800 | 1,800 | 0 | 0 | 0 | passes |
| 500 /s | `--reuse off` | 30,000 | 30,000 | 0 | 0 | 0 | fails *pooling only* |
| 500 /s | `--reuse always` | 30,000 | 29,844 | 156 | 0 | 156 | fails loss (156) and drain retention |

The `--rate 500` variant exists because the default rate cannot exercise the defect: it is the same
soak with arrivals close enough together that two flows overlap on one association. There the
`always` column reports 156 misdelivered flows out of 30,000 and `datagramsLost` 156 — the acceptance's
own loss assertion is what surfaces the misdelivery, because a flow answered by a sibling's echo is
not answered at all. `off` is clean at the same rate.

Note the `auto` column at the default rate: its zero misdelivery is the rate effect described above,
**not** the capability detector. `--reuse always` — the same rate with detection disabled entirely —
is equally clean (`1,800 / 0 / 0`), `udp.association.fallback` is 0 in the `auto` run (the detector
never fired), and the `auto` steady state is still sharing: 13 associations over the summary's
steady-state peak of 201 live sessions (`associationsPerSession` 0.0647; 0.065 in `always`, 0.13 in
the sampled rows' 13-for-101), where `off`'s per-flow placement reads 1.0 (200 associations for 200
sessions). The shared write path therefore ran for the whole churn window and had nothing to
misdeliver at 50 ms between arrivals; `--rate 500` is what exercises the defect.

## What the correction changed, and what it did not

**Superseded** (per-flow success claims from `../2026-09-28-udp-reuse/`):

- `udp.churn` `firstResponses`, `establishmentLossRate`, and the `firstResponseMs` distribution in
  Step 1 / Step 2 (`always`) / Step 3 (`auto`). Those columns recorded 48/48, 48/48 and 48/48
  first responses with loss 0 and p50 12.5 → 3.2 ms. The 48/48 was an artifact: the burst and
  churn sinks keyed their timestamp array by the payload's flow id, and the burst background tracker
  keyed its in-flight stamps by `(flowId, sequence)`, so a reply delivered to the wrong socket still
  matched. Measured on the pre-change binary at the burst's real scale, **20,105 of 253,673 replies
  (7.9 %) arrived on the flow that asked** — while that binary reported `firstResponses 48/48`,
  `establishmentLossRate 0` and `unattributedResponses 0`.
- Consequently the *latency* comparison between the per-flow and shared columns is superseded too:
  both sides move once a reply may only be recorded on its own flow (the shared column's p50 rises
  from ~3.2 ms to ~4.1 ms; the per-flow column's p50 is a setup-limiter figure that depends on the
  dial delay and wave count). Direction and rough magnitude are unchanged; the published numbers are
  not comparable with the ones here.
- `udp.burstEstablishment` `firstResponses` and `establishmentLossRate` (the 48/48, loss-0 columns).

**Still standing** (no ownership assumption):

- Every resource column: `bytesPerSession` (churn 13,066.5 / 7,556.7 / 7,564.7 / 7,577.5 B/session),
  waves, sessions, rejected, allocation and GC counts, `timeToIssueMs`, `retireMs`, `removed`.
- `udp.lossRate` in full. Its loss rate is forward-direction — the echo destination's own count
  against the datagrams sent — and its `responsesInjected` is a window total, so misdelivered replies
  never entered either (see the note in `UdpLossScenario` and `benchmarks/README.md`).
- `udp.sessionBudget`'s descriptor, association, retention and receive-buffer columns; only the
  derived `datagramsReceived` / `datagramsLost` change meaning, and only under sharing.
- `GcSoakScenario`'s totals and `UdpSessionBenchmarks`' `ResponseCountingSink` count: totals over a
  shape that makes no per-flow claim, and the perf benchmark runs under `off`.

The `udp.association.fallback`, `udp.setup.failed` and capacity counters in the old rows are
unaffected — they are product-event counts, not reply attribution.

## Series break

`udp.churn`, `udp.burstEstablishment` and `udp.sessionBudget` gain three fields and a `reuse`
parameter; rows without them are from the pre-ownership instrument and their per-flow success
columns are not comparable with these. The JSONL schema version is unchanged (additive fields), so
old files still parse.
