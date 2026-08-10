# Research: audit-findings-audit-ndisapi

- **Query**: Perform a read-only exhaustive audit of `src/WinForward.NdisApi` production files, their direct consumers, and existing tests. Examine x64 ABI/library imports/pack/size/marshalling, native last-error propagation, handle and capture-buffer ownership, enumeration versus adapter-handle distinctions, packet direction, and ABI safety. Distinguish confirmed code defects from Windows hardware-only gaps.
- **Scope**: mixed
- **Date**: 2026-08-10

## Findings

### Files Found

| File Path | Description |
|---|---|
| `src/WinForward.NdisApi/NdisApiAbi.cs` | Pinned v3.6.2 interop layout, native imports, native-library resolver, and driver SafeHandle. |
| `src/WinForward.NdisApi/NdisApiDriver.cs` | Open/enumerate/mode/read/send wrapper and unmanaged intermediate-buffer owner. |
| `src/WinForward.NdisApi/NdisCapture.cs` | Single-adapter polling capture pump and captured-packet lifetime hand-off. |
| `src/WinForward.Runtime/MultiAdapterCaptureLoop.cs` | Starts one pump per in-scope adapter against the same driver object. |
| `src/WinForward.Runtime/CapturePacketProcessor.cs` | Copies a native frame to managed ownership before the pump releases its buffer. |
| `src/WinForward.Runtime/NdisPacketActionExecutor.cs` | Rebuilds pass-path buffers and selects adapter versus MSTCP reinjection. |
| `src/WinForward.Runtime/NdisPacketReinjector.cs` | Direct driver-backed `IPacketReinjector`. |
| `src/WinForward.Runtime/NdisAdapterModeController.cs` | Snapshots, applies, and restores adapter modes with enumeration handles. |
| `src/WinForward.Runtime/TcpRedirectInjector.cs` | Direction-specific TCP reinjection buffer construction. |
| `src/WinForward.Runtime/UdpResponseReinjector.cs` | Direction-specific UDP response frame construction and reinjection. |
| `src/WinForward.Runtime/CaptureLifecycle.cs` | Mode rollback and capture-loop shutdown transaction. |
| `src/WinForward.Cli/Program.cs` | Owns the driver for adapter enumeration and the entire capture lifetime. |
| `tests/WinForward.Core.Tests/NdisApiAbiTests.cs` | Direct NDIS ABI and managed native-boundary regression tests, including the managed x64 layout assertion. |
| `tests/WinForward.Core.Tests/CapturePipelineTests.cs` | Pass direction/handle propagation tests through a fake reinjector. |
| `tests/WinForward.Core.Tests/TcpRedirectInjectorTests.cs` | TCP host/forwarded direction-flag tests. |
| `tests/WinForward.Core.Tests/UdpRelayTests.cs` | UDP response direction, origin-handle, and frame-cap tests. |
| `tests/WinForward.Core.Tests/CaptureLifecycleTests.cs` | Generic transactional rollback tests through fakes only. |

### Confirmed Code Defects

#### NDIS-1 — `Open()` accepts an opaque object even when the native driver failed to open

- **Anchors**: `src/WinForward.NdisApi/NdisApiDriver.cs:21`, `src/WinForward.NdisApi/NdisApiDriver.cs:23`, `src/WinForward.NdisApi/NdisApiAbi.cs:91`, `src/WinForward.NdisApi/NdisApiAbi.cs:136`.
- **Evidence**: managed `Open()` calls `OpenFilterDriver`, wraps the returned pointer in `SafeHandleZeroOrMinusOneIsInvalid`, and treats any nonzero pointer as success. The pinned native implementation calls `new CNdisApi(pszFileName)` unconditionally at [`ndisapi.cpp#L3333-L3335`](https://github.com/wiresock/ndisapi/blob/417b8734e844083a10236387fba705d94a2d6bc9/ndisapi/ndisapi.cpp#L3333-L3335). `CNdisApi` records a failed `CreateFile` internally instead of returning a null object; its `IsDriverLoaded()` result is the internal `m_bIsLoadSuccessfully` flag ([`ndisapi.cpp#L266-L301`](https://github.com/wiresock/ndisapi/blob/417b8734e844083a10236387fba705d94a2d6bc9/ndisapi/ndisapi.cpp#L266-L301), [`ndisapi.cpp#L2211-L2215`](https://github.com/wiresock/ndisapi/blob/417b8734e844083a10236387fba705d94a2d6bc9/ndisapi/ndisapi.cpp#L2211-L2215)). The C export exists ([`ndisapi.h#L334`](https://github.com/wiresock/ndisapi/blob/417b8734e844083a10236387fba705d94a2d6bc9/include/ndisapi.h#L334)), but the managed surface does not import or call it.
- **Effect**: a missing/stopped/unopenable NDISRD device produces a superficially valid `NdisApiDriver`; the first later operation, normally `GetAdapters()`, is the failure boundary instead of `Open()`. The original native `CreateFile` error is not the error reported by this managed open path.
- **Reproduction / gate**: on a Windows x64 test machine with the matching `ndisapi.dll`, stop or disable the `NDISRD` service/device, confirm `sc.exe query NDISRD` is not running, then run `WinForward.exe adapters`. Record whether `NdisApiDriver.Open()` succeeds and whether the subsequent enumeration error retains the original open error. This cannot be reproduced on this Linux host.
- **Coverage**: `tests/WinForward.Core.Tests/NdisApiAbiTests.cs:9` tests only managed layout; no test can simulate the native opaque-object/open-failure contract.

#### NDIS-2 — any `ReadPacket` failure is silently classified as an idle queue

- **Anchors**: `src/WinForward.NdisApi/NdisApiAbi.cs:152`, `src/WinForward.NdisApi/NdisApiDriver.cs:66`, `src/WinForward.NdisApi/NdisCapture.cs:30`.
- **Evidence**: `ReadPacket` is declared with `SetLastError = true`; the native API documents a `BOOL` result and `FALSE` denotes an unsuccessful call ([ReadPacket](https://www.ntkernel.com/docs/windows-packet-filter-documentation/c-api/readpacket/)). `TryReadPacket()` returns only that boolean and does not inspect its cached last error. Every `false` in `NdisCapturePump.RunAsync()` reaches `Task.Delay` and then retries, with no error path.
- **Effect**: a driver rejection such as an invalid/stale enumeration handle, an invalid request, or an unavailable driver is indistinguishable from an empty packet queue. The capture loop does not propagate the native failure to `MultiAdapterCaptureLoop` or the transactional mode-restoration path.
- **Reproduction / gate**: start capture on a Windows host, then disable/re-enable or remove an in-scope adapter so its enumeration handle becomes stale. Observe `ReadPacket` results and cached error with a debugger/ETW; the current managed path continuously takes `NdisCapture.cs:31-34` rather than faulting. Record the exact driver/DLL error for the release matrix.
- **Coverage**: there is no test for `NdisCapturePump`, failed read, stale adapter, or last-error handling; `NdisApiDriver` is sealed and invokes internal native imports directly.

#### NDIS-3 — multiple adapter pumps concurrently reuse one native `OVERLAPPED` object

- **Anchors**: `src/WinForward.Cli/Program.cs:152`, `src/WinForward.Cli/Program.cs:197`, `src/WinForward.Runtime/MultiAdapterCaptureLoop.cs:19`, `src/WinForward.Runtime/MultiAdapterCaptureLoop.cs:31`, `src/WinForward.NdisApi/NdisCapture.cs:10`.
- **Evidence**: the CLI creates one `NdisApiDriver` and passes it to one `MultiAdapterCaptureLoop`; the loop creates one `NdisCapturePump` per adapter and awaits all pump tasks concurrently. Each pump calls `NdisApiDriver.TryReadPacket()`. The pinned native `CNdisApi` opens the device with `FILE_FLAG_OVERLAPPED`, creates one member `m_ovlp`/event, and its default `DeviceIoControl` path always passes `&m_ovlp` ([`ndisapi.cpp#L266-L301`](https://github.com/wiresock/ndisapi/blob/417b8734e844083a10236387fba705d94a2d6bc9/ndisapi/ndisapi.cpp#L266-L301), [`ndisapi.cpp#L359-L379`](https://github.com/wiresock/ndisapi/blob/417b8734e844083a10236387fba705d94a2d6bc9/ndisapi/ndisapi.cpp#L359-L379)). Win32 documents that an overlapped operation needs a valid `OVERLAPPED` and that a structure must not be reused before its prior operation completes; separate structures/events are required for simultaneous requests ([DeviceIoControl](https://learn.microsoft.com/en-us/windows/win32/api/ioapiset/nf-ioapiset-deviceiocontrol), [OVERLAPPED](https://learn.microsoft.com/en-us/windows/win32/api/minwinbase/ns-minwinbase-overlapped)).
- **Effect**: a capture scope containing two or more adapters can issue simultaneous native IOCTLs through a single mutable `m_ovlp`, violating the underlying I/O contract. This includes read/read races and read/send or read/mode races, because all wrapper calls share the same native object.
- **Reproduction / gate**: select two active MSTCP-bound adapters on Windows, create sustained traffic on both, and use ETW/Process Monitor plus application logs to record concurrent `IOCTL_NDISRD_READ_PACKET` activity, completion/error codes, lost packets, or hangs. A single-adapter run does not exercise this defect.
- **Coverage**: no test instantiates `MultiAdapterCaptureLoop` or `NdisCapturePump`; `CaptureLifecycleTests` uses `FakeCapture` (`tests/WinForward.Core.Tests/CaptureLifecycleTests.cs:81`).

#### NDIS-4 — pass-through reinjection drops captured NDIS packet flags

- **Anchors**: `src/WinForward.NdisApi/NdisApiAbi.cs:64`, `src/WinForward.NdisApi/NdisApiDriver.cs:109`, `src/WinForward.NdisApi/NdisApiDriver.cs:127`, `src/WinForward.Runtime/CapturePacketProcessor.cs:30`, `src/WinForward.Runtime/NdisPacketActionExecutor.cs:39`.
- **Evidence**: `IntermediateBuffer` models native `m_Flags` as `Flags` at offset 24. A packet buffer is zero-allocated, and `SetFrame()` writes only the embedded adapter value, union padding, device flags, length, and bytes; it never writes `Flags`, so a rebuilt buffer has `m_Flags == 0`. The capture processor copies only `GetFrame()` bytes and `DeviceFlags` into managed flow metadata; the pass executor rebuilds a fresh buffer from exactly those values. The official `SendPacketToAdapter` contract says `INTERMEDIATE_BUFFER.m_Flags` should be initialized with the NDIS packet flags ([SendPacketToAdapter](https://www.ntkernel.com/docs/windows-packet-filter-documentation/c-api/sendpackettoadapter/)).
- **Effect**: every captured `ON_SEND` packet that takes the ordinary pass path reaches `SendPacketToAdapter` without the captured NDIS flags. This is a direct ABI contract violation independent of whether a particular adapter/driver visibly depends on those flags.
- **Reproduction / gate**: on Windows, capture an outgoing packet whose native `m_Flags` is nonzero, stop at `CapturePacketProcessor.ProcessAsync`, then stop at `NdisPacketActionExecutor.PassAsync` and inspect the outgoing `NdisPacketBuffer` in native memory. The captured `m_Flags` and reinjected buffer's zero flags demonstrate the loss. Functional impact requires an adapter/driver matrix observation.
- **Coverage**: `tests/WinForward.Core.Tests/CapturePipelineTests.cs:348` verifies frame bytes, enumeration handle, and `DeviceFlags`; it cannot inspect or assert native `IntermediateBuffer.Flags`.

#### NDIS-5 — resolver failure falls back to the default native-library search despite the app-directory-only claim

- **Anchors**: `src/WinForward.NdisApi/NdisApiAbi.cs:113`, `src/WinForward.NdisApi/NdisApiAbi.cs:121`, `src/WinForward.NdisApi/NdisApiAbi.cs:123`, `src/WinForward.NdisApi/NdisApiAbi.cs:125`.
- **Evidence**: `ResolveLibrary()` returns `nint.Zero` when `ndisapi.dll` is absent from `AppContext.BaseDirectory`; its comment states that this prevents PATH/current-directory search. .NET documents that a `DllImportResolver` returning `IntPtr.Zero` falls back to default resolution ([Native library loading](https://learn.microsoft.com/en-us/dotnet/standard/native-interop/native-library-loading)); the resolver is only the first resolution attempt ([SetDllImportResolver](https://learn.microsoft.com/en-us/dotnet/api/system.runtime.interopservices.nativelibrary.setdllimportresolver)).
- **Effect**: absence of the application-directory DLL does not enforce application-directory-only loading. The subsequent default resolver's probe behavior is platform and deployment-policy dependent, contradicting the managed comment and the NDIS spec statement at `.trellis/spec/backend/windows-ndisapi.md:162`.
- **Reproduction / gate**: on Windows, remove `ndisapi.dll` from the executable directory, make another copy reachable only through a default loader probe location, and run `WinForward.exe adapters` under Process Monitor filtered to `Load Image`. Record whether the alternate DLL is loaded and its resolved path; use the release package's actual loader policy.
- **Coverage**: no test exercises `NdisApiNative` resolver behavior, missing DLL behavior, or an alternate default probe path.

### Verified Contracts and Non-findings

- **x64 packed ABI matches the pinned non-jumbo header**: `NdisApiAbi.AssertManagedX64Layout()` requires 8-byte pointers and asserts `TCP_AdapterList=8836`, `INTERMEDIATE_BUFFER=1566`, `NDISRD_ETH_Packet=8`, `ETH_REQUEST=16`, `ADAPTER_MODE=12`, plus all managed field offsets (`src/WinForward.NdisApi/NdisApiAbi.cs:23`). The pinned header uses `#pragma pack(push,1)`, `MAX_ETHER_FRAME=1514`, and the corresponding structs ([`Common.h#L75-L81`](https://github.com/wiresock/ndisapi/blob/417b8734e844083a10236387fba705d94a2d6bc9/include/Common.h#L75-L81), [`Common.h#L121-L334`](https://github.com/wiresock/ndisapi/blob/417b8734e844083a10236387fba705d94a2d6bc9/include/Common.h#L121-L334)). `NdisApiAbiTests.PinnedX64LayoutMatchesV362NonJumboHeader` passed on Linux.
- **C ABI declarations match the pinned C exports**: every used entry point has the declared `__stdcall` C signature in [`ndisapi.h#L293-L304`](https://github.com/wiresock/ndisapi/blob/417b8734e844083a10236387fba705d94a2d6bc9/include/ndisapi.h#L293-L304); managed imports use `LibraryImport`, `UnmanagedCallConv(CallConvStdcall)`, pointer-sized handles, and `int` for native `BOOL` (`src/WinForward.NdisApi/NdisApiAbi.cs:152`).
- **Enumeration handle is correctly separated from the captured buffer pointer**: the pump stamps `NdisCapturedPacket` with `_adapterHandle`, not `buffer.CapturedAdapterHandle` (`src/WinForward.NdisApi/NdisCapture.cs:37`). The repository's hardware-verified contract states this is the only valid request `hAdapterHandle`, while the captured `m_hAdapter` is rejected with error 87 (`.trellis/spec/backend/windows-ndisapi.md:73`). The pass, TCP redirect, and UDP response paths carry that metadata/enumeration handle to send requests (`src/WinForward.Runtime/NdisPacketActionExecutor.cs:38`, `src/WinForward.Runtime/TcpRedirectInjector.cs:17`, `src/WinForward.Runtime/UdpResponseReinjector.cs:103`).
- **Current direct consumer honors capture-buffer ownership**: each pump owns `using var buffer` (`src/WinForward.NdisApi/NdisCapture.cs:39`); its production handler copies `packet.Buffer.GetFrame()` before returning (`src/WinForward.Runtime/CapturePacketProcessor.cs:28`). A buffer therefore does not outlive the current production handler call. `GetFrame()` rejects a post-dispose access and verifies the native length is no larger than the pinned frame storage before making a span (`src/WinForward.NdisApi/NdisApiDriver.cs:268`).
- **Direction mapping is unit-covered at the managed boundary**: ordinary pass maps `ON_SEND` to adapter and other capture direction to MSTCP (`src/WinForward.Runtime/NdisPacketActionExecutor.cs:40`), TCP reinjection emits `ON_RECEIVE` toward MSTCP and `ON_SEND` toward adapter (`src/WinForward.Runtime/TcpRedirectInjector.cs:17`), and UDP does the same (`src/WinForward.Runtime/UdpResponseReinjector.cs:103`). Tests cover pass mapping (`tests/WinForward.Core.Tests/CapturePipelineTests.cs:348`), TCP (`tests/WinForward.Core.Tests/TcpRedirectInjectorTests.cs:12`), and host/forwarded UDP (`tests/WinForward.Core.Tests/UdpRelayTests.cs:174`).
- **Safe driver ownership is paired, while shutdown remains a Runtime handoff**: `NdisApiSafeHandle.ReleaseHandle()` calls the paired native close (`src/WinForward.NdisApi/NdisApiAbi.cs:122`), and CLI owns the driver with `using` around the awaited capture runtime (`src/WinForward.Cli/Program.cs:152`). SafeHandle arguments protect the native driver object during each imported call, but they do not await active capture handlers; the required Runtime shutdown fix is recorded below.
- **Native errors are surfaced at managed failure boundaries**: all NDISAPI imports specify `SetLastError=true` (`src/WinForward.NdisApi/NdisApiAbi.cs:152`); open, enumerate, mode, queue/read, version-sentinel, and send errors now throw `Win32Exception` with native diagnostics (`src/WinForward.NdisApi/NdisApiDriver.cs:19`). Send failures include frame length, device flags, NDIS flags, and the enumeration adapter handle.

### Existing Test Coverage

| Area | Existing evidence | Boundary not covered |
|---|---|---|
| Managed non-jumbo x64 layout | `tests/WinForward.Core.Tests/NdisApiAbiTests.cs:9` | Actual `ndisapi.dll` exports, native `sizeof`/offsets, and a jumbo DLL. |
| Frame bounds | `NdisPacketBuffer.GetFrame` and `SetFrame` at `src/WinForward.NdisApi/NdisApiDriver.cs:120`; UDP cap tests at `tests/WinForward.Core.Tests/UdpRelayTests.cs:140` | Native driver write into the fixed buffer and live MTU/jumbo behavior. |
| Pass/reverse direction | `CapturePipelineTests.cs:348`, `TcpRedirectInjectorTests.cs:12`, `UdpRelayTests.cs:174` | A real Windows driver delivering each direction and a VM/forwarded data path. |
| Mode rollback | `tests/WinForward.Core.Tests/CaptureLifecycleTests.cs:8` | NDISAPI mode query/apply/restore, driver failure codes, and mode state after process interruption. |
| Adapter identity/handle distinction | Hardware record in `.trellis/spec/backend/windows-ndisapi.md:73`; managed propagation anchors above | Fresh driver/DLL/hardware proof of request handle versus captured pointer for the release package. |
| Pump, native reads, and multi-adapter behavior | `NdisApiAbiTests` covers managed queue/read decision paths and native-call gate contention. | Actual P/Invoke call ordering, buffer callback lifetime misuse, cancellation during native I/O, and the Windows driver matrix. |

### Windows Hardware-only Gates

1. **Pinned binary ABI**: run the selected x64 `ndisapi.dll`/`ndisrd.sys` pair against a tiny native `sizeof`/`offsetof` probe built from the pinned header, then compare it with the managed values in `NdisApiAbi.AssertManagedX64Layout()`. A DLL built with `JUMBO_FRAME_SUPPORTED` changes `INTERMEDIATE_BUFFER` storage and cannot be closed by the Linux layout test.
2. **Open-string character width**: native `OpenFilterDriver` takes `TCHAR*` ([`ndisapi.h#L293`](https://github.com/wiresock/ndisapi/blob/417b8734e844083a10236387fba705d94a2d6bc9/include/ndisapi.h#L293)); managed code marshals it as UTF-16 (`src/WinForward.NdisApi/NdisApiAbi.cs:128`). Confirm the shipped DLL's build character set by successfully opening `NDISRD` on Windows and retaining the DLL/version evidence. The source/header alone do not establish the selected binary's `TCHAR` width.
3. **Enumeration/captured-handle proof**: for every supported physical, Wi-Fi, VPN, and virtual adapter, read then reinject at least 100 packets in both directions with the enumeration handle. Record that use of the captured `m_hAdapter` fails with 87 and the enumeration handle succeeds, matching `.trellis/spec/backend/windows-ndisapi.md:75-87`.
4. **Two-adapter concurrency**: satisfy the NDIS-3 gate with real simultaneous traffic on two capture-scope adapters, including a run with send/reinject activity and an adapter-mode transition.
5. **Adapter churn**: satisfy the NDIS-2 gate by removing, disabling, and re-enabling an in-scope adapter while capture is active. `Program.cs` explicitly resolves the adapter list once and defers adapter-list-change handling (`src/WinForward.Cli/Program.cs:160`); this is an acknowledged runtime lifecycle gap, not a new ABI finding.
6. **Packet metadata semantics**: exercise outbound packets with nonzero native `m_Flags`, VLAN/802.1q metadata, maximum non-jumbo frames, and the exact selected NIC drivers. NDIS-4 is a static contract violation; hardware establishes its packet-level consequence.
7. **Forwarded/Hyper-V direction**: use a real guest/virtual-switch flow for TCP and UDP response injection. Repository documentation records that the current host has no guest VM stack, so those flows are unit-locked but not end-to-end exercised (`.trellis/spec/backend/windows-ndisapi.md:123`).

### Verification Performed

- `dotnet test WinForward.slnx -c Release --filter 'FullyQualifiedName~NdisApiAbiTests' --no-restore` — passed: 14/14.
- `dotnet build WinForward.slnx -c Release --no-restore` — passed: 0 warnings, 0 errors.
- `dotnet test WinForward.slnx -c Release --no-restore` — passed: 217/217.
- `git diff --check` — passed.
- All managed verification ran on Ubuntu `linux-x64`; no Windows DLL, NDISRD driver, administrator device access, physical NIC, or Hyper-V guest was available. The pre-existing working tree includes unrelated modifications under `src/WinForward.Protocols/`, `.trellis/.template-hashes.json`, and other audit task directories; this research write is confined to the active task directory.

## Implementation Update (2026-08-10)

### Applied Managed Fixes

| Finding | Implementation | Linux regression |
|---|---|---|
| NDIS-1 | `NdisApiDriver.Open` now calls the pinned `IsDriverLoaded` export after receiving a non-null wrapper object. A failed status disposes that object and reports the native error captured from the load check, falling back to the original open error only when the export supplies none. | Opaque-but-unloaded object, invalid `0`/`-1` handles, and loaded object decision paths. |
| NDIS-2 | `TryReadPacket` first obtains `GetAdapterPacketQueueSize` while holding the per-driver native call gate. Empty queues remain normal polling; a queue-query failure or a `ReadPacket` failure after a non-empty queue is reported with native error, queue depth, and enumeration handle. This avoids guessing an undocumented empty-queue last-error value. | Empty/non-empty queue decision paths and both query/read failure paths. |
| NDIS-3 | Every NDISAPI call, including version, adapter/mode queries, read, send, and driver disposal, is serialized through one per-driver gate. This prevents concurrent pumps using the pinned wrapper's single native `OVERLAPPED`/bytes-returned state concurrently. | Barrier-based concurrent gate regression proves the second caller blocks and maximum active native calls stays one. |
| NDIS-4 | `IntermediateBuffer.Flags` offset is asserted; `NdisPacketBuffer` exposes and accepts NDIS flags; the capture record copies the captured flags; send diagnostics report device and NDIS flags. Fresh buffers explicitly start with flags zero. | Buffer preservation/reset and captured-record propagation. |
| NDIS-5 | The resolver now throws for an absent app-local `ndisapi.dll` rather than returning zero, so .NET cannot fall back to default PATH/current-directory probing for this library. | Missing-sidecar resolver path. |
| NDIS-6 | `NdisApiDriver.Version` now turns the pinned wrapper's `0xFFFFFFFF` failure sentinel into a `Win32Exception` containing the captured native error, instead of exposing it as a valid version. | Valid-version and native-failure-sentinel decision paths. |

### Deferred Boundaries and Windows Gates

- **NDIS-4 Runtime handoff remains deferred:** `CapturePacketProcessor` currently copies only frame bytes, device flags, and the enumeration handle into `PacketCaptureMetadata`; `NdisPacketActionExecutor` therefore has no captured `m_Flags` value to pass to `SetFrame`. This task was forbidden from modifying Runtime. The runtime-capture-flow owner must extend that metadata and add an end-to-end pass-reinjection regression. The NDIS boundary is ready through `NdisCapturedPacket.Flags` and the four-argument `NdisPacketBuffer.SetFrame` overload.
- **Capture shutdown Runtime handoff:** `NdisCapturePump.DisposeAsync` only sets its stop flag and cannot cancel the caller-owned token or await an in-flight packet handler (`src/WinForward.NdisApi/NdisCapture.cs:54`). `MultiAdapterCaptureLoop.DisposeAsync` likewise stops pumps without awaiting their `RunAsync` tasks (`src/WinForward.Runtime/MultiAdapterCaptureLoop.cs:44`). The runtime-capture-flow owner must cancel and await the run before restoring adapter modes or disposing the caller-owned driver, with a deterministic blocking-handler regression; otherwise a handler can send through the driver after its native handle is closed.
- **Linux native-invocation seam remains pending:** `NdisApiDriver` invokes static `NdisApiNative` imports directly (`src/WinForward.NdisApi/NdisApiDriver.cs:28-151`), so host-independent tests exercise its status/gate helpers but cannot fake and assert the `Open`/queue/read/send call sequence or last-error capture at the P/Invoke boundary. Pending: introduce a narrow NDIS-owned call invoker whose production implementation delegates to `NdisApiNative`, then test `Open` cleanup, empty-queue no-read, read failure, and send diagnostics through that invoker. This is deliberately not claimed as driver/DLL proof; the Windows gates below remain required.
- **NDIS-1 Windows gate:** with the matched x64 `ndisapi.dll`/`ndisrd.sys`, stop `NDISRD` (`sc.exe stop NDISRD`) and run `WinForward.exe adapters`. Expected: `Open()` fails immediately with the original/open-load native error, rather than reaching adapter enumeration.
- **NDIS-2 Windows gate:** during capture, disable/re-enable an in-scope adapter. Expected: queue-size or a read after a positive queue count faults the pump with the driver error rather than continuing to poll. Record the concrete driver error; an adapter-list-change restart remains Runtime work.
- **NDIS-3 Windows gate:** capture sustained traffic on two adapters while reinjecting and changing an adapter mode. Expected: no overlapping NDISAPI IOCTLs per driver instance; validate with ETW/Process Monitor and packet/loss observations.
- **NDIS-4 Windows gate:** capture a packet with nonzero `m_Flags`, then after the Runtime handoff lands inspect the send buffer. Expected: the captured value is byte-identical in the reinjected `INTERMEDIATE_BUFFER.m_Flags`; also test VLAN/802.1q and maximum non-jumbo frames.
- **NDIS-5 Windows gate:** remove the executable-directory DLL while placing another copy only on a default probe path. Run `WinForward.exe adapters` under Process Monitor. Expected: actionable missing-sidecar failure and no alternate `ndisapi.dll` image load.
- **ABI/binary gates:** validate the selected x64 DLL export set, `TCHAR` width, and 1514-vs-9014 frame layout against a native probe built from pinned v3.6.2 headers before release.

### Final Verification

- `dotnet test WinForward.slnx -c Release --filter 'FullyQualifiedName~NdisApiAbiTests' --no-restore` — passed: 14/14.
- `dotnet build WinForward.slnx -c Release --no-restore` — passed: 0 warnings, 0 errors.
- `dotnet test WinForward.slnx -c Release --no-restore` — passed: 217/217.
- `git diff --check` — passed.

### External References

- [Pinned NDISAPI `Common.h`](https://github.com/wiresock/ndisapi/blob/417b8734e844083a10236387fba705d94a2d6bc9/include/Common.h) — packed ABI, 1514/9014 frame switch, request and buffer structs.
- [Pinned NDISAPI `ndisapi.h`](https://github.com/wiresock/ndisapi/blob/417b8734e844083a10236387fba705d94a2d6bc9/include/ndisapi.h) — C export signatures and stdcall convention.
- [Pinned NDISAPI `ndisapi.cpp`](https://github.com/wiresock/ndisapi/blob/417b8734e844083a10236387fba705d94a2d6bc9/ndisapi/ndisapi.cpp) — opaque open-object behavior and shared `OVERLAPPED` use.
- [WinpkFilter ReadPacket](https://www.ntkernel.com/docs/windows-packet-filter-documentation/c-api/readpacket/) — request handle/buffer requirements and false result contract.
- [WinpkFilter SendPacketToAdapter](https://www.ntkernel.com/docs/windows-packet-filter-documentation/c-api/sendpackettoadapter/) — request handle and `m_Flags` initialization contract.
- [WinpkFilter SendPacketToMstcp](https://www.ntkernel.com/docs/windows-packet-filter-documentation/c-api/sendpackettomstcp/) — enumeration-handle and packet-buffer requirements.
- [Microsoft DeviceIoControl](https://learn.microsoft.com/en-us/windows/win32/api/ioapiset/nf-ioapiset-deviceiocontrol) and [OVERLAPPED](https://learn.microsoft.com/en-us/windows/win32/api/minwinbase/ns-minwinbase-overlapped) — simultaneous overlapped I/O ownership requirements.
- [Microsoft Native library loading](https://learn.microsoft.com/en-us/dotnet/standard/native-interop/native-library-loading) — zero resolver return falls back to default loading.

## Related Specs

- `.trellis/spec/backend/windows-ndisapi.md` — hardware-verified adapter identity, enumeration-versus-captured-handle separation, direction, and outstanding Windows matrix.
- `.trellis/tasks/08-10-audit-ndisapi/prd.md` — audit acceptance criteria and Linux/Windows distinction.
- `.trellis/tasks/08-10-audit-ndisapi/design.md` — required lifetime and handle-origin audit model.

## Caveats / Not Found

- No actual `ndisapi.dll`, `ndisrd.sys`, or Windows host is present in this workspace; no claim above treats the Linux test run as hardware validation.
- The external source confirms the pinned header/source contracts, but the exact shipped DLL build configuration, version, character width, architecture, and jumbo-frame setting remain Windows-binary gates.
- This check phase added managed NDIS regressions and expanded ABI offset assertions; it did not modify Runtime source.
