# Windows NDISAPI Interop Contracts

> How WinForward talks to the WinpkFilter NDISAPI driver: adapter identity correlation, request-handle rules, native-call gating, DLL resolution, and the batched capture ABI. Split 2026-08-29 from the former monolithic NDISAPI file; sibling contracts: [tcp-local-redirect.md](./tcp-local-redirect.md), [udp-relay.md](./udp-relay.md), [traffic-policy-lifecycle.md](./traffic-policy-lifecycle.md).

---

## Adapter Identity Contract

> How WinForward correlates NDISAPI runtime adapters with stable Windows adapter identities. Verified on a real Windows 11 host with WinpkFilter 3.6.2.1 during the 08-07-winforward-proxy check phase.

### 1. Scope / Trigger

- Trigger: any code that enumerates adapters (`adapters` CLI command), resolves `adapterId`/`adapterName` config selectors, or maps an `INTERMEDIATE_BUFFER` adapter handle back to a configured adapter.
- This is an infra integration contract: NDISAPI (kernel driver handles) and Windows IP Helper (`NetworkInterface`) are two separate enumeration planes that must be joined correctly.

### 2. Signatures

- `WindowsAdapterInventory(Func<IReadOnlyList<(string InternalName, nint Handle, byte[] Mac, ushort Mtu)>> ndisAdapters, Func<IReadOnlyList<IPAdapterInfo>>? ipAdapters = null)` — the second provider is injectable so correlation is unit-testable without Windows hardware.
- `IReadOnlyList<WindowsAdapter> GetCurrentAdapters()` — `WindowsAdapter(StableId, FriendlyName, InternalName, RuntimeHandle, Generation)`.
- `IPAdapterInfo(string Id, string Name, byte[] Mac)` — projection of `NetworkInterface.Id` / `.Name` / `.GetPhysicalAddress()`.

### 3. Contracts

- NDISAPI `GetTcpipBoundAdaptersInfo` internal name is typically `\DEVICE\{GUID}`; stripping the `\DEVICE\` prefix yields the adapter's NetCfgInstanceId, which equals `NetworkInterface.Id` (`{GUID}`, same casing on observed systems — compare case-insensitively and brace-insensitively via `Guid.TryParse` normalization).
- **Correlation is GUID-primary.** MAC is only a sanity-check fallback when the internal name is not a GUID.
- `RuntimeHandle` is process-lifetime state: never persist it, never print it as identity, rebuild it on adapter-list change.
- Ambiguity rule: a correlation key must match **exactly one** IP Helper adapter; zero or multiple matches fall back (see matrix).
- IP Helper owner-PID IPv6 `ScopeId` fields are host-order DWORDs and must be preserved when constructing `IPAddress`; only owner-row port fields use network byte order and require conversion.

### 4. Validation & Error Matrix

| Condition | Result |
|---|---|
| GUID extracted from internal name, exactly one `NetworkInterface.Id` match | correlated (`StableId` = `Id`, `FriendlyName` = `Name`) |
| GUID match count 0 or >1 | try MAC fallback |
| MAC fallback matches exactly one | correlated |
| MAC fallback matches 0 or >1 | uncorrelated: `StableId`/`FriendlyName` fall back to the internal name (never guess) |
| Internal name not a GUID and MAC is all-zero (hidden/virtual adapters report `000000000000`) | uncorrelated fallback |

### 5. Good/Base/Bad Cases

- Good: `\DEVICE\{DD8CD9A1-...}` ↔ `Id = {DD8CD9A1-...}` → friendly name `Ethernet`. Verified for physical and hidden `Local Area Connection* N` adapters alike (the hidden ones have no MAC at all and only resolve via GUID).
- Base: adapter whose internal name is not a GUID → MAC fallback resolves it when the MAC is unique.
- Bad: MAC-only correlation. NDIS filter drivers (WFP Native/802.3 MAC Layer LWF, WinpkFilter's own NDIS LWF, Npcap, QoS Packet Scheduler) each clone the physical MAC, so one MAC appears on ~6 `NetworkInterface` entries and the ambiguity check always fails. **MAC-only correlation never works on a real Windows host.**

### 6. Tests Required

- Unit (hardware-independent, via injected `IPAdapterInfo` provider): GUID correlation success; brace/case-insensitive GUID compare; MAC fallback when internal name is not a GUID; bare-GUID internal name; duplicate-MAC interfaces must NOT corrupt GUID correlation; zero-MAC and ambiguous adapters fall back to the internal name. IP Helper projection tests preserve a nonzero IPv6 scope ID while converting network-order ports.
- Windows smoke: `WinForward.exe adapters` prints stable GUID + friendly name + internal name for every MSTCP-bound adapter (exit 0); without `ndisapi.dll` it exits 1 with an actionable diagnostic.

### 7. Wrong vs Correct

#### Wrong

```csharp
// MAC-only correlation: always ambiguous once any NDIS filter driver
// (including WinpkFilter itself) clones the MAC onto filter interfaces.
var matches = ipAdapters.Where(ip => ip.Mac.SequenceEqual(adapter.Mac)).Take(2).ToArray();
var matched = matches.Length == 1 ? matches[0] : null; // always null on real hosts
```

#### Correct

```csharp
// GUID primary (internal name GUID == NetworkInterface.Id), MAC as sanity fallback.
if (TryExtractGuid(adapter.InternalName, out var guid))
{
    var guidMatches = ipAdapters.Where(ip => TryExtractGuid(ip.Id, out var id) && EqualsOrdinalIgnoreCase(id, guid)).ToArray();
    if (guidMatches.Length == 1) return guidMatches[0];
}
// ... then MAC fallback, then internal-name fallback. See AdapterIdentity.cs.
```

**Related**: `.trellis/tasks/archive/2026-08/08-07-winforward-proxy/research/winpkfilter-ndisapi-design-constraints.md` (direction semantics, handle lifetime, IP Helper correlation evidence); `src/WinForward.Windows/AdapterIdentity.cs` (reference implementation).

---

## Adapter Handles: Enumeration vs Captured (hardware-verified 2026-08-07)

> **Warning**: `GetTcpipBoundAdaptersInfo` returns per-adapter handles that are the ONLY valid values for request-level `hAdapterHandle` fields. The `INTERMEDIATE_BUFFER.m_hAdapter` seen in captured packets is a DIFFERENT kernel pointer (observed: list handle `0xFFFFAD8A2B30B010` vs captured `0xFFFFAD8A2B30B2D0` on the same adapter). Passing the captured `m_hAdapter` as the request handle makes `SendPacketToAdapter`/`SendPacketToMstcp` fail with `ERROR_INVALID_PARAMETER` (87) on every packet.

- `NdisCapturePump` must stamp captured packets with the pump's enumeration handle (`NdisCapture.cs`), never with `NdisPacketBuffer.CapturedAdapterHandle`.
- A captured packet carries two distinct flag values: `DeviceFlags` selects MSTCP-relative direction, while `INTERMEDIATE_BUFFER.m_Flags` is NDIS packet metadata. Preserve both through the managed capture record and ordinary pass reinjection; a fresh synthetic frame intentionally starts with metadata flags zero.
- **Native-call gate topology (superseded 2026-08-28, task 08-28-udp-loss-design-flaws D3)**: `NdisApiDriver` splits serialization into a **control gate** (open/close/enumeration/mode snapshot+set+restore — cold path) and a **per-adapter-handle gate map** (`NdisAdapterGateMap` in `NdisNativeCallGate.cs`: leaf `Lock` + `Dictionary<nint, NdisNativeCallGate>`, grow-only) used by `TryReadPackets`, `SendPacketToMstcp`, `SendPacketToAdapter`, keyed by the enumeration handle the request carries. Within one adapter handle every native call stays serialized (preserves per-adapter read/reinject ordering and the historical OVERLAPPED concern — each request struct is method-local); **across adapters calls proceed in parallel**, so a slow IOCTL on adapter A can no longer stall adapter B's pump reads. The queue query + batch read pair keeps sharing ONE lease of the adapter's gate. Lock order is fixed: the map lock is a leaf (never held across `gate.Enter()`), and the control gate is never nested inside an adapter gate or vice versa. This replaces the earlier "serialize every operation on one driver instance" contract: that single-Monitor design coupled all adapters through one lock and was a confirmed driver-queue-overflow (silent loss) amplifier under multi-adapter/high-pps load. Gate contention telemetry (`MaxConcurrentCalls`) is preserved per gate. Rollback shape if driver-level coupling ever shows up on hardware: a single send-gate + per-adapter read-gates.
- This matches the official samples: `ETH_M_REQUEST.hAdapterHandle` is set once from the adapter list and reused for read/write requests.
- All NDISAPI `[LibraryImport]` declarations use `SetLastError = true`; send-path exceptions must include `Marshal.GetLastWin32Error()` — driver-side rejections are otherwise undiagnosable.
- Diagnostics context worth logging on send failure: native error, frame length, device flags, adapter handle.

**Symptom / Cause / Fix** (recorded as a verified bug class):

- Symptom: capture works, tunnel mode applies, but every pass reinjection fails with native error 87 and the runtime shuts down fail-closed.
- Cause: request built with the captured buffer's `m_hAdapter` instead of the enumeration handle.
- Fix: `NdisCapturedPacket(buffer, _adapterHandle, buffer.DeviceFlags)` in the pump.
- Verification: scratch harness `sendtest` (read -> reinject captured buffer with enumeration handle: 97/97 OK, both directions). Product re-verified: 100/100 ICMP pass-through with 0% loss and no duplicates.

**Performance note**: the pump now reads in batches (`ReadPackets`, batch capacity 32, one gate lease per batch; wired 2026-08-27 — see "Batched capture reads and buffer pooling" below). The 1 ms poll delay remains only on empty batches; since 2026-08-28 the capture run is wrapped in `WinForward.Windows.HighResolutionTimerScope` (winmm `timeBeginPeriod(1)`, fail-open with a one-shot warn), so the 1 ms delay resolves to ~1–2 ms instead of the ~15.6 ms default timer tick; `SetPacketEvent` event-driven reads are the optional next upgrade if empty-to-first-packet latency still matters.

---

## Native DLL resolution (fixed 2026-08-12)

- `NdisApiNative` (in `NdisApiAbi.cs`) registers a `NativeLibrary.SetDllImportResolver` in its static constructor that loads `ndisapi.dll` only from `AppContext.BaseDirectory` — matching the README claim; there is no PATH/current-directory search. Other library names return zero and use default resolution. AOT-safe (no reflection).

---

## Batched capture reads and buffer pooling (wired 2026-08-27)

### 1. Scope / Trigger

- Trigger: any change to the capture pump read loop, the frame copy path into `PacketLease`, TCP in-place rewrite, or `NdisPacketBuffer` allocation on the injection path.
- Infra contract: the NDISAPI batched ABI shape, the gate lease granularity, and the pooled-frame lifetime boundary across `WinForward.NdisApi` → `WinForward.Core` → `WinForward.Runtime`.

### 2. Signatures

- `NdisApiDriver.TryReadPackets(nint adapterHandle, NdisPacketBuffer[] buffers) -> int` — queue query + batched read merged into a single `NdisNativeCallGate` lease; returns the driver-filled success count (0 = empty queue).
- Batched send overloads exist only at the ABI layer (`NdisApiNative.SendPacketsToMstcp` / `SendPacketsToAdapter` over `EthernetMultiRequest*` in `NdisApiAbi.cs`); the driver's public send surface is single-packet `SendPacketToMstcp/SendPacketToAdapter(nint, NdisPacketBuffer)` — one gate lease per injected packet until batched sends are adopted.
- `PacketLease(ReadOnlyMemory<byte> frame, Action<ReadOnlyMemory<byte>>? onCompleted)` — completion-callback constructor; the plain constructor keeps null semantics.
- `NdisPacketBufferPool` (process-wide `Shared`, capacity 256) with `Rent()`/`Return()`; `NdisPacketBuffer` carries an owner-pool state machine (private ctor → Dispose frees; pool-rented → Dispose returns, double-Dispose no-op).

### 3. Contracts

- **ETH_M_REQUEST ABI** (x64, Pack=1): `hAdapterHandle(8) + dwPacketsNumber(4,in) + dwPacketsSuccess(4,out) + NDISRD_ETH_Packet[N]` (one `INTERMEDIATE_BUFFER*` each, 8 bytes) = 16 + 8N total. Caller fills handle/count/buffer pointers; the driver fills `dwPacketsSuccess` and each buffer's contents on success. Managed shape is `EthernetMultiRequest` (`NdisApiAbi.cs`, 24-byte fixed head, `FirstBuffer` aliases `EthPacket[0]`) with `AssertManagedX64Layout` size/offset assertions (24; 0/8/12/16). Exports verified against the pinned commit 417b8734 (`ndisapi.vs2012/ndisapi.def`, `include/ndisapi.h:300-302`): `ReadPackets`, `SendPacketsToMstcp`, `SendPacketsToAdapter` (all `BOOL __stdcall (HANDLE, PETH_M_REQUEST)`).
- **Frame lifetime boundary (the load-bearing rule)**: `FlowDispatcher.CompleteAsync` calls `lease.TryComplete(disposition)` BEFORE `execute()`, so every frame consumer (pass injection copy, UDP payload parse, clientMac slice, TCP in-place rewrite, RST template) runs AFTER lease completion but must stay INSIDE `CapturePacketProcessor.ProcessAsync`'s await window. Therefore the `ArrayPool<byte>.Shared.Return` lives in `ProcessAsync`'s outer `finally` — never on the lease completion callback. Any new code that lets a `Frame`/`Memory` slice escape the `ProcessAsync` window (returning it, capturing it in a background task, storing it without copying) reintroduces a use-after-return.
- **Copy-out points are mandatory**: data that must outlive the window is copied synchronously — UDP `clientMac` (`ToArray()` at session creation), SOCKS5 payload (`Encode` copies before any await), the bounded SYN template (recorded before rewrite).
- **In-place rewrite ordering**: `RecordClientSyn`/`RecordServerSynAck` (reads of the original frame) MUST run BEFORE `TryRewriteTcpEndpoints` (write) on the same frame, or the RST template is polluted by rewritten endpoints/MACs. `TryRewriteIpv4Tcp/Ipv6Tcp` keeps the invariant "every parse/validation precedes the first field write; no failure branch after writing begins", so an in-place rewrite either leaves the frame untouched or fully rewrites it. A lease whose `Frame` is not array-backed fails closed (`reason=rewrite`).
- **Pump batching**: batch buffers are pump-private for the pump's lifetime, released exactly once (run-loop exit or dispose); packets within a batch are awaited strictly in index order, so reinjection order matches arrival order. Empty batch keeps the poll-delay pacing.
- **Gate granularity**: one gate lease per batch on the read path (query + read inside the same lease); one gate lease per injected packet remains on the send path until batched sends are adopted.

### 4. Validation & Error Matrix

| Condition | Result |
|---|---|
| Queue query native call fails | throw `Win32Exception` (existing `HasQueuedPackets` semantics, now in `NdisNativeCallStatus.cs`) |
| Queue empty (`queuedPacketCount == 0`) | `TryReadPackets` returns 0; pump takes poll delay |
| `ReadPackets` returns FALSE with a non-empty queue | throw `Win32Exception` |
| Driver fills `dwPacketsSuccess` > requested count | clamped to the request count (defensive; `InterpretBatchReadResult` in `NdisNativeCallStatus.cs`) |
| `buffers` empty or contains null entries | 0 / `ArgumentNullException` (parameter validation) |
| Lease frame not array-backed at an in-place rewrite point | fail-closed `TcpRedirectOutcome.Blocked`, `reason=rewrite` |
| Pool return above capacity (256) or after drain | buffer freed immediately, never double-returned |

### 5. Good/Base/Bad Cases

- Good: a full batch of 32 frames flows through classify → dispatch → in-place rewrite → pool-rented inject with zero per-packet managed allocations beyond the single `ArrayPool` copy and zero native allocs.
- Base: zero-length frame (`ArrayPool.Rent(0)` returns an empty array; safe), partial batch (n < capacity) processed in order.
- Bad: returning the pooled array from a lease `TryComplete` callback — the executor then reads a reused array (torn frame); storing `lease.Frame` in a session without copying and reading it after `ProcessAsync` returns.

### 6. Tests Required

- `NdisApiAbiTests`: batch read error matrix (partial-batch clamp, non-empty failure throws).
- `NdisCapturePumpTests`: in-batch ordering with enumeration-handle stamping, partial batch, empty-batch poll, exactly-once batch-buffer release, capacity/null argument checks.
- `FlowDispatcherExecutorTests`: completion callback fires exactly once across double `TryComplete` + `Dispose`; `Dispose` is a completion path; plain constructor has no callback; pooled copy is byte-identical at actual length.
- `TcpProxyCoordinatorRewriteTests`: `SynRewriteParseFailureLeavesFrameByteIdentical` (parse failure leaves the frame byte-identical, returns Blocked, releases resources) — the in-place-rewrite no-intermediate-state lock.
- `NdisPacketBufferPoolTests`: rent/return round-trip reuse, over-capacity free, 64-thread rent uniqueness, double-Dispose no-op, drain-then-rent, foreign-buffer rejection, private-buffer (pump) dispose semantics regression.

### 7. Wrong vs Correct

#### Wrong

```csharp
// Returning the pooled frame when the lease completes: CompleteAsync runs
// TryComplete BEFORE the executor reads Frame, so another pump's Rent() can
// reuse this array before the pass injection copies it.
var lease = new PacketLease(frame, _ => ArrayPool<byte>.Shared.Return(array));
```

#### Correct

```csharp
// The return point is ProcessAsync's outer finally — the proven boundary of
// the whole dispatch chain. Every Frame consumer closes inside the window.
var pooledFrame = ArrayPool<byte>.Shared.Rent(frameSpan.Length);
try { ... await dispatcher.DispatchAsync(...); }
finally { ArrayPool<byte>.Shared.Return(pooledFrame); }
```

```csharp
// Wrong: record the SYN template after rewriting it in place — the RST
// builder then inherits rewritten endpoints/MACs and the client rejects it.
TryRewriteForwardLeg(frame, ...);
RecordClientSyn(frame.Span, association);

// Correct: read-then-write. The template keeps the original client bytes.
RecordClientSyn(frame.Span, association);
TryRewriteForwardLeg(frame, ...);
```

**Related**: task `08-27-fix-datapath-throughput` (prd/design/implement artifacts hold the full audit tables); the parent `08-27-fix-eof-reset-design-flaws` maps the throughput bottleneck to the RST/EOF frequency symptom.
