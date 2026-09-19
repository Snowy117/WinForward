# Research: Compatibility API Inventory

- **Query**: exhaustive inventory of "compatibility APIs" in `src/` that exist to avoid changing signatures or rewriting tests/callers rather than for a real product need.
- **Scope**: internal (production `src/` vs `tests/` vs `benchmarks/`), evidence by ripgrep + token reference map + `git log -S`/`git blame`.
- **Date**: 2026-09-19
- **Task**: `.trellis/tasks/09-19-compat-api-cleanup`
- **Method**: (1) enumerated every `internal`/`public` declaration in `src/` (excluding `obj/`, `bin/`); (2) built a whole-repo identifier→file occurrence map for `src/`, `tests/`, `benchmarks/` to classify each referenced symbol as production-only, test/bench-only, or dead; (3) verified every borderline candidate with explicit `rg -w` call-site lines; (4) traced provenance with `git log -S` where useful. All caller claims below cite file:line.

## Disposition legend

- **REMOVE (safe)** — zero callers repo-wide, or only `tests/`+`benchmarks/` callers and no behavioural dependency; call sites can be updated without a product change.
- **REPLACE-WITH-CLEANER-SEAM** — a legitimate seam exists but is currently exposed as an optional parameter / extra overload / public surface; rewrite the seam (e.g. options record, `TimeProvider`, internal + `InternalsVisibleTo`) instead of deleting the capability.
- **KEEP (justified)** — an internal seam consumed by the owning module's own tests, or a production-consumed member; per PRD R2 this is legitimate and stays.
- **FOLLOW-UP** — removal requires a larger API-surface change; record as a recommendation (PRD R5).

## Summary table

### A. Dead members — zero callers anywhere (src/tests/benchmarks)

| # | Candidate | file:line | Disposition | Conf. |
|---|---|---|---|---|
| A1 | `NdisApiDriver.ControlGateMaxConcurrentCalls` | `src/WinForward.NdisApi/NdisApiDriver.cs:303` | REMOVE | High |
| A2 | `NdisApiDriver.BatchedSendFlushCount` | `src/WinForward.NdisApi/NdisApiDriver.cs:311` | REMOVE | High |
| A3 | `NdisApiDriver.BatchedSendPacketCount` | `src/WinForward.NdisApi/NdisApiDriver.cs:314` | REMOVE | High |
| A4 | `NdisApiDriver.GetAdapterGateMaxConcurrentCalls` | `src/WinForward.NdisApi/NdisApiDriver.cs:320` | REMOVE (+ A4b) | High |
| A4b | `NdisAdapterGateMap.GetMaxConcurrentCalls` | `src/WinForward.NdisApi/NdisNativeCallGate.cs:76` | REPLACE (test-only after A4) | Medium |
| A5 | `TcpProxyCoordinator.SynCopyPool` (getter only) | `src/WinForward.Runtime/TcpRedirect/TcpProxyCoordinator.cs:625` | REMOVE | High |
| A6 | `NdisApiAbi.UpstreamVersion` | `src/WinForward.NdisApi/NdisApiAbi.cs:12` | REMOVE or wire to diagnostics | High |
| A7 | `NdisApiAbi.UpstreamCommit` | `src/WinForward.NdisApi/NdisApiAbi.cs:13` | REMOVE or wire to diagnostics | High |
| A8 | `NdisApiAbi.LoopbackFilter` | `src/WinForward.NdisApi/NdisApiAbi.cs:22` | REMOVE | High |
| A9 | `NativeFrameHandle.HasBuffer` | `src/WinForward.Runtime/FlowDispatcher.cs:49` | REMOVE | High |
| A10 | `TcpRedirectTable.TryResolveByTranslated` | `src/WinForward.Runtime/TcpRedirect/TcpRedirectTable.cs:233` | REMOVE | High |
| A11 | `UdpAssociations.TryRemoveOriginal` | `src/WinForward.Runtime/UdpProxy/UdpAssociations.cs:130` | REMOVE | High |
| A12 | `SetupExecutor.WorkerCount` | `src/WinForward.Runtime/SetupExecutor.cs:107` | REMOVE | High |
| A13 | `Socks5UdpCodec.TryEncode(IPAddress, …)` wrapper | `src/WinForward.Protocols/Socks5Udp.cs:40` | REMOVE | High |
| A14 | `IPPrefix(IPAddress, int)` ctor | `src/WinForward.Core/IPPrefix.cs:17` | REMOVE | High |

### B. Public/member surface referenced only by `tests/` or `benchmarks/`

| # | Candidate | file:line | Disposition | Conf. |
|---|---|---|---|---|
| B1 | `IPUdpPacket.TryParse(ReadOnlyMemory, out UdpPacketView)` (+ `UdpPacketView`) | `src/WinForward.Protocols/IPUdpPacket.cs:26` / `:8` | REMOVE (rewrite callers to `TryParseSpan`) | High |
| B2 | `PacketChecksums.TryRewriteUdpEndpoints` | `src/WinForward.Protocols/PacketChecksums.cs:23` | REMOVE or KEEP (public util, no prod caller) | Medium |
| B3 | `TcpResetBuilder.BuildReset(IPAddress/IPAddressValue, …)` | `src/WinForward.Protocols/TcpResetBuilder.cs:32,49` | REMOVE (tests → `TryBuildReset`) | High |
| B4 | `TcpResetBuilder.BuildResetFromSyn` | `src/WinForward.Protocols/TcpResetBuilder.cs:113` | REMOVE (tests → `TryBuildResetFromSyn`) | High |
| B5 | `Socks5Messages.UsernamePassword` | `src/WinForward.Protocols/Socks5State.cs:65` | REMOVE (tests → `WriteUsernamePassword`) | High |
| B6 | `Socks5Messages.Request` | `src/WinForward.Protocols/Socks5State.cs:98` | REMOVE (tests → `WriteRequest`) | High |
| B7 | `Socks5UdpCodec.Encode(IPAddressValue/IPAddress/string, …)` | `src/WinForward.Protocols/Socks5Udp.cs:44,51,69` | REMOVE (tests/loopback → `TryEncode`) | High |
| B8 | `UdpFrameBuilder.TryBuild(IPAddress/IPAddressValue, …)` | `src/WinForward.Protocols/UdpFrameBuilder.cs:24,36` | REMOVE (tests → `TryBuildInto`) | High |
| B9 | Memory send chain: `UdpProxyCoordinator.TrySendAsync` → `UdpProxySession.SendAsync` → `IUdpProxyTransport.SendAsync` → `Socks5UdpTransport.SendAsync` | `UdpProxyCoordinator.cs:176`; `UdpProxySession.cs:107`; `Socks5UdpTransport.cs:65` | **REMOVED (M5, task 09-19)** — span path is the only send seam; callers rewritten | High caller-evidence |
| B10 | `IPPrefix.Contains(IPAddress?)` | `src/WinForward.Core/IPPrefix.cs:60` | REMOVE | High |
| B11 | `NativeBufferPoolStats` + `NativeBufferPool.Stats` | `NativeBufferPool.cs:240` / `:62` | REPLACE (internal + IVT Benchmarks) or KEEP | Medium |
| B12 | `PacketLease.Disposition` | `src/WinForward.Core/PacketRuntime.cs:115` | REMOVE or make internal | High |
| B13 | `BoundedSetupQueue.Bytes` | `src/WinForward.Core/BoundedSetupQueue.cs:37` | REMOVE or make internal | High |
| B14 | `LayeredCaptureRunner.HealthSignal` | `src/WinForward.Runtime/Capture/LayeredCaptureRunner.cs:114` | REPLACE / KEEP documented | Medium |
| B15 | `TcpProxyCoordinator.Table` | `src/WinForward.Runtime/TcpRedirect/TcpProxyCoordinator.cs:83` | REPLACE / KEEP documented | Medium |
| B16 | `SetupExecutor.PendingCount/FreeCount/EnqueuedCount/CompletedCount/RejectedCount/OverflowAllocations` | `src/WinForward.Runtime/SetupExecutor.cs:108-118` | REPLACE (internal) or KEEP | Medium |
| B17 | `RuntimeCounters.RecordPoolRent` / `RecordPoolReturn` | `src/WinForward.Runtime/RuntimeCounters.cs:100,108` | REMOVE or make internal | High |
| B18 | `NdisCapturedPacket.FromCapture` | `src/WinForward.NdisApi/NdisCapture.cs:10` | REMOVE or make internal | High |
| B19 | `Socks5UdpTransport.CreateAsync` public 3-arg overload | `src/WinForward.Runtime/Socks5/Socks5UdpTransport.cs:165` | REMOVE (tests → internal overload) | High |
| B20 | `Socks5UdpReceiveResult.Received` / `Skipped` factories | `src/WinForward.Runtime/Socks5/Socks5UdpTransport.cs:49,52` | KEEP (bench uses `Received`) / consider internal | Medium |
| B21 | `AdapterSelector` type + `TryResolve` | `src/WinForward.Windows/WindowsAdapter.cs:10` | REMOVE or KEEP (public util, no prod caller) | Medium |

### C. Internal test seams consumed by the owning module's tests (PRD R2 — legitimate)

| # | Candidate | file:line | Disposition | Conf. |
|---|---|---|---|---|
| C1 | `NdisCapturePump.RunIterationForTests` | `src/WinForward.NdisApi/NdisCapture.cs:169` | KEEP (explicit test seam) | High |
| C2 | `NdisCapturePump` telemetry: `PumpThread`, `TransientReadRetryCount`, `TransientReadIncidentCount`, `LastDegradedNativeErrorCode` | `NdisCapture.cs:166,369,372,378` | KEEP | High |
| C3 | `HighResolutionTimerScope` internal delegate ctor | `src/WinForward.Windows/HighResolutionTimerScope.cs:44` | KEEP | High |
| C4 | `NdisAdapterListWatcher()` internal ctor + `WaitForSignal` | `src/WinForward.Runtime/Capture/AdapterListWatcher.cs:50,69` | KEEP | High |
| C5 | `NdisPacketActionExecutor.PendingPassCount` / `ImmediateSendLaneOverflowCount` / `DebugAssertNoPendingPasses` | `NdisPacketActionExecutor.cs:295,316,324` | KEEP | High |
| C6 | `MultiAdapterCaptureLoop.DegradedAdapterCount` | `MultiAdapterCaptureLoop.cs:48` | KEEP | High |
| C7 | `TcpProxyCoordinator.DrainPendingSetupsAsync` / `PendingSetups` / `CapacityResetCooldowns` / `CapacityRejectionCount` | `TcpProxyCoordinator.cs:633,622,636,104` | KEEP (test/diagnostic seam) | High |
| C8 | `UdpProxyCoordinator` `*ForDiagnostics` / `Setup*Count` props | `UdpProxyCoordinator.cs:104,116,119,122,125` | KEEP | High |
| C9 | UDP/TCP setup table diagnostics (`ActiveCount`, `ChargedBytes`, `RejectionCount`, `TtlExpiredCount`, `CooldownCount`, `QueueCountForDiagnostics`, `PendingBytes`, `DisposeLimiter`) | `TcpPendingSynSetup.cs:92-107`; `TcpRedirectTombstoneTable.cs:48`; `UdpSetupQueueBudget.cs:41,44`; `UdpSessionSetup.cs:76,79,211` | KEEP | High |
| C10 | `TcpResetCooldownTable.Remove` + `ClientResetInjector.CapacityResets` + `TcpProxyCoordinator.CapacityResetCooldowns` | `TcpResetCooldownTable.cs:53`; `ClientResetInjector.cs:50`; `TcpProxyCoordinator.cs:636` | KEEP (explicit “so tests can advance the window”) | High |
| C11 | `Socks5UdpTransport.IsAcceptableRelaySource` / `IsPossiblyTruncated` / `ClassifyReceiveFault` / `DisableUdpConnectionReset` | `Socks5UdpTransport.cs:393,404,422,412` | KEEP | High |
| C12 | `UnicastAddressInventory` internals (`ReadRows`, `ParseRows`, `ValidateEntryCount`, `GroupFingerprints`, `BuildFingerprint`, `ResolveInterfaceGuid`) | `UnicastAddressInventory.cs:63-151` | KEEP (prod path + module tests) | High |
| C13 | `IPHelperTables.ValidateRowCount` / `ReadRow`; `IPHelperAbi.AssertManagedLayout` + struct fields | `ProcessAttribution.cs:293,307`; `IPHelperAbi.cs:26,108-116` | KEEP (layout/ABI asserts) | High |
| C14 | `NdisApiDriver.MaxPacketsPerSendRequest` / `BuildMultiRequest` | `NdisApiDriver.cs:28,203` | KEEP | High |
| C15 | `PacketChecksums.TryRewriteTcpEndpointsFullRecompute` | `PacketChecksums.cs:176` | KEEP (documented property-test oracle) | High |
| C16 | `NdisNativeCallGate` internals (`ctor(onContention)`, `MaxConcurrentCalls`, `Enter`, `GateLease`) | `NdisNativeCallGate.cs:12,14,16,28` | KEEP (driver path) | High |

### D. Optional ctor/method parameters added for tests/benchmarks

| # | Candidate | file:line | Disposition | Conf. |
|---|---|---|---|---|
| D1 | `AdapterTransientRetryLogGate(logger, ticksProvider = null)` | `src/WinForward.Runtime/AdapterTransientRetryLogGate.cs:13` | REPLACE (use `TimeProvider`) or KEEP | High |
| D2 | `RuntimeHeartbeat.gcSnapshotProvider = null` | `src/WinForward.Runtime/RuntimeHeartbeat.cs:72` | REPLACE or KEEP | High |
| D3 | `WindowsProcessAttributor.retryDelay = null` (never overridden) | `src/WinForward.Windows/ProcessAttribution.cs:21` | REMOVE param / inline default | Medium |
| D4 | `UdpProxyCoordinator` internal ctor `beforeExpiryRecheck`, `setupQueueGlobalByteBudget` | `src/WinForward.Runtime/UdpProxy/UdpProxyCoordinator.cs:52` | KEEP (tests override budget) | High |
| D5 | `TcpProxyRelay(… logger = null, pumpBufferPool = null)` / `TcpProxyRelayFactory` optionals | `TcpProxyRelay.cs:121,12` | KEEP (prod passes, tests omit) | High |
| D6 | `TcpRedirectSession(… long flowGeneration = 0)` | `TcpProxyCoordinator.cs:662` | REMOVE optional (tests pass it explicitly) | Medium |
| D7 | `TcpRedirectTable(int? capacity = null)` | `TcpRedirectTable.cs:172` | KEEP (prod passes; default used by tests) | High |
| D8 | `TcpPendingSynSetupIndex(int? capacity = null, long? byteBudget = null)` | `TcpPendingSynSetup.cs:83` | KEEP (tests override) | High |
| D9 | `DurableCaptureBundle` internal ctor + optional pool params | `src/WinForward.Cli/DurableCaptureBundle.cs:52-64` | KEEP (documented fabrication seam) | High |
| D10 | `LayeredCaptureRunner` optionals `onScopeInstalled`, `minimumRefreshInterval`, `periodicRefreshInterval`, `interceptionHealthMonitor` | `LayeredCaptureRunner.cs:79-83` | KEEP (prod + tests); intervals could be test-only seam record | Medium |
| D11 | `UdpAdapterTargetSource(host = null, byStableId = null)` | `UdpAdapterTargetSource.cs:48` | KEEP | High |

### E. Types with zero production callers (only tests/benchmarks)

| Type | file:line | Disposition | Conf. |
|---|---|---|---|
| `AdapterSelector` | `src/WinForward.Windows/WindowsAdapter.cs:10` | REMOVE or KEEP (see B21) | Medium |
| `NativeBufferPoolStats` | `src/WinForward.Core/NativeBufferPool.cs:240` | see B11 | Medium |
| `UdpPacketView` | `src/WinForward.Protocols/IPUdpPacket.cs:8` | see B1 | High |
| `IPAdapterInfo` | `src/WinForward.Windows/AdapterIdentity.cs` | KEEP (in-file prod use) | High |
| `IPAdapterUnicastInfo` | `src/WinForward.Windows/AdapterLocalAddressProvider.cs` | KEEP (in-file prod path) | High |
| `UnicastAddressObservation` | `src/WinForward.Windows/UnicastAddressInventory.cs:12` | KEEP (internal, in-file prod) | High |
| `RetiredSession`, `RuntimeCaptureGeneration`, `RedirectSetup` | Runtime/TcpRedirect, Runtime/Capture | KEEP (in-file prod use) | High |

---

## Per-candidate detail

### A. Dead members (zero callers anywhere)

**A1–A4 `NdisApiDriver` telemetry (all four).**
- Signatures: `internal int ControlGateMaxConcurrentCalls => _controlGate.MaxConcurrentCalls;` (`NdisApiDriver.cs:303-304`); `internal long BatchedSendFlushCount => Volatile.Read(ref _batchedSendFlushCount);` (`:311`); `internal long BatchedSendPacketCount => Volatile.Read(ref _batchedSendPacketCount);` (`:314`); `internal IReadOnlyDictionary<nint,int> GetAdapterGateMaxConcurrentCalls() => _adapterGates.GetMaxConcurrentCalls();` (`:320`).
- Callers: **none** — `rg -w` over `src tests benchmarks` returns only the declaration lines; the counters themselves are incremented on the hot path (`Interlocked.Increment(ref _batchedSendFlushCount)` at `:293-294`) but never read.
- Why it exists: added as syscall-amortization evidence for batched IOCTLs — `BatchedSend*` in `0615976 perf(ndisapi,capture): batch Pass reinjection IOCTLs`, `ControlGate*`/`GetAdapterGateMaxConcurrentCalls` in `b82cd24 fix: eliminate UDP loss amplifiers…`. The tests/consumers that used them are gone.
- Note on A4: `NdisAdapterGateMap.GetMaxConcurrentCalls` (`NdisNativeCallGate.cs:76`) is called only by the dead driver method and by `tests/…/NdisAdapterGateMapTests.cs`; once A4 is removed it becomes test-only.
- Disposition: REMOVE A1–A4; REPLACE/decide A4b (test seam over the gate map). Confidence: High.

**A5 `TcpProxyCoordinator.SynCopyPool`.**
- `internal NativeBufferPool SynCopyPool => _synCopyPool;` (`TcpProxyCoordinator.cs:625`).
- Callers: none (declaration only). Field `_synCopyPool` is used by production composition at `:80`.
- Why: added in `c75f30e perf(hotpath): GC-less … connection paths` as an accessor; the consumer did not land.
- Disposition: REMOVE. Confidence: High.

**A6–A8 `NdisApiAbi` consts.**
- `public const string UpstreamVersion = "v3.6.2";` (`NdisApiAbi.cs:12`), `UpstreamCommit` (`:13`), `public const uint LoopbackFilter = 0x00000020;` (`:22`).
- Callers: repo-wide `rg` (including `.md`/scripts) finds only the declaration lines.
- Why: documented ABI provenance / an unused NDIS filter flag; no diagnostic prints them.
- Disposition: REMOVE, or (preferred if provenance is wanted) surface `UpstreamVersion`/`UpstreamCommit` in a startup diagnostic so the constant has a product consumer; `LoopbackFilter` REMOVE. Confidence: High (deadness), Medium (which disposition).

**A9 `NativeFrameHandle.HasBuffer`.**
- `public bool HasBuffer => Buffer is not null;` (`FlowDispatcher.cs:49`, inside `NativeFrameHandle` at `:47`).
- Callers: zero — no `src/`, `tests/`, `benchmarks/` reference; production branches on `NativeFrame.Buffer is { } buffer` directly (`FlowDispatcher.cs:50`).
- Introduced by `6b9f7f4 perf: zero-allocation hot path for the packet pipeline`.
- Disposition: REMOVE. Confidence: High.

**A10 `TcpRedirectTable.TryResolveByTranslated`.**
- `public bool TryResolveByTranslated(Endpoint translatedTuple, DateTimeOffset now, out TcpRedirectAssociation? association) => TryFind(_byTranslatedListener, translatedTuple, now, out association);` (`TcpRedirectTable.cs:233-234`).
- Callers: zero; production resolves via `TryResolveByReverse`/other paths. Introduced around `18841ba`/`99a7054`/`1471699`.
- Disposition: REMOVE. Confidence: High.

**A11 `UdpAssociations.TryRemoveOriginal`.**
- `public bool TryRemoveOriginal(FlowKey originalKey)` (`UdpAssociations.cs:130-138`), full implementation with doc “Used when a session is torn down…”.
- Callers: zero. Introduced `d3ad72b`/`21f4294`.
- Disposition: REMOVE. Confidence: High.

**A12 `SetupExecutor.WorkerCount`.**
- `public int WorkerCount => _workerCount;` (`SetupExecutor.cs:107`).
- Callers: zero (the ctor only). Introduced `c75f30e`.
- Disposition: REMOVE (or make private if a debug log wants it). Confidence: High.

**A13 `Socks5UdpCodec.TryEncode(IPAddress, …)`.**
- `public static bool TryEncode(IPAddress destinationAddress, …) => TryEncode(IPAddressValue.From(destinationAddress), …)` (`Socks5Udp.cs:39-41`).
- Callers: zero. Production `Socks5UdpTransport` calls the `IPAddressValue` core at `:257,:301,:330`; benchmarks/tests use `Encode`.
- Disposition: REMOVE. Confidence: High.

**A14 `IPPrefix(IPAddress, int)` ctor.**
- `public IPPrefix(IPAddress network, int prefixLength) : this(IPAddressValue.From(network), prefixLength) {}` (`IPPrefix.cs:17-20`).
- Callers: zero. Repo-wide `new IPPrefix(` matches only the internal `:47` `IPAddressValue` construction; the config parser uses `IPPrefix.TryParse(string)` (`ConfigurationModels.cs:383`) and the tests use `TryParse` too (`EndpointAndPolicyTests.cs:69,178,189,202,210,214`).
- Why: the class doc says “The `IPAddress`-based members are cold edges kept for configuration parsing and tests”, but configuration does not use this ctor — the comment is stale. Introduced `6b9f7f4`.
- Disposition: REMOVE (and fix the class doc, which currently justifies a dead member). Confidence: High.

### B. Surface referenced only by tests/benchmarks

**B1 `IPUdpPacket.TryParse(ReadOnlyMemory, out UdpPacketView)` + `UdpPacketView`.**
- Signatures: `public static bool TryParse(ReadOnlyMemory<byte> frame, out UdpPacketView packet)` (`IPUdpPacket.cs:26-35`); `public readonly record struct UdpPacketView(…, ReadOnlyMemory<byte> Payload, int IPHeaderLength)` (`:8`).
- Production caller: none — `NdisPacketActionExecutor.cs:434` uses `IPUdpPacket.TryParseSpan` and the only in-file caller of `TryParse` is nothing (line 28 is `TryParseSpan`, invoked by `TryParse`).
- Test/bench callers: `tests/…/UdpRelayTests.cs:33,105,233,324`; `tests/…/UdpPacketParsingTests.cs:107,113,122,155,169,195`; `tests/…/ProtocolAuditTests.cs:64`; `benchmarks/…/Perf/ParserBenchmarks.cs:46`.
- Why: the XML doc at `:37-42` explicitly says the span twin was named apart “so byte[]-backed call sites keep binding to the memory view” — a signature-compatibility rationale for tests/benchmarks.
- Disposition: REMOVE `TryParse` (rewrite callers to `TryParseSpan`; `Payload` consumers switch to `Payload(frame)`). Then `UdpPacketView` has no callers and can be removed too. Confidence: High; watch for `UdpPacketView` explicit-type usage (rg shows none outside this file).

**B2 `PacketChecksums.TryRewriteUdpEndpoints`.**
- `public static bool TryRewriteUdpEndpoints(Span<byte> ethernetFrame, …)` (`PacketChecksums.cs:23`).
- Callers: tests only — `UdpPacketParsingTests.cs:121,134,168`, `ProtocolAuditTests.cs:49,66`. No production caller (UDP flow translation does not rewrite UDP endpoint headers in production).
- Disposition: REMOVE if the UDP rewrite oracle is no longer needed; otherwise KEEP as a public protocol utility with a doc note. Medium.

**B3/B4 `TcpResetBuilder` allocating wrappers.**
- `public static byte[]? BuildReset(IPAddress, …)` (`:32`) forwards to `BuildReset(IPAddressValue, …)` (`:49`); `public static byte[]? BuildResetFromSyn(…)` (`:113`).
- Production uses the span cores: `ClientResetInjector.cs:69` (`TryBuildReset`) and `:108` (`TryBuildResetFromSyn`).
- Callers of the wrappers: tests only — `TcpResetBuilderTests.cs:73,75,77,78` (BuildReset) and `:97,115,129,130,132` (BuildResetFromSyn).
- Disposition: REMOVE the wrappers; rewrite the two tests to the span cores. Confidence: High.

**B5/B6 `Socks5Messages` allocating wrappers.**
- `public static byte[] UsernamePassword(…)` (`Socks5State.cs:65`), `public static byte[] Request(…)` (`:98`).
- Production uses `WriteUsernamePassword` (`Socks5ControlConnection.cs:292`) and `WriteRequest` (`:202,210`).
- Callers: tests only — `Socks5ProtocolTests.cs:17,24,30`.
- Disposition: REMOVE wrappers. Confidence: High.

**B7 `Socks5UdpCodec.Encode` family.**
- `public static byte[] Encode(IPAddressValue, …)` (`Socks5Udp.cs:44`, comment: “Raw-address convenience over the span-writing encode; cold edges (tests, loopback servers)”); `Encode(IPAddress, …)` (`:51`); `Encode(string domain, …)` (`:69`).
- Production uses `TryEncode(IPAddressValue, …)` (`Socks5UdpTransport.cs:257,301,330`).
- Callers: tests (`Socks5ControlTimeoutTests.cs:155`; `Socks5UdpTransportSendTests.cs:53`; `UdpPacketParsingTests.cs:16,32,44,53,83`; `UdpReceiveResilienceTests.cs:124,130,145`) and benchmarks (`LoopbackSocks5UdpServer.cs:317`; `ParserBenchmarks.cs:28,67`).
- Disposition: REMOVE the `Encode` overloads (rewrite call sites to `TryEncode`; loopback servers can pre-allocate a buffer). Confidence: High.

**B8 `UdpFrameBuilder.TryBuild` wrappers.**
- `public static byte[]? TryBuild(IPAddress, …)` (`UdpFrameBuilder.cs:24`) forwarding to `TryBuild(IPAddressValue, …)` (`:36`); production uses `TryBuildInto(…Span<byte>…)` (`:63`) at `UdpResponseReinjector.cs:143`.
- Callers: tests only — `UdpRelayTests.cs` (multiple), `HotPathAllocationGateTests.cs:93`, `ProtocolAuditTests.cs:101,103`.
- Disposition: REMOVE wrappers (tests → `TryBuildInto`). Confidence: High.

**B9 Memory send chain (largest candidate).**
- `public ValueTask<bool> UdpProxyCoordinator.TrySendAsync(FlowKey, Socks5Server, ReadOnlyMemory<byte>, CancellationToken, long packetSequence = 0, long flowGeneration = 0, MacAddress clientMac = default)` (`UdpProxyCoordinator.cs:176`) → private `SendOnReadySessionAsync` (`:230`) → `UdpProxySession.SendAsync` (`UdpProxySession.cs:107`) → `IUdpProxyTransport.SendAsync` (`Socks5UdpTransport.cs:65`) → `Socks5UdpTransport.SendAsync`.
- Production caller: **none.** The capture path only calls the span twin `TrySendSpanAsync` (`UdpProxyCoordinator.Send.cs:21`) from `NdisPacketActionExecutor.cs:449`. Benchmarks call `TrySendSpanAsync` (`GcSoakScenario.cs:675`).
- Test/bench callers of `TrySendAsync`: 77 test references and 12 benchmark references (e.g. `HotPathAllocationGateTests`, `IdleExpirySweeperFailureTests`, `Socks5UdpAssociateTests`, `UdpSessionBenchmarks`, `GcSoakScenario`).
- Why: the memory entry predates the span bridge; the span entry was added to avoid materializing native buffers, and the memory entry stayed so tests/benchmarks kept their signatures.
- Disposition: REPLACE / FOLLOW-UP — decide whether the coordinator keeps a public memory entry (documented product API) or whether tests/benchmarks bind to the internal span entry via `InternalsVisibleTo`. Removing the chain would touch `IUdpProxyTransport` and the transport fakes, so route as a follow-up unless the product genuinely exposes `TrySendAsync`. Confidence in caller evidence: High.

**B10 `IPPrefix.Contains(IPAddress?)`.**
- `public bool Contains(IPAddress? address) => address is not null && Contains(IPAddressValue.From(address));` (`IPPrefix.cs:60`).
- Production uses `Contains(Endpoint)` from `Policy.cs:22`.
- Callers: tests only — `EndpointAndPolicyTests.cs:215,216,223`. Introduced `6b9f7f4`.
- Disposition: REMOVE. Confidence: High.

**B11 `NativeBufferPoolStats` + `NativeBufferPool.Stats`.**
- `public NativeBufferPoolStats Stats { get; }` (`NativeBufferPool.cs:62`); `public readonly record struct NativeBufferPoolStats(…)` (`:240`).
- Callers: benchmarks only — `benchmarks/…/Stability/GcSoakScenario.cs:300-303`. `WinForward.Core.csproj` declares no `InternalsVisibleTo`, so the member is public solely to let the benchmark read it.
- Disposition: REPLACE — add `<InternalsVisibleTo Include="WinForward.Benchmarks" />` to Core and make `Stats`/`NativeBufferPoolStats` internal; or KEEP and document it as the sanctioned benchmark diagnostic. Medium.

**B12 `PacketLease.Disposition`.**
- `public PacketDisposition Disposition => _disposition;` (`PacketRuntime.cs:115`).
- Production reads: none (`rg '\.Disposition' src` returns no hits). Tests: 39 references (flow-disposition assertions).
- Disposition: REMOVE or make internal; tests have IVT. Confidence: High that it is test-only, Medium on whether it is a useful public observability property.

**B13 `BoundedSetupQueue.Bytes`.**
- `public int Bytes => _bytes;` (`BoundedSetupQueue.cs:37`). Internal code uses `_bytes` directly; tests read `Bytes` (`CoreFlowStructuresTests`, `UdpSetupQueueTests`).
- Disposition: REMOVE or make internal. Confidence: High.

**B14 `LayeredCaptureRunner.HealthSignal`.**
- Public property (`LayeredCaptureRunner.cs:114`), doc “interception-health signal”. No production reader; only `LayeredCaptureRunnerHealthSignalTests.cs`.
- Disposition: REPLACE (keep the constructor seam, drop the getter) or KEEP documented. Medium.

**B15 `TcpProxyCoordinator.Table`.**
- `public TcpRedirectTable Table => _table;` (`TcpProxyCoordinator.cs:83`). Production passes the table into the ctor and does not read it back; tests read it (`TcpFragmentHandlingTests`, `TcpProxyCoordinatorCapacityTests`, `TcpReversePrefilterTests`).
- Disposition: REPLACE (expose only what tests need via a narrower internal accessor) or KEEP documented. Medium.

**B16 `SetupExecutor` diagnostics.**
- `PendingCount` (`:108`), `FreeCount` (`:109`), `EnqueuedCount` (`:113`), `CompletedCount` (`:114`), `RejectedCount` (`:115`), `OverflowAllocations` (`:118`).
- Production: none read (only `OverflowAllocations` is mentioned in a comment/balance identity). Tests read all but `OverflowAllocations` (`SetupExecutorTests.cs`).
- Disposition: REPLACE — make internal (tests have IVT) or narrow. Confidence: Medium.

**B17 `RuntimeCounters.RecordPoolRent` / `RecordPoolReturn`.**
- `public void RecordPoolRent(string poolName)` (`:100`), `public void RecordPoolReturn(string poolName)` (`:108`).
- Production deliberately avoids them: `Program.cs:245-247` explains the helpers rebuild key strings per call and wires the allocation-free pre-created key boxes instead. Callers: `RuntimeCountersTests.cs:121-166`, `RuntimeHeartbeatTests.cs:278-293`. (`GetPoolOccupancy` at `:120` is used in-file by `GetPoolOccupancies` → production, keep.)
- Disposition: REMOVE or make internal. Confidence: High.

**B18 `NdisCapturedPacket.FromCapture`.**
- `public static NdisCapturedPacket FromCapture(…)` (`NdisCapture.cs:10-14`). Production builds captured packets from the pool; callers are tests only (`FlowDispatcherExecutorTests`, `NdisApiAbiTests`).
- Disposition: REMOVE or make internal. Confidence: High.

**B19 `Socks5UdpTransport.CreateAsync` public 3-arg overload.**
- `public static async ValueTask<Socks5UdpTransport> CreateAsync(Socks5Server, SelfTrafficRegistry, CancellationToken)` (`:165-166`) forwards to the internal 7-param overload (`:168-176`) with `null, null`.
- Production calls the internal overload from `Socks5UdpTransportFactory.CreateAsync` (`:106`). All 3-arg calls are tests (`UdpReceiveResilienceTests.cs:118,172`; `Socks5UdpAssociateTests.cs:111`; `Socks5ControlTimeoutTests.cs:150`; `Socks5UdpTransportSendTests.cs:37,150,187`).
- Disposition: REMOVE the public overload; tests can call the internal overload (IVT) or the factory. Confidence: High.

**B20 `Socks5UdpReceiveResult.Received` / `Skipped`.**
- `public static Socks5UdpReceiveResult Received(Socks5UdpDatagram)` (`:49`), `Skipped(Socks5UdpReceiveSkipReason)` (`:52`).
- `Received` is used by benchmarks (`EchoReceiver`, `GcSoakScenario`) and tests; production constructs the struct directly. `Skipped` tests only.
- Disposition: KEEP (or make the factories internal if benchmarks are reworked). Confidence: Medium.

**B21 `AdapterSelector`.**
- `public static class AdapterSelector` (`WindowsAdapter.cs:10`) with `TryResolve`. Callers: tests only — `AdapterSelectorTests.cs:18,20`; production adapter selection goes through `WindowsAdapterInventory` (`Program.cs:352`), `AdapterIdentity`.
- Disposition: REMOVE if the product selector is truly `WindowsAdapterInventory`; otherwise KEEP documented as a public utility. Confidence: Medium.

### C. Internal seams consumed by the owning module's own tests (KEEP)

These match PRD R2 (“an internal seam used by a module's own tests is legitimate”) and are recorded so AC4 can be closed without removing them. Evidence summarized:

- **C1 `RunIterationForTests`** (`NdisCapture.cs:169-174`, comment “Test seam: runs exactly one synchronous loop iteration…”): callers `NdisCapturePumpTests.cs:299,304`; delegates to the same private `RunIteration` the production loop uses. KEEP.
- **C2 pump telemetry** (`PumpThread` `:166`, `TransientReadRetryCount` `:369`, `TransientReadIncidentCount` `:372`, `LastDegradedNativeErrorCode` `:378`): no production consumer (production degraded state lives on `InterceptionHealthMonitor`); callers are `NdisCapturePumpTests`/`NdisCaptureResilienceTests`. KEEP as module diagnostics; if AC4 wants zero test-only members, convert to `internal`-only via a test hook object.
- **C3 `HighResolutionTimerScope(Func<uint,uint> beginPeriod, Func<uint,uint> endPeriod)`** (`:44`; comment “Test seam over the winmm begin/end pair”): public ctor forwards to it at `:35`; tests inject counting delegates. KEEP.
- **C4 `NdisAdapterListWatcher()`** (`:50`, comment “Driver-free seam over the wait-any/cancel/dispose semantics (unit tests)”) and `WaitForSignal` (`:69`, in-file caller `:65` + tests). KEEP.
- **C5** `PendingPassCount` (`:295`), `ImmediateSendLaneOverflowCount` (`:316`), `[Conditional("DEBUG")] DebugAssertNoPendingPasses` (`:324-333`): all documented diagnostic surfaces; production never reads them; tests assert lane-batch invariants. KEEP.
- **C6 `DegradedAdapterCount`** (`MultiAdapterCaptureLoop.cs:48`): no production reader (production exposes `PumpState` at `:55`, read by `Program.cs:309`); test `NdisCaptureResilienceTests.cs:215`. KEEP.
- **C7** `DrainPendingSetupsAsync` (`:633`, comment “internal test/diagnostic seam”, 18 test references in 8 files), `PendingSetups` (`:622`, 12 refs), `CapacityResetCooldowns` (`:636`, 1 ref), `CapacityRejectionCount` (`:104`, 4 refs). KEEP.
- **C8** `SetupCooldownCountForDiagnostics` (`:104`), `PendingSetupBytesForDiagnostics` (`:116`), `SetupBudgetRejectionCount` (`:119`), `SetupTtlExpiredCount` (`:122`), `SetupStampsRefreshedCount` (`:125`) — all doc’d “for tests and diagnostics”; 2–12 test refs each. KEEP.
- **C9** `TcpPendingSynSetupIndex.ActiveCount/ChargedBytes/RejectionCount/TtlExpiredCount/CooldownCount` (`TcpPendingSynSetup.cs:92-107`), `TcpRedirectTombstoneTable.QueueCountForDiagnostics` (`:48`), `UdpSetupQueueBudget.PendingBytes/RejectionCount` (`:41,44`), `UdpSessionSetup.TtlExpiredCount/StampsRefreshedCount` (`:76,79`): `ActiveCount` is read by production (`TcpProxyCoordinator.cs:164`); the rest are surfaced through test-only coordinator properties. `DisposeLimiter` (`UdpSessionSetup.cs:211`) is production-called at `UdpProxyCoordinator.cs:386`. KEEP.
- **C10 `TcpResetCooldownTable.Remove`** (`:53`, comment “internal so tests can advance the window”), surfaced via `ClientResetInjector.CapacityResets` (`:50`) and `TcpProxyCoordinator.CapacityResetCooldowns` (`:636`); `TcpProxyCoordinatorCapacityTests.cs:419` advances it. KEEP (a `TimeProvider`-based window would be the cleaner seam — optional follow-up).
- **C11** `IsAcceptableRelaySource` (`:393`), `IsPossiblyTruncated` (`:404`), `ClassifyReceiveFault` (`:422`) are all called from production `ReceiveAsync` (`:372,378,379`) *and* directly by tests; `DisableUdpConnectionReset` (`:412`) is the in-file default action (`:130`). KEEP.
- **C12** `UnicastAddressInventory` internals (`ReadRows` `:63`, `ParseRows` `:79`, `ValidateEntryCount` `:101`, `GroupFingerprints` `:113`, `BuildFingerprint` `:141`, `ResolveInterfaceGuid` `:151`): `ReadRows`/`ParseRows`/`ValidateEntryCount`/`GroupFingerprints` execute on the production read path; only helpers are additionally exercised by `UnicastAddressInventoryTests`. KEEP.
- **C13** `IPHelperTables.ValidateRowCount` (`ProcessAttribution.cs:293`, 4 in-file prod calls), `ReadRow` (`:307`, prod), `IPHelperAbi.AssertManagedLayout` (`IPHelperAbi.cs:26`, test), native struct fields (`:108-116`, layout only). KEEP.
- **C14** `MaxPacketsPerSendRequest` (`NdisApiDriver.cs:28`, in-file prod), `BuildMultiRequest` (`:203`, prod + tests). KEEP.
- **C15** `TryRewriteTcpEndpointsFullRecompute` (`PacketChecksums.cs:176`, doc “Full-segment endpoint rewrite kept as the property-test oracle…”): single test caller `TcpEndpointRewriteIncrementalTests.cs:159`. KEEP per R2 (documented oracle).
- **C16** `NdisNativeCallGate` internals (`:12,14,16,28`): driver path + `NdisAdapterGateMapTests`. KEEP.

### D. Optional parameters added for tests/benchmarks

- **D1 `AdapterTransientRetryLogGate(IRuntimeLogger logger, Func<long>? ticksProvider = null)`** (`AdapterTransientRetryLogGate.cs:13`): production `Program.cs:278` passes one arg; `AdapterTransientRetryLogGateTests.cs:30` injects a `ScriptedClock.Provider`. Disposition: REPLACE with a `TimeProvider` seam (consistent with the rest of Runtime) or KEEP. Confidence: High.
- **D2 `RuntimeHeartbeat(…, Func<RuntimeGcSnapshot>? gcSnapshotProvider = null)`** (`RuntimeHeartbeat.cs:72`): production `Program.cs:303-310` never passes it; tests pass fakes at `RuntimeHeartbeatTests.cs:125,212,238,283`. The XML doc at `:25` states “tests inject a fixed source so collection-delta coverage is deterministic”. Disposition: REPLACE (test-only seam record) or KEEP. Confidence: High.
- **D3 `WindowsProcessAttributor(TimeSpan? retryDelay = null, int cacheCapacity = 1024)`** (`ProcessAttribution.cs:21`): only production constructs it (`DurableCaptureBundle.cs:249`), and no caller overrides `retryDelay`; `cacheCapacity` has no override either. Disposition: REMOVE the never-overridden optional or make it a real configuration knob. Confidence: Medium.
- **D4 `UdpProxyCoordinator` internal ctor seams** (`UdpProxyCoordinator.cs:52`, `TimeProvider`, `Func<ValueTask>? beforeExpiryRecheck`, `long setupQueueGlobalByteBudget`): tests override `setupQueueGlobalByteBudget` (`UdpSetupQueueTests.cs:289,326,347`); `beforeExpiryRecheck` is a test visibility hook; `timeProvider` is a genuine DI seam used by production. Disposition: KEEP. Confidence: High.
- **D5 `TcpProxyRelay` / `TcpProxyRelayFactory` optionals** (`TcpProxyRelay.cs:121`, `:12`): production passes `logger`/`pumpBufferPool` (`:54`); tests/benchmarks use the 3-arg form. Disposition: KEEP (defaults are the production composition fallbacks). Confidence: High.
- **D6 `TcpRedirectSession(… long flowGeneration = 0)`** (`TcpProxyCoordinator.cs:662`): production passes it (`TcpRedirectSetup.cs:195`); two tests omit it (`TcpRelayObservationTests.cs:114`, `TcpRelayEndResetTests.cs:205`) purely to keep old call shapes. Disposition: REMOVE the default (make it required) and update the two tests. Confidence: Medium.
- **D7 `TcpRedirectTable(int? capacity = null)`** (`TcpRedirectTable.cs:172`): production passes `configuration.TcpFlowCapacity` (`DurableCaptureBundle.cs:112`); many tests use the parameterless form. Disposition: KEEP (default is a real value; nullable just allows omission). Confidence: High.
- **D8 `TcpPendingSynSetupIndex(int? capacity = null, long? byteBudget = null)`** (`TcpPendingSynSetup.cs:83`): production uses defaults; tests override (`TcpPendingSynSetupTests.cs:67,84,187`). Disposition: KEEP. Confidence: High.
- **D9 `DurableCaptureBundle` internal ctor + optional pools** (`DurableCaptureBundle.cs:52-64`): doc explicitly says internal-for-tests fabrication over fakes; `DurableCaptureBundleTests` is the only external caller. Disposition: KEEP (documented seam). Confidence: High.
- **D10 `LayeredCaptureRunner` optionals** (`LayeredCaptureRunner.cs:79-83`): `onScopeInstalled` is production-wired (`Program.cs:291`), `interceptionHealthMonitor` production-wired, `timeProvider` genuine DI; `minimumRefreshInterval`/`periodicRefreshInterval` are overridden only by tests (`LayeredCaptureRunnerRefreshTests`, `LayeredCaptureRunnerPeriodicRefreshTests.cs:39,62,91,115`). Disposition: KEEP the production seams; consider an options record for the two test-only intervals. Confidence: Medium.
- **D11 `UdpAdapterTargetSource(host = null, byStableId = null)`** (`UdpAdapterTargetSource.cs:48`): production uses the parameterless form (`DurableCaptureBundle.cs:182`); tests pass values. Disposition: KEEP (genuine empty-target default). Confidence: High.

---

## Caveats / false positives / not found

- **No `[Obsolete]` anywhere in `src/`** (rg over all source returned nothing).
- **`NdisApiAbi.UpstreamVersion/UpstreamCommit`** might be intentionally documented provenance rather than an API; decision is product/policy, but as code they are dead.
- **False positives from the automated scan, verified NOT candidates**: `NativeMemoryManager.GetSpan/Pin/Unpin` (`NativeBufferPool.cs:214-223`) are `MemoryManager<byte>` overrides invoked by the BCL, not by explicit callers; `IPHelperAbi.Mib*`/`MibUnicastIpAddressRow` fields (`:108-116`) are interop layout; `IPAdapterInfo`/`IPAdapterUnicastInfo`/`UnicastAddressObservation` are public/internal types but have in-file production uses; `InterceptionHealthMonitor.DefaultWindow`/`DefaultTriggerCooldown`/`DegradedTriggerSpacing`/`DegradedAfterConsecutiveTriggers` (`:37-40`) are used in-file by `ReportFailure` (could be `private`, but they are not compat APIs).
- **`NdisPacketActionExecutor.RetireLanesExcept`** was initially flagged by the token scan but has a production caller (`DurableCaptureBundle.cs:395`) and is KEEP.
- **`TcpProxyCoordinator.HandleSynAsync`/`HandleReverseAsync`** appear test-heavy because the production dispatcher calls the interface members `ITcpReverseHandler.HandleReverseIfApplicableAsync`/`WantsPacket` (`FlowDispatcher.cs:249`); the coordinator methods are internally reached (`TcpProxyCoordinator.cs:517,540,549`) — KEEP.
- **`RuntimeCounters.GetPoolOccupancy`** is used by in-file `GetPoolOccupancies` (production heartbeat path), unlike its sibling `RecordPoolRent`/`RecordPoolReturn`.
- **Not exhaustively verified**: the behavioural impact of removing the B9 memory send chain and the B11 stats surface (both may be intentional public library surfaces); these are marked REPLACE/FOLLOW-UP rather than REMOVE.
- Line numbers are from the working tree at commit `0463b9b` (task base `master`).
