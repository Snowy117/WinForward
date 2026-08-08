# Windows NDISAPI Adapter Identity Contract

> How WinForward correlates NDISAPI runtime adapters with stable Windows adapter identities. Verified on a real Windows 11 host with WinpkFilter 3.6.2.1 during the 08-07-winforward-proxy check phase.

---

## 1. Scope / Trigger

- Trigger: any code that enumerates adapters (`adapters` CLI command), resolves `adapterId`/`adapterName` config selectors, or maps an `INTERMEDIATE_BUFFER` adapter handle back to a configured adapter.
- This is an infra integration contract: NDISAPI (kernel driver handles) and Windows IP Helper (`NetworkInterface`) are two separate enumeration planes that must be joined correctly.

## 2. Signatures

- `WindowsAdapterInventory(Func<IReadOnlyList<(string InternalName, nint Handle, byte[] Mac, ushort Mtu)>> ndisAdapters, Func<IReadOnlyList<IpAdapterInfo>>? ipAdapters = null)` — the second provider is injectable so correlation is unit-testable without Windows hardware.
- `IReadOnlyList<WindowsAdapter> GetCurrentAdapters()` — `WindowsAdapter(StableId, FriendlyName, InternalName, RuntimeHandle, Generation)`.
- `IpAdapterInfo(string Id, string Name, byte[] Mac)` — projection of `NetworkInterface.Id` / `.Name` / `.GetPhysicalAddress()`.

## 3. Contracts

- NDISAPI `GetTcpipBoundAdaptersInfo` internal name is typically `\DEVICE\{GUID}`; stripping the `\DEVICE\` prefix yields the adapter's NetCfgInstanceId, which equals `NetworkInterface.Id` (`{GUID}`, same casing on observed systems — compare case-insensitively and brace-insensitively via `Guid.TryParse` normalization).
- **Correlation is GUID-primary.** MAC is only a sanity-check fallback when the internal name is not a GUID.
- `RuntimeHandle` is process-lifetime state: never persist it, never print it as identity, rebuild it on adapter-list change.
- Ambiguity rule: a correlation key must match **exactly one** IP Helper adapter; zero or multiple matches fall back (see matrix).

## 4. Validation & Error Matrix

| Condition | Result |
|---|---|
| GUID extracted from internal name, exactly one `NetworkInterface.Id` match | correlated (`StableId` = `Id`, `FriendlyName` = `Name`) |
| GUID match count 0 or >1 | try MAC fallback |
| MAC fallback matches exactly one | correlated |
| MAC fallback matches 0 or >1 | uncorrelated: `StableId`/`FriendlyName` fall back to the internal name (never guess) |
| Internal name not a GUID and MAC is all-zero (hidden/virtual adapters report `000000000000`) | uncorrelated fallback |

## 5. Good/Base/Bad Cases

- Good: `\DEVICE\{DD8CD9A1-...}` ↔ `Id = {DD8CD9A1-...}` → friendly name `Ethernet`. Verified for physical and hidden `Local Area Connection* N` adapters alike (the hidden ones have no MAC at all and only resolve via GUID).
- Base: adapter whose internal name is not a GUID → MAC fallback resolves it when the MAC is unique.
- Bad: MAC-only correlation. NDIS filter drivers (WFP Native/802.3 MAC Layer LWF, WinpkFilter's own NDIS LWF, Npcap, QoS Packet Scheduler) each clone the physical MAC, so one MAC appears on ~6 `NetworkInterface` entries and the ambiguity check always fails. **MAC-only correlation never works on a real Windows host.**

## 6. Tests Required

- Unit (hardware-independent, via injected `IpAdapterInfo` provider): GUID correlation success; brace/case-insensitive GUID compare; MAC fallback when internal name is not a GUID; bare-GUID internal name; duplicate-MAC interfaces must NOT corrupt GUID correlation; uncorrelated adapters fall back to internal name.
- Windows smoke: `WinForward.exe adapters` prints stable GUID + friendly name + internal name for every MSTCP-bound adapter (exit 0); without `ndisapi.dll` it exits 1 with an actionable diagnostic.

## 7. Wrong vs Correct

### Wrong

```csharp
// MAC-only correlation: always ambiguous once any NDIS filter driver
// (including WinpkFilter itself) clones the MAC onto filter interfaces.
var matches = ipAdapters.Where(ip => ip.Mac.SequenceEqual(adapter.Mac)).Take(2).ToArray();
var matched = matches.Length == 1 ? matches[0] : null; // always null on real hosts
```

### Correct

```csharp
// GUID primary (internal name GUID == NetworkInterface.Id), MAC as sanity fallback.
if (TryExtractGuid(adapter.InternalName, out var guid))
{
    var guidMatches = ipAdapters.Where(ip => TryExtractGuid(ip.Id, out var id) && EqualsOrdinalIgnoreCase(id, guid)).ToArray();
    if (guidMatches.Length == 1) return guidMatches[0];
}
// ... then MAC fallback, then internal-name fallback. See AdapterIdentity.cs.
```

**Related**: `.trellis/tasks/08-07-winforward-proxy/research/winpkfilter-ndisapi-design-constraints.md` (direction semantics, handle lifetime, IP Helper correlation evidence); `src/WinForward.Windows/AdapterIdentity.cs` (reference implementation).

---

## Adapter Handles: Enumeration vs Captured (hardware-verified 2026-08-07)

> **Warning**: `GetTcpipBoundAdaptersInfo` returns per-adapter handles that are the ONLY valid values for request-level `hAdapterHandle` fields. The `INTERMEDIATE_BUFFER.m_hAdapter` seen in captured packets is a DIFFERENT kernel pointer (observed: list handle `0xFFFFAD8A2B30B010` vs captured `0xFFFFAD8A2B30B2D0` on the same adapter). Passing the captured `m_hAdapter` as the request handle makes `SendPacketToAdapter`/`SendPacketToMstcp` fail with `ERROR_INVALID_PARAMETER` (87) on every packet.

- `NdisCapturePump` must stamp captured packets with the pump's enumeration handle (`NdisCapture.cs`), never with `NdisPacketBuffer.CapturedAdapterHandle`.
- This matches the official samples: `ETH_M_REQUEST.hAdapterHandle` is set once from the adapter list and reused for read/write requests.
- All NDISAPI `[LibraryImport]` declarations use `SetLastError = true`; send-path exceptions must include `Marshal.GetLastWin32Error()` — driver-side rejections are otherwise undiagnosable.
- Diagnostics context worth logging on send failure: native error, frame length, device flags, adapter handle.

**Symptom / Cause / Fix** (recorded as a verified bug class):

- Symptom: capture works, tunnel mode applies, but every pass reinjection fails with native error 87 and the runtime shuts down fail-closed.
- Cause: request built with the captured buffer's `m_hAdapter` instead of the enumeration handle.
- Fix: `NdisCapturedPacket(buffer, _adapterHandle, buffer.DeviceFlags)` in the pump.
- Verification: scratch harness `sendtest` (read -> reinject captured buffer with enumeration handle: 97/97 OK, both directions). Product re-verified: 100/100 ICMP pass-through with 0% loss and no duplicates.

**Performance note**: the current polling pump (`ReadPacket` + 1 ms delay) adds ~5-15 ms RTT under tunnel mode (observed avg 8 ms vs 0.6 ms direct). Event-driven reads (`SetPacketEvent` + `ReadPackets` batch, as in `simple_packet_filter`) are the documented upgrade path when throughput work starts.
