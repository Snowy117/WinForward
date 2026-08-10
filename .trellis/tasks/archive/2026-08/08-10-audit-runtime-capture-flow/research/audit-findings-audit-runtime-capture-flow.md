# Research: audit runtime capture flow

- **Query**: Perform a read-only exhaustive audit of the 11 owned Runtime capture/flow/lifecycle files, their callers, and existing tests. Verify packet-lease terminal disposition, pass/block/proxy direction, self-traffic priority, cancellation/exceptions, expiry, adapter restoration, and pump-handler shutdown behavior.
- **Scope**: internal
- **Date**: 2026-08-10

## Findings

### Files Found

| File Path | Description |
|---|---|
| `src/WinForward.Runtime/CaptureAdapterScopeResolver.cs` | Resolves and validates the adapter scope before modes are changed. |
| `src/WinForward.Runtime/CaptureLifecycle.cs` | Transactional mode application, capture execution, stop, and restoration state machine. |
| `src/WinForward.Runtime/CapturePacketProcessor.cs` | Copies a native frame, creates a managed `PacketLease`, classifies it, and dispatches it. |
| `src/WinForward.Runtime/FlowDispatcher.cs` | Selects pass/block/proxy terminal action with self-traffic and TCP reverse precedence. |
| `src/WinForward.Runtime/IdleExpirySweeper.cs` | Periodically invokes flow, TCP redirect, and UDP relay expiry. |
| `src/WinForward.Runtime/MultiAdapterCaptureLoop.cs` | Runs one NDIS capture pump per selected adapter and joins their run tasks. |
| `src/WinForward.Runtime/NdisAdapterModeController.cs` | Snapshots, tunnels, and restores NDIS adapter flags. |
| `src/WinForward.Runtime/NdisPacketActionExecutor.cs` | Executes pass/block/proxy outcomes. |
| `src/WinForward.Runtime/NdisPacketReinjector.cs` | Maps runtime reinjection calls to NDISAPI driver sends. |
| `src/WinForward.Runtime/PacketFlowClassifier.cs` | Builds flow/non-flow context and sets host versus forwarded origin from NDIS direction. |
| `src/WinForward.Runtime/SelfTrafficRegistry.cs` | Tracks owned TCP/UDP tuples, including reverse and wildcard-bound socket matching. |
| `src/WinForward.NdisApi/NdisCapture.cs` | Caller-side pump that owns the native packet buffer during a handler invocation. |
| `src/WinForward.Cli/Program.cs` | Production composition root and Ctrl+C cancellation path. |
| `tests/WinForward.Core.Tests/CaptureLifecycleTests.cs` | Existing transactional-runtime lifecycle tests. |
| `tests/WinForward.Core.Tests/CapturePipelineTests.cs` | Existing classifier, scope, dispatcher, and normal pass-direction tests. |
| `tests/WinForward.Core.Tests/FlowDispatcherTests.cs` | Existing self-traffic, TCP reverse gate, and UDP response direction tests. |

### Call and Ownership Map

`Program.RunInterceptionAsync` resolves scope at `Program.cs:164`, then `RunCaptureLoopAsync` composes the registry, coordinators, executor, dispatcher, processor, mode controller, capture loop, transactional runtime, and sweeper at `Program.cs:177-211`.

The data path is:

`NdisCapturePump.RunAsync` (`NdisCapture.cs:35-50`) allocates one native `NdisPacketBuffer` per iteration and awaits its handler. `MultiAdapterCaptureLoop` binds that handler to `CapturePacketProcessor.ProcessAsync` for the corresponding `WindowsAdapter` (`MultiAdapterCaptureLoop.cs:19-26`). The processor copies the frame and carries device flags, NDIS metadata flags, and the enumeration handle before the native `using` buffer exits (`CapturePacketProcessor.cs:28-44`), then the dispatcher selects an executor action. Pass returns through `NdisPacketActionExecutor` and `NdisPacketReinjector` to `NdisApiDriver.SendPacketToAdapter` or `SendPacketToMstcp` with the captured pass metadata preserved.

The normal CLI shutdown path is:

`Console.CancelKeyPress` cancels the `shutdown` CTS (`Program.cs:183-188`); `TransactionalCaptureRuntime.StartAsync` links that token before snapshot and mode application (`CaptureLifecycle.cs:68-82`); `MultiAdapterCaptureLoop.RunAsync` awaits `Task.WhenAll` for every pump (`MultiAdapterCaptureLoop.cs:31-40`); every pump has awaited its current handler before its `RunAsync` returns (`NdisCapture.cs:40-42`); only then does the runtime cleanup task dispose capture and restore modes (`CaptureLifecycle.cs:84-87`, `:145-155`).

### Confirmed Defects

#### RCF-1: Concurrent public `StopAsync` does not wait for active capture execution (resolved)

- **Severity/type**: Medium, lifecycle concurrency; fixed in the current implementation.
- **Original defect**: The earlier implementation restored modes after signalling capture disposal without awaiting the active `StartAsync`/`_capture.RunAsync` task.
- **Current fix**: `TransactionalCaptureRuntime` stores the run task while holding `_gate`; the first `StopAsync` cancels `_shutdown`, while all overlapping stops await the active run. Its one cached cleanup task then disposes capture and restores modes exactly once after that run returns (`CaptureLifecycle.cs:35-39`, `:57-88`, `:91-115`, `:136-177`). `MultiAdapterCaptureLoop.RunAsync` joins all pump tasks before returning (`MultiAdapterCaptureLoop.cs:29-41`).
- **Deterministic regression**: `CaptureLifecycleTests.ConcurrentStopWaitsForCaptureRunBeforeRestoringModes` gates capture cancellation and completion with `TaskCompletionSource`, asserting restoration is still empty while the run is blocked (`CaptureLifecycleTests.cs:51-70`). The coordinator-composition variant asserts proxy sessions are closed only after the capture run on concurrent stop (`CaptureLifecycleTests.cs:144-168`); `ConcurrentStopsBeforeStartWaitForTheSameCaptureCleanup` proves overlapping pre-start stops await one shared cleanup rather than reporting closed while capture disposal is active (`CaptureLifecycleTests.cs:86-103`).

#### RCF-2: Captured NDIS metadata flags were dropped before ordinary pass reinjection (resolved)

- **Severity/type**: Medium, NDIS capture metadata integrity.
- **Minimal reproduction**: Capture a frame whose `INTERMEDIATE_BUFFER.m_Flags` is nonzero, select `Pass`, and inspect the reinjection request. `NdisCapturedPacket` carried the flags (`NdisCapture.cs:5-12`), but Runtime did not propagate them into the pass buffer.
- **Fix**: `PacketCaptureMetadata` now carries `Flags` with a default of zero for existing synthetic/test callers (`FlowDispatcher.cs:9-17`). The processor copies `packet.Flags` and the pass executor supplies it to `NdisPacketBuffer.SetFrame` (`CapturePacketProcessor.cs:28-33`, `NdisPacketActionExecutor.cs:35-43`).
- **Regression**: `CapturePipelineTests.ProcessorPreservesCapturedNdisFlagsThroughPassReinjection` constructs a flagged captured buffer and runs the real processor, dispatcher, and pass executor before asserting the fake reinjector received `0x4000_0021` (`CapturePipelineTests.cs:384-400`).

### Verified Behavior

#### Packet lease disposition and failures

- `PacketLease` atomically accepts exactly one disposition; its disposal attempts a `Block` only if no earlier completion succeeded (`PacketRuntime.cs:19-26`).
- For every internal dispatcher route, `FlowDispatcher.CompleteAsync` performs `TryComplete` once before invoking the executor (`FlowDispatcher.cs:206-210`). The routes cover self traffic (`:82-85`), TCP reverse outcomes (`:93-105`), existing flow decisions (`:108-122`), flow-table capacity rejection (`:132-135`), non-flow (`:147-165`), and pass/block/proxy/default decisions (`:187-203`).
- Unexpected parsing, classification, dispatcher, or executor errors after lease creation are caught by the processor, which disposes the lease and rethrows (`CapturePacketProcessor.cs:34-55`). An already-selected disposition is not replaced because `PacketLease.TryComplete` is one-shot. `DispatcherCompletesLeaseExactlyOnceWhenExecutorThrows` explicitly verifies this: a throwing pass executor leaves the recorded `Pass` disposition and rejects a second `Block` (`CapturePipelineTests.cs:309-323`).
- The disposition is therefore the selected terminal action before executor side effects, rather than a success receipt for native reinjection. A failed pass injection propagates, the original captured packet is not reinjected, and the runtime follows its failure shutdown path; the recorded lease disposition remains `Pass` by the one-shot contract.
- A native frame whose `GetFrame()` fails before `PacketLease` construction has no managed lease. The pump handler faults before reinjection, its native buffer is disposed by the pump's `using`, and the capture runtime takes its failure shutdown path (`CapturePacketProcessor.cs:28-30`, `NdisCapture.cs:30-42`).

#### Direction, pass/block/proxy, and adapter handles

- The pump stamps captured metadata with its enumeration `RuntimeHandle`, not the captured buffer adapter pointer, and preserves captured NDIS packet flags (`NdisCapture.cs:5-12`, `:46-50`). This matches `spec/backend/windows-ndisapi.md:75-87`.
- `PacketFlowClassifier` derives `Host` for `ON_SEND` and `Forwarded` for `ON_RECEIVE` (`PacketFlowClassifier.cs:20-29`).
- A normal pass maps `ON_SEND` to `SendToAdapter` and other capture direction to `SendToMstcp`, using the capture metadata handle and preserving NDIS packet flags (`NdisPacketActionExecutor.cs:35-44`). `NdisPacketReinjector` forwards those calls unchanged to the driver (`NdisPacketReinjector.cs:27-29`). `ExecutorReinjectsOnSendTowardAdapterAndOnReceiveTowardMstcp` asserts both mappings and handles (`CapturePipelineTests.cs:365-382`), while `ProcessorPreservesCapturedNdisFlagsThroughPassReinjection` asserts the complete metadata path (`CapturePipelineTests.cs:384-400`).
- `Metadata.Flags` is passive outside ordinary pass: the TCP coordinator reads only `Metadata.AdapterHandle` for redirect injection (`TcpProxyCoordinator.cs:143`, `:200`, `:268`, `:317`), while proxy parsing/rewrite uses the lease frame and flow context. No proxy or rewrite path reads or changes the NDIS metadata flags.
- Block performs no reinjection (`NdisPacketActionExecutor.cs:46-50`). Proxy-selected UDP frames are sent to the UDP coordinator and are not reinjected; parsing/relay failure returns without a pass (`NdisPacketActionExecutor.cs:52-83`). TCP proxy errors also return without a pass (`:85-110`). Existing UDP executor tests cover forwarding without reinjection and malformed-frame fail-closed behavior (`UdpRelayTests.cs:271-308`).
- TCP reverse delivery selects MSTCP for host-originated associations and the adapter path for forwarded associations (`TcpProxyCoordinator.cs:279-295`). TCP and UDP response injectors assign `ON_RECEIVE` for MSTCP and `ON_SEND` for adapter delivery (`TcpRedirectInjector.cs:9-20`, `UdpResponseReinjector.cs:99-112`). Existing fakes verify both TCP directions (`TcpRedirectInjectorTests.cs:12-45`) and forwarded TCP/UDP direction choice (`TcpProxyCoordinatorTests.cs:273-314`, `UdpRelayTests.cs:197-223`).

#### Self-traffic priority

- The dispatcher checks `_selfTraffic.IsOwned` before the TCP reverse hook, flow cache, attribution, and policy for flow packets (`FlowDispatcher.cs:79-86`), and before non-flow policy evaluation (`:147-154`).
- The registry recognizes an exact tuple and its reverse (`SelfTrafficRegistry.cs:22-29`), then recognizes wildcard-local registrations (`IPAddress.Any`/`IPv6Any`) by local port and remote endpoint in either orientation (`:30-48`). Generation-bound tokens prevent disposing an older registration from deleting a newer one (`:12-19`, `:50-79`).
- TCP and UDP SOCKS5 control/relay factories register their own connection tuples before their SYN or relay traffic is emitted (`TcpProxyRelay.cs:22-31`, `Socks5Client.cs:281-300`). Tests cover priority ahead of a catch-all rule, reverse matching, token generation safety, and wildcard-bound TCP/UDP tuples (`FlowDispatcherTests.cs:70-108`, `TcpProxyCoordinatorTests.cs:470-515`).

#### Cancellation, exceptions, and pump joining

- Any non-cancellation pump failure cancels the loop's linked token. `Task.WhenAll` waits for all per-pump wrapper tasks before propagating the failure (`MultiAdapterCaptureLoop.cs:29-41`, `:52-67`). A cancellation exception caused by that linked token is treated as normal per-pump exit (`:58-61`).
- A pump serially awaits each handler before releasing its native buffer or reading another frame (`NdisCapture.cs:30-42`); pumps on different adapters run concurrently.
- On the CLI cancellation path, this establishes the required await barrier before `CaptureLifecycle` reaches `RestoreBestEffortAsync`. The archived high finding about CLI pump work surviving mode restoration no longer applies to the present `MultiAdapterCaptureLoop.RunAsync` path, and the concurrent public `StopAsync` path is covered by the lifecycle regressions cited under RCF-1.
- A dispatcher cancellation/error is converted to an unreinjected, blocked lease and rethrown by the processor (`CapturePacketProcessor.cs:46-55`). Proxy coordinator handling deliberately catches non-cancellation failures and returns fail-closed instead of reinjecting (`NdisPacketActionExecutor.cs:66-82`, `:91-109`).

#### Adapter modes and expiry

- The mode controller snapshots each scope adapter's existing flags, applies only `SentTunnel | ReceiveTunnel` in addition to those flags, and restores the exact snapshot flags (`NdisAdapterModeController.cs:28-49`).
- The runtime records an adapter only after successful mode application, restores recorded adapters in reverse order, continues after a restore exception, and disposes modes after restoration (`CaptureLifecycle.cs:74-80`, `:119-134`). Startup rollback coverage confirms a partially applied set restores only the successfully applied adapter (`CaptureLifecycleTests.cs:9-19`).
- `IdleExpirySweeper.Start` creates one loop (`IdleExpirySweeper.cs:41-45`). Each tick removes expired dispatcher entries and awaits TCP then UDP expiry; a tick-local exception is isolated (`:47-77`). Disposal cancels and awaits the sweeper task (`:79-91`).
- The CLI declares `runtime` before `idleExpirySweeper` (`Program.cs:198-199`), so reverse `await using` disposal stops and awaits the sweeper before runtime disposal starts. This is the actual teardown order. It differs from the historical backend-guide prose that states the sweeper is disposed after the capture runtime stops; this audit found no in-code packet or handle failure attributable to that order.

### Owned File Conclusions

| Owned file | Conclusion and principal caller/test evidence |
|---|---|
| `CaptureAdapterScopeResolver.cs` | Validates selectors and widens unconstrained/fallback policies to all startup adapters (`:16-53`); called by `Program.cs:164`; scope cases are covered at `CapturePipelineTests.cs:104-214`. |
| `CaptureLifecycle.cs` | Start-path and concurrent-stop disposal restore modes only after the capture run returns (`:57-177`); RCF-1 is fixed and covered by `CaptureLifecycleTests.cs:51-168`. |
| `CapturePacketProcessor.cs` | Copies frame before native buffer lifetime ends, preserves all pass metadata, and blocks/rethrows after lease-creation exceptions (`:28-55`); called by the multi-pump delegate (`MultiAdapterCaptureLoop.cs:25`); direct native-copy/pass coverage is `CapturePipelineTests.cs:384-400`. |
| `FlowDispatcher.cs` | Self traffic first, TCP reverse second, then cache/attribution/policy, all with one `CompleteAsync` call (`:79-210`); composed at `Program.cs:192-195`; covered in `FlowDispatcherTests.cs:12-108` and `CapturePipelineTests.cs:254-378`. |
| `IdleExpirySweeper.cs` | Tick and disposal behavior are as described above (`:41-91`); composed/started at `Program.cs:199-213`; no direct sweeper test. |
| `MultiAdapterCaptureLoop.cs` | Per-adapter pumps are joined on `RunAsync` (`:24-42`) and only signalled stopped on direct dispose (`:44-50`); used at `Program.cs:197-198`; no direct loop test. |
| `NdisAdapterModeController.cs` | Snapshot/OR/restore-exact behavior (`:28-52`); used at `Program.cs:196-198`; production driver interactions have no direct test. |
| `NdisPacketActionExecutor.cs` | Pass direction and fail-closed block/proxy actions (`:35-110`); used at `Program.cs:191-192`; normal and UDP executor tests cited above. |
| `NdisPacketReinjector.cs` | Direct NDISAPI method mapping (`:21-29`); constructed at `Program.cs:178`; direction is test-covered through an `IPacketReinjector` fake, not this driver wrapper. |
| `PacketFlowClassifier.cs` | Direction-to-origin classification and non-flow context construction (`:20-41`); called by processor (`CapturePacketProcessor.cs:36-44`); covered at `CapturePipelineTests.cs:72-100`. |
| `SelfTrafficRegistry.cs` | Exact/reverse/wildcard ownership plus generation-safe token removal (`:12-80`); injected at `Program.cs:177-194`; dispatcher and coordinator tests cited above. |

### Coverage Gaps

- No test constructs `NdisCapturePump` or `MultiAdapterCaptureLoop` through a controllable driver/pump seam. Consequently, the tests do not directly prove native-buffer lifetime, one handler at a time per adapter, cancellation while a handler is blocked, multi-adapter failure joining, or direct loop disposal behavior.
- `CapturePacketProcessor` now has direct pass coverage with a native `NdisPacketBuffer`; parse-failure, dispatcher-exception, and cancellation paths across the native-copy/classification bridge remain untested.
- `IdleExpirySweeper` has no direct time-controlled test for start-once, first tick, dispatch/TCP/UDP call order, per-tick exception isolation, or disposal awaiting an active tick. Underlying flow/TCP/UDP expiry methods have tests (`FlowAndConfigurationTests.cs:197-209`, `TcpProxyCoordinatorTests.cs:403-428`, `UdpProxyCoordinatorTests.cs:118-149`).
- `NdisAdapterModeController` has no production-driver test of exact flag preservation/restoration or a native failure. `CaptureLifecycleTests` establishes the transactional contract using a fake controller only.
- Unit fakes prove pass/reverse direction and enumeration-handle propagation. Actual NDISAPI pass/revert direction, physical adapter selection for forwarded reverse packets, mode restoration, and Ctrl+C during active native traffic require the Windows hardware gate described in `spec/backend/windows-ndisapi.md`.
- Focused tests, the full Release build/test, and diff checks are recorded in the implementation verification section after those commands complete. Windows hardware execution remains a gate.

### External References

- No external search was required; the audit used the repository source, tests, backend contracts, and the archived lifecycle report.

### Related Specs

- `.trellis/spec/backend/windows-ndisapi.md` — enumeration-handle rule, pass/revert direction, reverse-flow direction, and Windows hardware constraints.
- `.trellis/spec/backend/error-handling.md` — proxy-selected packets must fail closed rather than silently pass.
- `.trellis/spec/backend/quality-guidelines.md` — coordinator ownership and expiry contracts.
- `.trellis/tasks/archive/2026-08/08-07-winforward-proxy/research/review-capture-lifecycle-config-2026.md` — prior pump-disposal finding independently checked above.

## Caveats / Not Found

- The active-task resolver returned `.trellis/tasks/08-10-audit-protocols` through session fallback, while the assigned task explicitly named `.trellis/tasks/08-10-audit-runtime-capture-flow`; this report was written to the explicitly assigned task directory.
- No Windows NDISAPI driver or hardware run was available in this audit. Direction and adapter-mode conclusions beyond code/fake coverage are therefore limited to the documented contract and unit-test seams.

## Implementation Verification

- Focused capture-flow: `dotnet test tests/WinForward.Core.Tests/WinForward.Core.Tests.csproj --configuration Release --no-restore --filter "FullyQualifiedName~CaptureLifecycleTests|FullyQualifiedName~CapturePipelineTests|FullyQualifiedName~FlowDispatcherTests"` passed 37/37 tests.
- Release build: `dotnet build WinForward.slnx --configuration Release --no-restore` passed with 0 warnings and 0 errors.
- Full Release: `dotnet test WinForward.slnx --configuration Release --no-restore` passed 219/219 tests.
- Diff check: `git diff --check` passed.
- Windows gate: not run on this Linux host. Real `ndisapi.dll` pass reinjection, adapter mode restoration, forwarded-adapter delivery, and Ctrl+C under live traffic still require the documented Windows + WinpkFilter hardware matrix.
