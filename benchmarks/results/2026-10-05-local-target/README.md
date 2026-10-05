# 2026-10-05 local targets — the local-hop column beside the reuse columns

> **Removed feature, 2026-10-05 (task `10-05-remove-udp-association-sharing`, R6).** This directory's
> `--reuse auto` column (`churn-auto.jsonl`, `burst-auto.jsonl`) measures UDP association sharing,
> which no longer exists: the three configuration keys were removed, the pool / lease /
> control-association / capability machinery was deleted, and a proxy-decided flow now owns its own
> authenticated association. Its `--reuse off` column measured the placement that **did** ship (one
> association per flow), but it was recorded through the same knob, so both reuse columns' commands
> are **historical**: `--reuse` is gone from the harness, and a command line that still passes it
> fails at parse with the harness's unknown-argument error. The `--target local` column never
> consulted the pool; its rows and metadata nonetheless carry the then-default `reuseMode: auto` /
> `reuse: auto` fields, which now name a removed knob. Confirmation series for the surviving columns:
> [`../2026-10-05-no-association-sharing/`](../2026-10-05-no-association-sharing/README.md).
> **No number in this directory was edited or deleted** — this note is the only change.

Task `10-05-local-dns-transport` (child of `10-05-udp-association-sharing-correctness`, R7). This
directory is the measured column for the local transport: the same flows, pacing, sinks, and wave
shape as the reuse columns, sent through the **product** `UdpTransportFactory` composite to a
loopback endpoint that answers each datagram to its own sender, with the loopback SOCKS5 server
still listening in the same process so its counters can be read beside the local hop's.

That is what makes the zero-handshake claim an observation rather than a construction. Both servers
are hosted in every column: the SOCKS5 columns record the local responder receiving nothing, and the
local column records the SOCKS5 server accepting no control connection and writing no `UDP
ASSOCIATE` reply for the whole run — while serving exactly the flows the relayed columns serve: the
same 48 per churn wave, and in the burst the same 256 background flows plus 48 burst flows.

Host: NixOS 26.11, .NET 10.0.12, X64. One run per cell (not a ≥3-run batch). All six columns were
produced from **one binary**; the placement is the `--target` argument (`socks5` is the default), and
the reuse columns additionally pass `--reuse`.

## Commands

```text
# churn, three columns (48 flows/wave, 3 waves, 50 ms dial delay)
dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- \
  --stability --scenario udpChurn --burst-flows 48 --churn-waves 3 --dial-delay-ms 50 \
  --target socks5 --reuse off --output benchmarks/results/2026-10-05-local-target/churn-off.jsonl
dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- \
  --stability --scenario udpChurn --burst-flows 48 --churn-waves 3 --dial-delay-ms 50 \
  --target socks5 --reuse auto --output benchmarks/results/2026-10-05-local-target/churn-auto.jsonl
dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- \
  --stability --scenario udpChurn --burst-flows 48 --churn-waves 3 --dial-delay-ms 50 \
  --target local --output benchmarks/results/2026-10-05-local-target/churn-local.jsonl

# burst establishment, three columns (48 burst flows, 256 background at 25 kpps, 50 ms dial delay)
dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- \
  --stability --scenario udpBurst --burst-flows 48 --dial-delay-ms 50 \
  --target socks5 --reuse off --output benchmarks/results/2026-10-05-local-target/burst-off.jsonl
dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- \
  --stability --scenario udpBurst --burst-flows 48 --dial-delay-ms 50 \
  --target socks5 --reuse auto --output benchmarks/results/2026-10-05-local-target/burst-auto.jsonl
dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- \
  --stability --scenario udpBurst --burst-flows 48 --dial-delay-ms 50 \
  --target local --output benchmarks/results/2026-10-05-local-target/burst-local.jsonl
```

Every run's `metadata.options` carries `target` (and `reuseMode`), and every result row's
`parameters` carries `target` (and `reuse`), so a column stays self-describing once the file is
separated from its command line. Each command wrote its file directly to the path above — that path
is what the file's own `metadata.options.outputPath` records — and each row also carries the two
counter groups described below. The local columns' metadata records the default `auto` placement;
the local transport never rents a lease and never consults the pool, so no `--reuse` mode is
meaningful for them (the composite dispatches on the target kind before any factory runs).

## The counters a row carries

| Field | Source | Window |
|---|---|---|
| `socks5Handshakes.controlConnections` | TCP connections the loopback SOCKS5 server accepted | cumulative from the start of the run's load (the unmeasured warmup wave included) to the instant the row was written |
| `socks5Handshakes.associateReplies` | `UDP ASSOCIATE` success replies that server wrote | same |
| `localResponder.datagramsReceived` | datagrams that arrived on the local endpoint | same |
| `localResponder.datagramsReplied` | answers the local endpoint sent back | same |

The counters are cumulative rather than windowed because a shared association is established once and
then serves later waves: under `--reuse auto` the churn wave deltas are `0/0` in every wave, which is
the *pool working*, not the relay being unused. The cumulative difference says the honest thing —
`off` re-establishes per wave (96 → 144 → 192 control connections over three measured waves plus the
warmup wave, i.e. 48 per wave), `auto` establishes 3 for the whole run, `local` establishes none. The
same rule applies to the local responder's counters, which is why the local churn rows read 96 / 144 /
192 datagrams: 48 per wave including the warmup wave.

## `udp.churn` (48 flows/wave, 3 waves, 50 ms dial delay)

Per-flow accounting is `own / misdelivered / noResponse`; the local column's expected shape is
`own == accepted`, `misdelivered == 0`, `noResponse == flows − accepted`.

| Wave | Column | own / misdelivered / noResponse | `accepted` | `socks5Handshakes` | `localResponder` | firstResponse p50 (ms) | `bytesPerSession` |
|---|---|---|---|---|---|---|---|
| 0 | `--reuse off` | 48 / 0 / 0 | 48 | 96 / 96 | 0 / 0 | 158.2 | 92,750 |
| 1 | `--reuse off` | 48 / 0 / 0 | 48 | 144 / 144 | 0 / 0 | 156.0 | 93,134 |
| 2 | `--reuse off` | 48 / 0 / 0 | 48 | 192 / 192 | 0 / 0 | 165.3 | 87,243 |
| 0 | `--reuse auto` | 5 / 43 / 0 | 48 | 3 / 3 | 0 / 0 | 5.1 | 14,098 |
| 1 | `--reuse auto` | 4 / 44 / 0 | 48 | 3 / 3 | 0 / 0 | 5.0 | 13,004 |
| 2 | `--reuse auto` | 7 / 41 / 0 | 48 | 3 / 3 | 0 / 0 | 4.2 | 13,022 |
| 0 | `--target local` | **48 / 0 / 0** | 48 | **0 / 0** | 96 / 96 | 4.1 | 7,574 |
| 1 | `--target local` | **48 / 0 / 0** | 48 | **0 / 0** | 144 / 144 | 3.6 | 7,321 |
| 2 | `--target local` | **48 / 0 / 0** | 48 | **0 / 0** | 192 / 192 | 3.1 | 7,477 |

`own + misdelivered + noResponse == 48` in every row of every column, and `accepted == 48`
(every wave's 48 first datagrams were admitted; `removed == 48` retired each wave). No row carries a
non-zero product event (no setup failure, no capacity block, no association fallback), and only
`off`'s third wave collected a gen0 collection.

## `udp.burstEstablishment` (48 burst flows, 256 background flows at 25 kpps)

| Metric | `off` | `auto` | `local` |
|---|---|---|---|
| burst own / misdelivered / noResponse | 48 / 0 / 0 | 3 / 45 / 0 | **48 / 0 / 0** |
| `establishmentLossRate` | 0.000 | 0.938 | 0.000 |
| `socks5Handshakes` | 304 / 304 | 19 / 19 | **0 / 0** |
| `localResponder` | 0 / 0 | 0 / 0 | 250,436 / 250,436 |
| firstResponse p50 (ms) | 158.7 | 101.5 | 8.2 |
| firstResponse min / max (ms) | 57.5 / 310.1 | 53.3 / 152.9 | 1.0 / 10.3 |
| `timeToIssueMs` (48 flows) | 4.58 | 5.13 | 4.83 |
| background `misdeliveredFlows` (of 256) | 0 | 251 | 0 |
| background control `injected / sent` | 124,224 / 124,224 | 9,425 / 124,250 | 124,500 / 124,500 |
| background burst `injected / sent` | 8,338 / 8,338 | 15,247 / 748,326 | 710 / 710 |
| background post `injected / sent` | 124,692 / 124,692 | 0 / 124,924 | 124,922 / 124,922 |
| `unattributedResponses` | 0 | 50,192 | **0** |

Identity: `3 + 45 + 0 == 48` and `48 + 0 + 0 == 48`. The counter figures here are whole-run totals
(the 256 background flows are established inside the same run, so `off` pays 256 + 48 = 304
connections and 304 `UDP ASSOCIATE` replies, `auto` shares them into 19, `local` pays none).

Two window shapes differ between columns and are worth reading explicitly, because both are the
existing behavior of the burst instrument rather than anything the local hop changed:

- The **burst window's duration** is the wait for the burst's own first responses (bounded by the
  adaptive timeout). `off` closes it after 0.34 s, `local` after 0.03 s (48 answers inside ~10 ms,
  the 20 ms poll sets the floor), and `auto` runs it to the 30 s floor because only 3 of 48 flows
  ever saw their own reply — so `auto`'s burst-window background counts cover 30 s of pacing while
  the other two cover 0.34 s and 0.03 s. The per-window `injected / sent` figures are only
  comparable within a row.
- The **shared column's control-window injection** (9,425 of 124,250) is the response-ownership
  defect: a reply delivered to a sibling's socket is not credited, which is the same finding the
  ownership-corrected baseline records.

## What the column shows

- **The flows are answered locally, by the flow that asked.** 48 of 48 per wave and 48 of 48 in the
  burst, with `misdelivered == 0` and `unattributedResponses == 0` — against the same harness shape
  that reports 41–45 of 48 misdelivered when the shared association's last-sender write path carries
  them. The local endpoint is one socket serving every flow of the run (the analogue of the shared
  association's single relay socket), so a last-sender write path there would show up in exactly the
  same fields; the responder has no such field, and the rows are the empirical half of that argument.
- **Zero handshakes, observed.** `socks5Handshakes` is `0/0` in every local row while the same
  process's SOCKS5 server is accepting 96–192 connections (churn) and 304 (burst) for the other
  columns, and the local responder's counters show the traffic that replaced them.
- **The local hop is the cheapest placement measured.** 7.3–7.6 KB per session against 13.0–14.1 KB
  for the shared association and 87–93 KB for per-flow (`off`): ~44 % below the shared column and
  ~92 % below `off`, because there is neither a dial, nor an `UDP ASSOCIATE`, nor a pooled lease to
  account for.
- **Latency sits in the shared column's band, without the sharing.** firstResponse p50 3.1–4.1 ms
  (churn) and 8.2 ms under the burst's 25 kpps background, against 4.2–5.1 ms and 101.5 ms for the
  shared column and 158.2–165.3 ms and 158.7 ms for `off`. The `off` figures are the serialized
  8-wide setup limiter at a 50 ms dial delay (`ceil(48/8) × 50 ms`), not a per-flow network cost.
- **The whole column is lossless, background population included.** In the local run the 256
  background flows are served by the local hop as well, and all three windows carry every datagram:
  124,500 / 710 / 124,922 injected of sent, zero loss, `sendP95Ms` ≈ 0.06 ms. The background
  response ownership is exact at that scale — `misdeliveredFlows` 0 of 256 and
  `unattributedResponses` 0 across the run's 250,436 local datagrams — where the `auto` column
  reports 251 of 256 background flows with a reply delivered to a sibling.

## Superseded / Still standing

**Superseded:** nothing. The `--reuse off` and `--reuse auto` columns here are new runs of the same
commands as `../2026-10-05-udp-reuse-ownership/`, so their per-flow figures differ from that
directory's by run-to-run noise (e.g. `auto` own/misdelivered 5/43, 4/44, 7/41 here against
9/39, 5/43, 4/44 there; p50 4.2–5.1 ms against 3.4–4.1 ms). Neither set replaces the other: this
directory's columns exist to carry the local column's control, and the ownership-corrected baseline's
numbers remain the recorded measurement of that run.

**Still standing:** every number in `../2026-10-05-udp-reuse-ownership/`. Its per-flow accounting
and resource columns were recorded with the same scenario shape — the same flows, pacing, sinks and
wave counts — and the only shape difference introduced here is that the idle local responder socket
is hosted in the SOCKS5 columns too, where its own counters read zero datagrams for the whole run.
No historical file was edited or replaced; this directory is additive.

## Series break

None in comparability. `udp.churn` and `udp.burstEstablishment` rows gain three additive fields —
the `target` parameter, `socks5Handshakes`, and `localResponder` — and `metadata.options` gains
`target`. The JSONL schema version is unchanged (2), so older files still parse: a row without
`target` is from before the knob, and a row without the counter groups was recorded before the two
servers were counted. Fields already present keep their meaning.

One instrument note for the next reader: a wave row's `socks5Handshakes`/`localResponder` counts
cover the run up to that row, so wave 1's row includes wave 0's handshakes. Read the last row of a
run for its totals, or subtract two rows for one wave's own cost.
