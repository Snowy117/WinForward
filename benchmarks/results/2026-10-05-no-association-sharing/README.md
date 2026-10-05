# 2026-10-05 no association sharing — the surviving columns, confirmed

Task `10-05-remove-udp-association-sharing` (child of `10-05-udp-association-sharing-correctness`,
R6). This directory is the confirmation run for the two UDP flow-establishment columns that survive
the deletion: the per-flow column (`--target socks5`, each flow dialing, ASSOCIATEing and owning its
own authenticated association) and the local column (`--target local`, the same flows through the
**product** `UdpTransportFactory` composite to a loopback responder). Association sharing — the
pool, the lease, the shared control connection, and the `--reuse` placement knob — no longer exists
in the tree these runs were taken from, so there is no `off`/`auto`/`always` axis here and no
`reuse` / `reuseMode` field in any file.

The scenarios are the same shape the historical reuse series ran: `udp.churn` fires three measured
48-flow waves at a 50 ms dial delay after one unmeasured warmup wave, and `udp.burstEstablishment`
fires 48 burst flows over 256 pre-established background flows at 25 kpps with the same 50 ms dial
delay. Every run in this directory was produced from **one binary** and every command below is the
exact command that wrote its file; the only difference from
[`../2026-10-05-local-target/`](../2026-10-05-local-target/README.md)'s surviving commands is that
the `--reuse` argument is gone, so the file names drop the placement suffix.

Host: NixOS 26.11, .NET 10.0.12, X64. One run per cell (not a ≥3-run batch), taken on the same host
and in the same session as the rest of this task's gates.

## Commands

```text
# churn, the per-flow column (48 flows/wave, 3 waves, 50 ms dial delay)
dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- \
  --stability --scenario udpChurn --burst-flows 48 --churn-waves 3 --dial-delay-ms 50 \
  --target socks5 --output benchmarks/results/2026-10-05-no-association-sharing/churn-socks5.jsonl

# churn, the local column
dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- \
  --stability --scenario udpChurn --burst-flows 48 --churn-waves 3 --dial-delay-ms 50 \
  --target local --output benchmarks/results/2026-10-05-no-association-sharing/churn-local.jsonl

# burst establishment, the per-flow column (48 burst flows, 256 background at 25 kpps, 50 ms dial delay)
dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- \
  --stability --scenario udpBurst --burst-flows 48 --dial-delay-ms 50 \
  --target socks5 --output benchmarks/results/2026-10-05-no-association-sharing/burst-socks5.jsonl

# burst establishment, the local column
dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- \
  --stability --scenario udpBurst --burst-flows 48 --dial-delay-ms 50 \
  --target local --output benchmarks/results/2026-10-05-no-association-sharing/burst-local.jsonl
```

The scenario token is `udpBurst`; the row it emits names the scenario `udp.burstEstablishment`.
Each run's `metadata.options` carries `target` and each result row's `parameters` carries `target`,
so a column stays self-describing once the file is separated from its command line. A command line
that still passes the removed flags is refused at parse before any load runs — `--reuse off` and
`--require-pooling` both exit 134 with `Unknown stability argument '--reuse'` /
`'--require-pooling'`, and write no output file.

## The counters a row carries

| Field | Source | Window |
|---|---|---|
| `socks5Handshakes.controlConnections` | TCP connections the loopback SOCKS5 server accepted | cumulative from the start of the run's load (the unmeasured warmup wave included) to the instant the row was written |
| `socks5Handshakes.associateReplies` | `UDP ASSOCIATE` success replies that server wrote | same |
| `localResponder.datagramsReceived` | datagrams that arrived on the local endpoint | same |
| `localResponder.datagramsReplied` | answers the local endpoint sent back | same |

The counters are cumulative rather than windowed, so wave 1's row includes wave 0's handshakes:
read the last row of a run for its totals, or subtract two rows for one wave's own cost. Both
servers run in every column, which is what makes the local column's zeros an observation rather
than a construction: the local column writes no SOCKS5 handshake while the same process's SOCKS5
server stays up, and the relay columns leave the local responder's counters at zero while the
SOCKS5 server pays for every flow.

## `udp.churn` (48 flows/wave, 3 waves, 50 ms dial delay)

Per-flow accounting is `own / misdelivered / noResponse`, and the identity is
`own + misdelivered + noResponse == accepted == 48` in every row below.

| Wave | Column | own / misdelivered / noResponse | `accepted` / `removed` | `socks5Handshakes` | `localResponder` | firstResponse min / p50 / max (ms) | `bytesPerSession` | gen0 |
|---|---|---|---|---|---|---|---|---|
| 0 | `--target socks5` | 48 / 0 / 0 | 48 / 48 | 96 / 96 | 0 / 0 | 51.71 / 155.97 / 310.22 | 92,861 | 0 |
| 1 | `--target socks5` | 48 / 0 / 0 | 48 / 48 | 144 / 144 | 0 / 0 | 51.93 / 155.67 / 308.55 | 93,723 | 0 |
| 2 | `--target socks5` | 48 / 0 / 0 | 48 / 48 | 192 / 192 | 0 / 0 | 52.13 / 164.10 / 315.77 | 84,541 | 1 |
| 0 | `--target local` | 48 / 0 / 0 | 48 / 48 | **0 / 0** | 96 / 96 | 0.97 / 4.50 / 6.82 | 6,963 | 0 |
| 1 | `--target local` | 48 / 0 / 0 | 48 / 48 | **0 / 0** | 144 / 144 | 2.20 / 3.48 / 5.17 | 7,599 | 0 |
| 2 | `--target local` | 48 / 0 / 0 | 48 / 48 | **0 / 0** | 192 / 192 | 0.37 / 2.98 / 5.15 | 8,063 | 0 |

The per-flow column's handshake counts are the 1:1 shape read straight off the two servers'
cumulative counters: 192 control connections and 192 `UDP ASSOCIATE` replies by the third row is
48 flows × 4 waves (the unmeasured warmup wave plus the three measured ones), i.e. **one control
connection and one ASSOCIATE reply per flow** — the counter deltas between rows are exactly 48/48
per wave. The local column's zero handshakes sit beside the local responder's 96 / 144 / 192
received-and-replied datagrams, which is the same per-wave count on the other transport. No row
carries a non-zero product event (no setup failure, no capacity block, no cooldown warn), and
`bytesPerSession` is ~84.5–93.7 KB for a relayed flow against ~7.0–8.1 KB for a local one.

## `udp.burstEstablishment` (48 burst flows, 256 background flows at 25 kpps)

| Metric | `--target socks5` | `--target local` |
|---|---|---|
| burst own / misdelivered / noResponse | 48 / 0 / 0 | 48 / 0 / 0 |
| `establishmentLossRate` | 0.000 | 0.000 |
| `socks5Handshakes` | **304 / 304** | **0 / 0** |
| `localResponder` | 0 / 0 | 250,808 / 250,808 |
| firstResponse min / p50 / p95 / p99 / max (ms) | 58.44 / 160.60 / 311.34 / 311.60 / 311.60 | 0.64 / 4.05 / 8.03 / 8.87 / 8.87 |
| `timeToFirstMs` / `timeToLastMs` | 58.44 / 311.60 | 0.64 / 8.87 |
| `timeToIssueMs` (48 flows) | 4.91 | 7.53 |
| background `misdeliveredFlows` (of 256) | 0 | 0 |
| background control `injected / sent` | 124,250 / 124,250 | 124,651 / 124,651 |
| background burst `injected / sent` | 8,250 / 8,250 | 849 / 849 |
| background post `injected / sent` | 124,750 / 124,750 | 125,004 / 125,004 |
| `unattributedResponses` | 0 | 0 |

Identity: `48 + 0 + 0 == 48` in both columns. The per-flow column's 304/304 handshakes are one
control connection and one `UDP ASSOCIATE` reply for each of the run's 304 flows — 256
pre-established background flows plus the 48-flow burst — while the local column pays none and the
local responder accounts for every datagram of the run (250,808 = the 250,504 background datagrams
plus the 256 background warmup flows and the 48 burst flows). The burst window closes when the
burst's own first responses arrive, so read its `injected / sent` counts with its duration: the
per-flow column's window spanned 58.4 → 311.6 ms (the 50 ms dial delay serialized through the
8-wide setup limiter, `ceil(48 / 8) × 50 ms`), the local column's 0.64 → 8.87 ms, which is why the
two burst-window counts differ. Every window in both columns is lossless (`injected == sent`,
`lossRate == 0`, `sendP95Ms` ≈ 0.06 ms).

## What the columns show

- **The per-flow column answers every flow on its own association.** 48 of 48 per churn wave and 48
  of 48 in the burst, with `misdelivered == 0` and `unattributedResponses == 0` — the shape the
  deletion shipped, confirmed on the tree without the pool.
- **The handshake cost is one control connection and one ASSOCIATE reply per flow, observed.** The
  per-flow column's cumulative counters rise by exactly 48/48 per wave and total 304/304 in the
  burst; the local column's stay at 0/0 in both scenarios while the same process's SOCKS5 server is
  up and its own responder counters carry the traffic.
- **The local hop remains the cheapest placement.** 6,963–8,063 bytes per session against
  84,541–93,723 for a relayed flow, with p50 first-response latency of 3.0–4.5 ms against
  155.7–164.1 ms. The relay figures are the 50 ms dial delay serialized through the 8-wide setup
  limiter, not a per-flow network cost — the same reading the historical `off` column recorded.
- **Nothing on either surviving column lost, misdelivered, or rejected a flow.** No product event
  fired in any of the four runs (setup queue, capacity block, setup failure, cooldown), and the
  background populations were served losslessly in both burst columns.

## Superseded / Still standing

**Superseded:** the `--reuse off` column of
[`../2026-10-05-local-target/`](../2026-10-05-local-target/README.md) as the *current* statement of
the per-flow SOCKS5 placement — this directory measures the same placement after the pool's
deletion, without the removed flag, so this is the series to quote for the shipped shape. Its
numbers are not retracted: that directory remains the record of what the same scenario produced
with the pool still in the tree.

**Still standing:** every historical number under `results/`; none was edited or deleted by this
task, and the three directories whose columns describe association reuse
([`../2026-09-28-udp-reuse/`](../2026-09-28-udp-reuse/README.md),
[`../2026-10-05-udp-reuse-ownership/`](../2026-10-05-udp-reuse-ownership/README.md),
[`../2026-10-05-local-target/`](../2026-10-05-local-target/README.md)) now carry a "removed
feature" note saying so. The ownership-corrected baseline in
`../2026-10-05-udp-reuse-ownership/` remains the evidence that decided the removal (4–7 of 48 churn
flows per wave answered by a sibling, 3 of 48 burst flows with 251 of 256 background flows
misdelivered), and the local-target directory's zero-handshake finding stands — it is reproduced
here, not replaced.

This set is a **confirmation run, not a new comparison**: it re-measures only the columns that
survived, on the deletion tree, so the shipped shape has a current series rather than only the
historical `off` column. It does not re-open the sharing question and it does not replace the
`auto`/`always` columns' role as the record of what was removed.

## Series break

The `reuse` parameter is gone from every `udp.churn` and `udp.burstEstablishment` row and
`reuseMode` is gone from `metadata.options`; those are the only field removals in this set's
scenarios. `udp.sessionBudget` (not run here) additionally drops its pooling evidence —
`leasedFlows`, `associationsPerSession`, `steadyStatePeakAssociations`, the `pooling` block, and
`verdict.poolingCovered` / `requirePooling` — and keeps its `associations` column beside `sessions`,
which the shipped 1:1 shape makes equal. The JSONL schema version is unchanged (2), so older files
still parse: a row carrying `reuse` is from before the deletion. Fields already present keep their
meaning.
