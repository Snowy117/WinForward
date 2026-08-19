# Implementation Results: Portable Performance Batch

## Environment And Commands

- Date: 2026-08-17
- Runtime: .NET 10.0.9, Ubuntu 26.04 x64
- Harness: Release, dependency-free managed executable
- Full count/warmup: 200,000 / 20,000 (cardinality cases reduce operation count at 16,384 and 65,535)
- Frames: 64, 512, 1514 bytes
- Cardinalities: 0, 1,000, 16,384, 65,535
- Session counts: 1, 100, 1,000
- Baseline: `research/baseline-managed.jsonl`
- Accepted candidate: `research/candidate-managed-final.jsonl`
- Rejected relay candidate: `research/candidate-managed-v2.jsonl`

```text
dotnet run --project benchmarks/WinForward.Benchmarks/WinForward.Benchmarks.csproj \
  -c Release --no-build -- \
  --output .trellis/tasks/08-17-performance-hotspots/research/<result>.jsonl
```

These are managed-only measurements. They do not measure NDISAPI, Windows IP Helper, ETW,
packet loss, RTT/TTFB percentiles, socket/handle counts, or Native AOT heap behavior.

## Accepted Before/After Results

| Scenario | Baseline | Accepted candidate | Result |
|---|---:|---:|---:|
| UDP payload parse, 1514-byte frame | 1576.08 B/op, 290.43 ns/op | 80.08 B/op, 94.23 ns/op | 94.9% less allocation; 67.6% less elapsed |
| SOCKS5 UDP decode, 1472-byte payload | 1536.08 B/op, 134.92 ns/op | 40.08 B/op, 23.35 ns/op | 97.4% less allocation; 82.7% less elapsed |
| Missing flow, 65,535 states | 1,425,925 ns/op | 472.6 ns/op | about 3,017x faster; no cardinality-linear scan |
| Cross-adapter hit, 65,535 states | 640,900 ns/op | 693.7 ns/op | about 924x faster |
| Wildcard self-traffic miss, 65,535 entries | 140,005 ns/op | 834.7 ns/op | about 168x faster |
| Disabled-trace warm dispatcher | 928.17 B/op, 836.75 ns/op | 664.70 B/op, 1168.92 ns/op | 28.4% less allocation; 39.7% elapsed regression |
| Native buffer allocate/set/dispose, 1514 bytes | 24.16 B/op, 90.12 ns/op | unchanged primitive | pump now uses measured reuse path: 0.16 B/op, 24.11 ns/op |

The disabled-trace allocation reduction is accepted as a portable allocation result, but its
managed elapsed regression is unresolved. The logging batch requires Windows pass p99 validation
before an end-to-end latency claim. Event output, field order, threshold, and privacy tests remain
green.

## UDP Session Footprint

The accepted coordinator requests a 1,537-byte pooled receive buffer per active session (1,514-byte
frame cap + 22-byte maximum SOCKS5 UDP header + one oversize sentinel), instead of allocating a
65,535-byte managed array. The 1,000-session candidate run allocated 4,704,360 bytes total across
sessions, tasks, dictionaries, transports, and first pool rentals. The old receive arrays alone had
a source-derived 65,535,000-byte footprint for 1,000 sessions. `workingSetDeltaBytes` was only
180,224 in this process-level run and is not treated as reliable retained-heap evidence because the
shared pool and OS working-set accounting are process-global.

## Rejected Candidate

A single linked timeout CTS/timer per TCP relay direction reduced benchmark allocation by 68-81%,
but after removing redundant timer disarms it still regressed elapsed time by 3-14% across the
1-byte, 1 KiB, 8 KiB, and 64 KiB sender matrix. The product change was rolled back. The JSONL is
retained so a future Windows/socket-specific timeout design can compare against the rejected shape.

## Implemented Product Changes

- Reuse one native `NdisPacketBuffer` for each capture pump lifetime; no event/batch ABI imports.
- Return UDP parser and SOCKS5 decoder payloads as owner-bound `ReadOnlyMemory<byte>` slices.
- Copy the client MAC only while creating the first UDP session for a flow.
- Rent bounded UDP receive storage and return it in a receive-loop `finally`.
- Resolve flow transport tuples and self-traffic wildcards through O(1) indexes under existing locks.
- Skip Windows owner-table attribution when policy has no process selectors.
- Guard high-frequency trace field construction at call sites.

## Quality Review Fixes

- Reject a SOCKS5 UDP receive that fills the bounded buffer, because `ReceiveFromAsync` does not
  expose a truncation flag and the extra sentinel byte is the deterministic oversized-datagram
  boundary.
- Pass the pinned NDISAPI frame cap to both the UDP coordinator and response reinjector instead of
  relying on two currently equal defaults.
- Verify pooled receive storage is returned after an immediate receive-loop failure as well as
  coordinator shutdown.
- Mark benchmark metadata as managed-only on every operating system; running this harness on
  Windows does not turn its primitive measurements into NDISAPI, ETW, or hardware evidence.

## Deferred And Unverified

- NDISAPI packet events, batch imports/structures, bounded queue drain, and native gate changes: the
  pinned DLL/export surface and Windows hardware were unavailable. No imports were guessed.
- Windows tunnel pass RTT p50/p95/p99, loss, duplication, direction, adapter scaling, TCP/UDP proxy
  TTFB, Hyper-V forwarding, ETW allocation/CPU, socket/handle counts, and mode restoration smoke.
- Value-type parsed addresses: the remaining parser cost is about 80 B/op; the broader key/API
  redesign was deferred behind the larger payload-copy and lookup wins.
- TCP relay timeout allocation: the portable candidate failed its throughput gate and was rolled back.
- Full pass/block/TCP-proxy/UDP-proxy end-to-end benchmark coverage remains Windows-only; the portable
  harness covers their parser, lookup, dispatcher, buffer, session, codec, and relay primitives.

## Quality Gate

- `dotnet restore`: succeeded; all projects up to date.
- `dotnet build WinForward.slnx -c Release --no-restore`: succeeded, 0 warnings/errors; 3.11 s on
  the final parent-session run.
- `dotnet test -c Release --no-restore --no-build`: 294/294 passed in 605 ms test duration on the
  final parent-session run.
- `dotnet run --project benchmarks/WinForward.Benchmarks/WinForward.Benchmarks.csproj -c Release
  --no-build -- --quick`: completed the managed parser, codec, native-buffer, lookup, session,
  dispatcher, and relay smoke matrix with `managedOnly=true`.
- `git diff --check`: passed.
- `dotnet publish src/WinForward.Cli/WinForward.Cli.csproj -c Release -r win-x64 --no-restore`:
  blocked by `Cross-OS native compilation is not supported` from the .NET Native AOT compiler on
  Linux. Product project compilation before the native link step succeeded.
