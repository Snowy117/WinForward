# 2026-09-28 UDP association reuse — Step 1 acceptance baseline

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

