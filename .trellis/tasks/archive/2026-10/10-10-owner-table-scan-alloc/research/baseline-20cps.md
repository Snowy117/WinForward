# REL 20 cps baseline (pre-fix, FDD build of `dc5ede8`)

Host: WinLTSC VM, 16 logical processors, product = single-file FDD (embedded `System.GC.HeapHardLimit=128 MiB`).
Load: `WinForward.E2E client` REL arm, `connectionsPerSecond=20`, 120 s, mode mix
`clean=25,resetAfterN=25,partialFin=25,halfClose=25`; TCP target on the Linux host; a minimal local
SOCKS5 CONNECT upstream on `127.0.0.1:1080` standing in for sing-box.

## `dotnet-counters` (`System.Runtime`, 1 s refresh, 123 samples over the 120 s load)

| counter | mean | max |
|---|---|---|
| `dotnet.gc.heap.total_allocated` | **27.8 MB/s** | 38.1 MB/s |
| `dotnet.gc.last_collection.memory.committed_size` | 50.7 MB | 60.7 MB |
| `dotnet.process.memory.working_set` | 101.0 MB | 114.5 MB |
| `dotnet.gc.collections` gen2 | 0.8 /s | 2 /s |
| `dotnet.monitor.lock_contentions` | 7.1 /s | 24 /s |

Private memory: 30.8 MiB before the load → 63.2 MiB after (+32.4 MiB), no return after the load
stopped. 2400 connections ⇒ **~1.4 MB per connection**; the same arm at 1 cps costs 42 KB per
connection and never steps.

## `dotnet-trace --profile gc-verbose` (11,939 allocation ticks, 1878.9 MiB sampled)

Type attribution:

| MiB | ticks | type |
|---|---|---|
| 1066.9 | 3959 | `WinForward.Windows.TcpOwnerRow[]` |
| 738.8 | 7267 | `System.Net.IPAddress` |
| 14.2 | 140 | `WinForward.Windows.OwnerTable` |
| 9.6 | 94 | `WinForward.Core.PacketLease` |
| 7.4 | 73 | `WinForward.Runtime.RetainedPacket[]` |

Stack attribution: 1802.4 of 1878.9 MiB (**96 %**) under
`IPHelperOwnerTableReader.ReadTcp4()` ← `ProcessOwnerTableCache.Lookup` ←
`WindowsProcessAttributor.FindOwnerSafely` ← `FlowDispatcher.AttributeProcessAsync` ←
`FlowAttributionPipeline.RunAttributionAsync` ← `SetupExecutor.Execute` ← `SetupExecutor.WorkerLoop`.

Full dump: `alloc-profile-20cps.txt` (same directory).

## Why the scan rate tracks the new-flow rate

A snapshot can only contain sockets that existed when it was taken, so a new flow's lookup can never
be answered by an existing snapshot: it misses, and the cache scans. `FindAsync` retries once 2 ms
later, which for a negative answer is a second scan (the retry's request instant precedes the first
scan's publish, so it joins that scan — a retry costs a scan only when the first answer was negative
*and* another request re-armed the slot). Measured scans therefore track new flows, not packets, and
each scan materialized the whole system TCP table.
