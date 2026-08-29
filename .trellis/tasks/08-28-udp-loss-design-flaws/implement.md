# Implement — UDP loss design flaws fix (R1–R6)

Validation baseline before any edit:
`dotnet build -c Release` (expect the current tree to build clean; the editor-reported
"duplicate IUdpResponseSink / TcpRedirectSession ambiguity" diagnostics are stale LSP
workspace views — verify with a real build once at the start and file an issue if real).

## Ordered checklist

### Step 0 — Baseline
- [ ] `dotnet build -c Release && dotnet test -c Release` green on master.

### Step 1 — R5 transport send serialization (smallest, isolated)
Files: `src/WinForward.Runtime/Socks5UdpTransport.cs`
- [ ] Add `SemaphoreSlim(1,1)` guarding encode+`SendToAsync` in `SendAsync`.
- [ ] Dispose the semaphore in `DisposeAsync`.
Tests: concurrent `SendAsync` from two tasks with a fake socket factory → encoded
datagrams never interleave (extend existing transport tests).

### Step 2 — R2 receive-loop robustness
Files: `src/WinForward.Runtime/Socks5UdpTransport.cs`, `src/WinForward.Runtime/UdpProxySession.cs`,
`src/WinForward.Runtime/UdpProxyCoordinator.cs` (only if seam signature moves)
- [ ] Change `IUdpProxyTransport.ReceiveAsync` to a discriminated result
  (valid datagram vs `UnexpectedSource`/`Oversized`/`Malformed` skip) — no throw for
  per-datagram anomalies.
- [ ] `UdpProxySession.ReceiveLoopAsync`: skip+continue on skip results with a
  per-session rate-limited summary log; fatal only for socket-level exceptions.
- [ ] Wrap `_sink.InjectAsync` per response: non-cancellation exceptions → rate-limited
  log + continue.
- [ ] Transport fakes in tests updated to the new seam.
Tests: AC2 — malformed / unexpected-source / oversized datagrams interleaved with valid
ones; all valid datagrams around each bad one delivered; session survives.

### Step 3 — R1 non-blocking session setup
Files: `src/WinForward.Runtime/UdpProxyCoordinator.cs`, `src/WinForward.Runtime/UdpProxySession.cs`,
`src/WinForward.Core/PacketRuntime.cs` (no code change expected — `BoundedSetupQueue` reused as-is)
- [ ] Session state machine `SettingUp → Flushing → Ready` in the coordinator (marker
  entries in `_sessions` so the dict remains the single ownership point).
- [ ] `TrySendAsync` fast path: enqueue (copy via `BoundedSetupQueue`, 32 pkts / 32KB,
  drop-oldest) during SettingUp/Flushing; inline send only when Ready.
- [ ] Background `CreateSessionAsync` (existing task pattern + `TrackFailedSetup`),
  guarded by `SemaphoreSlim(8)` global setup cap.
- [ ] Tombstone cooldown (1s) for failed setups; dropped datagrams traced
  (`udp.setupqueue.dropped`, `udp.setup.cooldown`).
- [ ] Flush drains FIFO under the session gate before switching to Ready.
- [ ] Dispose drops queued datagrams; idle sweep also cleans tombstones.
Tests: AC1 with a fake factory that delays `CreateAsync` ≥5s — pump-side dispatcher call
completes without awaiting setup; buffered datagrams are relayed in order after setup;
overflow drops oldest; failure cooldown rejects + retries after 1s (fake TimeProvider).

### Step 4 — R3 per-adapter native gates
Files: `src/WinForward.NdisApi/NdisApiDriver.cs`
- [ ] Control gate for open/close/enum/mode ops; per-adapter-handle
  `Dictionary<nint, NdisNativeCallGate>` (creation lock) for read + send paths.
- [ ] Keep queue-query+batch-read under one lease of the adapter's gate.
- [ ] Preserve/extend gate contention telemetry per gate.
Tests: existing `NdisApiDriver` tests stay green (they exercise single-adapter paths);
add a multi-adapter concurrency test with a fake native seam if the existing harness
allows (blocking call on adapter A must not delay adapter B read) — else verify by
design review note in the task.
Rollback point: fallback = single send-gate + per-adapter read-gates (see design D3).

### Step 5 — R4 response-path buffers/allocation
Files: `src/WinForward.Runtime/UdpResponseReinjector.cs`, `src/WinForward.Protocols/UdpFrameBuilder.cs`,
`src/WinForward.NdisApi/NdisPacketBuffer.cs`, `src/WinForward.Runtime/Socks5UdpTransport.cs`
- [ ] `Socks5UdpTransport.CreateAsync`: explicit `ReceiveBufferSize` (512KB const).
- [ ] `UdpFrameBuilder.TryBuildInto(Span<byte>, ..., out int length)`; allocating
  `TryBuild` delegates to it (single code path).
- [ ] `NdisPacketBuffer`: internal write-path to set frame length/flags without a copy
  (frame written directly into `GetFrame()` span).
- [ ] Reinjector: rent from `NdisPacketBufferPool.Shared`, build in place, return on
  completion.
Tests: AC4 — allocation test over N reinjections (no per-datagram managed `byte[]`,
pool rent/return balanced); existing frame-builder tests extended to the span overload.

### Step 6 — R6 timer resolution scope
Files: `src/WinForward.Windows/` (new `HighResolutionTimerScope`), `src/WinForward.Cli/Program.cs`
- [ ] winmm `timeBeginPeriod(1)`/`timeEndPeriod(1)` wrapper (dispose-safe, idempotent).
- [ ] Create around the capture run, dispose in the existing cleanup path.
Tests: unit test the P/Invoke seam with a fake; AC6 evidence via existing pump tests +
a timing smoke on Windows (documented; CI-independent).

### Step 7 — Full-scope check (last iteration)
- [ ] `dotnet test -c Release` green; benchmarks harness run for the UDP proxy path —
  confirm steady-state pass/block allocations unchanged (hot-path spec gate).
- [ ] Review: no public behavior/config changes; per-flow FIFO and per-adapter ordering
  notes in design still hold after implementation drift check.

## Validation commands
```
dotnet build -c Release
dotnet test -c Release
dotnet run -c Release --project benchmarks/...   # allocation gates for hot path
```

## Risky files / rollback
- `UdpProxyCoordinator` (concurrency-heavy): Steps are ordered so each is independently
  revertible; coordinator changes (Step 3) are the largest — review gate after its tests pass.
- `NdisApiDriver` gate split (Step 4): documented fallback design; do not stack Step 5
  before Step 4 is validated.
- No git commits until Phase 3.4.

## Pre-start checklist
- [x] prd.md converged (Q1 answered: R1–R6; Q2 resolved: drop-oldest, recorded in design D1)
- [ ] implement.jsonl / check.jsonl curated with real spec entries
- [ ] Final planning summary approved by user
