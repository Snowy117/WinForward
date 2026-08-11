# Audit Summary

Audit date: 2026-08-10

## Scope and Outcome

All 42 current production C# files under `src/` were assigned once in
`module-coverage-matrix.md`. The original 41-file estimate omitted the Windows assembly metadata
file. Eight child audits reviewed source, direct callers, and host-independent tests; their source
and regression details are archived under `.trellis/tasks/archive/2026-08/`.

The child reports contain 42 finding records and 41 distinct defects: PRT-007 and RUS-001 are the
same SOCKS attempt-deadline defect reported by both the Protocols and Runtime UDP/SOCKS audits.
Every host-independent defect has a green regression.
The fixes are already in the current history, principally `c180beb`, `9a1f413`, `68afd12`,
`2af5eca`, `03eb0bb`, `24c6bda`, `88822bb`, `7781159`, and `eb9a174`.

## Confirmed Findings

| ID | Severity | Type | Source anchor | Resolution and regression |
| --- | --- | --- | --- | --- |
| F1 | Medium | Flow lifecycle | `Domain.cs:220-262` | Resolution now touches activity. `FlowTableLookupRefreshesActivityBeforeIdleExpiry`. |
| F2 | Medium | Validation | `ConfigurationModels.cs:136-285` | Null collection values produce indexed diagnostics. `ConfigurationRejectsNullSectionEntriesWithoutThrowing`; `ConfigurationRejectsNullRuleMatchValuesWithoutThrowing`. |
| F3 | Low | Configuration | `ConfigurationModels.cs:266-312` | Port intervals are sorted and merged. `ConfigurationNormalizesAndMergesRemotePortRanges`. |
| F4 | Low | Diagnostic/redaction | `ConfigurationModels.cs:80-86` | Parser keeps JSON path and emits a fixed redacted diagnostic. `ConfigurationParseDiagnosticsNameTheFailingFieldWithoutEchoingCredentials`. |
| F5 | Low | Boundary validation | `Domain.cs:46-51` | Endpoint factory rejects null explicitly. `EndpointFactoryRejectsNullAddressWithArgumentException`. |
| PRT-001 | High | Protocol bounds | `IpTcpUdpPacket.cs:113-128` | Short TCP segments reject without indexing past the declared transport span. `TcpParserRejectsShortDeclaredSegmentWithoutThrowing`. |
| PRT-002 | Medium | Protocol bounds | `PacketChecksums.cs:75-86` | IPv6 UDP rewriter checks header bytes before reads or writes. `UdpIpv6RewriterRejectsShortPayloadWithoutMutating`. |
| PRT-003 | Low | IPv4 protocol validity | `IpTcpUdpPacket.cs:65-68`; `IpUdpPacket.cs:31-34`; `PacketChecksums.cs:54-60,95-103` | Reserved fragment flag rejects without mutation. `ReservedIpv4FragmentFlagIsRejectedWithoutMutating`. |
| PRT-004 | Medium | SOCKS5 RFC 1928 | `Socks5State.cs:65-89,154-174` | REP, RSV, ATYP, and reply-prefix fields are validated. `SocksReplyParsersRejectInvalidFixedFields`. |
| PRT-005 | Low | Checksum arithmetic | `PacketChecksums.cs:8-23` | One's-complement accumulation folds before overflow. `InternetChecksumFoldsLargeGenericInputWithoutOverflow`. |
| PRT-006 | Low | UDP frame construction | `UdpFrameBuilder.cs:42-47` | Wire-length overflow rejects before allocation/casts. `FrameBuilderRejectsPayloadsBeyondWireLengthFields`. |
| PRT-007 / RUS-001 | Medium | SOCKS timeout | `Socks5Client.cs:11-16,63-86,126-157,205-237` | Attempt deadline covers resolve, connect, handshake, command, and reply-domain lookup. `ServerAddressResolutionTimeoutDoesNotDependOnResolverCancellation`; `HandshakeTimeoutIncludesMethodSelectionRead`; `CommandTimeoutIncludesReplyRead`. |
| NDIS-1 | High | Driver lifecycle | `NdisApiDriver.cs:19-36` | Opaque but unloaded native objects are rejected by `IsDriverLoaded`. `OpenValidationRejectsOpaqueUnloadedDriverObject`. |
| NDIS-2 | High | Native failure handling | `NdisApiDriver.cs:66-95` | Idle queue polling is separated from queue/read errors. `QueueStatusSeparatesIdlePollingFromNativeFailures`; `ReadResultRejectsFailureFromNonEmptyQueue`. |
| NDIS-3 | High | Native concurrency | `NdisApiDriver.cs:28-151` | One per-driver gate serializes all native calls using the wrapper's shared OVERLAPPED state. `NativeCallGateSerializesConcurrentOperations`. |
| NDIS-4 | High | ABI metadata | `NdisApiAbi.cs:64`; `NdisCapture.cs:5-12` | Captured NDIS flags are preserved through buffer and capture records. `PacketBufferPreservesExplicitNdisFlagsAndClearsThemForNewFrames`; `ProcessorPreservesCapturedNdisFlagsThroughPassReinjection`. |
| NDIS-5 | High | Native DLL loading | `NdisApiAbi.cs:113-125` | Missing app-local sidecar throws instead of falling back to default DLL probing. `AppLocalResolverRejectsMissingNdisApiSidecar`. |
| NDIS-6 | Medium | Native status | `NdisApiDriver.cs:47-64` | Native version failure sentinel is not exposed as a version. `DriverVersionStatusRejectsNativeFailureSentinel`. |
| W1 | High | Windows ABI | `IpHelperAbi.cs:8`; `ProcessAttribution.cs:212` | IPv6 UDP uses the valid owner-PID table class. `UdpOwnerPidTableClassIsSharedByIpv4AndIpv6`. |
| W2 | Medium | IPv6 endpoint ABI | `IpHelperAbi.cs:23`; `ProcessAttribution.cs:219,251` | Scope IDs stay host order; only ports are endian-converted. `Ipv6ProjectionPreservesHostOrderScopeId`; `OwnerPortProjectionConvertsNetworkOrderLowWord`. |
| W3 | Medium | Adapter identity | `AdapterIdentity.cs:81` | All-zero MAC cannot be a fallback correlation key. `AdapterSelectorTests` zero/duplicate-MAC cases. |
| W4 | Medium | Process attribution | `ProcessAttribution.cs:110-138` | Image paths use limited-query access and remain unknown on access failure. Windows hardware gate. |
| RCF-1 | Medium | Lifecycle concurrency | `CaptureLifecycle.cs:35-177` | Concurrent stop waits for active capture before disposal/restoration. `ConcurrentStopWaitsForCaptureRunBeforeRestoringModes`. |
| RCF-2 | Medium | Capture metadata | `CapturePacketProcessor.cs:28-33`; `NdisPacketActionExecutor.cs:35-43` | Ordinary pass reinjection retains captured NDIS flags. `ProcessorPreservesCapturedNdisFlagsThroughPassReinjection`. |
| CLI-1 | High | Composition lifecycle | `Program.cs:179-181,214-255,398-430` | Sweeper, capture, and proxy sessions close before adapter-mode restoration. `CoordinatorShutdownCompositionClosesProxySessionsBeforeRestoringModes`; failure and stop variants. |
| TCR-001 | High | Reverse routing isolation | `TcpRedirectTable.cs:57-60,95-143` | Reverse lookup uses the full pre-rewrite wire tuple. `SameFamilyUnrelatedReverseTupleDoesNotMatchAssociation`; `NonExactReverseProbeDoesNotRefreshActivity`. |
| TCR-002 | High | Association claim | `TcpRedirectTable.cs:84-115` | All aliases are checked before atomically claiming them. `DistinctTranslatedTuplesMaySharePortWithoutPartialClaim`; `TranslatedTupleAliasCollisionIsRejectedFailClosed`. |
| TCR-003 | High | Forwarded delivery | `TcpProxyCoordinator.cs:139-143,307-318` | Reverse injection uses the stored origin enumeration handle. `ForwardedFlowReverseInjectsTowardOriginAdapter`; `HostFlowReverseInjectsTowardMstcp`. |
| TCR-004 | High | Failure ownership | `TcpProxyCoordinator.cs:255-336,653-708` | Rewrite/injection failures retire the exact association; caller cancellation stays local. `ExistingFlowInjectionFailureReleasesAssociation`; cancellation regressions. |
| TCR-005 | Medium | Expiry race | `TcpProxyCoordinator.cs:435-449,685-694` | Only redirecting sessions expire; late relays cannot attach to retired state. `ExpiryRacingRelayEstablishmentCannotAttachDetachedRelay`. |
| TCR-006 | High | Dispose/setup race | `TcpProxyCoordinator.cs:68-73,453-514` | Single-flight disposal waits for in-flight setup before snapshot. `DisposeWaitsForInFlightSetupAndReleasesLateListener`. |
| TCR-007 | Medium | TCP protocol | `TcpProxyCoordinator.cs:362-413` | SYN payload/TCP Fast Open is blocked before redirect claim. `SynWithPayloadIsBlockedWithoutClaim`. |
| TCR-008 | Medium | TCP relay | `TcpProxyRelay.cs:77-150` | Relay forwards half-close and allows response completion. `HalfClosePropagatesFinAndAllowsReverseResponse`. |
| TCR-009 | Medium | TCP relay failure | `TcpProxyRelay.cs:84-110,170-177` | One pump fault cancels its stalled sibling promptly. `OneSidedRelayFailureCancelsSiblingPumpImmediately`. |
| TCR-010 | Medium | Accepted socket ownership | `TcpProxyCoordinator.cs:546-555`; `TcpRedirectListener.cs:42-47` | Unexpected accepted peers are closed before SOCKS relay setup. `UnrelatedAcceptedConnectionIsClosedBeforeRelaySetup`. |
| RUS-002 | Medium | Socket ownership | `Socks5Client.cs:264-308` | UDP socket is owned from allocation and released on control setup failure. `UdpSocketIsDisposedWhenControlSetupFails`. |
| RUS-003 | Medium | Cancellation | `Socks5Client.cs:122-142`; `UdpProxyCoordinator.cs:81-103` | Caller/shutdown cancellation stays cancellation. `CallerCancellationRemainsCancellationDuringHandshake`; `CoordinatorDisposalPreservesSetupCancellation`. |
| RUS-004 | Medium | Shared setup ownership | `UdpProxyCoordinator.cs:35-60,130-177` | Late setup faults remove their exact slot and release capacity. `CancelledWaiterDoesNotTearDownSharedSetupAndLateFaultReleasesCapacity`. |
| RUS-005 | Medium | Association liveness | `UdpAssociations.cs:9-23`; `UdpProxyCoordinator.cs:200-240` | Session send/receive refreshes the owned association. `SuccessfulSendRefreshesAssociationAndAvoidsStaleExpiry`; `ActiveSessionRetainsRelayAliasAfterItsCreationTimestampExpires`. |
| RUS-006 | Medium | Expiry race | `UdpProxyCoordinator.cs:130-159,204-227` | Expiry rechecks activity under the session gate before removal. `ExpirySnapshotDoesNotDisposeSessionWhoseSendRefreshesActivity`; receive variant. |
| RUS-007 | Medium | Receive failure lifecycle | `UdpProxyCoordinator.cs:180-255` | Receive faults promptly retire the exact session and transport. `ReceiveFaultDisposesAndRemovesSessionWithoutAnotherSend`; immediate-fault variant. |

The source evidence, reproduction details, and line-level review notes for each group remain in:

- `archive/2026-08/08-10-audit-core-config/research/audit-findings-audit-core-config.md`
- `archive/2026-08/08-10-audit-protocols/research/audit-findings-audit-protocols.md`
- `archive/2026-08/08-10-audit-ndisapi/research/audit-findings-audit-ndisapi.md`
- `archive/2026-08/08-10-audit-windows/research/audit-findings-audit-windows.md`
- `archive/2026-08/08-10-audit-runtime-capture-flow/research/audit-findings-audit-runtime-capture-flow.md`
- `archive/2026-08/08-10-audit-runtime-tcp/research/audit-findings-audit-runtime-tcp.md`
- `archive/2026-08/08-10-audit-runtime-udp-socks/research/audit-findings-audit-runtime-udp-socks.md`
- `archive/2026-08/08-10-audit-cli-integration/research/audit-findings-audit-cli-integration.md`

## Verification

- Baseline from the parent PRD: `dotnet build WinForward.slnx -c Release` completed with zero warnings/errors; `dotnet test WinForward.slnx -c Release` passed 149 tests.
- Final verification on this working tree: `dotnet build WinForward.slnx --configuration Release --no-restore` passed with 0 warnings and 0 errors; `dotnet test WinForward.slnx --configuration Release --no-restore` passed 232/232.
- Windows/NDIS and Native AOT verification is not implied by Linux tests; the concrete remaining procedure is in `windows-validation.md`.
