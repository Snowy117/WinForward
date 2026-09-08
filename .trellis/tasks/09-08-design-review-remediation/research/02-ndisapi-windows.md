# Design Review — WinForward.NdisApi + WinForward.Windows (interop layer)

> Sub-agent report (explore), 2026-09-08. Read-only review; no files modified.
> Conventions checked against: .trellis/spec/backend/windows-ndisapi.md.

## 1. Layered diagram (internal structure)

```
                        WinForward.Runtime / WinForward.Cli (consumers)
                              |            |             |
              INdisPacketReader   IPacketReinjector   NdisApiDriver (concrete class:
              (read seam)         (send seam,          control path: modes,
                                   runtime-level)      enumeration, list-change events)
                              |            |             |
        ===================== NdisApiDriver ===========================
        |  _controlGate (NdisNativeCallGate)      cold path: open/close,    |
        |      enumeration, mode get/set/restore, SetAdapterListChangeEvent |
        |  _adapterGates (NdisAdapterGateMap: ConcurrentDictionary)         |
        |      hot path: TryReadPackets, 4x sends — serialized per          |
        |      enumeration handle, parallel across adapters                 |
        =================================================================
                              |
                       NdisApiNative (internal, NdisApiAbi.cs)
                       14x [LibraryImport] stdcall, SetLastError=true,
                       DllImportResolver → AppContext.BaseDirectory only
                              |
                       ndisapi.dll (WinpkFilter NDISRD)

 Sibling types:
   NdisCapturePump ----consumes---> INdisPacketReader (implemented by NdisApiDriver)
   NdisPacketBuffer (IntermediateBuffer* wrapper, owner state machine)
   NdisPacketBufferPool (capacity 256, Shared process-wide)
   NdisNativeCallStatus (pure Win32 error interpretation, unit-testable)
   NdisApiAbi (layout structs + AssertManagedX64Layout, called at Open())

 WinForward.Windows:
   WindowsAdapterInventory (GUID-primary association)  ← Platform.cs (WindowsAdapter, selectors, diagnostics)
   WindowsAdapterLocalAddressProvider (snapshot cache) ← IAdapterLocalAddressProvider
   WindowsProcessAttributor + IPHelperTables → iphlpapi/kernel32
   HighResolutionTimerScope → winmm (delegate-based test seam)
```

Dependency direction: `Runtime → {NdisApi, Windows} → Core`. Everything targets plain `net10.0` (Directory.Build.props), so the test project can load and run it all on Linux hosts; Windows-only P/Invoke is gated only by `[SupportedOSPlatform]` and runtime `OperatingSystem.IsWindows()` checks.

## 2. Seam inventory

| Seam | src adapters | test adapters | Consumers |
|---|---|---|---|
| `INdisPacketReader` (NdisCapture.cs:22) | 1 (`NdisApiDriver`) | 7 fakes across 3 files (NdisCaptureResilienceTests.cs:181,243,248,253; NdisCapturePumpTests.cs:195; BatchedPassReinjectionE2eTests.cs:135,164) | `NdisCapturePump`, `MultiAdapterCaptureLoop` |
| `IPacketReinjector` (send seam, runtime level) | 1 (`NdisPacketReinjector`) | PacketReinjectorFakes.cs | redirect injectors / reinjector |
| `IAdapterLocalAddressProvider` (AdapterLocalAddressProvider.cs:18) | 1 | 1 (AdapterFakes.cs:11) + 2 test files testing the real object via injected delegates | TcpRedirectSetup, TcpProxyCoordinator |
| `IProcessAttributor` (Platform.cs:61) | 2 (`WindowsProcessAttributor`, `UnsupportedProcessAttributor` null object) | 1 (CapturePipelineFakes.cs:19) | FlowDispatcher (optional) |
| `IWindowsAdapterInventory` (AdapterIdentity.cs:6) | 1 | 0 (tests inject constructor delegates, not the interface) | **none** — Program.cs:298 and AdapterEnumeration.cs:52 bind the concrete class |
| `HighResolutionTimerScope` internal ctor (delegate pair) | 1 | 1 (HighResolutionTimerScopeTests) | Program.cs:188 |
| `NdisPacketBufferPool` (injectable, `Shared` default) | 1 + static Shared | 3 test files | TcpRedirectInjector, UdpResponseReinjector, NdisPacketActionExecutor |

Conclusion: seams are real and heavily faked for the hot path. The read seam is the star: 7 fakes unit-lock retry/degradation/ordering logic without hardware. The send/control path is consumed only via the concrete `NdisApiDriver` (NdisAdapterModeController.cs:17, NdisCaptureGeneration.cs:47, NdisPacketReinjector.cs:28, AdapterListWatcher.cs:32, AdapterEnumeration.cs:41) — that part is hardware-tested only, except for the internal `BuildMultiRequest` slot-layout check (NdisApiDriver.cs:203, exercised in NdisApiBatchedSendAbiTests).

## 3. Top 3 strengths

1. **Textbook ABI isolation.** `NdisApiAbi.cs` is purely layout + imports: every struct's size and offsets asserted via `AssertManagedX64Layout` at driver open (NdisApiAbi.cs:23-56, invoked NdisApiDriver.cs:40), `LibraryImport` source generation with stdcall convention, `SetLastError=true` everywhere (lines 173–231), and a reflection-free AOT-safe resolver locked to BaseDirectory (lines 154–171). `EthernetMultiRequest.FirstBuffer` aliasing `EthPacket[0]` (lines 117–123) is a clean trick avoiding fixed-size array pain.
2. **Policy separated from P/Invoke, and the policy is unit-testable.** `NdisApiDriver` defers all Win32 error interpretation to the pure static `NdisNativeCallStatus` (bounded batch results, transient error table, open-failure taxonomy — NdisNativeCallStatus.cs:35–66) covered by 13 test cases; retry/degradation logic above the seam is all hardware-independent. Error messages carry WHAT and WHERE: native error, frame length, both flag words, adapter handle, block range (NdisApiDriver.cs:230, 243, 290).
3. **Buffer lifetime discipline.** `NdisPacketBuffer`'s owner-pool CAS state machine makes `using` callers uniform for private vs rented buffers (NdisPacketBuffer.cs:139–151); the pool documents return-race handling (NdisPacketBufferPool.cs:64–78); `GetFrame` validates driver-reported length against the pinned ABI before touching the pointer (NdisPacketBuffer.cs:74); `GetFrameStorage`/`CompleteFrame` separate copy-free injection into an explicit pair with explicit contracts (lines 78–105).

## 4. Ranked issues

1. **Dead interface: `IWindowsAdapterInventory`** — AdapterIdentity.cs:6. Declared, implemented, zero consumers; both call sites (Program.cs:298, AdapterEnumeration.cs:52) bind the concrete class, and tests inject constructor delegates. A shallow seam hiding nothing — either consume it or delete it. Related: `Platform.cs` is a junk drawer holding five unrelated public types (diagnostics, adapter record, selectors, process identity, attributor interface) in one 58-line file.
2. **Pump `DisposeAsync` safe only by convention.** `NdisCapturePump.DisposeAsync` sets `_stopped` and immediately frees batch buffers (NdisCapture.cs:194–205); `MultiAdapterCaptureLoop.DisposeAsync` disposes pumps sequentially without a cancellation token (MultiAdapterCaptureLoop.cs:62–67). If a caller invokes `DisposeAsync` mid-`RunAsync` without cancelling, `NativeMemory.Free` runs on buffers a handler may still be processing. Current flows do await `RunAsync` before disposal, but the invariant is not encoded in the type — awaiting loop exit inside `DisposeAsync` (or documenting the precondition) would make it misuse-proof.
3. **Unsafe discipline asymmetry: `IPHelperTables.ReadTable` blindly trusts `dwNumEntries`.** Row count is read from the HGlobal buffer itself, then rows are dereferenced at `buffer + 4 + index * sizeof(T)` without cross-checking `4 + rowCount*sizeof(T) ≤ size` (ProcessAttribution.cs:274–279). The NDISAPI layer bounds-checks both dimensions against similar driver-supplied data (GetAdapters: NdisApiDriver.cs:73; PacketsSuccess: NdisNativeCallStatus.cs:44); iphlpapi is an OS DLL so risk is lower, but the discipline should be symmetric since the sister seam documents "malicious driver" reasoning.
4. **Retained dead native surface.** `GetDriverVersion` import + `EnsureDriverVersion` helper and `ReadPacket` import + `InterpretReadResult` helper have no product callers — test-referenced only (NdisApiAbi.cs:185–187, 209–211; NdisNativeCallStatus.cs:9–13, 28–33). They are single-packet-read-era leftovers; they widen the ABI's unproven surface.
5. **Pool re-rental weakens the double-`Dispose` claim.** XML doc says repeated `Dispose` is a no-op (NdisPacketBuffer.cs:8–11), which holds only while the buffer sits idle in the pool. A stale `Dispose` (by a previous renter, after re-rental) CASes `StateRented→StateIdle` and returns the buffer to the pool under the current renter (line 145). Pool-inherent, safe only under calling discipline — the documentation promises more than the type delivers.
6. **Minor readability:** `NdisCapturePump` ctor takes 9 parameters, 4 of them optional callbacks (NdisCapture.cs:63) — a clear options-record candidate. `NdisNativeCallGate.MaxConcurrentCalls` can never exceed 1 per gate because it increments while holding the monitor (NdisNativeCallGate.cs:16–26) — an invariant assertion sold as a metric. `WindowsAdapterInventory._generation` uses non-atomic `++` (AdapterIdentity.cs:51). `NdisPacketBuffer` post-`Dispose` property reads silently return 0 while `Pointer`/`GetFrame` throw (NdisPacketBuffer.cs:49–52 vs 45, 73) — deliberate tolerance but undocumented.

## 5. Depth verdicts per public type

| Type | Verdict |
|---|---|
| `NdisApiDriver` | **Deep** — 8-method surface hides dual-gate topology, batch-boundary math, heap/stack allocation switching, telemetry, full error context |
| `NdisCapturePump` | **Deep** — single `RunAsync` hides batching, strict ordering, transient retry/degradation state machine, release-exactly-once teardown |
| `NdisPacketBuffer` | **Medium-deep** — raw pointer + state machine; `GetFrameStorage`/`CompleteFrame` pair is a well-designed copy-free API |
| `NdisApiAbi`/`NdisApiNative` | **Appropriately shallow** — layout + imports; trivial passing is its job |
| `NdisPacketReinjector` | **Shallow but justified** — 2-line pass-through existing to be the `IPacketReinjector` seam |
| `WindowsAdapterLocalAddressProvider` | **Deep** — 1-method interface hides snapshot cache, TTL, invalidation CAS, subnet preference |
| `WindowsProcessAttributor`/`IPHelperTables` | **Medium** — 1-method interface hides retry, LRU identity cache, size-adaptive path queries, 4 table decoders |
| `WindowsAdapterInventory` | **Medium**; `IWindowsAdapterInventory` | **shallow/dead** (issue 1) |
| `HighResolutionTimerScope` | Small and fully correct (fails open, idempotent, delegate seam) |
| `PlatformRequirements`, `AdapterSelector`, `NdisAdapter` | pure functions/data — shallow is right |

## 6. Unsafe code discipline

Bounds checked before span construction: `GetFrame` validates `_buffer->Length` against `MaximumEthernetFrame` (NdisPacketBuffer.cs:74); `SetFrame`/`CompleteFrame` validate length before copying (99, 130); driver-reported counts bounded in both directions (NdisApiDriver.cs:73, NdisNativeCallStatus.cs:44); `stackalloc` guaranteed ≤1024 bytes by construction (NdisApiDriver.cs:24, 277). `fixed`/`Span` used correctly in pointer arithmetic; `ReadRow` uses `Unsafe.ReadUnaligned` (ProcessAttribution.cs:278). Stack-overflow path in `SendPacketsBatch` unreachable because chunk capacity is statically derived. The only gap is issue 4.3 (ReadTable).

## 7. Spec compliance (.trellis/spec/backend/windows-ndisapi.md)

Checked against documented contracts — **no violations found**:

- GUID-primary association with exactly-one match + MAC fallback + internal-name fallback: AdapterIdentity.cs:71–84, normalized `TryExtractGuid` 100–110. Line-by-line match matrix incl. zero-MAC rejection.
- Enumeration-handle tagging, never captured `m_hAdapter`: NdisCapture.cs:131–137 via `NdisCapturedPacket.FromCapture`.
- Gate topology (control gate + per-adapter `GetOrAdd` map, no per-call map lock, grow-only): NdisNativeCallGate.cs:69–81 — matches the 2026-08-30 D4 revision.
- Batched read = query + read sharing a single gate lease; `PacketsSuccess` bounded to requested count: NdisApiDriver.cs:154–166, NdisNativeCallStatus.cs:42–44.
- `SetLastError=true` on all NDISAPI imports, send exceptions carry `GetLastWin32Error` + length/flags/handle: NdisApiAbi.cs:173–231, NdisApiDriver.cs:229–243.
- Transient table {21, 170, 1237, 995, 1167, 31} with fail-conservative commented rationale: NdisNativeCallStatus.cs:60–66 — matches R7 exactly; 87 correctly kept permanent.
- Send `PacketsSuccess` unobservable / all-or-nothing: NdisApiDriver.cs:251–254, 286–291.
- Pool capacity 256, release-beyond-capacity drop, double-release no-op, drain-but-rented-remains-valid: NdisPacketBufferPool.cs.
- DLL resolution: BaseDirectory only, no PATH/CWD, AOT-safe, actionable `DllNotFoundException`: NdisApiAbi.cs:159–171.
- IPv6 `ScopeId` host-order preservation + network-order port conversion: IPHelperAbi.cs:21–23, locked by WindowsBoundaryAuditTests.
- **≤400 effective lines rule** (directory-structure.md:57): largest file is ProcessAttribution.cs at 257 effective lines (292 raw); all 14 reviewed files pass comfortably.

**Overall**: the NdisApi interop layer is the most rigorous part of the codebase — seams sit in the right places, hide real complexity, and keep hardware behind only the top-level concrete driver. WinForward.Windows is weaker: one dead interface, one junk-drawer file, and an iphlpapi decode path slightly looser than the NDISAPI seam's discipline.
