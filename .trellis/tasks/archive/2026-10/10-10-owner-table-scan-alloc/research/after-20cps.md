# REL 20 cps after the fix (reusable owner-table slots)

Same host, same load as `baseline-20cps.md`: FDD build of the task branch (workstation, non-concurrent
GC, `System.GC.HeapHardLimit=128 MiB`), REL arm at 20 connections per second for 120 s (2400
connections), minimal local SOCKS5 CONNECT upstream.

## Allocation

| counter | baseline | after |
|---|---|---|
| `dotnet.gc.heap.total_allocated` mean | 27.8 MB/s | **0.546 MB/s** |
| … max | 38.1 MB/s | 1.51 MB/s |
| GC collections per run | ~556 / 180 s | 8 / 120 s (6 gen0, 2 gen1, 1 gen2) |
| heartbeat `GcAllocatedBytes` | ~3.3 GB / 120 s | **63.97 MB / 120 s** (second run 56.53 MB) |
| `dotnet.monitor.lock_contentions` mean | 7.1 /s | 5.1 /s |
| heap committed mean | 50.7 MB | 33.3 MB |
| working set mean | 101.0 MB | 79.0 MB |

Per connection: 0.55 MB/s ÷ 20 = **27 KB** (baseline 1.4 MB; the task's target was ~100 KB).

`gc-verbose` trace of the same load: sampled total **61.7 MiB** (baseline 1878.9 MiB), and the
owner-table stack is now 1.79 MiB (`TcpOwnerRow[]` growth) + 1.11 MiB (one `OwnerTableSnapshot` per
scan) ≈ 4.7 % — no `IPAddress` allocation at all. The remaining top types are the 09-18
out-of-scope BCL/per-flow set: `RetainedPacket[]` 7.32, `<>c__DisplayClass24_0` (slow-path closure)
4.37, `Byte[]` 3.66, `AwaitableSocketAsyncEventArgs` 3.26 MiB. Full dump:
`alloc-profile-after.txt`.

The heartbeat counter `attributionOwnerTableScans` reports **2401 scans** for 2400 connections: the
scan rate is exactly the new-flow rate, as the design predicts, and the scans are now free.

## Private memory shape (2 s sampling, idle 25 s → load 120 s → idle 70 s)

| phase | baseline | after (128 MiB fuse) | after (32 MiB fuse) |
|---|---|---|---|
| idle before load | 29.6–32.7 | 32.3 | 31.2–32.6 |
| +5 s into load | **65.2** | 44.1 | 43.6 |
| plateau during load | 55–73, sawtooth | 44–50, flat | 38–42, flat |
| after load (70 s idle) | 62–65 | ~50 | ~41 |

The 30 MB step and the GC sawtooth are gone; a **12 MB step remains**, and it is segment
commitment, not churn: with only 64 MB allocated in 120 s, the managed committed heap still grows
9.3 → 40 MB, of which the LOH goes 7.3 → 23.7 MB. LOH-sized allocation in the whole run totals
2.83 MiB and comes from >85 KB arrays that are not on the owner-table path anymore:

| bytes | site |
|---|---|
| 1.64 MiB | `TcpOwnerRow[]` growth inside the reusable slot (logarithmic, stops at the widest table) |
| 0.74 MiB | `TcpRedirectTombstoneTable` reverse dictionary resize |
| 0.46 MiB | `TcpRedirectTombstoneTable` forward dictionary resize |

So the residual step is the runtime committing an LOH segment (plus SOH segment slack) for a few
megabytes of arrays that the per-flow tables need; it is bounded by the widest table the process
ever sees, not by connection count. `DOTNET_GCHeapHardLimit=0x2000000` (32 MiB) shows the fuse is a
second lever: same 12 MB step, but the plateau drops by ~8 MB (38–42 vs 44–50).

## Re-measured on the committed revision

The numbers above come from the revision before the final hardening pass (the `Unavailable` constant
became non-fillable; no production path is affected). The same 20 cps REL run on the exact revision
that is committed reports: `total_allocated` mean **0.533 MB/s** (max 1.45), committed mean 26.6 MB
(max 29.5), working set mean 78.0 MB, gen2 collections ~0/s, lock contentions 6.5/s,
`attributionOwnerTableScans = 2402` for 2400 connections, and a private-memory step of
**+11.2 MiB** (30.8 idle → 42.0 under load → 40.6 after the load stopped).

## REL arm A/B (same plan and upstream, one run each)

| metric | baseline | after |
|---|---|---|
| attempts | 1746 | 1795 |
| exchanged / connectFail | 1635 / **110** | **1795 / 0** |
| connect p50 / p99 / max | 8.75 / 17.18 / 32.80 ms | 7.20 / 15.24 / 22.37 ms |

The affected arm is faster and no longer loses connections to setup failures. (The
`unexpectedEof`/`halfCloseViolation` outcomes are this environment's known close-semantics findings,
unchanged in kind by this work.)

## Evidence files

- `alloc-profile-after.txt` — allocation type/stack ranking of the post-fix trace.
- Raw counters CSV, REL jsonl and memory series are reproduced by the scripts described in
  `implement.md`; the machine-readable copies live with the run on the VM under
  `C:\wfbench\results` (`fix20.counters.csv`, `memshape-before/after`).
