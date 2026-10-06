# 2026-10-06 UoT per-flow — the mode's harness column

Task `10-06-uot-per-flow-transport` (R8, design §9). This directory is the measurement that decides
whether the opt-in UoT v2 (UDP-over-TCP, connect mode) carriage is ever *recommended* — it does not
decide whether it ships: the mode is one `udpOverTcp` field on a SOCKS5 server entry, default off,
and the native per-flow association stays the default and the recommendation on lossy legs.

Every column here is one run on the tree that adds the mode, and the comparison in every table is
**the same scenario, the same load parameters, the same binary, one target field apart**:
`--target socks5` (one authenticated control connection + `UDP ASSOCIATE` + one relay socket per
flow) against `--target uot` (one authenticated stream connection per flow, the first datagram
pipelined into the handshake flight). The harness composes the *same*
`Socks5UdpTransportFactory` for both — UoT is a mode of the existing SOCKS5 target, not a third
kind — so the only wiring difference is the target's `udpOverTcp` field and the server it points at.

**Wire correction and re-measurement (later the same session).** All six columns below were re-run
after the UoT request header's destination type was corrected from the protocol's per-datagram values
(`0x00`/`0x01`) to the ordinary SOCKS set (`0x01`/`0x04`) that the pinned server reads. The first
pass had measured a fixture that mirrored the client's own mapping, which made it
internal-consistency evidence for a wire the real server does not parse
([`r7-server-verification.md` §2](../../../.trellis/tasks/10-06-uot-per-flow-transport/research/r7-server-verification.md)
carries the finding and its resolution). The two fixtures now decode the request destination with the
server's SOCKS address mapping and count a family that mapping does not define as a protocol
violation. Every headline figure reproduced the pre-fix pass within this host's run-to-run noise, and
the exact quantities — the counter identities (`48`, `304`, `1816`), the `protocolViolations` zeros
and the descriptor ratios (`2.00` vs `1.00`) — are unchanged; the re-run's own deltas are quoted
under each table. The recorded `.jsonl` files hold the post-fix pass measured on the final tree (the
one change after the first post-fix pass was the fixtures' domain-form rejection being restated by
name, which no IP-family flow can observe); the pre-fix figures survive only in the comparison
sentences below. The six commands were re-run verbatim, wall times 5 / 4 / 18 / 16 / 216 / 216 s
(within a second of the first pass).

**Teardown-contract re-measurement (same session, after the send-gate fix).** The six columns were
re-run once more after the send-transport teardown fix: the send gate is deliberately no longer
disposed (disposing it with waiters parked strands them, and a stranded sender holds its caller's
work lease), and a sender that waited through teardown is refused by the post-wait guard instead
([`hot-path.md`](../../../.trellis/spec/backend/hot-path.md), "The UDP transport's disposal guard is
outside the warm shape"). The fix touches only the teardown and contended-gate paths, and every
headline figure moved within this host's run-to-run noise: churn native p50 160.9 / 155.0 / 164.1 ms
against UoT 56.4 / 63.0 / 65.5 ms, burst native p50/p95 155.0 / 306.4 ms against UoT 56.0 / 56.9 ms,
descriptors per session 2.00 against 1.00, both session verdicts `passed`/`retentionBounded`, and the
counter identities (`48`, `304`, `1816`) exact again. The `.jsonl` files hold this pass; the tables
below quote the immediately preceding one, and the two agree within noise.

Host: NixOS 26.11 (Zokor), .NET 10.0.12, X64. One run per cell (not a ≥3-run batch), taken on the
same host and in the same session as the rest of this task's gates, with no other load on the box.

## Commands

```text
# churn, the native per-flow column (48 flows/wave, 3 waves, 50 ms dial delay)      [5 s]
dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- \
  --stability --scenario udpChurn --burst-flows 48 --churn-waves 3 --dial-delay-ms 50 \
  --target socks5 --output benchmarks/results/2026-10-06-uot-per-flow/churn-socks5.jsonl

# churn, the UoT column — same load parameters                                        [4 s]
dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- \
  --stability --scenario udpChurn --burst-flows 48 --churn-waves 3 --dial-delay-ms 50 \
  --target uot --output benchmarks/results/2026-10-06-uot-per-flow/churn-uot.jsonl

# burst establishment, the native per-flow column
# (48 burst flows, 256 background flows at 25 kpps, 50 ms dial delay)                [18 s]
dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- \
  --stability --scenario udpBurst --burst-flows 48 --dial-delay-ms 50 \
  --target socks5 --output benchmarks/results/2026-10-06-uot-per-flow/burst-socks5.jsonl

# burst establishment, the UoT column — same load parameters                          [16 s]
dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- \
  --stability --scenario udpBurst --burst-flows 48 --dial-delay-ms 50 \
  --target uot --output benchmarks/results/2026-10-06-uot-per-flow/burst-uot.jsonl

# session budget, the native per-flow column (shipped defaults: 20 flows/s, 90 s churn,
# 120 s drain, capacity 16384)                                                       [216 s]
dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- \
  --stability --scenario udpSessionBudget --target socks5 \
  --output benchmarks/results/2026-10-06-uot-per-flow/session-socks5.jsonl

# session budget, the UoT column — same load parameters                              [216 s]
dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- \
  --stability --scenario udpSessionBudget --target uot \
  --output benchmarks/results/2026-10-06-uot-per-flow/session-uot.jsonl
```

Every parameter is the default except the ones written above, and both columns of a pair got the
identical command line. The churn and burst pairs reuse the parameters of
[`../2026-10-05-no-association-sharing/`](../2026-10-05-no-association-sharing/README.md) verbatim
(48 flows/wave × 3 waves at a 50 ms dial delay; 48 burst flows over 256 background flows at
25 kpps), so their native columns are directly comparable with that series. The burst pair uses the
scenario's default `--flows 256 --pps 25000` rather than the Windows-safe
`--flows 16 --pps 4000` recommendation, because 25 kpps is the shape the recorded Linux series ran
and the UoT column has to answer that shape for the comparison to mean anything.

`--dial-delay-ms 50` is the one parameter whose *meaning* differs by column, and it is the point of
the measurement: on the native server it delays the `UDP ASSOCIATE` reply, which the client must read
before its first datagram can leave (a serialized round trip inside the 8-wide setup limiter); on the
UoT server it delays the **deferred CONNECT reply**, which the client never waits for — the first
frame is forwarded toward the echo destination *before* that delay, and the reply is written after
it. Same modelled remote round trip, one of them on the critical path.

Wall times are the wall-clock seconds the runner printed for the whole `dotnet run`, i.e. including
the up-to-date build check; the session-budget pair is the 90 s + 120 s load plus its warm-up and
drain.

## The counters a row carries

| Field | Source | Window |
|---|---|---|
| `socks5Handshakes.controlConnections` | TCP connections the loopback SOCKS5 server accepted | cumulative from the start of the run's load to the instant the row was written |
| `socks5Handshakes.associateReplies` | `UDP ASSOCIATE` success replies that server wrote | same |
| `uotHandshakes.connections` | TCP connections the loopback UoT fixture accepted (one per flow) | same |
| `uotHandshakes.connectReplies` | deferred CONNECT success replies it wrote (one per connection whose first frame arrived) | same |
| `uotHandshakes.framesReceived` / `framesReplied` | datagram frames read off those connections / framed echoes written back | same |
| `uotHandshakes.protocolViolations` | flights the fixture refused: not a domain-typed CONNECT to `sp.v2.udp-over-tcp.arpa`, a request destination whose address family the SOCKS mapping does not define (or that is the domain form), a request header that is not connect mode, or an unservable greeting | same |
| `localResponder.datagramsReceived` / `datagramsReplied` | datagrams that arrived on the local endpoint and the answers it sent | same |

`uotHandshakes` is the direct analogue of `socks5Handshakes` and is emitted **only** by a column
that dialled the fixture, so the `socks5` and `local` rows keep exactly the fields they had. The
fixture asserts the wire sequence it is the evidence for rather than assuming it: the CONNECT target
is compared with `UotCodec.MagicAddress`, the request header must be connect mode, and its
destination is decoded with the ordinary SOCKS address mapping (`0x01` IPv4 / `0x04` IPv6) — the
mapping the pinned server reads it with, not the client's own reader — so a column whose product
flight drifted would fail loudly (stderr) and count those connections as violations instead of
recording a plausible row for the wrong protocol. Every violation count in this directory is **0**
(the pre-fix pass had none either: it was measuring its own mirror), which is the observation behind
"the mode's wire sequence is what was measured".

The counters are cumulative rather than windowed: read the last row of a run for its totals, or
subtract two rows for one wave's own cost. The UoT column's cumulative counters start one unmeasured
wave earlier than the native columns' do — see "the uot column's second warm-up wave" below.

## `udp.churn` (48 flows/wave, 3 waves, 50 ms dial delay)

Per-flow accounting is `own / misdelivered / noResponse`, and the identity
`own + misdelivered + noResponse == accepted == 48` holds in every row of both columns.

| Wave | Column | own / misdelivered / noResponse | `accepted` / `removed` | handshake counters | firstResponse min / p50 / p95 / max (ms) | `timeToIssueMs` / `retireMs` | `bytesPerSession` |
|---|---|---|---|---|---|---|---|
| 0 | `--target socks5` | 48 / 0 / 0 | 48 / 48 | `socks5Handshakes` 96 / 96 | 51.98 / 156.42 / 308.12 / 309.34 | 4.68 / 7.02 | 93,417 |
| 1 | `--target socks5` | 48 / 0 / 0 | 48 / 48 | `socks5Handshakes` 144 / 144 | 52.19 / 157.54 / 308.51 / 308.68 | 4.32 / 6.10 | 92,850 |
| 2 | `--target socks5` | 48 / 0 / 0 | 48 / 48 | `socks5Handshakes` 192 / 192 | 52.29 / 163.04 / 298.16 / 306.59 | 13.64 / 6.42 | 87,110 |
| 0 | `--target uot` | 48 / 0 / 0 | 48 / 48 | `uotHandshakes` 144 / 144 / 144 / 144 | 53.71 / **56.78** / 61.22 / 61.33 | 4.43 / 5.27 | 160,395 |
| 1 | `--target uot` | 48 / 0 / 0 | 48 / 48 | `uotHandshakes` 192 / 192 / 192 / 192 | 52.16 / **60.83** / 65.55 / 66.74 | 4.72 / 5.09 | 155,649 |
| 2 | `--target uot` | 48 / 0 / 0 | 48 / 48 | `uotHandshakes` 240 / 240 / 240 / 240 | 51.35 / **64.17** / 68.00 / 68.33 | 4.25 / 5.91 | 144,904 |

The `uotHandshakes` cells abbreviate `connections / connectReplies / framesReceived / framesReplied`;
`protocolViolations` is 0 in every row of both UoT runs, and the `socks5Handshakes` field of the UoT
rows is `0 / 0` — the native server stayed up in that column and was never dialled, which is the
same "zeros beside non-zeros" evidence the local column carries. The UoT column's
`localResponder` is `0 / 0` too.

The per-wave deltas are the shape claim: the native column's counters rise by 48/48 per wave (one
control connection and one `UDP ASSOCIATE` reply per flow, 192/192 by the third row after its single
warm-up wave), and the UoT column's rise by 48/48/48/48 per wave (one stream connection per flow,
240 by the third row after its two warm-up waves). `framesReceived == framesReplied == connections`
in every row because each wave sends exactly one datagram per flow and every one is answered.

The native column's p50 of ~156–163 ms is the 50 ms dial delay serialized through the 8-wide setup
limiter (`ceil(48/8) × 50 ms = 300 ms` worst case, measured max 306.6–309.3 ms), which is why its
`min` is ~52 ms and its `p95` ~298.2–308.5 ms: the first eight flows pay one delay, the last eight pay six.
The UoT column answers the whole wave in **56.8–64.2 ms p50 with a 51.4–68.3 ms total spread**: the
serialization is gone, and what remains is one modelled round trip plus the harness's own
per-connection cost (see "what the harness cannot see"). The pre-fix pass read 155.5–165.2 ms p50
natively and 57.7–64.8 ms over UoT, i.e. the same two shapes with the wave-to-wave scatter this host
shows at identical parameters.

`bytesPerSession` is the one column where the UoT row reads *worse* (144.9–160.4 KB against 87.1–93.4 KB)
and the reason is harness-side, not product-side: the fixture's per-connection buffers are two 64 KiB
scratches (one for frames read off the stream, one for framed echoes written back), while the native
fixture's relay connection holds one 65,536-byte receive loop buffer. Subtracting that one extra
64 KiB, the two columns' per-session allocation is within ~1.5 KB of each other (160,395 − 65,536 =
94,859 against 93,417), i.e. the UoT transport's own managed cost per flow is not higher than the
association's — the session-budget run below is where the *descriptor* claim is priced.

## `udp.burstEstablishment` (48 burst flows, 256 background flows at 25 kpps)

| Metric | `--target socks5` | `--target uot` |
|---|---|---|
| burst own / misdelivered / noResponse | 48 / 0 / 0 | 48 / 0 / 0 |
| `establishmentLossRate` | 0.000 | 0.000 |
| firstResponse min / p50 / p95 / p99 / max (ms) | 59.71 / **161.77** / **312.90** / 313.05 / 313.05 | 52.08 / **56.86** / **57.97** / 58.46 / 58.46 |
| mean first response (ms) | 186.36 | 56.64 |
| `timeToFirstMs` / `timeToLastMs` | 59.71 / 313.05 | 52.08 / 58.46 |
| `timeToIssueMs` (48 flows) | 4.66 | 5.29 |
| handshake counters | `socks5Handshakes` 304 / 304 | `uotHandshakes` 304 / 304 / 250,059 / 250,059 |
| background control `injected / sent` | 124,250 / 124,250 @ 24,845 pps | 123,384 / 123,384 @ 24,672 pps |
| background burst `injected / sent` | 8,307 / 8,307 @ 24,806 pps | 1,866 / 1,866 @ 24,858 pps |
| background post `injected / sent` | 124,690 / 124,690 @ 24,936 pps | 124,505 / 124,505 @ 24,898 pps |
| background `misdeliveredFlows` (of 256) | 0 | 0 |
| `unattributedResponses` | 0 | 0 |
| product events | none | none |

Identity: `48 + 0 + 0 == 48` in both columns, and both columns' counters are **304 = 256
background + 48 burst**, i.e. exactly one connection per flow on either transport. The UoT column's
`framesReceived`/`framesReplied` of 250,059 is the whole run's datagram traffic carried as stream
frames: the same background population the native column pushed through per-flow UDP relay sockets,
sustained losslessly at 24.7–24.9 kpps in every window (`sendP95Ms` 0.065–0.072 ms in both columns).

The burst wave shape is the R8 claim and it is visible in the distribution, not just the median:
the native column's last flow answers at 313.05 ms — `ceil(48/8) × 50 ms` of serialized `UDP
ASSOCIATE` plus scheduling — while the UoT column's last flow answers at **58.46 ms**, one modelled
round trip plus the harness's per-connection cost. **p50 improves 161.77 → 56.86 ms (2.85×) and p95
improves 312.90 → 57.97 ms (5.40×)**, with 48/48 own responses, zero misdelivery, and zero loss in
both columns. The pre-fix pass read 156.87 → 57.12 ms p50 and 307.89 → 58.11 ms p95, i.e. the same
two shapes. The background windows answer a different number of datagrams in the two columns
because each window closes when the burst population has answered (the native column's window spans
the 300 ms serialization, the UoT column's ~60 ms), which is why its `injected` counts are larger —
read the achieved pps beside them, not across columns.

## `udp.sessionBudget` (shipped defaults: 20 flows/s, 90 s churn, 120 s drain)

Both columns ran the identical command line and both verdicts **passed**
(`verdict.retentionBounded: true`, `failures: []`).

| Metric | `--target socks5` | `--target uot` |
|---|---|---|
| `accepted` / `rejected` / `expired` | 1800 / 0 / 1816 | 1800 / 0 / 1816 |
| `datagramsReceived` / `datagramsLost` / `misdelivered` / `noResponse` | 1800 / 0 / 0 / 0 | 1800 / 0 / 0 / 0 |
| `firstResponseMs` min / p50 / p95 / p99 / max | 1.16 / 1.44 / 1.83 / 2.44 / 25.06 | 0.82 / **1.09** / 1.46 / 3.26 / 21.96 |
| steady-state sampled sessions / arrival-granular peak | 112 / 207 | 110 / 205 |
| retention ceiling `rate × (idle + 2 × sweep) + margin` | 840 | 840 |
| `associations` (harness server's live connections) at t = 10…90 s | 107 = `sessions` | 105 = `sessions` |
| raw process descriptors, steady state | 518 | 406 |
| `fileDescriptors` (harness's 2 sockets per live connection subtracted) | 304 | 196 |
| **`fileDescriptorsPerSession`** | **2.00** | **1.00** |
| descriptor budget `ceil(sessions × 1.25) + associations` + 32 margin, worst headroom | 58 | **162** |
| `managedBytesPerSession`, `relayReceiveBufferBytesPerSession` | 361 KB, 65,536 | 349 KB, 65,536 |
| drain: `sessionsZeroAtSeconds` | 100.31 | 100.30 |
| drain end: `sessions` / `associations` / descriptor delta | 0 / 0 / +2 | 0 / 0 / +2 |
| `uotHandshakes` at drain end | — | 1816 / 1816 / 1816 / 1816 |

Read the descriptor rows as the mode's second claim, and read them against the budget the scenario
charges rather than against each other alone. The native column's steady state is **two descriptors
per live session** — one control connection and one relay socket — which is exactly the shipped
shape's budget (`ceil(sessions × 1.25)` slack plus one control connection per association); the UoT
column's is **one descriptor per live session** (the single stream connection), with the harness's
own two sockets per connection (the accepted stream socket and the per-connection upstream UDP
socket) subtracted from the raw count before the ratio is taken. The worst-case budget headroom
therefore grows from 58 to 162 descriptors at essentially the same population (107 vs 105 live
sessions): the UoT column fits the same budget with room to spare, and a session that leaked a
second descriptor would not fit it.

`associations` means the same observation in both columns and counts different objects: **the number
of connections the harness server is serving at that instant** — accepted minus closed, read on the
peer side of the dial, not copied from the coordinator. For `--target socks5` that is one control
connection per live flow (107 = `sessions` = 107 at t = 10…90 s, and 112 = 112 at the churn-end
sample); for `--target uot` it is **one stream connection per live flow** (105 = `sessions` = 105,
110 = 110 at churn end), because a UoT flow's connection *is* its relay.
The two columns' equality with their own `sessions` column is the one-connection-per-flow shape seen
from the server, and the drain's association term (0 at drain end in both) is what makes a connection
that outlived its session visible.

The `uotHandshakes` block is the same claim from the fixture's side: 1816 connections, 1816 CONNECT
replies and 1816 frames for 1816 flows (16 warm-up + 1800 churn), zero protocol violations, and the
counters stop rising at the point the sessions do — a flow's connection lives exactly as long as its
flow. The UoT column's first-response p50 is also **1.09 ms against the native column's 1.44 ms** at
one datagram per flow: the establishment round trip the native path pays inside setup is overlapped
by the UoT flight here too, though at this rate the difference is small in absolute terms (one flow
in flight at a time at 20 flows/s).

The steady-state population is set by how the churn's one-shot flows retire against the idle window,
which varies with arrival order between runs: this pass sampled 107 live sessions natively and 105
over UoT, the pre-fix pass 105 / 104, and an earlier post-fix pass 100 / 100 — hence the different
absolute descriptor counts between passes. The per-session ratio is exact in every pass — **2.00 vs
1.00** — and the retention verdict, the descriptor delta, and the drain behaviour are unchanged.

## What the harness can see, and what it cannot

- **Loopback makes the TCP-carriage cost invisible.** Every column is one host with sub-millisecond
  RTTs and zero loss, so nothing here measures per-flow head-of-line blocking, congestion-control
  interaction (QUIC-over-TCP stacking), or the cost of one retransmission on a stream that carries
  one flow. The mode's documented caveat — native stays the recommendation on lossy legs — rests on
  the protocol, not on these numbers. What the harness *can* see is what the change claims: the
  setup serialization (burst and churn latency distributions) and the per-flow descriptor count
  (session budget).
- **The UoT latency figures are upper bounds, not lower ones.** The fixture is a second in-process
  server: one accept loop, one UDP socket bind and two 64 KiB buffers per connection. That cost is
  on the critical path in the UoT column (its burst spread is 52.1 → 58.5 ms, its churn p50 sits
  ~5 ms above its min) and it is *not* the product's. The native column's equivalent harness cost is
  hidden behind the 50 ms delay it serializes. A real remote server would answer with its own cost
  in the same place, so the honest reading is "one modelled round trip plus the server's own work",
  and the mode's win is the serialization that is gone, not the last five milliseconds.
- **`bytesPerSession` is not comparable across these two columns** for the reason given under
  `udp.churn`: the UoT fixture holds one 64 KiB buffer per connection more than the native fixture,
  so the comparison there is harness-shaped. The descriptor and latency columns are not affected.
- **The uot column's second warm-up wave.** The churn scenario fires one unmeasured warm-up wave per
  process, and a `uot` column fires two: the fixture is new code in the same process (a connection
  task and a reply loop per flow) and with a single warm-up wave its first-call cost landed inside
  the first *measured* row (measured before the change: p50 8.7 ms in wave 0, 1.8–2.0 ms in the later
  waves; the same decay is reproducible in an isolated probe against a coordinator and disappears
  once the path is warm). The consequence is stated in every UoT churn row's counters: its
  cumulative `uotHandshakes` totals start 48 connections (one wave) higher than the
  native columns' do. The `socks5` and `local` columns keep the documented single wave, so their
  series stay comparable with `../2026-10-05-no-association-sharing/` row for row.
- **One run per cell.** No column here is a ≥3-run batch; the deltas quoted are 2.85×–5.40× on the
  burst distribution and exactly 2.0 → 1.0 descriptors per session, which are far outside this
  host's measured run-to-run noise (the native column's own p50 varies by ~10 ms across its three
  churn waves at identical parameters, and the pre-fix pass over the same six commands agreed with
  every figure quoted here within a few percent — the recorded files are the post-fix pass), but a
  single-run artifact would not be visible in the medians alone. What is not a median — the 304 = 256 + 48 counter identity, the 0 violations, the
  descriptor ratio, and the retention verdicts — is exact.

## Accounting identity

- `udp.churn`: `own + misdelivered + noResponse == accepted == 48` in every row of both columns;
  `removed == 48` per wave; the UoT column's per-wave counter delta is
  `connections = connectReplies = framesReceived = framesReplied = 48`.
- `udp.burstEstablishment`: `48 + 0 + 0 == 48` in both columns; `connections = connectReplies = 304
  = 256 background + 48 burst` in both.
- `udp.sessionBudget`: `own(1800) + misdelivered(0) + noResponse(0) == accepted(1800)`,
  `expired(1816) == 16 warm-up + 1800 churn`, and the UoT fixture's
  `connections(1816) == connectReplies(1816) == framesReceived(1816) == framesReplied(1816)`.
- Every counter block in every row of this directory reports `protocolViolations: 0`.

Every identity above held in the pre-fix pass and holds in the re-measured pass recorded here: the
wire correction moved no counter.

## Does this support recommending the mode?

**Yes, for the shape it targets, with the caveat the loopback harness cannot test.** On the flows
this task is about — short non-DNS request/response flows through a remote SOCKS5 proxy — the mode
removes the setup serialization that dominates them (burst p50 161.8 → 56.9 ms, p95 312.9 → 58.0 ms;
churn p50 156.4–163.0 → 56.8–64.2 ms) and halves the per-flow descriptor cost while holding the same
retention verdict, the same zero misdelivery, and the same zero loss, with the counter identities
showing exactly one stream connection per flow (304/304, 1816/1816, 48 per churn wave). At one
modelled round trip of 50 ms the mode's first response is one round trip; the native path's is
between one and six of them, decided by arrival order.

The caveats are equally part of the answer: this harness cannot price TCP carriage on a lossy leg,
where the native path stays the recommendation; the mode's latency numbers here are upper bounds
because the in-process fixture's own accept path is on the critical path; and the mode remains
opt-in per server entry — nothing in these numbers argues for changing a default.

## Superseded / Still standing

**Superseded:** this directory's own pre-fix pass, overwritten in place by the re-measurement above.
Those files measured a loopback fixture that mirrored the client's request-header mapping
(`r7-server-verification.md` §2), so they were internal-consistency evidence for a wire format the
pinned server does not parse; the `.jsonl` files now hold the post-fix runs, and the pre-fix figures
survive only in the comparison sentences of this README. The `socks5` column was re-measured with
them, for the same-session pairing, and its figures moved only within noise.

**Also superseded:** the first post-fix pass, overwritten in place by the teardown-contract
re-measurement (the send gate is no longer disposed). Its figures and the files now on disk agree
within noise; the tables above quote that pass, and the bullets under them record the deltas.

**Still standing:** every historical number under `results/`, unedited. In particular
[`../2026-10-05-no-association-sharing/`](../2026-10-05-no-association-sharing/README.md) remains
the series for the shipped per-flow SOCKS5 placement and
[`../2026-10-05-local-target/`](../2026-10-05-local-target/README.md) for the local placement: this
directory's `--target socks5` column reproduces that series' shape (same 48-flow/3-wave churn and
48-over-256 burst parameters, same one-connection-and-one-ASSOCIATE-per-flow accounting) and its
numbers land where that series' did (churn p50 155.7–164.1 ms there and 156.4–163.0 ms here; burst p50
160.60 ms there and 161.77 ms here), so the native column is a re-measurement on the mode tree
rather than a new baseline — with the difference that both columns here were taken on the same
binary in the same session as the UoT column, which is what makes the pair comparable.

## Series break

None for the `socks5` and `local` columns: no field was removed, renamed or re-based, and their rows
carry the same fields with the same meanings and the same single warm-up wave as
`../2026-10-05-no-association-sharing/`. The additive field is `uotHandshakes`, present only in
`uot` rows (the writer omits a field nobody observed rather than reporting zeros), and the
`udp.sessionBudget` rows of a `uot` column carry it too. The JSONL schema version is unchanged (2),
so every older file still parses. The wire correction changed no field: the fixture's decoder and the
client's ATYP byte are the only differences between the two passes, and the `uot` series in this
directory starts with the post-fix pass.
