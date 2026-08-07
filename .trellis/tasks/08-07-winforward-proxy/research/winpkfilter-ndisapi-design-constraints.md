# WinpkFilter / NDISAPI design constraints for WinForward

Research date: 2026-08-07

Scope: packet direction, adapter identity/enumeration, process ownership lookup, loop prevention, and C# Native AOT interop. This document records constraints and evidence; it intentionally does not select behavior for the user-owned open questions in the PRD.

## Source baseline

- Official WinpkFilter user-mode library: `wiresock/ndisapi`, commit [`417b8734e844083a10236387fba705d94a2d6bc9`](https://github.com/wiresock/ndisapi/tree/417b8734e844083a10236387fba705d94a2d6bc9).
- Official API reference: [Windows Packet Filter 3.4 Developer Reference](https://www.ntkernel.com/docs/windows-packet-filter-documentation/).
- Microsoft Windows and .NET documentation is cited inline.
- Additional implementation evidence (not treated as API authority):
  - ProxiFyre-derived repository `hc990275/SOCKS5`, commit [`dd1512840e1e3bc596b06b80eda4e2dcd6a9c9ed`](https://github.com/hc990275/SOCKS5/tree/dd1512840e1e3bc596b06b80eda4e2dcd6a9c9ed).
  - WinTProxy repository, commit [`19c22554be53f789a8740c56327d4e947948a6d9`](https://github.com/NukaColaM/WinTProxy/tree/19c22554be53f789a8740c56327d4e947948a6d9).

## 1. Packet direction is relative to MSTCP and a specific adapter

### API facts

The WinpkFilter direction model is not an abstract application-level "ingress/egress" flag. It describes traversal between the Microsoft TCP/IP stack (MSTCP) and a particular NDIS interface:

- `PACKET_FLAG_ON_SEND`: packet goes **MSTCP -> network interface**.
- `PACKET_FLAG_ON_RECEIVE`: packet goes **network interface -> MSTCP**.

The official `ReadPackets` reference defines those exact meanings for `INTERMEDIATE_BUFFER.m_dwDeviceFlags`: [ReadPackets](https://www.ntkernel.com/docs/windows-packet-filter-documentation/ndisapi-c-2/readpackets/). The constants are also declared in the official shared header: [`include/Common.h#L100-L119`](https://github.com/wiresock/ndisapi/blob/417b8734e844083a10236387fba705d94a2d6bc9/include/Common.h#L100-L119).

Tunnel/listen modes determine whether a packet is only copied or actually diverted:

- `MSTCP_FLAG_SENT_TUNNEL`: queue packets sent from MSTCP to the interface and drop the original.
- `MSTCP_FLAG_RECV_TUNNEL`: queue packets indicated by the interface to MSTCP and drop the original.
- `*_LISTEN`: queue a copy but let the original continue.

Evidence: [SetAdapterMode](https://www.ntkernel.com/docs/windows-packet-filter-documentation/ndisapi-c-2/setadaptermode/). A transparent proxy that must modify, suppress, or redirect packets requires tunnel semantics on the directions it owns. Listen mode cannot enforce a one-copy outcome because the original continues independently.

The reinjection side is also explicit:

- `SendPacketsToAdapter`: inject Ethernet frames toward a selected interface. The `ETH_M_REQUEST.hAdapterHandle` selects that interface. [Official reference](https://www.ntkernel.com/docs/windows-packet-filter-documentation/ndisapi-c-2/sendpacketstoadapter/).
- `SendPacketsToMstcp`: simulate receive from a selected interface upward into MSTCP. Its `hAdapterHandle` is documented as the interface "from which you would like to simulate receive." [Official reference](https://www.ntkernel.com/docs/windows-packet-filter-documentation/ndisapi-c-2/sendpacketstomstcp/).

The official `simple_packet_filter` demonstrates the canonical pass/revert matrix:

| Captured direction | Pass/original direction | Revert/opposite direction |
|---|---|---|
| `PACKET_FLAG_ON_SEND` | send to adapter | send to MSTCP |
| `PACKET_FLAG_ON_RECEIVE` | send to MSTCP | send to adapter |

Evidence: [`simple_packet_filter.h#L320-L392`](https://github.com/wiresock/ndisapi/blob/417b8734e844083a10236387fba705d94a2d6bc9/examples/cpp/common/ndisapi/simple_packet_filter.h#L320-L392). The sample establishes `MSTCP_FLAG_SENT_TUNNEL | MSTCP_FLAG_RECV_TUNNEL` before processing: [`simple_packet_filter.h#L193-L218`](https://github.com/wiresock/ndisapi/blob/417b8734e844083a10236387fba705d94a2d6bc9/examples/cpp/common/ndisapi/simple_packet_filter.h#L193-L218).

### Implications for host-originated versus forwarded traffic

- A packet observed `ON_SEND` on an external adapter is normally leaving the host stack for that adapter, but that alone does not prove a user process originally created it. Windows forwarding/routing can also emit a previously received packet through another adapter.
- A packet received from a Hyper-V virtual adapter is `ON_RECEIVE` relative to that adapter as it moves toward MSTCP/Windows routing. If Windows then forwards it out a physical adapter, it can later be observed `ON_SEND` there. Thus one forwarded flow may be visible at multiple adapter boundaries.
- An adapter rule therefore needs an explicit semantic distinction such as "packet boundary observed on this adapter and in which direction" versus "logical ingress/egress path of a forwarded flow." The driver supplies adapter handle plus MSTCP-relative direction, not a guest process identity or a ready-made end-to-end forwarding provenance object.
- Enabling tunnel mode on multiple adapters creates a duplicate-classification/double-interception risk for routed traffic unless flow state records the intended boundary and processing stage. Reinjection must preserve the appropriate adapter association; unsorted APIs explicitly store the associated/target adapter in `INTERMEDIATE_BUFFER.m_hAdapter` ([implementation documentation](https://github.com/wiresock/ndisapi/blob/417b8734e844083a10236387fba705d94a2d6bc9/ndisapi/ndisapi.cpp#L2286-L2375)).

### Loopback is a separate flag concern

By default loopback packets bypass helper-driver processing. `MSTCP_FLAG_LOOPBACK_FILTER` opts them into processing; `MSTCP_FLAG_LOOPBACK_BLOCK` drops them (apart from the documented broadcast/multicast exception). See [SetAdapterMode](https://www.ntkernel.com/docs/windows-packet-filter-documentation/ndisapi-c-2/setadaptermode/) and [`Common.h#L111-L119`](https://github.com/wiresock/ndisapi/blob/417b8734e844083a10236387fba705d94a2d6bc9/include/Common.h#L111-L119). A localhost transparent relay design must therefore test actual loopback behavior rather than assuming packets will enter the NDISAPI path.

## 2. Adapter enumeration gives a runtime handle and internal name, not one complete durable identity

### What NDISAPI returns

`GetTcpipBoundAdaptersInfo` fills `TCP_AdapterList` with **MSTCP-associated** interfaces: internal name, handle, medium, MAC, and MTU. The handle is required for adapter-associated calls. Evidence:

- [GetTcpipBoundAdaptersInfo](https://www.ntkernel.com/docs/windows-packet-filter-documentation/ndisapi-c-2/gettcpipboundadaptersinfo/)
- [`TCP_AdapterList` reference](https://www.ntkernel.com/docs/windows-packet-filter-documentation/structures/_tcp_adapterlist/)
- Official structure, including fixed maximum of 32 names/handles and packed ABI: [`include/Common.h#L69-L159`](https://github.com/wiresock/ndisapi/blob/417b8734e844083a10236387fba705d94a2d6bc9/include/Common.h#L69-L159).

The NDISAPI handle is an opaque, runtime adapter-operation key. It should not be persisted as a stable identifier. The official C++ wrapper constructs each adapter with all three separate values—handle, internal name, and friendly name—and describes the internal name as "typically GUID": [`network_adapter.h#L118-L151`](https://github.com/wiresock/ndisapi/blob/417b8734e844083a10236387fba705d94a2d6bc9/examples/cpp/common/ndisapi/network_adapter.h#L118-L151).

`ConvertWindows2000AdapterName` translates the internal name to a user-facing connection name by looking under the adapter's registry connection key; it is not itself an identifier service. Official implementation: [`ndisapi.cpp#L2980-L3068`](https://github.com/wiresock/ndisapi/blob/417b8734e844083a10236387fba705d94a2d6bc9/ndisapi/ndisapi.cpp#L2980-L3068). The official list-adapters sample displays both converted/friendly name and internal name: [`ListAdapters/Program.cs#L21-L56`](https://github.com/wiresock/ndisapi/blob/417b8734e844083a10236387fba705d94a2d6bc9/examples/legacy/CSharp/ListAdapters/Program.cs#L21-L56).

`SetAdapterListChangeEvent` signals plug/unplug, enable/disable, and related bound-list changes; official guidance is to call `GetTcpipBoundAdaptersInfo` again and renew adapter-associated structures: [SetAdapterListChangeEvent](https://www.ntkernel.com/docs/windows-packet-filter-documentation/ndisapi-c-2/setadapterlistchangeevent/). This reinforces that handles and the current bound list are runtime state.

### Correlating to Windows stable/friendly fields

For a discovery command and config validation, NDISAPI enumeration can be correlated with Windows IP Helper enumeration:

- `IP_ADAPTER_ADDRESSES.AdapterName` is documented as permanent and not user-modifiable, whereas `FriendlyName` is the human-readable alias. The same structure exposes `Luid`, interface indexes, physical address, and other properties. [Microsoft `IP_ADAPTER_ADDRESSES`](https://learn.microsoft.com/en-us/windows/win32/api/iptypes/ns-iptypes-ip_adapter_addresses_lh).
- Microsoft explicitly warns that `IfIndex`/`Ipv6IfIndex` can change after disable/enable and must not be considered persistent. The permanent `AdapterName` or interface GUID is preferable as a stable configured identifier.
- `ConvertInterfaceLuidToGuid` converts an interface LUID to its GUID, for both IPv4 and IPv6 interfaces: [Microsoft reference](https://learn.microsoft.com/en-us/windows/win32/api/netioapi/nf-netioapi-convertinterfaceluidtoguid).
- The official NDISAPI helper correlates IP Helper data and records `InterfaceGuid`, LUID, IP Helper adapter name, and friendly name separately: [`network_adapter_info.h#L271-L347`](https://github.com/wiresock/ndisapi/blob/417b8734e844083a10236387fba705d94a2d6bc9/examples/cpp/common/iphelper/network_adapter_info.h#L271-L347).

Practical correlation should use exact normalized identity values (internal name/GUID/LUID and, where needed, MAC/medium as a sanity check), not list position. `GetAdaptersAddresses` can include all NDIS interfaces with `GAA_FLAG_INCLUDE_ALL_INTERFACES`: [Microsoft reference](https://learn.microsoft.com/en-us/windows/win32/api/iphlpapi/nf-iphlpapi-getadaptersaddresses).

### Hyper-V implications and unresolved semantics

Hyper-V virtual NICs that are MSTCP-bound should appear through `GetTcpipBoundAdaptersInfo` just like other bound NDIS interfaces. However, this must be verified on the supported Windows/driver matrix; the API promises MSTCP-associated interfaces, not every object visible in every Windows networking UI.

The PRD's user-owned choices remain unresolved and are not decided here:

- When both a stable ID and exact friendly name appear in one rule, whether both are required, one is fallback/diagnostic, or ambiguity is a validation error.
- Whether adapter matching means `ON_RECEIVE`, `ON_SEND`, both boundaries, or a tracked logical ingress/egress path.
- What to do when exact-name matching finds zero or multiple adapters (duplicate friendly aliases are technically possible).

## 3. NDISAPI packets do not carry PID; ownership lookup is a racy IP Helper correlation

`INTERMEDIATE_BUFFER` contains adapter association, direction, lengths/flags, filter ID, and the Ethernet frame; no PID/process token is present. See [`include/Common.h#L161-L199`](https://github.com/wiresock/ndisapi/blob/417b8734e844083a10236387fba705d94a2d6bc9/include/Common.h#L161-L199).

The official WinpkFilter `socksify` example derives process identity separately with IP Helper flow tables, using the packet's TCP tuple. On a miss it refreshes the table and retries: [`socksify.cpp#L31-L92`](https://github.com/wiresock/ndisapi/blob/417b8734e844083a10236387fba705d94a2d6bc9/examples/cpp/socksify/socksify.cpp#L31-L92). Its shared process helper:

- maps TCP by a 4-tuple/session;
- maps UDP by local endpoint and also tries wildcard-address endpoint;
- snapshots `GetExtendedTcpTable` and `GetExtendedUdpTable` for IPv4/IPv6.

Evidence: [`process_lookup.h#L32-L235`](https://github.com/wiresock/ndisapi/blob/417b8734e844083a10236387fba705d94a2d6bc9/examples/cpp/common/iphelper/process_lookup.h#L32-L235), [`process_lookup.h#L340-L481`](https://github.com/wiresock/ndisapi/blob/417b8734e844083a10236387fba705d94a2d6bc9/examples/cpp/common/iphelper/process_lookup.h#L340-L481).

Microsoft documents:

- `GetExtendedTcpTable` can return owner-PID or owner-module TCP tables for IPv4 and IPv6: [reference](https://learn.microsoft.com/en-us/windows/win32/api/iphlpapi/nf-iphlpapi-getextendedtcptable).
- `GetExtendedUdpTable` owner tables contain only local address/scope/port identity (UDP is connectionless), for IPv4 and IPv6: [reference](https://learn.microsoft.com/en-us/windows/win32/api/iphlpapi/nf-iphlpapi-getextendedudptable).
- Owner-module resolution can return a process, service, or component name; protected processes may return empty name/path unless the caller is properly elevated. TCP evidence: [GetOwnerModuleFromTcpEntry](https://learn.microsoft.com/en-us/windows/win32/api/iphlpapi/nf-iphlpapi-getownermodulefromtcpentry); UDP evidence: [GetOwnerModuleFromUdpEntry](https://learn.microsoft.com/en-us/windows/win32/api/iphlpapi/nf-iphlpapi-getownermodulefromudpentry).

### Consequences

- **Timing race:** a SYN or first UDP datagram can reach NDISAPI before the endpoint appears in an independently sampled owner table. Refresh-and-retry reduces, but cannot eliminate, this race.
- **TCP precision:** the full local/remote tuple provides much better disambiguation after the connection row exists.
- **UDP ambiguity:** Windows owner-table identity is local-endpoint based. Wildcard binds, shared/reused ports, socket handoff, and short-lived endpoints can make per-datagram ownership ambiguous. It cannot prove a remote peer for general unconnected UDP from the table alone.
- **Forwarded traffic:** packets received from a VM/external adapter have no local owning socket/PID merely because Windows routes them. Process matching must be treated as unavailable for such forwarded traffic rather than assigned to a system routing process.
- **Failures need policy:** owner lookup can miss or yield `SYSTEM`, service/component identities, inaccessible paths, or a process that exits between table enumeration and image lookup. Rule evaluation needs a defined "unknown owner" behavior; it should not guess.

### Process-name/path cache safety

A PID alone is not a durable process identity because Windows reuses PIDs. A cache keyed only by PID plus a TTL can associate a new process with a stale executable. The cache key/validation should include process creation time, obtainable with `GetProcessTimes`, and invalidate when the process cannot be opened or creation time changes. Microsoft documents that `GetProcessTimes` returns the process creation time and requires query rights: [reference](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-getprocesstimes).

Executable paths can be resolved with `QueryFullProcessImageNameW` after opening the process with `PROCESS_QUERY_LIMITED_INFORMATION`; access can still fail. [Microsoft reference](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-queryfullprocessimagenamew). A process snapshot is useful for a bare executable name but does not replace a creation-time check for cache correctness or reliably provide a full path.

## 4. Loop prevention must exist below user rules and cover both TCP control and UDP relay traffic

WinForward's own connection to a SOCKS server traverses MSTCP and the same adapters as ordinary traffic. A broad/catch-all proxy rule can therefore proxy its own SOCKS connection recursively.

### Evidence from current NDISAPI-based proxy implementations

The ProxiFyre-derived `SOCKS` implementation uses two independent protections:

1. It automatically excludes its current process by PID during process matching: [`socks_local_router.h#L1773-L1795`](https://github.com/hc990275/SOCKS5/blob/dd1512840e1e3bc596b06b80eda4e2dcd6a9c9ed/netlib/src/proxy/socks_local_router.h#L1773-L1795).
2. It installs high-priority/static `PASS` filters for upstream SOCKS endpoints before broad redirection. TCP is exempted by upstream address+configured SOCKS port in both directions. UDP is exempted by proxy-host address because `UDP ASSOCIATE` may return a relay port unrelated to the configured SOCKS port: [`socks_local_router.h#L1421-L1519`](https://github.com/hc990275/SOCKS5/blob/dd1512840e1e3bc596b06b80eda4e2dcd6a9c9ed/netlib/src/proxy/socks_local_router.h#L1421-L1519).

WinTProxy similarly classifies its configured proxy endpoint, local relay ports, and DNS relay as self traffic before normal policy processing: [`src/path/classify.c#L64-L88`](https://github.com/NukaColaM/WinTProxy/blob/19c22554be53f789a8740c56327d4e947948a6d9/src/path/classify.c#L64-L88).

### Constraints for a robust design

- Safety exemptions must be evaluated before user-configurable proxy rules and cannot be overridable by a catch-all proxy rule.
- Process/PID self-exclusion is helpful for host-originated sockets but is insufficient as the only guard: ownership lookup is asynchronous/racy, PID/path resolution can fail, helpers might run in another process in a future architecture, and a local SOCKS server may share an endpoint with unrelated traffic.
- Endpoint-based pass filters should cover both directions. TCP control/CONNECT traffic can match proxy endpoint+port. UDP relay traffic cannot generally be pinned to the configured SOCKS TCP port because the `UDP ASSOCIATE` response supplies a potentially different relay address/port; the dynamically returned relay endpoint must be registered, or a deliberately broader proxy-host UDP exemption used with acknowledged bypass scope.
- With multiple named SOCKS servers, exclusions must be updated atomically with active server resolution/association state. Hostnames can resolve to several/changeable IPv4/IPv6 addresses; stale or missing exclusions can create loops, while an overbroad address-only exemption can allow unrelated direct traffic to the same host.
- Local transparent relay ports and generated return traffic also need invariant classification/flow markers so redirected traffic is not treated as a new client flow.
- Driver static `PASS` filters reduce user-mode load and recursion exposure, but filter ordering and lifecycle must be transactional: install safety passes before enabling broad redirect/tunnel processing, and roll them back cleanly on startup failure.

No product decision is made here about whether endpoint exemptions, current-process exclusion, connection-state marking, or a combination is the normative mechanism. The evidence supports defense in depth rather than relying on only one signal.

## 5. C# Native AOT interop: use the native C ABI, not the C++/CLI wrapper

### Supported boundary

The official NDISAPI README identifies:

- `ndisapi.dll`: native Win32 DLL wrapper, supplied for x86/x64/ARM64;
- `ndisapi.lib`: native static wrapper;
- `ndisapi.net`: C++/CLI mixed class library.

Evidence: [`README.md#library-components`](https://github.com/wiresock/ndisapi/blob/417b8734e844083a10236387fba705d94a2d6bc9/README.md#library-components). The official API docs say the C-style exported interface exists for C#, Delphi, and other callers: [C interface overview](https://www.ntkernel.com/docs/windows-packet-filter-documentation/ndisapi-c-2/).

.NET Native AOT explicitly does **not** support C++/CLI, and publishes architecture-specific self-contained native applications. [Microsoft Native AOT deployment](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/). Therefore direct use of `ndisapi.net` is incompatible with the Native AOT goal; the viable existing boundary is the exported C ABI in `ndisapi.dll`, or a purpose-built plain C shim/static-link integration evaluated separately.

Official exports use `__stdcall`, including `OpenFilterDriver`, `CloseFilterDriver`, and packet APIs: [`include/ndisapi.h#L290-L339`](https://github.com/wiresock/ndisapi/blob/417b8734e844083a10236387fba705d94a2d6bc9/include/ndisapi.h#L290-L339). The exact ABI for every chosen target architecture must be validated against the corresponding official DLL/header/driver release.

### Source-generated P/Invoke and AOT

Microsoft recommends `[LibraryImport]` on .NET 7+ and explains that source-generated marshalling is compiled ahead of time, unlike runtime-generated IL stubs. [P/Invoke source generation](https://learn.microsoft.com/en-us/dotnet/standard/native-interop/pinvoke-source-generation); [interop best practices](https://learn.microsoft.com/en-us/dotnet/standard/native-interop/best-practices).

For NDISAPI this implies:

- declare exact `EntryPoint` names;
- specify stdcall via `[UnmanagedCallConv(CallConvs = new[] { typeof(CallConvStdcall) })]` where required;
- use `nint`/`IntPtr` for `HANDLE`, `uint` for `DWORD`/`BOOL` returns as appropriate, and avoid treating native `BOOL` as a 1-byte C++ `bool`;
- prefer blittable unsafe structs/pointers and fixed buffers over managed arrays embedded with `[MarshalAs(ByValArray)]` in the hot path;
- use a `SafeHandle`-style owner for the `OpenFilterDriver` object and deterministic `Dispose`/shutdown ordering.

The official legacy C# wrapper is valuable as a layout reference, not as an AOT-ready implementation. It uses `[DllImport]`, managed by-value arrays, `MarshalAs`, and `Pack=1`: [`examples/legacy/CSharp/ndisapi.cs#L90-L221`](https://github.com/wiresock/ndisapi/blob/417b8734e844083a10236387fba705d94a2d6bc9/examples/legacy/CSharp/ndisapi.cs#L90-L221) and [`#L275-L385`](https://github.com/wiresock/ndisapi/blob/417b8734e844083a10236387fba705d94a2d6bc9/examples/legacy/CSharp/ndisapi.cs#L275-L385). Source-generated marshalling does not support every legacy `MarshalAs` pattern; unsupported forms fail at compile time. Manual/blittable layout is safer for these large packed packet buffers.

### Layout and architecture hazards

- Native headers use `#pragma pack(push,1)` for shared structures: [`Common.h#L121-L159`](https://github.com/wiresock/ndisapi/blob/417b8734e844083a10236387fba705d94a2d6bc9/include/Common.h#L121-L159). Managed definitions must reproduce packing, unions, fixed arrays, and pointer-sized handles exactly.
- `TCP_AdapterList` and `INTERMEDIATE_BUFFER` change size with pointer width due to `HANDLE`/`LIST_ENTRY`; do not hardcode one x64 size for ARM64/x86.
- Build-time ABI tests should compare managed `sizeof` and field offsets against a tiny native program compiled from the exact pinned NDISAPI headers for every RID.
- NDISAPI has special WOW64 handle-conversion paths. Official unsorted read/send functions are explicitly unavailable in WOW64 mode: [`ndisapi.cpp#L2286-L2375`](https://github.com/wiresock/ndisapi/blob/417b8734e844083a10236387fba705d94a2d6bc9/ndisapi/ndisapi.cpp#L2286-L2375). Native AOT should publish architecture-matched binaries and DLLs rather than depend on 32-bit-on-64-bit behavior.
- `INTERMEDIATE_BUFFER` defaults to `MAX_ETHER_FRAME=1514` unless the native library/header was built with `JUMBO_FRAME_SUPPORTED`, which changes it to 9014: [`Common.h#L75-L81`](https://github.com/wiresock/ndisapi/blob/417b8734e844083a10236387fba705d94a2d6bc9/include/Common.h#L75-L81). This is an ABI compatibility issue, not only a packet-size feature.
- `ndisapi.dll` and `ndisrd.sys` compatibility must be checked as a matched native component set. Startup diagnostics should distinguish missing DLL, missing export/ABI, inability to open/load driver, and driver-version incompatibility.

### Native library loading and deployment

Native AOT's single executable does not automatically absorb an arbitrary dynamic `ndisapi.dll` referenced by P/Invoke. Unless static linking or a custom extraction mechanism is deliberately designed, the matching native DLL remains a deployed sidecar dependency. Microsoft documents P/Invoke library-name resolution and custom `NativeLibrary.SetDllImportResolver`: [native library loading](https://learn.microsoft.com/en-us/dotnet/standard/native-interop/native-library-loading).

Use a controlled resolver/search path and verify architecture/version before opening the driver; do not rely on an uncontrolled current-directory/PATH DLL search. This is both a diagnosability and DLL preloading concern.

## 6. Verification items and unresolved risks

The following require Windows-host experiments or a user/product decision; documentation alone does not close them:

1. **Adapter direction semantics (user-owned):** decide what a rule with an adapter field means for `ON_RECEIVE`, `ON_SEND`, forwarded flows observed twice, and both identifier+name fields.
2. **Supported matrix:** test the exact Windows versions, CPU architectures, `ndisrd.sys`, and `ndisapi.dll` builds selected for release. Confirm x64 and/or ARM64 exports and structure ABI.
3. **Hyper-V enumeration:** verify external/internal/private vSwitch adapters, dynamically recreated vNICs, names, internal GUIDs, and list-change behavior on a real host.
4. **Forwarding pipeline:** capture a VM flow across virtual and physical adapters to document which direction/adapter observations occur and whether reinjection causes a second observation.
5. **Process attribution SLO:** measure first-packet TCP misses, short-lived UDP ambiguity, IPv6 tables, protected/service processes, wildcard binds, port reuse, and PID reuse. Define behavior for unknown owner.
6. **Loop safety:** integration-test catch-all proxying with remote/local, IPv4/IPv6, TCP CONNECT, UDP ASSOCIATE whose relay port differs, DNS-changing proxy hostnames, and multiple proxies.
7. **Native DLL packaging:** decide whether the Native AOT executable ships with a sidecar DLL, statically links a reviewed native library/shim, or uses another supported package. Confirm licensing and update strategy separately.
8. **Jumbo-frame ABI:** pin whether the native DLL uses 1514- or 9014-byte `INTERMEDIATE_BUFFER` and test MTU/fragment behavior.
9. **Lifecycle/failure mode (user-owned):** decide fail-open/fail-closed policy. Regardless of selection, ensure modes/events/filter tables are restored transactionally on partial startup and shutdown.

