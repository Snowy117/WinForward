# Design: NDISAPI static filter table kernel bypass

Task: `10-18-ndis-filter-table-bypass`. Scope decision: **B, per-flow pass cache, TCP-only MVP**
(PRD "Scope"). This document is the technical contract; the PRD owns requirements and acceptance.

## 1. Summary and boundaries

Three layers, each independently testable:

```
FlowDispatcher / FlowAttributionPipeline     decides Pass and tags the packet
        │  PacketPassKind.Flow
        ▼
NdisPacketActionExecutor.PassAsync           observes every pass packet that still reaches user mode
        │  IKernelPassFlowObserver.OnPassPacket(key)
        ▼
NdisKernelPassCache (Runtime, Windows-only)  leases a 5-tuple mirror; one worker thread
        │  INdisKernelFilterTable.Replace / ReadRulePacketCounts / Reset
        ▼
NdisApiDriver (NdisApi)                      control-gate serialized IOCTLs
        │  SetPacketFilterTable / GetPacketFilterTableSize / GetPacketFilterTable / ResetPacketFilterTable
        ▼
ndisrd.sys                                   sequential static rules, first match wins
```

- **The kernel rule is a leased mirror of a decision already made in user mode.** It never creates a
  decision, never upgrades a block, and never exists for a protocol the policy path did not decide
  as `Pass`.
- **The table is PASS-only.** No DROP/REDIRECT/RDR action is ever encoded. A crash can leave at
  most a benign pass-through rule; the next start clears the table before installing.
- **The cache is off by default.** When off, no cache object exists, the executor's observer is
  null, and the packet path is byte-for-byte today's.
- **No pump-thread IOCTL.** Observation is a marker test plus a bounded ring append; every driver
  call runs on the cache worker.

## 2. Native ABI

### 2.1 Records and layout

A new file `src/WinForward.NdisApi/NdisStaticFilterAbi.cs` owns the mirrors, the constants, the
encoder, and a `AssertManagedLayout()` called from `NdisApiAbi.AssertManagedLayout()`. All records
are 1-byte packed, exactly as `include/Common.h` (`#pragma pack(push,1)` at line 122, pop at 891).

`STATIC_FILTER` — 184 bytes:

| Offset | Field | Type | Size |
|---:|---|---|---:|
| 0 | `Adapter` | ulong (`ULARGE_INTEGER`) | 8 |
| 8 | `DirectionFlags` | uint | 4 |
| 12 | `FilterAction` | uint | 4 |
| 16 | `ValidFields` | uint | 4 |
| 20 | `LastReset` | uint | 4 |
| 24 | `PacketsIn` | ulong | 8 |
| 32 | `BytesIn` | ulong | 8 |
| 40 | `PacketsOut` | ulong | 8 |
| 48 | `BytesOut` | ulong | 8 |
| 56 | `DataLinkFilter` | `DATA_LINK_LAYER_FILTER` | 24 |
| 80 | `NetworkFilter` | `NETWORK_LAYER_FILTER` | 84 |
| 164 | `TransportFilter` | `TRANSPORT_LAYER_FILTER` | 20 |

Nested records (each pinned by size and offset tests):

- `DATA_LINK_LAYER_FILTER` (24): `UnionSelector` 0; union at 4 — `ETH_802_3_FILTER` (20) with
  `ValidFields` 0, `SrcAddress[6]` 4, `DestAddress[6]` 10, `Protocol` 16, `Padding` 18.
- `NETWORK_LAYER_FILTER` (84): `UnionSelector` 0; union at 4 — `IP_V4_FILTER` (32) with
  `ValidFields` 0, `SrcAddress` 4, `DestAddress` 16, `Protocol` 28, `Padding[3]` 29, and
  `IP_V6_FILTER` (80) with `ValidFields` 0, `SrcAddress` 4, `DestAddress` 40, `Protocol` 76,
  `Padding[3]` 77.
- `IP_ADDRESS_V4` (12): `AddressType` 0; union at 4 (`IP_SUBNET_V4` 8: `Ip` 0, `IpMask` 4).
- `IP_ADDRESS_V6` (36): `AddressType` 0; union at 4 (`IP_SUBNET_V6` 32: `Ip[16]` 0, `IpMask[16]` 16).
- `TRANSPORT_LAYER_FILTER` (20): `UnionSelector` 0; union at 4 — `TCPUDP_FILTER` (16) with
  `ValidFields` 0, `SourcePort` 4, `DestPort` 8, `TcpFlags` 12, `Padding[3]` 13; `ICMP_FILTER` (8)
  with `ValidFields` 0, `TypeRange` (`BYTE_RANGE`, 2) at 4, `CodeRange` at 6.
- `BYTE_RANGE` (2): `StartRange` 0, `EndRange` 1.
- `PORT_RANGE` (4): `StartRange` 0, `EndRange` 2, host byte order.
- `STATIC_FILTER_TABLE` header (8): `TableSize` 0, `Padding` 4. Entries start at byte 8.

Constants (the pinned header values): `FILTER_PACKET_PASS=1`, `PACKET_FLAG_ON_SEND=1`,
`PACKET_FLAG_ON_RECEIVE=2`, `DATA_LINK_LAYER_VALID=1`, `NETWORK_LAYER_VALID=2`,
`TRANSPORT_LAYER_VALID=4`, `IPV4=1`, `IPV6=2`, `TCPUDP=1`, `IP_V4_FILTER_SRC_ADDRESS=1`,
`IP_V4_FILTER_DEST_ADDRESS=2`, `IP_V4_FILTER_PROTOCOL=4` (the V6 values are identical),
`TCPUDP_SRC_PORT=1`, `TCPUDP_DEST_PORT=2`, `IP_SUBNET_V4_TYPE=1`, `IP_SUBNET_V6_TYPE=1`,
`IPPROTO_TCP=6`.

### 2.2 Native imports

Added to the existing `internal static partial class NdisApiNative` (a second partial file is fine):

```csharp
[LibraryImport(LibraryName, EntryPoint = "SetPacketFilterTable", SetLastError = true)]
[UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
internal static unsafe partial int SetPacketFilterTable(NdisApiSafeHandle handle, byte* table);

[LibraryImport(LibraryName, EntryPoint = "ResetPacketFilterTable", SetLastError = true)]
[UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
internal static partial int ResetPacketFilterTable(NdisApiSafeHandle handle);

[LibraryImport(LibraryName, EntryPoint = "GetPacketFilterTableSize", SetLastError = true)]
[UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
internal static unsafe partial int GetPacketFilterTableSize(NdisApiSafeHandle handle, uint* tableSize);

[LibraryImport(LibraryName, EntryPoint = "GetPacketFilterTable", SetLastError = true)]
[UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
internal static unsafe partial int GetPacketFilterTable(NdisApiSafeHandle handle, byte* table);
```

`SetPacketFilterTable`'s native size expression is
`sizeof(STATIC_FILTER_TABLE) + (m_TableSize - 1) * sizeof(STATIC_FILTER)` with `ANY_SIZE == 1`, so a
zero-entry table underflows the unsigned byte count. **The driver wrapper never calls it with zero
entries; clearing is `ResetPacketFilterTable`.** This is an ABI comment and a test.

### 2.3 Table encoding

`8 + 184 × N` bytes, `N = 2 × ruleCount`, `TableSize = N`, `Padding = 0`. Rule `i` becomes two
oriented entries:

- entry `2i`: source = A, destination = B, source port = A, destination port = B;
- entry `2i+1`: source = B, destination = A, source port = B, destination port = A.

Every entry: `Adapter = 0` (all adapters), `DirectionFlags = ON_SEND | ON_RECEIVE`,
`FilterAction = FILTER_PACKET_PASS`, `ValidFields = NETWORK_LAYER_VALID | TRANSPORT_LAYER_VALID`,
statistics zero. The header tuple disambiguates the direction; setting both flags costs nothing and
keeps the rule independent of which observation orientation produced it.

Network selector = `IPV4` or `IPV6` (both endpoints must share a family; a mixed-family rule is
refused). `ValidFields = SRC | DEST | PROTOCOL`, `Protocol = 6`. Each address is
`IP_SUBNET_*_TYPE` with an all-ones mask (exact match). Address bytes are the network-order bytes
produced by `IPAddressValue.TryWrite`; the IPv4 `m_Ip`/`m_IpMask` DWORD is the little-endian
interpretation of those four bytes, exactly what the legacy sample gets from `in_addr`'s byte
fields. Ports are host-order `ushort`; `TCPUDP_FILTER.ValidFields = SRC_PORT | DEST_PORT`; the
transport selector is `TCPUDP`; `TcpFlags = 0`.

No data-link filter and no catch-all rule are ever encoded.

## 3. Driver seam

New public contract in `src/WinForward.NdisApi/NdisKernelFilterTable.cs`:

```csharp
public enum NdisKernelFilterStatus { Success, Unavailable, TableMismatch, Failed }

/// Canonical tuple: A and B are byte-wise ordered by the caller; both endpoints share a family.
public readonly record struct NdisTcpFlowRule(IPAddressValue AddressA, ushort PortA,
                                              IPAddressValue AddressB, ushort PortB);

public readonly record struct NdisTrafficCounters(long ReadCalls, long PacketsRead,
                                                  long SendCalls, long PacketsReinjected);

public interface INdisKernelFilterTable
{
    NdisKernelFilterStatus Replace(ReadOnlySpan<NdisTcpFlowRule> rules, out int nativeError);
    NdisKernelFilterStatus ReadRulePacketCounts(ReadOnlySpan<NdisTcpFlowRule> rules,
                                                Span<long> packetCounts, out int nativeError);
    NdisKernelFilterStatus Reset(out int nativeError);
    NdisTrafficCounters GetTrafficCounters();
}
```

`NdisApiDriver` implements it. Each method enters `_controlGate` exactly once (the filter table is
cold control state, like enumeration and mode changes). `Replace`:

1. Validates non-empty, count fits the encoder's bound, each rule is TCP-valid (non-zero ports,
   same family, not IPv6 link-local, no scope id).
2. Rents `byte[8 + 184 × 2N]` from `ArrayPool<byte>.Shared`, writes the header, encodes the entries
   with the encoder (no managed struct array), pins the buffer, calls the import.
3. Maps `FALSE` to `Failed` with `Marshal.GetLastWin32Error()`; catches `EntryPointNotFoundException`
   / `DllNotFoundException` as `Unavailable` (nativeError 0). Returns the pool buffer in `finally`.
4. On `Success`, records the rule order as the current expected table (for read-back verification).

`ReadRulePacketCounts`:

1. `GetPacketFilterTableSize`; if the size is not `2N` → `TableMismatch` (nativeError 0). A
   mismatched table means another client or a failed replace owns the table; the cache treats it
   as a fault.
2. Rents `byte[8 + 184 × size]`, writes `TableSize = size` into the header (the user-mode
   SDK sizes the output buffer from that field), calls `GetPacketFilterTable`, then for each rule
   `i` verifies entries `2i`/`2i+1` are `PASS`, adapter 0, the expected tuple (addresses, ports,
   protocol, selectors, masks) and reads `PacketsIn + PacketsOut` at offsets 24 and 40. Any mismatch
   → `TableMismatch`.
3. `FALSE` → `Failed` with the native error. `EntryPointNotFoundException` → `Unavailable`.

`Reset` calls `ResetPacketFilterTable` and returns the same mapping. It is the only way to clear the
table.

Traffic counters: `NdisApiDriver` adds `_packetsRead` (`Interlocked.Add` per successful drain with
the returned count), `_sendCalls`, and `_packetsReinjected` (per batched chunk and per single send).
`ReadCalls` reuses `_batchReads`. `GetTrafficCounters()` is a lock-free snapshot. These counters are
diagnostics only; a failed read/send still counts its call where the existing counters already do.

Failure/availability posture: `Unavailable` and `Failed` are returned, never thrown, so the cache
can degrade without touching capture. A read-back failure after a successful replace is
`TableMismatch`, not `Failed` — distinct because a mismatch is a table-ownership conflict, not a
native error.

## 4. Runtime cache

`src/WinForward.Runtime/Capture/NdisKernelPassCache.cs`, `internal sealed`, Windows-only
(`[SupportedOSPlatform("windows")]`), owned by `DurableCaptureBundle`.

### 4.1 State model

```
Created ──Start()──► Running ──Pause()──► Paused ──Resume()──► Running
   │                   │                    │
   └──reset fails──────┴──Disable(reason)──►Disabled
```

`Disabled` is terminal for the run (a persistent native fault must not retry forever). `Start()`
calls `Reset` first — this both clears a previous run's table and proves the API exists. `Pause()`
clears the table and the cache state; `Resume()` returns to an empty table and re-learns from
observations. `DisposeAsync` sets `Disabled`, stops the loop, and resets best-effort only when
the startup `Reset` claimed the table; a cache disabled by a failed startup reset never clears a
table it could not claim.

### 4.2 Observation path (pump thread, allocation-free)

`OnPassPacket(in FlowKey key)`:

1. `if (Volatile.Read(ref _state) != Running) return;` — one branch when the feature is paused/off.
2. `if (key.Protocol != TransportProtocol.Tcp) return;` and `if (key.Local.Port == 0 ||
   key.Remote.Port == 0) return;`
3. Build the canonical `NdisTcpFlowRule` (order the two endpoints by family/family-bits/port as
   unsigned values). Refuse mixed families, IPv6 link-local (`fe80::/10`), and any endpoint with a
   non-zero `ScopeId` (the native record cannot express the scope).
4. Marker test-and-set: a fixed `long[] _markers` (4096 slots, empty = 0, hash 0 mapped to 1),
   FNV-1a over family, address bits, and ports of both endpoints. Linear probe up to 8 slots with
   `Interlocked.CompareExchange`. A hit means the tuple is already pending or installed — return.
   A full probe chain drops the observation (counter `markerOverflow`); no allocation.
5. Append `(rule, seenAtTicks)` to a preallocated ring under `_ringGate` (a `Lock`). Ring full →
   clear the marker (so a later packet retries), increment `rejected`, return.
6. No logging, no clock allocation (`TimeProvider.GetUtcNow()` returns a struct), no LINQ.

The marker set is the only dedupe: every pass packet that reaches user mode for a non-installed
flow attempts the marker; after the first, subsequent packets hit the marker and return. When the
rule is removed, the worker clears the marker; the next pass packet re-arms the flow. The cost in
steady state is zero (the rule keeps packets out of user mode); the cost before install/re-entry is
one hash probe per pass packet.

### 4.3 Worker tick

`QuiescenceScope.Run` + `PeriodicTimer(Tick, timeProvider)`, one tick every 250 ms. Each tick:

1. **Activity** — only when `_tableMatchesDesired` and rules exist: call
   `ReadRulePacketCounts(rules, counts)`. On `Success`, for each rule compute
   `delta = counts[i] - rule.LastCounter`; if `delta > 0` set `rule.LastActive = now`; if
   `delta < 0` treat the counter as reset (baseline only). Add the positive deltas to
   `_bypassedTotal`. Reset the read-failure streak. On `TableMismatch`/`Failed`, increment the
   streak, log throttled, and **skip expiry this tick** (keeping rules on an unreadable table is
   the safe choice: the table itself still bypasses only pass flows).
2. **Expiry** — remove rules whose `now - LastActive >= IdleLease`, clearing their markers.
3. **Drain** — copy ring entries older than `InstallDelay` to a preallocated scratch list under
   `_ringGate`; release the lock; for each, skip if already installed; otherwise add, evicting the
   least-recently-active rule when `MaxFlows` is reached. A candidate whose flow has gone away is
   not detectable here (the cache deliberately does not read the flow table); the lease bounds it.
4. **Apply** — if the desired set changed: empty → `Reset`; non-empty → `Replace`. On `Success`
   set every rule's `LastCounter = 0` and `_tableMatchesDesired = true`. On `Failed`/
   `Unavailable`, increment the apply-failure streak, log throttled, set `_tableMatchesDesired =
   false` (skip activity reads), and retry next tick.
5. **Fault escalation** — after `ReadFailureLimit` consecutive read failures or
    `ApplyFailureLimit` consecutive apply failures, `Disable(reason)`: log once, attempt a final
    `Reset` best-effort (and again at dispose when the cache claimed the table), clear the marker
    set/ring/rules, and stop observing. A startup reset failure does not attempt a reset: the cache
    must not clobber a table it could not claim.
    The capture path is unchanged from today.
6. **Summary** — every `SummaryInterval` (10 s) emit the `kernel.passCache` line and mirror the
   event counters into `RuntimeCounters`.

One apply per tick is the coalescing contract: a burst of observations costs one IOCTL plus one
table copy, not one per flow.

### 4.4 Lease semantics and the 5-tuple reuse window

- `InstallDelay = 1 s`: a flow must be observed once and the worker must see the candidate at least
  1 s old before installing. One-shot TCP flows (sub-second HTTP, health checks) usually die before
  install; the table stays for long flows.
- `IdleLease = 2 s`: while the driver counters advance, `LastActive` is refreshed and the rule
  stays. With no matched packet for 2 s, the rule is removed at the next tick (≤2.25 s after the
  last packet). Because a removed rule's next packet re-enters user mode and calls `PassAsync`
  again, an active flow re-arms automatically after the install delay; an idle-then-active flow
  pays the same one-time cost.
- The worst-case stale window for a recycled TCP 5-tuple is therefore the idle lease plus the tick
  (~2.25 s), not the flow table's 5-minute idle timeout. Graceful closes additionally sit in
  TIME_WAIT; RST-closed ports are the residual risk that makes the feature opt-in.
- The cache does not read the flow table and does not keep pass flow entries alive. A rule may
  outlive its flow by at most the idle lease. This is the deliberate safety/performance trade.

### 4.5 Bounds and constants

| Constant | Value | Rationale |
|---|---:|---|
| `MaxFlows` | 128 | 256 entries = 47,112-byte table, under the untested (but likely) 64 KiB buffered-IOCTL ceiling; revisit from W3. |
| `Tick` | 250 ms | Four observations/expiry passes per second; bounded gate holds. |
| `InstallDelay` | 1 s | Skips one-shot flows; 3 % of a 30 s transfer. |
| `IdleLease` | 2 s | Bounds the recycled-tuple window; removes dead rules promptly. |
| `SummaryInterval` | 10 s | A 30 s A/B arm gets at least two lines. |
| `MarkerSlots` | 4,096 | 32 KiB; ~4 k live tuples at a 128-flow cap is far under load. |
| `CandidateCapacity` | 1,024 | 64 KiB of rule slots; ring overflow is counted and safe. |
| `ReadFailureLimit` | 40 ticks (10 s) | Escalate a persistently unreadable table. |
| `ApplyFailureLimit` | 8 ticks (2 s) | Escalate a persistently refused replace. |

### 4.6 Metrics

The worker holds cumulative `_bypassedTotal` and reads `GetTrafficCounters()` at each summary:

`crossingsPerMille = 1000 × (packetsRead + packetsReinjected) / (packetsRead + packetsBypassed)`
`callsPerMille     = 1000 × (readCalls + sendCalls) / (packetsRead + packetsBypassed)`

The denominator is the packets the driver path accounted for: packets read into user mode plus
packets the kernel pass rules matched. Synthetic injections (RST/SYN templates) count as
reinjections but not as read packets, so the ratio is a diagnostic, not an exact ledger. Zero
denominator omits the ratio. Counters mirrored to `RuntimeCounters`: `kernelPassCache.bypassed`,
`.applies`, `.evictions`, `.rejected`, `.markerOverflow`, `.readFailures`, `.applyFailures`,
`.disabled`.

### 4.7 Logging

`KernelPassCacheLog` (`[LoggerMessage]`, event names below):

- `kernel.passCache` (Information): `rules`, `active`, `bypassed`, `captured`, `reinjected`,
  `crossingsPerMille`, `callsPerMille` — deltas since the previous line, active = rules whose
  counters advanced in the interval.
- `kernel.passCache.unavailable` (Warning, once): the deployment's DLL does not export the API or
  the initial reset failed; the feature is disabled.
- `kernel.passCache.disabled` (Error, once): a persistent read/apply fault disabled the cache;
  `reason` and the last native error.
- `kernel.passCache.fault` (Warning, throttled 5 s): a single read/apply failure with operation and
  native error.
- `kernel.passCache.reset` (Information): `reason` (`startup`, `pause`, `shutdown`), the resulting
  status, and the native error when the clear failed. It records that the table was claimed back or
  cleared; a failed startup reset is followed by the `.unavailable` warning, while a failed pause
  reset is retried on resume and a failed shutdown reset is allowed to remain as a benign PASS rule.

No message includes a flow tuple, address, or port (privacy rule: the logging guide's per-packet
privacy posture); the summary is aggregate only.

## 5. Dispatch observation contract

### 5.1 Why a new enum

`NdisPacketActionExecutor.PassAsync` is called for several semantically different passes:

| Caller | Meaning | May become a kernel rule? |
|---|---|---|
| `FlowDispatcher.ExecuteDecisionAsync` (`FlowAction.Pass`) | policy pass (host/forwarded) | **yes** |
| `FlowAttributionPipeline.Execute` (`FlowAction.Pass`) | policy pass decided on a worker | **yes** |
| `FlowDispatcher.TryHandleSelfTrafficAsync` | WinForward's own socket | no (out of MVP) |
| `FlowDispatcher.DispatchNonFlowAsync` | non-flow frame (ARP/ICMP/…) | no (degenerate key) |
| `FlowDispatcher` UDP reverse-response branch | reverse leg of a **proxied** UDP flow | **never** — a rule would bypass the proxy in the forward direction |
| `NdisPacketActionExecutor.ProxyAsync` (`NotRelevant`) | proxy-decided flow that predates capture | **never** |

A bare observer on `PassAsync` cannot tell these apart without a flow-table lookup per packet. The
dispatcher already knows the reason; it tags the packet.

### 5.2 `PacketPassKind`

In `src/WinForward.Runtime/FlowDispatcher.cs` (beside `CapturedFlowPacket`):

```csharp
public enum PacketPassKind : byte { None, Flow, SelfTraffic, NonFlow, Reverse }
```

`CapturedFlowPacket` gains a trailing `PacketPassKind PassKind = PacketPassKind.None`. Call sites set
it immediately before completing/passing the lease:

- warm and slow `FlowAction.Pass` decisions (inline and the attribution pipeline's `Pass` case) →
  `Flow`;
- `TryHandleSelfTrafficAsync` and the self-traffic branch of `DispatchNonFlowAsync` →
  `SelfTraffic`;
- the ordinary non-flow branch → `NonFlow`;
- the UDP reverse-response pass → `Reverse`;
- everything else keeps `None` (including the redirect `NotRelevant` fallback, where the executor
  calls `PassAsync` directly on a `Proxy` decision).

`NdisPacketActionExecutor.PassAsync` starts with:

```csharp
if (packet.PassKind == PacketPassKind.Flow) _passFlowObserver?.OnPassPacket(packet.Context.Key);
```

One enum compare and one null check on the pass path; no allocation. The observer interface is
`public interface IKernelPassFlowObserver { void OnPassPacket(in FlowKey key); }`, implemented by
the cache; tests use a recording fake.

## 6. Composition and lifecycle

- `Program.RunCaptureLoopAsync` already owns the `NdisApiDriver`; it passes the driver to
  `DurableCaptureBundle.CreateAsync` as the `INdisKernelFilterTable` (the driver implements it).
- `DurableCaptureBundle.CreateAsync` gains the writer parameter; `BuildBundle` creates the cache
  **before** the executor when `configuration.KernelPassCache` is true, passes the cache as the
  executor's `passFlowObserver`, and starts it. The cache is stored on the bundle
  (`internal NdisKernelPassCache? KernelPassCache`) for tests and the heartbeat.
- `OnScopeInstalled`: an empty scope calls `cache.Pause()` (interception paused; clear the table);
  a non-empty scope calls `cache.Resume()`. Generation refreshes with a non-empty scope keep the
  table: rules are adapter-agnostic (`m_Adapter = 0`) and the flow table is durable across a
  refresh.
- `DisposeCoreAsync` disposes the cache **first**, before the sweeper/UDP/TCP teardown: the driver
  is still open, so the final `Reset` claims the table back. The pumps have already stopped (the
  runner stops the generation before disposing the durable layer), so no observation races the
  reset.
- Feature off: no cache object, `passFlowObserver` null, no behavior change, no driver table touched
  at startup (an old table from a previous crash is only cleared when the feature is enabled — a
  deliberate non-interference choice).

## 7. Configuration

- `WinForwardConfigDto.KernelPassCache` (`bool?`, PascalCase key) → `ValidatedConfiguration
  .KernelPassCache` (default `false`).
- Strict JSON already rejects unknown/wrongly-cased keys; the new key is just another property.
  No range/diagnostic is needed for a boolean.
- README: add the key to the key table and a paragraph under the TCP flow settings explaining the
  mechanism, the opt-in default, the exclusive driver-table claim, and the TCP-only/2 s-lease
  residual.
- `appsettings.example.json`: add `"KernelPassCache": false` beside the other `WinForward`
  settings; `AppSettingsExampleTests` validates the shipped example, so the key is exercised there.

## 8. Concurrency and ordering

- **Pump thread:** marker CAS + ring append under `_ringGate`. No driver call, no flow-table call,
  no logging, no allocation. The ring lock is held only for an array copy; the worker copies ready
  entries out and releases it before the driver IOCTL.
- **Worker thread:** the only writer of the rule list, marker clears, and driver table. All driver
  calls take `_controlGate`; the worker never holds `_ringGate` across a driver call.
- **Lock order:** `_ringGate` → (never) control gate; control gate → (never) `_ringGate`. The cache
  never takes the flow-table gate (it does not read the flow table), so no new nesting exists.
- **Memory:** marker slots use `Interlocked`; `_state` is `Volatile`; the rule list is
  worker-private; counters use `Interlocked`.
- **Ordering:** the driver evaluates rules sequentially; the cache's order is install order. Each
  packet matches at most one oriented rule. Rule order does not affect behavior because all actions
  are PASS and the tuples are disjoint; the read-back verifies the order anyway.

## 9. Failure matrix

| Condition | Result |
|---|---|
| DLL lacks `SetPacketFilterTable`/`ResetPacketFilterTable` | `Start` reset → `Unavailable`; one warn; feature disabled; capture unchanged. |
| Initial `Reset` returns `Failed` | one warn; feature disabled; no install attempted (do not clobber a table we cannot own). |
| `Replace` fails | throttled warn; `_tableMatchesDesired=false`; retry next tick; 8 consecutive failures → reset best-effort + disable. |
| `ReadRulePacketCounts` fails or mismatches | throttled warn; skip activity/expiry this tick; 40 consecutive failures → reset best-effort + disable. |
| Counter total moves backwards | treat as a driver reset: set the baseline, no activity. |
| Ring full / marker table full | drop the observation (counter), clear the marker; the flow keeps today's path. |
| `MaxFlows` reached | evict the least-recently-active rule (clear marker); the evicted flow re-enters user mode. |
| Empty scope (interception paused) | `Pause`: clear rules/markers/ring, reset the table; no observations until resume. |
| Graceful shutdown | stop loop, final reset, final summary line. |
| Crash | PASS-only rules remain until the next start's `Reset`; benign (traffic passes, as it was decided to). |
| Second WinForward instance | its `Reset` clears the first instance's rules; the first instance's next read sees `TableMismatch`, disables bypass (capture unaffected). Documented unsupported sharing. |

## 10. Residual risks and trade-offs

- **5-tuple reuse within ~2.25 s after a flow ends** can make a new TCP connection with the same
  tuple bypass policy. TIME_WAIT covers graceful closes; RST closes do not. This is the main reason
  the feature is opt-in and TCP-only; it is recorded in the spec.
- **The driver's static table is global.** Enabling the feature replaces any rules installed by
  another WinpkFilter client. The product assumes it is the only client; the startup reset is
  therefore behind the opt-in switch.
- **Table evaluation cost is the measured offset.** With 4 flow rules (8 records) the on arms used
  26.5–27.8 s/GB against 24.0 s/GB for the best off arm; the driver's sequential rule scan runs for
  every packet, including the proxied packets that do not match. The optional
  `EnablePacketFilterCache` side probe raised on-arm CPU and depressed the off arm's throughput, so
  it is not enabled; revisit only with an invalidation test and a CPU-positive measurement. The cap
  remains 128 flows / 256 records.

- **Only policy pass flows are covered.** UDP pass flows, self-traffic tuples, and non-flow frames
  keep their two crossings. The E2E hotspot (sing-box egress over TCP) is covered.
- **The ratio metric is a diagnostic**, not an exact ledger (synthetic injections; the read→apply
  window). The primary Windows evidence is the exact captured-packet count and the per-rule
  bypassed count, with CPU as a series comparison.
- **IPv6 link-local and scoped addresses** are refused (no scope id in the native record).

## 11. Test plan

- `WinForward.NdisApi.Tests`:
  - `NdisStaticFilterAbiTests`: every size/offset from §2.1; table length formula; header bytes;
    IPv4 and IPv6 encoding; host-order ports; exact masks; action/adapter/direction/valid fields;
    `SetPacketFilterTable` is never called for zero rules.
  - `NdisKernelFilterTableTests` (new): driver over a fake native seam (the existing
    `INdisReadPacketCalls` pattern extended to the four filter calls, or a `CreateForTests`
    overload) proving replace bytes, read-back mapping/validation, `Unavailable` on a missing
    export, `Failed` + native error, reset, and traffic counters.
- `WinForward.Runtime.Capture.Tests`:
  - `NdisKernelPassCacheTests` with a fake `INdisKernelFilterTable`, a manual `TimeProvider`, and
    a tick driver: install delay, one-apply coalescing, counter renewal, idle removal, re-arm,
    eviction, pause/resume, start/dispose reset, fault escalation, ring/marker overflow, and the
    0 B observation gate.
  - `NdisPacketActionExecutorPassKindTests`: only `PassKind.Flow` notifies; the other four kinds do
    not, including the redirect `NotRelevant` fallback.
- `WinForward.Runtime.Flow.Tests` (or the dispatcher's existing test file): the dispatcher sets
  `Flow` for inline and deferred attribution passes, `SelfTraffic`, `NonFlow`, `Reverse` for the
  other paths.
- `WinForward.Configuration.Tests`: the new key parses, defaults false, and the strict reader
  rejects `kernelPassCache` (wrong case).
- `WinForward.Benchmarks` / allocation gates: the warm dispatch gate and the executor pass gate
  stay 0 B; the new observation path gets an exact 0 B gate.
- Full suite green, zero-warning Release build, format and inspectcode gates.

## 12. Windows verification — executed 2026-10-18

The plan below ran on the WinLTSC VM when the concurrent E2E campaign was not using it. The
deployment lived under `C:\wfprof\kpcache` beside the earlier CPU analysis; `C:\wfbench` was not
touched. Full evidence, commands, and the raw JSONL/CSV:
`benchmarks/results/2026-10-18-ndis-filter-table-bypass/README.md`.

| Item | Actual |
|---|---|
| W1 exports | all four present on the deployed `ndisapi.dll` 3.6.1 |
| W2 PASS-bypass go/no-go | passed: three on arms, 4 rules, ≈0.88–0.90 M bypassed packets, client `failed=false` |
| W3 table ladder | 1/64/128/256 flow rules (2/128/256/512 records) all accepted; no cap change |
| W4 counters/order/reset | read-back accepted every table; reset cleared the set (`TableMismatch`); `bypassed>0` proves the counters advance |
| W5 THRU A/B | on arms 479.85–479.93 Mbps with crossing ratio 1.004–1.006; off arms 346.98–469.74 Mbps; CPU not lower |
| W6 soak | THRU 479.87 Mbps/0 loss, DNS 3,001/3,001 answered, LOSS 9,001/9,001 arrived, 0 foreignConnection |

The intended plan was: run the exports, the PASS-bypass go/no-go, the table ladder, the
counter/order probe, three off/on THRU pairs, and a correctness soak. That is what ran; the only
changes from the plan were the deployment directory (the VM's `C:\wfbench` had been redeployed by
the concurrent campaign, so the experiment used the pre-existing `C:\wfprof` tree) and an extra
side probe of the driver's packet-filter cache, which was negative and disabled afterwards.

**Outcome:** the mechanism works and the crossing/throughput goals are met; the CPU goal is not.
The measured CPU cost is in §10. The default remains off (§13); the negative branch of the original
plan (PASS still queues to user mode) did not apply.


## 13. Rollout / rollback

- **Default off retained.** Enabling is `"KernelPassCache": true` + restart. The 2026-10-18 Windows
  measurement shows the transition-count and sustained-throughput win but no CPU win, so the default
  is not flipped; a flip requires a CPU-positive measurement. Disabling resets the table at
  shutdown; a crash's PASS-only rules are cleared by the next start with the feature enabled.
- **Rollback** is the config flip. If a future run finds PASS still queues to user mode (the W2
  negative branch), remove the runtime wiring; the ABI layer may stay as a tested, unused seam only
  if the task record says so.
- **The driver's `EnablePacketFilterCache` stays off.** The side probe raised the on arm's CPU and
  depressed the off arm's throughput; it needs an invalidation test and a CPU-positive measurement
  before it is reconsidered.

- The feature is not AOT-specific: no reflection, no dynamic code; the native calls are
  `LibraryImport` like the rest of the driver.

## 14. Files (expected)

New: `src/WinForward.NdisApi/NdisStaticFilterAbi.cs`,
`src/WinForward.NdisApi/NdisKernelFilterTable.cs`,
`src/WinForward.Runtime/Capture/NdisKernelPassCache.cs`,
`src/WinForward.Runtime/Logging/KernelPassCacheLog.cs`,
`tests/WinForward.NdisApi.Tests/NdisStaticFilterAbiTests.cs`,
`tests/WinForward.NdisApi.Tests/NdisKernelFilterTableTests.cs`,
`tests/WinForward.Runtime.Capture.Tests/NdisKernelPassCacheTests.cs`,
`tests/WinForward.Runtime.Capture.Tests/NdisPacketActionExecutorPassKindTests.cs`,
`benchmarks/results/2026-10-18-ndis-filter-table-bypass/` (evidence),
`.trellis/spec/backend/ndis-kernel-pass-cache.md` (Phase 3.3).

Changed: `src/WinForward.NdisApi/NdisApiAbi.cs` (layout call, partial imports),
`src/WinForward.NdisApi/NdisApiDriver.cs` (interface implementation, counters),
`src/WinForward.Runtime/FlowDispatcher.cs` (enum, field, call sites),
`src/WinForward.Runtime/FlowAttributionPipeline.cs` (Pass kind),
`src/WinForward.Runtime/Capture/NdisPacketActionExecutor.cs` (observer),
`src/WinForward.Runtime/RuntimeCounters.cs` (keys),
`src/WinForward.Configuration/ConfigurationModels.cs`,
`src/WinForward.Cli/DurableCaptureBundle.cs`, `src/WinForward.Cli/Program.cs`,
`README.md`, `src/WinForward.Cli/appsettings.example.json`,
`.trellis/spec/backend/windows-ndisapi.md` (hub link).
