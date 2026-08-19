# Research: WinForward forwarding performance hotspots

- **Query**: Read-only performance hotspot analysis of the WinForward repository, covering memory allocations, packet copies, parsing/rebuilding, async/socket overhead, locking/contention, logging, and end-to-end forwarding latency; include exact file:line evidence, suspected severity/confidence, limitations, and concrete profiling/benchmark experiments.
- **Scope**: mixed (repository source, tests, project settings, Trellis specifications, README, and external profiling/NDISAPI references)
- **Date**: 2026-08-17

## Findings

### Executive summary

The forwarding path is allocation- and copy-heavy by construction, and several shared locks or inline awaits can serialize work across adapters. The strongest evidence-backed hotspots are:

| Hotspot | Evidence | Suspected severity | Confidence |
|---|---|---:|---:|
| Single-packet polling, queue probe, and one global native-call gate | `NdisCapture.cs:35-50`; `NdisApiDriver.cs:100-121,193-243`; `MultiAdapterCaptureLoop.cs:19-35` | High; directly tied to latency and adapter scaling | High |
| Full-frame managed/native copies on capture and reinjection | `CapturePacketProcessor.cs:32-36`; `NdisPacketActionExecutor.cs:35-43`; `NdisApiDriver.cs:246-289` | High at packet rate; applies to pass traffic and each synthetic packet | High |
| Repeated parser/address allocations and TCP/UDP payload/frame rebuilding | `IpTcpUdpPacket.cs:43-129`; `IpUdpPacket.cs:67-74`; `TcpProxyCoordinator.cs:214-237,344-357,391-416`; `Socks5Client.cs:456-470` | High for proxied traffic; medium for pass-only traffic | High |
| Two full scans of `FlowTable` on every new-flow miss | `FlowDispatcher.cs:86-108`; `Domain.cs:147-175,223-263` | High to critical as flow cardinality approaches 65,536 | High |
| Wildcard self-traffic scan under one lock | `SelfTrafficRegistry.cs:22-40`; proxy registrations in `Socks5Client.cs:411-428` and `TcpProxyCoordinator.cs:256-270` | High with many active proxy sessions; otherwise low | High |
| Host process attribution enumerates the whole OS owner table on new flows | `Program.cs:237-242`; `FlowDispatcher.cs:105-108,149-153`; `ProcessAttribution.cs:179-275` | High for host new-flow rate; medium when flows are reused | High |
| Inline capture-pump handler and first UDP relay setup | `NdisCapture.cs:37-50`; `UdpProxyCoordinator.cs:54-109,170-197`; `Socks5Client.cs:63-135,164-180` | High for first-datagram latency and queue growth | High |
| Per-session UDP receive buffer and per-chunk TCP timeout CTS objects | `UdpProxyCoordinator.cs:487-505`; `TcpProxyRelay.cs:112-150` | High memory for many UDP sessions; high allocation rate for TCP throughput | High |
| Logging construction below threshold and synchronous trace output | `FlowDispatcher.cs:73-76,187-227`; `NdisPacketActionExecutor.cs:136-145`; `RuntimeLogging.cs:57-90` | Medium at info/debug; high at trace | High |
| Periodic expiry scans hold the same table gates as packet lookup | `IdleExpirySweeper.cs:50-78`; `Domain.cs:209-220`; `TcpRedirectTable.cs:193-205`; `UdpAssociations.cs:135-147` | Medium; high-cardinality latency spikes are plausible | High |

The repository contains one hardware-observed baseline: the Trellis NDISAPI specification records the current polling pump as adding approximately 5–15 ms RTT under tunnel mode, with an observed average of 8 ms versus 0.6 ms direct (`.trellis/spec/backend/windows-ndisapi.md:92`). No benchmark project, allocation benchmark, throughput result, packet-rate result, or automated latency measurement was found in the searched source/tests/settings.

### Files found

| File path | Description |
|---|---|
| `src/WinForward.NdisApi/NdisCapture.cs` | Per-adapter polling pump and handler ownership boundary. |
| `src/WinForward.NdisApi/NdisApiDriver.cs` | Queue inspection, single-packet native read/send, global native-call gate, and unmanaged packet buffer. |
| `src/WinForward.Runtime/MultiAdapterCaptureLoop.cs` | One pump/task per adapter, sharing one driver instance. |
| `src/WinForward.Runtime/CapturePacketProcessor.cs` | Native-to-managed frame copy, parse/classification, packet lease. |
| `src/WinForward.Runtime/FlowDispatcher.cs` | Self-traffic, reverse, flow-table, attribution, policy, action sequencing, and per-packet logs. |
| `src/WinForward.Core/Domain.cs` | `FlowKey`, `FlowState`, and locked `FlowTable`. |
| `src/WinForward.Runtime/SelfTrafficRegistry.cs` | Locked exact/reverse/wildcard self-traffic lookup. |
| `src/WinForward.Runtime/NdisPacketActionExecutor.cs` | Pass/block/proxy disposition and reinjection/copy boundary. |
| `src/WinForward.Protocols/IpTcpUdpPacket.cs` | Initial flow parser and endpoint extraction. |
| `src/WinForward.Protocols/IpUdpPacket.cs` | UDP payload parser that materializes a payload array. |
| `src/WinForward.Runtime/TcpProxyCoordinator.cs` | TCP redirect, frame copies, endpoint rewrites, reverse path, and session lifecycle. |
| `src/WinForward.Runtime/UdpProxyCoordinator.cs` | UDP flow/session dictionary, setup, send, receive, and response handoff. |
| `src/WinForward.Runtime/Socks5Client.cs` | SOCKS5 control connection, UDP framing, sockets, and setup awaits. |
| `src/WinForward.Runtime/TcpProxyRelay.cs` | Bidirectional stream relay and per-I/O timeout handling. |
| `src/WinForward.Runtime/UdpResponseReinjector.cs` | SOCKS5 UDP response frame rebuilding and NDIS reinjection. |
| `src/WinForward.Protocols/UdpFrameBuilder.cs` | Complete Ethernet/IP/UDP response-frame allocation and checksum rebuild. |
| `src/WinForward.Runtime/RuntimeLogging.cs` | Structured formatter and synchronous stderr writer. |
| `src/WinForward.Windows/ProcessAttribution.cs` | Windows TCP/UDP owner-table enumeration and process identity cache. |
| `src/WinForward.Cli/Program.cs` | Production composition: driver, scope, attribution, coordinators, capture loop, sweeper. |
| `tests/WinForward.Core.Tests/NdisApiAbiTests.cs` | ABI and native-call serialization correctness tests. |
| `tests/WinForward.Core.Tests/CapturePipelineTests.cs` | Parser, dispatcher, pass/reinject, and lease correctness tests. |
| `tests/WinForward.Core.Tests/TcpProxyRelayTests.cs` | Relay half-close and failure behavior tests, not throughput tests. |
| `tests/WinForward.Core.Tests/UdpProxyCoordinatorTests.cs` | UDP reuse/concurrency/expiry/failure tests, not allocation tests. |
| `tests/WinForward.Core.Tests/RuntimeLoggingTests.cs` | Threshold and formatting correctness tests. |

## Capture, copy, and disposition path

The production composition creates one `NdisCapturePump` for each scoped adapter, but every pump receives the same `NdisApiDriver` (`Program.cs:242`; `MultiAdapterCaptureLoop.cs:19-26`). The flow for a captured packet is:

1. `NdisCapturePump.RunAsync` allocates an unmanaged `NdisPacketBuffer` on every loop iteration (`NdisCapture.cs:37-40`), calls `TryReadPacket`, sleeps for the configured delay when the queue is empty (`NdisCapture.cs:40-43`), and awaits the packet handler inline (`NdisCapture.cs:49-50`). The default delay is 1 ms (`NdisCapture.cs:25-33`).
2. `TryReadPacket` first calls `GetAdapterPacketQueueSize` and only then calls `ReadPacket` if the queue is reported non-empty (`NdisApiDriver.cs:100-121`). Both native calls are inside the same `NdisNativeCallGate` lease.
3. The processor copies the native span into a new managed array before the native buffer is disposed (`CapturePacketProcessor.cs:32-36`). It then parses/classifies and awaits the dispatcher (`CapturePacketProcessor.cs:46-56`).
4. The dispatcher checks self traffic, TCP reverse handling, existing flow resolution, attribution for a new host flow, flow claim, policy, and action in that order (`FlowDispatcher.cs:73-125`).
5. Pass creates another unmanaged `NdisPacketBuffer`, copies the managed frame into it, and calls the directional send (`NdisPacketActionExecutor.cs:35-44`; `NdisApiDriver.cs:124-145`). Block still incurred the capture-side managed frame copy, but does not reinject (`NdisPacketActionExecutor.cs:47-51`).

For ordinary pass traffic, the explicit full-frame copy boundaries are therefore native capture buffer → managed `byte[]` (`CapturePacketProcessor.cs:34`) and managed `byte[]` → new native buffer (`NdisApiDriver.cs:276-289`). A fresh unmanaged 1,566-byte `IntermediateBuffer` allocation occurs at `NdisPacketBuffer` construction (`NdisApiDriver.cs:246-254`) for every poll, including an empty poll; pass adds a second such buffer at `NdisPacketActionExecutor.cs:39`.

### Native polling and serialization

- The `NdisNativeCallGate` uses one monitor for every driver operation (`NdisApiDriver.cs:193-230`). It covers version, adapter enumeration, mode operations, queue inspection, reads, sends, and close (`NdisApiDriver.cs:17-25,51-61,75-98,100-152`). The test `NativeCallGateSerializesConcurrentOperations` asserts `MaxConcurrentCalls == 1` (`tests/WinForward.Core.Tests/NdisApiAbiTests.cs:121-145`).
- Because all adapter pumps share the driver, multi-adapter capture cannot perform native queue checks/reads/sends concurrently even though `Task.WhenAll` starts one pump task per adapter (`MultiAdapterCaptureLoop.cs:29-40`). A reinjection from one pump competes for the same gate as a read from another adapter.
- The pump does not batch: one `ReadPacket` and one handler await correspond to one loop iteration (`NdisCapture.cs:35-50`). The official WinpkFilter references found during research document an event signaled when a queue is non-empty (`SetPacketEvent`) and a `ReadPackets` API whose request contains multiple packet buffers (`External References` below). The repository specification separately records the event/batch approach as the documented future comparison and the 5–15 ms measured RTT effect (`.trellis/spec/backend/windows-ndisapi.md:92`).
- Capture scope may include every MSTCP-bound adapter when the policy contains an unconstrained rule or no scoped rule (`CaptureAdapterScopeResolver.cs:44-51`). Thus idle polling/native gate traffic scales with adapter count even when only one adapter carries the workload.

**Assessment**: high severity for latency and multi-adapter throughput; high confidence for the code behavior, with the 5–15 ms effect being the only repository-recorded measurement rather than a new measurement from this analysis.

## Managed allocation and packet-copy observations

### Per-packet object and array materialization

The capture processor creates, at minimum, a managed frame array and a `PacketLease` per packet (`CapturePacketProcessor.cs:34-35`). A parseable packet also creates two `IPAddress` objects in `IpTcpUdpPacket.TryParse` for IPv4 (`IpTcpUdpPacket.cs:56-72`) or IPv6 (`IpTcpUdpPacket.cs:75-99`). The parser comment states “No allocation occurs on the parse path” (`IpTcpUdpPacket.cs:39-42`), but the implementation constructs those `IPAddress` instances; no payload array is created by this first parser.

`PacketFlowClassifier.ClassifyFlow` then wraps the endpoints into a `FlowContext` record object (`PacketFlowClassifier.cs:20-29`). Existing-flow resolution updates the packet using a record `with` expression (`FlowDispatcher.cs:86-101`), which creates a replacement `CapturedFlowPacket` object for packets where a flow generation is attached. The exact number of async state-machine allocations depends on whether each `ValueTask` completes synchronously, so that part needs measurement rather than source-only byte accounting.

The `PacketLease` itself is allocation-free after construction and uses `Interlocked.Exchange` to enforce one completion (`PacketRuntime.cs:10-26`). `BoundedSetupQueue` explicitly copies each enqueued frame (`PacketRuntime.cs:29-53`), but no production reference to that queue was found in the searched runtime sources; it is covered by a unit test (`tests/WinForward.Core.Tests/FlowAndConfigurationTests.cs:246-255`).

### Pass path

`NdisPacketActionExecutor.PassAsync` creates a new unmanaged buffer and copies the complete managed frame into the pinned ABI buffer (`NdisPacketActionExecutor.cs:35-43`; `NdisApiDriver.cs:276-289`). This is present for unchanged pass traffic, self traffic, UDP reverse response traffic, and “not relevant” TCP proxy packets that are explicitly passed (`NdisPacketActionExecutor.cs:72-79`).

### TCP proxy path

- New redirect setup copies the captured frame to a second managed array before rewriting (`TcpProxyCoordinator.cs:204-218`). The injector then allocates another unmanaged ABI buffer and copies the rewritten frame (`TcpRedirectInjector.cs:9-20`).
- Existing client-to-listener data repeats the full managed copy and rewrite (`TcpProxyCoordinator.cs:344-357`).
- Reverse redirect traffic repeats the same full managed copy and endpoint rewrite (`TcpProxyCoordinator.cs:377-416`).
- Host-shaped MAC swapping allocates a fresh six-byte temporary array per rewrite (`TcpProxyCoordinator.cs:305-314`).
- Each initial SYN is parsed again by `ClassifyTcpSyn` (`TcpProxyCoordinator.cs:466-485,499-517`) and then parsed again by `RecordClientSyn` before the bounded original-frame copy (`TcpProxyCoordinator.cs:321-328`). Thus the first SYN can execute the generic capture parser, the SYN classifier parser, and the SYN-record parser; each parser constructs source/destination `IPAddress` instances.
- Endpoint rewriting recomputes IPv4 header and TCP checksums over the header/segment (`PacketChecksums.cs:90-112,115-131,150-159`). The checksum pass is allocation-free but CPU work scales with the complete TCP segment length, including payload.

**Assessment**: high severity for proxied packet CPU/allocation rate; high confidence for the explicit copies and temporary allocation. The amount of OS-level copying performed by NDISAPI/socket stacks is not visible in this repository and requires ETW or packet-capture measurement.

### UDP proxy path

- The generic parser first extracts endpoints (`CapturePacketProcessor.cs:48-56`; `IpTcpUdpPacket.cs:43-129`). The UDP executor parses the frame again (`NdisPacketActionExecutor.cs:103-109`). `IpUdpPacket.TryParse` materializes the datagram payload with `ToArray()` (`IpUdpPacket.cs:67-74`).
- The executor also copies the Ethernet source MAC to a new six-byte array on every proxied datagram (`NdisPacketActionExecutor.cs:112-119`), including subsequent datagrams whose session already owns its original client MAC.
- `Socks5UdpTransport.SendAsync` allocates a new SOCKS5 UDP frame and copies the payload into it (`Socks5Client.cs:456-460`; `Socks5UdpCodec.Encode` at `Socks5Udp.cs:11-22`).
- A response is received into a reusable 65,535-byte session buffer (`UdpProxyCoordinator.cs:487-495`), but `Socks5UdpCodec.TryDecode` allocates a new payload array for each decoded response (`Socks5Udp.cs:38-62`). `UdpFrameBuilder.TryBuild` then allocates a complete Ethernet/IP/UDP frame and copies the payload into it (`UdpFrameBuilder.cs:23-79,112-120`). `UdpResponseReinjector` copies that frame into a new native ABI buffer before reinjection (`UdpResponseReinjector.cs:73-102`).
- The dispatcher has an explicit reverse-response pass branch (`FlowDispatcher.cs:86-101`), and the NDISAPI specification records that relay responses must be reinjected and then recognized as reverse-of-stored-key traffic (`.trellis/spec/backend/windows-ndisapi.md:179-183`). Where the driver recaptures those synthetic/self packets, the response path adds another capture managed-array copy and pass reinjection copy.

**Assessment**: high severity for UDP payload-rate allocation/copy cost, especially for larger datagrams; high confidence for each managed allocation/copy, medium confidence for the exact number of recaptured legs because that depends on driver/host behavior.

## Flow lookup, policy, and locking/contention

### FlowTable miss behavior

`FlowDispatcher.DispatchAsync` first calls `_flows.TryResolve` (`FlowDispatcher.cs:86-103`). For a miss, it attributes the process and then calls `_flows.TryClaimResolved`, whose implementation calls `TryResolveLocked` again before deciding/adding (`FlowDispatcher.cs:105-113`; `Domain.cs:161-175`). `TryResolveLocked` performs four keyed lookups and then scans every value in `_states` when those lookups miss (`Domain.cs:223-263`). The scan is intentionally manual to avoid per-packet LINQ allocation (`Domain.cs:249`).

Consequently, a genuinely new flow absent from all direct/reverse/origin-flipped keys reaches the adapter-agnostic scan once in the initial resolve and again inside the atomic claim. The scan is protected by the one `_gate` lock (`Domain.cs:118-152`). At the default capacity of 65,536 (`Domain.cs:120-128`; `FlowDispatcher.cs:50`), new-flow admission can become an O(number-of-existing-flows) critical section twice per new flow. Existing flows that hit a direct or flipped key avoid the value scan but still take the table lock.

`PolicySnapshot.Evaluate` and `EvaluateForwarded` linearly walk the configured rules (`Policy.cs:41-59`). Matchers may linearly scan remote networks and remote-port intervals (`Policy.cs:14-24`). This is primarily a new-flow cost because the decision is cached in `FlowState`; packet-rate impact depends on flow churn and rule count.

**Assessment**: high severity at high flow cardinality/churn, potentially critical near table capacity; high confidence in the repeated scan and lock sequence. No benchmark measures the slope.

### Self-traffic registry

`SelfTrafficRegistry.IsOwned` constructs the observed key and reverse key, checks both dictionary indexes under `_gate`, then scans every registered key for wildcard matches (`SelfTrafficRegistry.cs:22-40`). The source comment describes the entry count as “tiny” (`SelfTrafficRegistry.cs:30-33`), but the production composition registers one TCP listener tuple per redirect (`TcpProxyCoordinator.cs:256-270`), a TCP upstream control tuple per relay (`TcpProxyRelay.cs:27-31`), and UDP control/relay tuples per UDP session (`Socks5Client.cs:411-428`). The exact scan length therefore follows active proxy-session cardinality and wildcard registrations.

The self-traffic check runs before reverse handling and flow lookup for every packet (`FlowDispatcher.cs:73-84`). A non-self packet that misses the two exact entries can take the registry lock and scan all wildcard candidates. A self packet may also take the same lock, although it usually returns on an exact/reverse match.

**Assessment**: high severity for many active sessions, low-to-medium for a small session set; high confidence in O(active registrations) behavior and the shared lock.

### Process attribution

The production dispatcher is always composed with `WindowsProcessAttributor` (`Program.cs:237-241`). For every new host flow with no preexisting identity, `AttributeProcessAsync` calls the attributor before the flow claim (`FlowDispatcher.cs:105-108,149-153`); the decision does not first check whether the policy contains a process selector.

`FindAsync` reads the complete TCP or UDP owner table, waits the configured 2 ms, and reads it again when the first lookup returns no owner (`ProcessAttribution.cs:21-38`). `ReadTcp4/6` and `ReadUdp4/6` allocate an unmanaged table buffer, managed row arrays, and `IPAddress`/`Endpoint` projections (`ProcessAttribution.cs:179-255,258-279`). The final owner match uses LINQ and materializes a distinct PID array (`ProcessAttribution.cs:179-191`). Process identity itself is cached by PID plus creation time, but owner-table enumeration occurs before the identity cache lookup (`ProcessAttribution.cs:28-38,61-85`).

Forwarded flows skip attribution because the origin check is host-only (`FlowDispatcher.cs:149-153`), which creates a measurable host-versus-forwarded comparison axis.

**Assessment**: high severity for host new-flow latency/CPU and medium-to-high allocation pressure; high confidence in the synchronous placement and table materialization. Actual owner-table sizes and Windows API durations require a Windows run.

### Coordinator and table locks

- `TcpRedirectTable` protects all indexes with one `_gate` (`TcpRedirectTable.cs:82-87,113-142,154-225`). TCP handling can call reverse-candidate lookup, reverse resolution, and original-flow resolution in separate operations (`TcpProxyCoordinator.cs:451-497`), each taking the table gate.
- `UdpAssociationTable` protects original and relay indexes with one `_gate`; lookup, touch, claim, remove, and expiry all use it (`UdpAssociations.cs:25-161`).
- `UdpProxyCoordinator` protects the flow-to-session task dictionary with `_gate` (`UdpProxyCoordinator.cs:54-76,111-167`). A successful send then enters `UdpProxySession._activityGate`, performs socket send, re-enters `_activityGate`, and updates the association through the activity observer (`UdpProxyCoordinator.cs:425-443,522-531`).
- `TcpProxyCoordinator` protects its session dictionary and setup-drain counters with `_gate` (`TcpProxyCoordinator.cs:97-117,256-271,857-871`). Most setup network awaits occur outside that lock, but each packet still crosses table/session gates.
- `ConsoleRuntimeLogger` serializes all writes with `_gate` and performs synchronous `TextWriter.WriteLine` (`RuntimeLogging.cs:79-90`).

**Assessment**: medium severity for ordinary small-cardinality traffic, high under multi-adapter/high-session/high-log load; high confidence in the lock topology. Contention duration and scheduler impact are not measured in the repository.

## Async and socket overhead

### Capture-pump backpressure

The pump awaits `_handler` before polling again (`NdisCapture.cs:49-50`). For pass/block, the handler may complete synchronously; for a new UDP proxy datagram it can await complete session creation and send (`NdisPacketActionExecutor.cs:54-59,103-125`; `UdpProxyCoordinator.cs:54-109`). This means the adapter’s capture loop is not merely a reader: its next read waits behind dispatcher, policy, attribution, proxy setup, and socket send work. Other adapter pumps continue as separate tasks, but their native operations contend on the shared driver gate.

The first UDP session creates a SOCKS5 control connection, authenticates, sends UDP ASSOCIATE, creates/binds a UDP socket, and registers self traffic before the first relay send (`Socks5Client.cs:396-433`; `Socks5ControlConnection.ConnectAsync` at `Socks5Client.cs:63-135`; `UdpProxyCoordinator.cs:170-197`). TCP redirect setup itself awaits listener allocation and local injection inline (`TcpProxyCoordinator.cs:132-184,204-253`), while accepted-socket upstream setup runs in the background accept loop (`TcpProxyCoordinator.cs:663-720`).

**Assessment**: high severity for first-packet latency and queue buildup during slow attribution/proxy setup; high confidence in inline-await behavior. Network latency is environment-dependent.

### TCP relay

Each TCP relay has two 8,192-byte buffers, one for each direction (`TcpProxyRelay.cs:112-150`). For every read and every write operation, `PumpAsync` creates a linked `CancellationTokenSource`, calls `CancelAfter(30 minutes)`, awaits the I/O, and disposes the CTS (`TcpProxyRelay.cs:112-140`). This is an allocation/timer-registration path proportional to stream chunk count, not just connection count.

The relay’s two directional tasks race via `Task.WhenAny`, then use `Task.WhenAll` and cancellation to coordinate half-close/failure (`TcpProxyRelay.cs:77-109`). Existing tests verify half-close and sibling cancellation (`tests/WinForward.Core.Tests/TcpProxyRelayTests.cs:12-49`), but do not report chunk rate, allocation rate, throughput, or CPU.

**Assessment**: high severity for sustained high-throughput TCP; high confidence in per-I/O CTS creation. Actual timer/CTS allocation cost depends on runtime implementation and completion mode.

### UDP session footprint and socket operations

Every successful UDP session starts a background receive task and owns a 65,535-byte managed buffer (`UdpProxyCoordinator.cs:416-423,487-505`). The default UDP capacity is 16,384 sessions (`UdpProxyCoordinator.cs:28-51`), so the receive buffers alone have a theoretical upper bound of:

```text
65,535 bytes × 16,384 sessions = 1,073,725,440 bytes (about 1,024 MiB minus 16 KiB)
```

This excludes socket/native receive buffers, tasks, session objects, dictionaries, association entries, and relay control connections. Idle cleanup runs every minute by default and uses a two-minute UDP idle timeout (`IdleExpirySweeper.cs:23-41,50-63`), so stale sessions remain until a later sweep rather than being released at the exact timeout boundary.

Each send uses `Socket.SendToAsync` with a freshly encoded datagram (`Socks5Client.cs:456-460`); each receive uses `ReceiveFromAsync`, constructs a sender endpoint, validates it, and decodes the payload (`Socks5Client.cs:462-471`).

**Assessment**: high memory severity under high UDP flow cardinality; high confidence in buffer size/capacity arithmetic. Actual attainable session count is constrained by sockets, relay availability, and process memory.

## Logging overhead

The default runtime level is `info` (`README.md:101-105`; `logging-guidelines.md:12-17`). Trace callers that explicitly guard construction include `CapturePacketProcessor` (`CapturePacketProcessor.cs:39-45,60-74`) and the receive loop (`UdpProxyCoordinator.cs:499-505`).

Several high-frequency helper calls are not guarded at the call site. For example, `FlowDispatcher.DispatchAsync` passes `new RuntimeLogField(...)` arguments directly to `LogPacketStage` (`FlowDispatcher.cs:73-76,86-90,187-211`), and `LogPacketStage` performs its `IsEnabled` check only after receiving the already-created `params` array (`FlowDispatcher.cs:214-227`). `NdisPacketActionExecutor.LogPacket` has the same shape (`NdisPacketActionExecutor.cs:35-51,136-145`), as do `UdpProxyCoordinator.LogTrace` and `UdpResponseReinjector.LogTrace` (`UdpProxyCoordinator.cs:54-66,347-355`; `UdpResponseReinjector.cs:73-87,147-156`). Numeric and enum `RuntimeLogField.Value` values are stored as `object?` (`RuntimeLogging.cs:9`), so those field constructions can also box values before the helper threshold check.

When trace/debug is enabled, the logger allocates an additional fields array in each helper and `ConsoleRuntimeLogger.Event` creates a `StringBuilder` and formatted strings (`FlowDispatcher.cs:217-227`; `NdisPacketActionExecutor.cs:139-145`; `RuntimeLogging.cs:57-75`). Every enabled line then constructs a sanitized line and takes the writer lock (`RuntimeLogging.cs:79-90`). A normal packet may emit capture/classification/flow/action/reinjection/completion events; TCP and UDP proxy lifecycle events add more (`CapturePacketProcessor.cs:39-45`; `FlowDispatcher.cs:76-211`; `TcpProxyCoordinator.cs:895-913`; `UdpProxyCoordinator.cs:338-355`).

The README explicitly describes trace as potentially high volume and temporary-diagnosis-only (`README.md:101-109`). The logging tests verify filtering and format output (`tests/WinForward.Core.Tests/RuntimeLoggingTests.cs:12-80`) but do not measure disabled-level construction, stderr throughput, or lock wait.

**Assessment**: medium-to-high severity depending on level; high confidence for synchronous output and unguarded helper argument construction. The amount of overhead at a given event rate needs allocation and I/O measurement.

## Expiry and lifecycle work

The sweeper runs every minute and calls flow, TCP, and UDP expiry sequentially (`IdleExpirySweeper.cs:50-67`). `FlowTable.RemoveExpired` enumerates all states and materializes an expired-key array while holding `_gate` (`Domain.cs:209-220`). TCP and UDP expiry similarly enumerate and materialize arrays under their table/coordinator gates (`TcpRedirectTable.cs:193-205`; `UdpAssociations.cs:135-147`; `UdpProxyCoordinator.cs:212-245`; `TcpProxyCoordinator.cs:539-555`).

At large cardinalities, this creates periodic full-table CPU work and lock hold time that can overlap packet processing. The sweeper deliberately isolates failures and does not stop capture (`IdleExpirySweeper.cs:69-78`), but no timing or lock-duration metric is present.

**Assessment**: medium severity with possible high p99 impact at capacity; high confidence in periodic scans and allocations, medium confidence in user-visible spikes until a long-run trace is collected.

## End-to-end forwarding latency map

### Host pass

```text
NDIS queue probe/read
  -> 1 ms polling schedule when empty
  -> native frame copied to managed array
  -> parser + self-traffic/flow table/attribution/policy
  -> managed frame copied to new unmanaged buffer
  -> NDIS reinjection
```

Relevant anchors: `NdisCapture.cs:35-50`, `CapturePacketProcessor.cs:32-56`, `FlowDispatcher.cs:73-125`, `NdisPacketActionExecutor.cs:35-44`.

### Host TCP proxy

The first SYN traverses the host pass stages, then a full managed rewrite and native reinjection (`TcpProxyCoordinator.cs:204-253`). The reverse SYN-ACK and subsequent data are recaptured, classified, reverse-checked, copied, rewritten, and reinjected (`FlowDispatcher.cs:79-84`; `TcpProxyCoordinator.cs:377-435`). After local acceptance, a SOCKS5 control connection performs authentication and CONNECT before the stream relay begins (`TcpProxyRelay.cs:12-45`; `Socks5Client.cs:250-305`). The relay then adds two userspace stream pumps and per-I/O timeout setup (`TcpProxyRelay.cs:77-150`).

### Host UDP proxy

The first datagram waits inline for SOCKS5 control setup and UDP ASSOCIATE before the encoded datagram is sent (`NdisCapture.cs:49-50`; `UdpProxyCoordinator.cs:170-197`; `Socks5Client.cs:164-180,396-460`). Relay responses are decoded, rebuilt into complete IP frames, and reinjected (`UdpProxyCoordinator.cs:487-505`; `UdpResponseReinjector.cs:66-106`). The dispatcher has a reverse-of-stored-flow pass branch for those injected responses (`FlowDispatcher.cs:86-101`).

### Forwarded adapter traffic

Forwarded traffic is classified from receive direction and adapter identity (`PacketFlowClassifier.cs:15-29`). It skips process attribution (`FlowDispatcher.cs:149-153`) but still passes through capture copy, self-traffic, flow table, policy, action, and native reinjection. The capture scope can include every adapter (`CaptureAdapterScopeResolver.cs:44-51`), and forwarded TCP/UDP responses use origin-adapter handles/MACs (`UdpResponseReinjector.cs:109-145`; `TcpProxyCoordinator.cs:396-416`).

**Measured baseline limitation**: the repository’s recorded 8 ms average versus 0.6 ms direct is attributed to the polling pump under tunnel mode (`.trellis/spec/backend/windows-ndisapi.md:92`), but the commit/session does not include raw trace files, packet counts, machine configuration, percentile data, or a reproducible benchmark command. The current analysis environment is Linux, while the production path is Windows-only (`README.md:9-16`); no hardware run was performed here.

## Tests, settings, and what is not measured

- The project targets `net10.0` with nullable analysis, warnings-as-errors, trim/AOT analyzers, and unsafe code enabled (`Directory.Build.props:1-18`).
- The CLI is configured for `PublishAot`, `win-x64`, single-file publishing, and invariant globalization (`src/WinForward.Cli/WinForward.Cli.csproj:1-10`). The test project references all product projects but only Microsoft.NET.Test.Sdk/xUnit packages (`tests/WinForward.Core.Tests/WinForward.Core.Tests.csproj:1-17`). There is no BenchmarkDotNet package, benchmark project, or performance test fixture in the searched files.
- Tests strongly cover correctness: native-call serialization (`NdisApiAbiTests.cs:121-145`), parser/rewrite invariants (`ProtocolAuditTests.cs`, `TcpEndpointRewriteTests.cs`), flow reuse/capacity (`FlowAndConfigurationTests.cs:218-329`), dispatcher attribution and direction (`CapturePipelineTests.cs:218-359`), TCP concurrency/expiry (`TcpProxyCoordinatorTests.cs:283-328,650-721`), UDP concurrency/expiry (`UdpProxyCoordinatorTests.cs:17-74,154-386`), and logging format/filtering (`RuntimeLoggingTests.cs:12-99`). These tests use fakes/barriers and do not measure production NDISAPI, Windows process-table, socket, GC, or ETW costs.
- `IpTcpUdpPacket` and `UdpFrameBuilder` comments describe allocation-conscious contracts (`IpTcpUdpPacket.cs:38-42`; `UdpFrameBuilder.cs:6-10`), but the former still constructs `IPAddress` objects and the latter intentionally returns a new frame. The distinction needs allocation measurement rather than relying on comments.
- The current runtime logger has no EventSource/Meter instrumentation. `EventSourceSupport` is not set in `src/WinForward.Cli/WinForward.Cli.csproj`; the Microsoft Native AOT diagnostics documentation says EventPipe support is optional for Native AOT and that heap analysis is not supported for Native AOT. This limits direct managed-allocation diagnosis on the published executable.

## Concrete profiling and benchmark experiments

These are read-only measurement plans tied to the code paths above; no repository code or planning artifact was changed to run them.

### 1. End-to-end Windows matrix

Run on a supported Windows x64 host with the pinned NDISAPI driver and the same adapter scope:

| Case | Traffic | Primary measurements |
|---|---|---|
| Direct baseline | direct TCP/UDP and ICMP where applicable | RTT p50/p95/p99, throughput, loss |
| Tunnel pass | explicit pass policy | RTT, packets/s, CPU, working set |
| Host TCP proxy | repeated new TCP connections and one persistent connection | time to connect, time to first byte, p50/p99, CPU, bytes/s |
| Host UDP proxy | cold and warm DNS-sized datagrams plus larger payloads | time to first response, datagram latency/loss, bytes/s |
| Forwarded proxy | real VM/Hyper-V adapter | same metrics split by origin adapter |
| Logging matrix | `info`, `debug`, `trace`, and stderr redirected to a sink that does not block | p99, CPU, allocation rate, output bytes/s |

Use the recorded direct/tunnel comparison (0.6 ms vs 8 ms average) as a historical comparison point only; record packet counts, adapter count, driver version, CPU power state, and raw trace timestamps with each run.

### 2. Polling, queue, and native-gate experiment

Measure idle and saturated runs with 1, 2, and 4 in-scope adapters. Collect queue depth, packet throughput, CPU time, timer wakeups, context switches, and lock contention. Correlate native call stacks for `NdisApiDriver.TryReadPacket`, `GetAdapterPacketQueueSize`, `ReadPacket`, `SendPacketTo*`, and `NdisNativeCallGate.Enter`.

Use the NDISAPI event/batch API documentation as a comparison reference, but keep the repository’s current single-read path as the measured baseline. The independent variables are adapter count, empty-queue duration, packet rate, frame size, and pass versus proxy disposition. This isolates the cost of `NdisCapture.cs:39-43` from dispatcher/proxy work.

### 3. Managed allocation and copy experiment

Use a Release, non-published build for managed allocation attribution, with fixed synthetic frames of 64, 512, and 1,514 bytes. Exercise:

1. capture → parse → pass;
2. capture → parse → block;
3. capture → TCP endpoint rewrite → inject;
4. capture → UDP parse → SOCKS5 encode;
5. UDP response decode → frame build → inject.

For each case, run a warmed loop and record allocated bytes/op, allocation count/op, Gen0 rate, CPU/op, and copied-byte totals. Attribute stacks to the anchors `CapturePacketProcessor.cs:34`, `IpTcpUdpPacket.cs:70-71`, `IpUdpPacket.cs:73`, `NdisPacketActionExecutor.cs:39,115`, `TcpProxyCoordinator.cs:214,310,327,346,391`, `Socks5Udp.cs:14,29,61`, and `UdpFrameBuilder.cs:49`.

For the published Native AOT executable, use process private-bytes/heap ETW and CPU sampling rather than assuming managed heap tooling is available; see the Native AOT limitation in `External References`.

### 4. Flow-table cardinality and miss-slope experiment

In an isolated benchmark harness, prepopulate `FlowTable` with 0, 1,000, 16,384, and 65,535 states. Time:

- direct existing-key `TryResolve`;
- reverse/origin-flipped existing-key `TryResolve`;
- absent-key `TryResolve`;
- absent-key `TryResolve` followed by `TryClaimResolved`;
- concurrent absent-key admission with 1, 2, 4, and 8 callers.

Record wall-clock p50/p99, CPU time, allocated bytes, and lock contention. The expected source-derived discriminator is whether absent-flow work grows with state count because `TryResolveLocked` is reached twice (`FlowDispatcher.cs:86-108`; `Domain.cs:223-263`).

### 5. Self-traffic cardinality experiment

Register 0, 100, 1,000, and 16,384 wildcard local entries in `SelfTrafficRegistry`. Time non-self misses, exact hits, reverse hits, and wildcard hits while running single-threaded and with concurrent callers. Record lock wait and operation time. Repeat with the registry populated by a realistic mix of TCP listener, TCP control, UDP control, and UDP relay entries from `Socks5Client.cs:411-428` and `TcpProxyCoordinator.cs:256-270`.

### 6. Process attribution experiment

On Windows, measure `WindowsProcessAttributor.FindAsync` for IPv4/IPv6 TCP and UDP with small, medium, and large OS connection tables. Separate first lookup, retry-after-miss, cached-process-identity, and forwarded-flow (attributor skipped) cases. Record native table bytes, managed allocations, API duration, and the fraction of new-flow latency attributable to the optional 2 ms retry (`ProcessAttribution.cs:28-38`). Repeat with a policy that has no process matcher to verify the production composition still invokes attribution (`Program.cs:237-241`; `FlowDispatcher.cs:149-153`).

### 7. TCP relay chunk/timeout experiment

Use one persistent relay and transfer payloads with application write sizes of 1 byte, 1 KiB, 8 KiB, and 64 KiB in each direction. Measure throughput, CPU, allocated bytes/sec, Gen0 rate, timer/ThreadPool activity, and p99 write-to-read latency. Correlate allocation stacks to the two `CancellationTokenSource.CreateLinkedTokenSource` sites inside `TcpProxyRelay.PumpAsync` (`TcpProxyRelay.cs:120-140`).

### 8. UDP session footprint and lifecycle experiment

Create 1, 100, 1,000, and as many as the host safely supports of distinct UDP flow keys, send one datagram per flow, then hold them idle. Sample managed working set, private bytes, socket/handle count, receive-buffer footprint, and association/session counts. Continue past the two-minute relay idle threshold and the one-minute sweeper interval to record release timing and sweep-duration/packet-latency correlation (`UdpProxyCoordinator.cs:487-505`; `IdleExpirySweeper.cs:37-63`).

### 9. Logging construction/output experiment

At a fixed packet rate, compare `NullRuntimeLogger`, the production `ConsoleRuntimeLogger` at `info`, `debug`, and `trace`, and each level with stderr redirected to a fast sink versus a deliberately slow sink. Record allocated bytes/packet, CPU, writer-lock wait, events/packet, output bytes/second, and forwarding p99. This distinguishes below-threshold `params`/boxing construction (`FlowDispatcher.cs:214-227`) from enabled formatter/writer cost (`RuntimeLogging.cs:57-90`).

### 10. ETW/WPR cross-layer latency experiment

Capture a short synchronized Windows Performance Recorder trace around a fixed packet workload, including CPU sampling, context switches/scheduler, network I/O, process/thread, heap/virtual allocation where available, and CLR/EventPipe providers where supported. Correlate peer-side packet timestamps with runtime trace packet sequence fields at trace level only in a separate diagnostic run. Analyze stacks for polling delay, driver calls, monitor waits, process-table APIs, socket I/O, checksum loops, and logger writes. Repeat with trace disabled because trace itself changes the path.

## External references

- [WinpkFilter `SetPacketEvent`](https://www.ntkernel.com/docs/windows-packet-filter-documentation/c-api/setpacketevent/) — documents a per-adapter event signaled when the packet queue is non-empty; relevant to measuring the current `Task.Delay` polling behavior.
- [WinpkFilter `ReadPackets`](https://www.ntkernel.com/docs/windows-packet-filter-documentation/c-api/readpackets/) — documents a multi-packet request with multiple user-provided `INTERMEDIATE_BUFFER` entries; relevant to the current one-packet `ReadPacket` baseline.
- [.NET Native AOT diagnostics](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/diagnostics) — states that Native AOT has partial CPU-profiling support, does not support heap analysis, and requires optional EventPipe support for `dotnet-trace`/`dotnet-counters` scenarios.
- [Microsoft PerfView](https://github.com/microsoft/perfview) — Windows CPU/memory analysis tool that can consume ETW and EventPipe traces; relevant to allocation stacks, CPU samples, and contention analysis.
- [Windows Performance Recorder](https://learn.microsoft.com/en-us/windows-hardware/test/wpt/windows-performance-recorder) — ETW-based recorder used with WPA for CPU, scheduling, network, and resource-consumption traces.

## Related specs

- `.trellis/spec/backend/windows-ndisapi.md:74-92` — verified NDISAPI handle/flag contracts and the recorded polling RTT baseline.
- `.trellis/spec/backend/windows-ndisapi.md:164-183` — UDP relay response/reinjection and recapture behavior.
- `.trellis/spec/backend/quality-guidelines.md:21-37` — project conventions for allocation-conscious packet paths, locked tables, coordinator ownership, and lifecycle ordering.
- `.trellis/spec/backend/logging-guidelines.md:5-18,42-47,68-81` — logger levels, high-frequency `IsEnabled` contract, and no-behavioral-impact logging expectations.
- `README.md:101-112,129-143` — runtime log volume warning, supported traffic, and proxy-only parsing behavior.

## Caveats / Not found

- No research file existed in the active task directory before this report; this file is the only file written by this research pass.
- No product code, test code, project setting, PRD, implementation plan, or other planning artifact was modified.
- No BenchmarkDotNet reference, benchmark project, `Stopwatch`-based performance test, `GC.GetAllocatedBytesForCurrentThread` measurement, throughput result, packet-rate result, or raw latency trace was found in the searched repository files.
- The only numeric latency evidence is the prior hardware-observation text in `.trellis/spec/backend/windows-ndisapi.md:92`; it is not accompanied by raw data in this checkout.
- The current environment is Linux, while `README.md:9-16` and the CLI project target Windows x64/Native AOT. Windows driver, IP Helper, socket, NDISAPI, ETW, and published-AOT behavior therefore remains unmeasured here.
- Managed object/array conclusions are based on explicit constructors, `new byte[]`, `ToArray`, `params`, LINQ materialization, and `CancellationTokenSource` sites. JIT/AOT escape analysis, `ValueTask` completion mode, socket-provider internals, kernel copies, and allocator implementation can change measured cost.
- The theoretical UDP buffer total assumes all 16,384 session slots are active simultaneously; it is a capacity-bound calculation, not an observed resident set.
- The exact number of recaptured legs for self-traffic and synthetic UDP responses depends on the Windows driver/adapter path. The repository documents the reverse-response behavior, but this analysis did not perform a hardware packet trace.
