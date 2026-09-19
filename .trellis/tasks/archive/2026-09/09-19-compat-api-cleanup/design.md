# Design: Compatibility API cleanup

Task: `.trellis/tasks/09-19-compat-api-cleanup`
Evidence base: `research/compat-inventory.md` (callers), `research/design-health.md` (seams).
Line numbers are from base commit `0463b9b`.

## 1. Classification rules

A production symbol is in scope as a compatibility API when one of these holds and no
production caller exists:

| Rule | Definition | Action |
|---|---|---|
| Dead | Zero references anywhere (src/tests/benchmarks) | **DELETE** |
| Test/bench-only | Referenced only from `tests/` or `benchmarks/` | **DELETE**, rewrite callers to the production seam |
| Public wrapper | Public overload forwarding to an internal/span core that production uses | **DELETE** the wrapper; callers use the core |
| Test-only parameter on a public signature | Optional ctor/method parameter never passed by production | **MOVE** to an internal overload (capability preserved) |
| Benchmark-only diagnostic | Public member whose only reader is `benchmarks/` | **NARROW** to `internal` (+ `InternalsVisibleTo` where missing) |

Legitimate KEEP (per PRD R2): `internal` seams consumed by the owning module's own tests; and
production-consumed members. These are listed in the inventory §C and are not touched.

## 2. Deletion set (M1 + M2)

### M1 — Dead (zero callers)

| Symbol | Location | Note |
|---|---|---|
| `NdisApiDriver.ControlGateMaxConcurrentCalls` + backing use | `NdisApiDriver.cs:303` | delete property |
| `NdisApiDriver.BatchedSendFlushCount` | `NdisApiDriver.cs:311` | delete property, field, and its `Interlocked.Increment` with no reader |
| `NdisApiDriver.BatchedSendPacketCount` | `NdisApiDriver.cs:314` | same |
| `NdisApiDriver.GetAdapterGateMaxConcurrentCalls` | `NdisApiDriver.cs:320` | delete; `NdisAdapterGateMap.GetMaxConcurrentCalls` stays (internal, test seam) |
| `TcpProxyCoordinator.SynCopyPool` | `TcpProxyCoordinator.cs:625` | getter only; `_synCopyPool` stays |
| `NdisApiAbi.UpstreamVersion` / `UpstreamCommit` | `NdisApiAbi.cs:12-13` | dead provenance constants; provenance belongs in docs |
| `NdisApiAbi.LoopbackFilter` | `NdisApiAbi.cs:22` | unused flag |
| `NativeFrameHandle.HasBuffer` | `FlowDispatcher.cs:49` | production branches on the field |
| `TcpRedirectTable.TryResolveByTranslated` | `TcpRedirectTable.cs:233` | no caller |
| `UdpAssociations.TryRemoveOriginal` | `UdpAssociations.cs:130` | no caller |
| `SetupExecutor.WorkerCount` | `SetupExecutor.cs:107` | no caller |
| `Socks5UdpCodec.TryEncode(IPAddress, …)` | `Socks5Udp.cs:39-41` | forwarding overload |
| `IPPrefix(IPAddress, int)` | `IPPrefix.cs:17-20` | no caller; fix stale class doc at `:1-15` |

### M2 — Test/benchmark-only wrappers

| Symbol | Location | Replacement seam |
|---|---|---|
| `IPUdpPacket.TryParse(ReadOnlyMemory, out UdpPacketView)` + `UdpPacketView` | `IPUdpPacket.cs:26/8` | `TryParseSpan` (+ `Payload(frame)`) |
| `TcpResetBuilder.BuildReset(...)` wrappers | `TcpResetBuilder.cs:32,49` | `TryBuildReset` into caller buffer |
| `TcpResetBuilder.BuildResetFromSyn` | `TcpResetBuilder.cs:113` | `TryBuildResetFromSyn` |
| `Socks5Messages.UsernamePassword` / `Request` | `Socks5State.cs:65,98` | `WriteUsernamePassword` / `WriteRequest` |
| `Socks5UdpCodec.Encode` family (3 overloads) | `Socks5Udp.cs:44,51,69` | `TryEncode(IPAddressValue, …)` |
| `UdpFrameBuilder.TryBuild(IPAddress/IPAddressValue, …)` | `UdpFrameBuilder.cs:24,36` | `TryBuildInto(…Span<byte>…)` |
| `IPPrefix.Contains(IPAddress?)` | `IPPrefix.cs:60` | `Contains(Endpoint)` / `Contains(IPAddressValue)` |
| `Socks5UdpTransport.CreateAsync` public 3-arg | `Socks5UdpTransport.cs:165` | internal 8-param overload (tests) or `Socks5UdpTransportFactory` |
| `BoundedSetupQueue.Bytes` | `BoundedSetupQueue.cs:37` | internal (tests have IVT) |
| `PacketLease.Disposition` | `PacketRuntime.cs:115` | internal |
| `RuntimeCounters.RecordPoolRent` / `RecordPoolReturn` | `RuntimeCounters.cs:100,108` | internal (tests have IVT); production already uses pre-built key boxes |
| `NdisCapturedPacket.FromCapture` | `NdisCapture.cs:10-14` | internal |

Suite-level deletions allowed under PRD R4: tests whose unit under test is itself dead
(`AdapterSelectorTests`, if M4 removes `AdapterSelector`). Every such deletion is listed in the
final report.

## 3. Parameter and visibility strategy (M3 + M4)

### 3.1 Pattern: public production surface + internal test overload

Where a public method mixes production and test-only parameters, keep the public method with
**only** production parameters and add an `internal` overload carrying the test-only ones:

- `Socks5ControlConnection.ConnectAsync` (`:59-67`): public keeps `server`, `onSocketReady`,
  `maxAttempts`, `perAttemptTimeout`, `addressCache`, `cancellationToken`; the internal
  overload additionally takes `resolveAddresses` and `socketFactory`. Tests bind to the internal
  overload through `InternalsVisibleTo` (Runtime already grants it).
- `RuntimeHeartbeat` (`:65-86`): public ctor keeps production parameters; an `internal`
  ctor overload takes `gcSnapshotProvider`.
- `AdapterTransientRetryLogGate` (`:13`): replace `Func<long>? ticksProvider` with
  `TimeProvider?` if it can reuse the existing `TimeProvider` seam; otherwise move it to an
  internal overload.

### 3.2 Pattern: remove never-passed parameters

- `TcpProxyCoordinator` ctor (`:46-58`): delete `framePool`; `_framePool` becomes
  `NdisPacketBufferPool.Shared` (current fallback) with no ownership flag.
- `WindowsProcessAttributor` ctor (`ProcessAttribution.cs:21`): delete the never-overridden
  `retryDelay` optional (inline the default) or promote it to a real configuration knob —
  choose deletion unless production configuration exposes it.
- `TcpRedirectSession` (`TcpProxyCoordinator.cs:662`): drop the `flowGeneration = 0` default;
  the two tests that omit it pass the value explicitly.

### 3.3 Pattern: narrow public test/benchmark diagnostics to internal

| Symbol | Location | Change |
|---|---|---|
| `TcpProxyCoordinator.Table` | `:83` | `public` → `internal` |
| `NativeBufferPoolStats` + `NativeBufferPool.Stats` | `NativeBufferPool.cs:62,240` | → `internal`; add `<InternalsVisibleTo Include="WinForward.Benchmarks" />` to `WinForward.Core.csproj` |
| `SetupExecutor` counters (`PendingCount`, `FreeCount`, `EnqueuedCount`, `CompletedCount`, `RejectedCount`, `OverflowAllocations`) | `:108-118` | → `internal` |
| `Socks5UdpReceiveResult.Received` / `Skipped` | `Socks5UdpTransport.cs:49,52` | → `internal` (Runtime already grants Benchmarks IVT) |
| `ISetupExecutor` / `SetupWorkItem` / `SetupWorkKind` | `SetupExecutor.cs:25-64` | demote to `internal` if no non-IVT assembly consumes them (Cli has IVT as `WinForward`); otherwise record as follow-up |

`AdapterSelector` (`WindowsAdapter.cs:10`): production adapter selection uses
`WindowsAdapterInventory`; the type's only consumers are its own tests. Decide at
implementation time by grep; if confirmed, delete the type and its test file (PRD R4).

## 4. B9 — span-only UDP transport send seam (M5)

Current chain (production never calls the memory entry):

```
UdpProxyCoordinator.TrySendAsync(ReadOnlyMemory)   // public, tests/bench only
  → SendOnReadySessionAsync → UdpProxySession.SendAsync
  → IUdpProxyTransport.SendAsync
  → Socks5UdpTransport.SendAsync
```

Production and the soak use the span twin:

```
UdpProxyCoordinator.TrySendSpanAsync(ReadOnlySpan)  // internal, NdisPacketActionExecutor + bench
  → SendOnReadySessionSpanAsync → UdpProxySession.SendSpanAsync
  → IUdpProxyTransport.SendSpanAsync
  → Socks5UdpTransport.SendSpanAsync
```

Target state: the span chain is the only send seam. Delete the memory methods and the
`IUdpProxyTransport.SendAsync` member; rewrite test/benchmark call sites to the internal span
entry (`InternalsVisibleTo` already covers Runtime for tests and benchmarks). Tests holding a
`byte[]`/`ReadOnlyMemory` pass `.AsSpan()` at the call site; no span crosses an `await`.

Deletion set: `UdpProxyCoordinator.TrySendAsync` (+ `SendOnReadySessionAsync` if the span path
does not share it), `UdpProxySession.SendAsync`, `IUdpProxyTransport.SendAsync`,
`Socks5UdpTransport.SendAsync` and its fakes' implementations.

Risk: ~77 test references and ~12 benchmark references; test fakes implement the transport
interface. Mitigation: mechanical rewrite, one file per commit milestone; the span path is
already covered by allocation gates and the gc-soak.

## 5. Compatibility guarantees

- No behavioural change: all changes are deletions of uncalled code, visibility narrowing, or
  test-side rewrites to an equivalent production seam.
- Allocation contracts, pool balance expectations, heartbeat payload, and soak semantics are
  untouched.
- `WinForward.Core` gains a `WinForward.Benchmarks` IVT entry (test/benchmark visibility only).

## 6. Risks and rollback

| Risk | Mitigation / rollback |
|---|---|
| A "dead" symbol is referenced from Windows-only code that Linux build skips | Grep evidence already covers `src/`; after edits, `dotnet build -c Release` and a repo-wide grep gate run before commit |
| Removing the public wrappers breaks a test that asserted wrapper behaviour | Rewrite the test against the span core with the same assertions; only oracle-shape changes, not coverage loss |
| B9 rewrite destabilizes UDP tests | B9 is the last milestone; revert it independently (single commit) and split into its own task if needed |
| IVT additions widen internals visibility | IVT is compile-time only and already granted to these test/benchmark assemblies by the other projects |

## 7. Verification

1. `dotnet build -c Release` — 0 warnings, 0 errors.
2. `dotnet test -c Release` — fully green (baseline 724; permitted decrease only for tests of
   deleted dead code, itemized).
3. Repository grep gate: each M1/M2 symbol returns zero hits.
4. `dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- --stability --scenario gc-soak --duration 20` — exit 0, zero allocation/overflow deltas (smoke).
5. `HotPathAllocationGateTests` and `Socks5UdpTransportSendTests.WarmSyncSendAllocatesNoManagedBytes` unchanged and green.
