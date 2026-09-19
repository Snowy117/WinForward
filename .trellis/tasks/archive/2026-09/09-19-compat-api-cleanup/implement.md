# Implementation Plan: Compatibility API cleanup

Base commit `0463b9b`. Validation commands run at repo root (Linux, nix shell provides
dotnet-sdk_10). Each milestone is independently verifiable and revertable.

## M1 — Dead members (zero callers)

- [x] Delete A1–A4 `NdisApiDriver` telemetry (`ControlGateMaxConcurrentCalls`,
  `BatchedSendFlushCount` + field/increment, `BatchedSendPacketCount` + field/increment,
  `GetAdapterGateMaxConcurrentCalls`); keep `NdisAdapterGateMap.GetMaxConcurrentCalls`.
- [x] Delete `TcpProxyCoordinator.SynCopyPool`.
- [x] Delete `NdisApiAbi.UpstreamVersion`/`UpstreamCommit`/`LoopbackFilter`.
- [x] Delete `NativeFrameHandle.HasBuffer`.
- [x] Delete `TcpRedirectTable.TryResolveByTranslated`.
- [x] Delete `UdpAssociations.TryRemoveOriginal`.
- [x] Delete `SetupExecutor.WorkerCount`.
- [x] Delete `Socks5UdpCodec.TryEncode(IPAddress, …)`.
- [x] Delete `IPPrefix(IPAddress, int)` and fix the stale class doc that justifies it.

Validation: `dotnet build -c Release`; `dotnet test -c Release`; grep gate (each symbol → 0 hits).

## M2 — Test/benchmark-only wrappers

- [x] `IPUdpPacket.TryParse(ReadOnlyMemory, out UdpPacketView)` + `UdpPacketView`: delete;
  rewrite test/bench callers to `TryParseSpan` and `Payload(frame)`.
- [x] `TcpResetBuilder.BuildReset(IPAddress/IPAddressValue, …)` and `BuildResetFromSyn`: delete;
  rewrite `TcpResetBuilderTests` to the `TryBuild*` span cores.
- [x] `Socks5Messages.UsernamePassword` / `Request`: delete; rewrite `Socks5ProtocolTests` to
  `WriteUsernamePassword` / `WriteRequest`.
- [x] `Socks5UdpCodec.Encode` family: delete; rewrite tests and `LoopbackSocks5UdpServer` to
  `TryEncode(IPAddressValue, …)` with a pre-allocated buffer.
- [x] `UdpFrameBuilder.TryBuild` wrappers: delete; rewrite tests to `TryBuildInto`.
- [x] `IPPrefix.Contains(IPAddress?)`: delete; rewrite `EndpointAndPolicyTests` callers.
- [x] `Socks5UdpTransport.CreateAsync` public 3-arg: delete; tests use the internal overload.
- [x] `BoundedSetupQueue.Bytes`, `PacketLease.Disposition`, `NativeBufferPool`-adjacent
  `RuntimeCounters.RecordPoolRent`/`RecordPoolReturn`, `NdisCapturedPacket.FromCapture`:
  `public` → `internal`.

Validation: `dotnet test -c Release`; grep gate.

## M3 — Test-only parameters off public signatures

- [x] `TcpProxyCoordinator`: delete the never-passed `framePool` ctor parameter; `_framePool`
  becomes `NdisPacketBufferPool.Shared`.
- [x] `TcpProxyCoordinator.Table`: `public` → `internal`.
- [x] `RuntimeHeartbeat`: move `gcSnapshotProvider` to an `internal` ctor overload.
- [x] `Socks5ControlConnection.ConnectAsync`: move `resolveAddresses`/`socketFactory` to an
  `internal` overload; public signature keeps production parameters only.
- [x] `WindowsProcessAttributor`: delete the never-overridden `retryDelay` optional.
- [x] `TcpRedirectSession` (nested in `TcpProxyCoordinator`): drop the `flowGeneration = 0`
  default; update the two tests that relied on it.
- [x] `AdapterTransientRetryLogGate`: replace `Func<long>? ticksProvider` with `TimeProvider?`
  (or move to an internal overload if `TimeProvider` does not fit).
- [x] `ISetupExecutor`/`SetupWorkItem`/`SetupWorkKind`: grep non-IVT consumers; if none,
  demote to `internal`; otherwise record follow-up.

Validation: `dotnet test -c Release`; confirm allocation gates unaffected.

## M4 — Benchmark-only diagnostic surface

- [x] `WinForward.Core.csproj`: add `<InternalsVisibleTo Include="WinForward.Benchmarks" />`.
- [x] `NativeBufferPoolStats` + `NativeBufferPool.Stats`: `public` → `internal`.
- [x] `SetupExecutor` counters (`PendingCount`, `FreeCount`, `EnqueuedCount`, `CompletedCount`,
  `RejectedCount`, `OverflowAllocations`): `public` → `internal`.
- [x] `Socks5UdpReceiveResult.Received` / `Skipped`: `public` → `internal`.
- [x] `AdapterSelector`: grep consumers; if only `AdapterSelectorTests` uses it, delete type +
  its test file (PRD R4); otherwise document KEEP.

Validation: `dotnet build -c Release` (benchmarks included); `dotnet test -c Release`.

## M5 — Span-only UDP transport send seam (B9)

- [x] Delete `UdpProxyCoordinator.TrySendAsync` (and `SendOnReadySessionAsync` if not shared
  with the span path).
- [x] Delete `UdpProxySession.SendAsync`; keep `SendSpanAsync` as the only send entry.
- [x] Delete `IUdpProxyTransport.SendAsync`; update `Socks5UdpTransport` and all transport
  fakes to the span member only.
- [x] Rewrite test/benchmark call sites (≈77 refs / 12 refs) to `TrySendSpanAsync` /
  `SendSpanAsync` via `InternalsVisibleTo`.

Validation: `dotnet test -c Release --filter "UdpProxyCoordinator|UdpProxySession|UdpSetupQueue|Socks5Udp|HotPathAllocationGate"`; full suite; short gc-soak smoke.

## M6 — Verification and follow-ups

- [x] Full `dotnet build -c Release` (0 warnings) and `dotnet test -c Release` (green).
- [x] Grep gate for M1/M2 symbols and any remaining public test-only members.
- [x] `--stability --scenario gc-soak --duration 20` smoke: exit 0, zero deltas.
- [x] Update `research/compat-inventory.md` dispositions with the final outcome.
- [x] Record the design-health follow-ups (report §5 items 7–18) and propose the follow-up
  task to the user (PRD R5).

## Risky files / rollback points

- `src/WinForward.Runtime/UdpProxy/UdpProxyCoordinator.cs` + `UdpProxyCoordinator.Send.cs`,
  `UdpProxySession.cs`, `src/WinForward.Runtime/Socks5/Socks5UdpTransport.cs` (M5) — highest
  blast radius; isolated in the last milestone, revertable as one commit.
- `src/WinForward.NdisApi/NdisApiDriver.cs` (M1) — driver telemetry; removal only, no call path.
- `src/WinForward.Runtime/TcpRedirect/TcpProxyCoordinator.cs` (M1/M3) — ctor and visibility
  changes; M3 changes are compile-time only.

## Pre-start checklist

- [x] Spec review via trellis-before-dev (hot-path, quality-guidelines, udp-relay,
  tcp-local-redirect, windows-ndisapi, error-handling).
- [x] Fresh baseline recorded: 724 tests, 0 warnings (verified 2026-09-19).
- [x] PRD/design approved (Phase 1.4 gate).
