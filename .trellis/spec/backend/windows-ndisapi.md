# Windows NDISAPI Interop Contracts

> How WinForward talks to the WinpkFilter NDISAPI driver: how a runtime adapter is correlated with a
> stable Windows identity, which handle is valid for what, how the native calls are serialized and
> resolved, and the request ABI both batching paths share. Read it before touching
> `src/WinForward.NdisApi/`, the adapter enumeration, or anything that passes an adapter handle
> around. Children: [ndis-capture-refresh.md](./ndis-capture-refresh.md) (when the capture view is
> rebuilt and why), [ndis-batched-capture.md](./ndis-batched-capture.md) (the batched read ABI and
> buffer pooling), [ndis-batched-send.md](./ndis-batched-send.md) (batched reinjection, pass lanes and
> redirect lanes).

---

## Adapter Identity Contract

> How WinForward correlates NDISAPI runtime adapters with stable Windows adapter identities. Verified
> on a real Windows 11 host with WinpkFilter 3.6.2.1 during the 08-07-winforward-proxy check phase.

**Signatures** (`src/WinForward.Windows/AdapterIdentity.cs`):

- `WindowsAdapterInventory(Func<IReadOnlyList<(string InternalName, nint Handle, byte[] Mac, ushort Mtu)>> ndisAdapters, Func<IReadOnlyList<IPAdapterInfo>>? ipAdapters = null)` — the second provider is injectable so correlation is unit-testable without Windows hardware.
- `IReadOnlyList<WindowsAdapter> GetCurrentAdapters()` — `WindowsAdapter(StableId, FriendlyName, InternalName, RuntimeHandle, Generation)`.
- `IPAdapterInfo(string Id, string Name, byte[] Mac)` — projection of `NetworkInterface.Id` / `.Name` / `.GetPhysicalAddress()`.

**Contracts**:

- NDISAPI `GetTcpipBoundAdaptersInfo` internal name is typically `\DEVICE\{GUID}`; stripping the
  `\DEVICE\` prefix yields the adapter's NetCfgInstanceId, which equals `NetworkInterface.Id`
  (`{GUID}`, same casing on observed systems — compare case-insensitively and brace-insensitively via
  `Guid.TryParse` normalization).
- **Correlation is GUID-primary; MAC is only a sanity-check fallback** when the internal name is not a
  GUID. Matching is exact-one: zero or multiple matches fall back (matrix below). Physical and hidden
  adapters both resolve by GUID — a hidden `Local Area Connection* N` has no MAC at all. MAC-only
  correlation never works on a real host: NDIS filter drivers (WFP Native/802.3 MAC Layer LWF,
  WinpkFilter's own NDIS LWF, Npcap, QoS Packet Scheduler) each clone the physical MAC, so one MAC
  appears on ~6 `NetworkInterface` entries and the ambiguity check always fails. `MatchAdapter` in
  `src/WinForward.Windows/AdapterIdentity.cs` is the reference implementation.
- **Adapter identity is interned once per process (F4, 2026-09-30).** `AdapterSlotTable` is created
  once in `DurableCaptureBundle` and injected into the durable `CapturePacketProcessor`; each
  adapter's GUID-primary `StableId` is interned into a monotone, never-reused `ushort` slot at
  generation build, and `FlowKey` stores that slot plus the parsing enumeration's `Generation`
  instead of a string. The generation is deliberately **not** part of the interned identity (it moves
  on every enumeration), so the table is bounded by the number of distinct adapters the process has
  ever seen; slots are never reused, which is what makes an old key unable to alias a different
  adapter (ABA). Interning and refresh run once per adapter per enumeration under a gate: `TryIntern`
  on an already-interned stable ID republishes a replacement immutable `AdapterMetadata` and is the
  table's only writer (the design's by-slot `Observe` seam was removed as unused); the read side
  (`TryResolve`) is a lock-free array index. Facts: `AdapterSlotTableRoundTripsStableId`,
  `TryInternIsIdempotentPerStableIdAndSlotsAreNeverReused` (idempotence and never-reuse in one fact),
  `AdapterSlotSurvivesARefresh`, `FlowKeyEqualityKeepsTheGeneration`.
- **A capture-scope adapter that cannot be interned is refused, never keyed `NoSlot`.** The generation
  factory excludes it from the scope with one rate-limited error `adapter.slot-exhausted`
  (`src/WinForward.Runtime/Capture/NdisCaptureGeneration.cs`); two adapters collapsed onto one slot
  would compare equal on a shared key and serve each other's state. The table-level refusal is pinned
  by `AnAdapterThatCannotBeInternedIsRefusedNotAliased`; the factory's exclusion/log loop has no test
  (Windows-only, real driver required).
- `RuntimeHandle` is process-lifetime state: never persist it, never print it as identity, and rebuild
  it on adapter-list change.
- IP Helper owner-PID IPv6 `ScopeId` fields are host-order DWORDs and must be preserved when
  constructing `IPAddress`; only owner-row port fields use network byte order and require conversion
  (`IPHelperAbi.DecodeIpv6Address` / `DecodeNetworkPort`).

| Condition | Result |
|---|---|
| GUID extracted from internal name, exactly one `NetworkInterface.Id` match | correlated (`StableId` = `Id`, `FriendlyName` = `Name`) |
| GUID match count 0 or >1 | try MAC fallback |
| MAC fallback matches exactly one | correlated |
| MAC fallback matches 0 or >1 | uncorrelated: `StableId`/`FriendlyName` fall back to the internal name (never guess) |
| Internal name not a GUID and MAC is all-zero (hidden/virtual adapters report `000000000000`) | uncorrelated fallback |

**Tests**: unit, hardware-independent via the injected `IPAdapterInfo` provider — GUID correlation
success; brace/case-insensitive GUID compare; MAC fallback when the internal name is not a GUID;
bare-GUID internal name; duplicate-MAC interfaces must NOT corrupt GUID correlation; zero-MAC and
ambiguous adapters fall back to the internal name. Windows smoke: `WinForward.exe adapters` prints
stable GUID + friendly name + internal name for every MSTCP-bound adapter (exit 0); without
`ndisapi.dll` it exits 1 with an actionable diagnostic.

**Related**: `.trellis/tasks/archive/2026-08/08-07-winforward-proxy/research/winpkfilter-ndisapi-design-constraints.md`
(direction semantics, handle lifetime, IP Helper correlation evidence).
`AddressFingerprint` — a diff input this contract's enumeration feeds — is defined in
[ndis-capture-refresh.md](./ndis-capture-refresh.md).

---

## Adapter Handles: Enumeration vs Captured (hardware-verified 2026-08-07)

> **Warning**: `GetTcpipBoundAdaptersInfo` returns per-adapter handles that are the ONLY valid values
> for request-level `hAdapterHandle` fields. The `INTERMEDIATE_BUFFER.m_hAdapter` seen in captured
> packets is a DIFFERENT kernel pointer (observed: list handle `0xFFFFAD8A2B30B010` vs captured
> `0xFFFFAD8A2B30B2D0` on the same adapter). Passing the captured `m_hAdapter` as the request handle
> makes `SendPacketToAdapter`/`SendPacketToMstcp` fail with `ERROR_INVALID_PARAMETER` (87) on every
> packet — capture works and tunnel mode applies, but the runtime shuts down fail-closed. Fix:
> `NdisCapturePump` stamps captured packets from the enumeration handle via
> `NdisCapturedPacket.FromCapture(_batchBuffers[index], _adapterHandle)`, which also carries the NDIS
> metadata `Flags` that `NdisPacketBuffer.DeviceFlags` does not. Scratch harness `sendtest` read and
> reinjected captured buffers with the enumeration handle: 97/97 OK both directions; product
> re-verified 100/100 ICMP pass-through, 0 % loss, no duplicates.

- A captured packet carries two distinct flag values: `NdisPacketBuffer.DeviceFlags` selects
  MSTCP-relative direction, while `INTERMEDIATE_BUFFER.m_Flags` is NDIS packet metadata. Preserve both
  through the managed capture record and ordinary pass reinjection; a fresh synthetic frame
  intentionally starts with metadata flags zero.
- **Native-call gate topology (superseded 2026-08-28, task 08-28-udp-loss-design-flaws D3)**:
  `NdisApiDriver` splits serialization into a **control gate** (open/close/enumeration/mode
  snapshot+set+restore — cold path) and a **per-adapter-handle gate map** (`NdisAdapterGateMap` in
  `NdisNativeCallGate.cs`; since 2026-08-30, task 08-30-batched-ioctls D4, a
  `ConcurrentDictionary.GetOrAdd` — no per-call lock; the map only grows, bounded by the adapter
  count, and a racing `GetOrAdd` may construct a discarded gate at most once per handle, which is
  harmless for these lazily-registered passive objects) used by `TryReadPackets`,
  `SendPacketToMstcp`, `SendPacketToAdapter`, keyed by the enumeration handle the request carries.
  Within one adapter handle every native call stays serialized (preserves per-adapter
  read/reinject ordering and the historical OVERLAPPED concern — each request struct is
  method-local); **across adapters calls proceed in parallel**, so a slow IOCTL on adapter A can no
  longer stall adapter B's pump reads. Lock order is fixed: the map is never held across
  `gate.Enter()`, and the control gate is never nested inside an adapter gate or vice versa. This
  replaces the earlier "serialize every operation on one driver instance" contract: that
  single-Monitor design coupled all adapters through one lock and was a confirmed
  driver-queue-overflow (silent loss) amplifier under multi-adapter/high-pps load. Gate contention
  telemetry (`MaxConcurrentCalls`) is preserved per gate. Rollback shape if driver-level coupling
  ever shows up on hardware: a single send-gate + per-adapter read-gates.
- This matches the official samples: `ETH_M_REQUEST.hAdapterHandle` is set once from the adapter list
  and reused for read/write requests.
- All 14 NDISAPI `[LibraryImport]` declarations in `NdisApiAbi.cs` use `SetLastError = true`;
  send-path exceptions must include `Marshal.GetLastWin32Error()` — driver-side rejections are
  otherwise undiagnosable. Diagnostics context worth logging on send failure: native error, frame
  length, device flags, adapter handle.

---

## The shared request ABI

- **ETH_M_REQUEST** (x64, `Pack=1`): `hAdapterHandle(8) + dwPacketsNumber(4,in) +
  dwPacketsSuccess(4,out) + NDISRD_ETH_Packet[N]` (one `INTERMEDIATE_BUFFER*` each, 8 bytes). The
  fixed header is **16 bytes**, so a request for N buffers spans `16 + 8N`
  (`NdisApiDriver.MultiRequestByteCount(N)`); caller fills handle/count/buffer pointers, the driver
  fills `dwPacketsSuccess` and each buffer's contents on success. The managed mirror is
  `EthernetMultiRequest` (`NdisApiAbi.cs`): `sizeof` is 24 because it carries `AdapterHandle`,
  `PacketsNumber`, `PacketsSuccess` and `FirstBuffer`, which aliases `EthPacket[0]`. `NdisApiAbi.AssertManagedLayout()`
  runs at `NdisApiDriver.Open()` and pins size 24 plus offsets 0/8/12/16 (and the other mirrored
  structs) under a 64-bit guard, so a packing mistake fails at startup instead of on the wire.
- Exports verified against the pinned commit `417b8734` (`ndisapi.vs2012/ndisapi.def`,
  `include/ndisapi.h:300-302`): `ReadPackets`, `SendPacketsToMstcp`, `SendPacketsToAdapter`, all
  `BOOL __stdcall (HANDLE, PETH_M_REQUEST)`.

---

## Native DLL resolution (fixed 2026-08-12)

- `NdisApiNative` (in `NdisApiAbi.cs`) registers a `NativeLibrary.SetDllImportResolver` in its static
  constructor that loads `ndisapi.dll` only from `AppContext.BaseDirectory` — matching the README
  claim; there is no PATH/current-directory search. Other library names return zero and use default
  resolution. AOT-safe (no reflection).

---

## Where things moved

The 2026-10-09 revision split this document into a hub plus three children. Old section names (and
the numbered citations frozen in `benchmarks/results/**`) resolve as follows:

| Old section (and old numbers) | Now in |
|---|---|
| Adapter Identity Contract (§1–§7) | this file, [Adapter Identity Contract](#adapter-identity-contract) |
| Adapter Handles: Enumeration vs Captured | this file, [Adapter Handles: Enumeration vs Captured](#adapter-handles-enumeration-vs-captured-hardware-verified-2026-08-07) |
| The `[LibraryImport]`/`GetLastWin32Error()` rules | this file, same section |
| Native DLL resolution | this file, [Native DLL resolution](#native-dll-resolution-fixed-2026-08-12) |
| The ETH_M_REQUEST ABI bullet (was the read doc's §3) | this file, [The shared request ABI](#the-shared-request-abi) |
| Adapter list change refresh — layered capture generations (§1–§5) | [ndis-capture-refresh.md](./ndis-capture-refresh.md) |
| Adapter-view self-healing — address fingerprints, periodic re-enumeration, health-triggered forced refresh (§1–§4) | [ndis-capture-refresh.md](./ndis-capture-refresh.md) |
| Batched capture reads and buffer pooling (§1–§7) | [ndis-batched-capture.md](./ndis-batched-capture.md) |
| Batched reinjection sends (§1–§6) | [ndis-batched-send.md](./ndis-batched-send.md) |
| Redirect deferred-injection lanes (was §3.1) | [ndis-batched-send.md](./ndis-batched-send.md#redirect-deferred-injection-lanes) |
| Pump performance note (batch capacity, arrival-signal idle wait, `HighResolutionTimerScope`) | [ndis-batched-capture.md](./ndis-batched-capture.md#pump-batching-and-idle-pacing) |

The frozen dated citations that name this file for *adapter identity* — e.g.
`benchmarks/results/2026-09-30-flow-key-parse-once/README.md:179` — point at the table above; the
facts they name all still exist.
