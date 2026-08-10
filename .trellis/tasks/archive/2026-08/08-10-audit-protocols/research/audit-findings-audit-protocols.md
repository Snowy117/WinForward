# Research: protocols audit findings

- **Query**: Perform a read-only audit of all six `src/WinForward.Protocols` files, their callers, and existing tests. Verify parser bounds, reject-without-mutation, IPv4/IPv6/TCP/UDP semantics, checksum correctness, SOCKS5 RFC 1928/1929 state handling, and SOCKS5 UDP ATYP/FRAG/length/caps.
- **Scope**: mixed
- **Date**: 2026-08-10

## Audit Method

The audit read every source line in the six requested files, all direct callers, and every protocol-facing test in `tests/WinForward.Core.Tests`. Static reproductions use only public APIs and packet bytes. The working tree contains Protocols fixes and task-specific regressions; the earlier report statement that this audit made no source or test changes was stale.

The focused Release run covering Protocols, SOCKS control, and adjacent packet tests passed:

```text
dotnet test tests/WinForward.Core.Tests/WinForward.Core.Tests.csproj -c Release --filter 'FullyQualifiedName~ProtocolAuditTests|FullyQualifiedName~Socks5ControlConnectionTests|FullyQualifiedName~TcpEndpointRewriteTests|FullyQualifiedName~UdpRelayTests|FullyQualifiedName~FlowAndConfigurationTests'
Passed: 113; Failed: 0; Skipped: 0
```

`dotnet build -c Release` passed with 0 warnings and 0 errors. `git diff --check` passed. Full Release test verification is recorded below after the run.

## Implementation Reconciliation

The confirmed findings are implemented in the current patch and mapped to regression coverage as follows:

| Finding | Implementation | Regression coverage |
|---|---|---|
| PRT-001 | `src/WinForward.Protocols/IpTcpUdpPacket.cs:113-128` checks the minimum TCP header and physical span before reading the data offset. | `tests/WinForward.Core.Tests/ProtocolAuditTests.cs:17-28` rejects an IPv4 TCP segment declaring only 8 transport bytes without throwing. |
| PRT-002 | `src/WinForward.Protocols/PacketChecksums.cs:75-86` verifies available IPv6 UDP bytes and declared UDP length before mutation. | `tests/WinForward.Core.Tests/ProtocolAuditTests.cs:30-51` covers 0-7 payload bytes and asserts byte-for-byte immutability. |
| PRT-003 | `src/WinForward.Protocols/IpTcpUdpPacket.cs:65-68`, `IpUdpPacket.cs:31-34`, and `PacketChecksums.cs:54-60,95-103` reject the IPv4 reserved fragment flag before rewriting. | `tests/WinForward.Core.Tests/ProtocolAuditTests.cs:53-69` covers both parsers and both endpoint rewriters, including unchanged-input assertions. |
| PRT-004 | `src/WinForward.Protocols/Socks5State.cs:65-89,154-174` validates reply REP/RSV/ATYP/prefix fields and returns the discriminated reply result. | `tests/WinForward.Core.Tests/ProtocolAuditTests.cs:71-85` rejects nonzero RSV, unassigned REP, unknown ATYP, and short prefixes; existing `FlowAndConfigurationTests` covers valid/failure/truncated reply behavior. |
| PRT-005 | `src/WinForward.Protocols/PacketChecksums.cs:8-23` folds the Internet checksum during accumulation, preventing `uint` overflow. | `tests/WinForward.Core.Tests/ProtocolAuditTests.cs:84-91` validates a 131,076-byte all-`0xff` oracle vector. |
| PRT-006 | `src/WinForward.Protocols/UdpFrameBuilder.cs:42-47` rejects payload and IPv4/UDP lengths that exceed wire field widths before allocation/casts. | `tests/WinForward.Core.Tests/ProtocolAuditTests.cs:93-102` rejects oversized IPv4 and IPv6 payloads with an empty output frame. |
| PRT-007 | `src/WinForward.Runtime/Socks5Client.cs:11-16,63-86,126-157,205-237` applies bounded resolution, handshake, and command operations while disposing failed attempts. | `tests/WinForward.Core.Tests/Socks5ControlConnectionTests.cs:11-84` covers resolver, greeting-read, caller-cancellation, and command-reply stalls; existing connection-attempt tests cover cap/disposal. |

## Findings

### Confirmed Defects

#### PRT-001 - TCP classifier reads beyond a short declared TCP segment

- **Severity**: High
- **Anchors**: `src/WinForward.Protocols/IpTcpUdpPacket.cs:113-127`; reachable from `src/WinForward.Runtime/CapturePacketProcessor.cs:36-44`.
- **Evidence**: `TryParseTransport` accepts `availableLength >= 8`, reads source/destination ports, and then unconditionally indexes `frame[transportOffset + 12]` for TCP's data offset. A TCP header is at least 20 bytes, so a declared IP payload of 8 through 19 bytes is invalid. When the physical frame ends at that declared payload, the index is outside the span and throws instead of returning `false`.

```csharp
// Ethernet(14) + IPv4(20) + declared TCP payload(8).
var frame = new byte[42];
frame[12] = 0x08; frame[13] = 0x00;
frame[14] = 0x45;
BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(16, 2), 28);
frame[23] = 6; // TCP
// IpTcpUdpPacket.TryParse(frame, out _) indexes frame[46] and throws.
```

The capture processor catches that exception only to dispose the lease and rethrow (`CapturePacketProcessor.cs:46-54`), despite its documented malformed-frame non-flow path (`CapturePacketProcessor.cs:10-15`). A malformed captured TCP frame can therefore escape classification and terminate the surrounding capture workflow.

- **Existing coverage**: `CapturePipelineTests.cs:50-57` covers an IPv4 frame truncated before its header and an MF fragment, but not a short declared TCP segment. No test invokes `CapturePacketProcessor.ProcessAsync`; repository search found no process-level parser failure test.
- **Regression coverage now present**: `ProtocolAuditTests.TcpParserRejectsShortDeclaredSegmentWithoutThrowing` uses the 42-byte frame and verifies a `false` result without an exception. Capture-processor non-flow routing remains a coverage gap.

#### PRT-002 - IPv6 UDP endpoint rewriter indexes a missing UDP header

- **Severity**: Medium
- **Anchors**: `src/WinForward.Protocols/PacketChecksums.cs:64-76`, specifically the read at `:70`; `TryFindIpv6Transport` at `:123-138`.
- **Evidence**: An IPv6 base header with `Payload Length == 0` and `Next Header == UDP` passes the physical-length check and makes `TryFindIpv6Transport` return a transport offset of 54. `TryRewriteIpv6` then reads `frame.Slice(udpOffset + 4, 2)` before proving that any UDP header bytes exist. The public `TryRewriteUdpEndpoints` API therefore throws rather than returning `false` for a malformed frame.

```csharp
var frame = new byte[14 + 40];
frame[12] = 0x86; frame[13] = 0xdd;
frame[14] = 0x60; // IPv6
frame[20] = 17;   // UDP, while Payload Length remains zero

PacketChecksums.TryRewriteUdpEndpoints(
    frame, IPAddress.Parse("2001:db8::1"), 1,
    IPAddress.Parse("2001:db8::2"), 2);
// ArgumentOutOfRangeException from Slice(58, 2), not false.
```

The same condition exists after a valid extension chain when fewer than six bytes remain before the IPv6 payload boundary. No bytes have been rewritten before this throw, but the `Try...` reject contract is not met.

- **Existing coverage**: `FlowAndConfigurationTests.cs:844-866` covers only a valid IPv6/UDP rewrite. `TcpEndpointRewriteTests.cs:162-186` covers a TCP rewriter's IPv6 fragment and unsupported-chain rejections, not the UDP rewriter's short payloads or immutability on rejection.
- **Caller reachability**: No production caller currently invokes `TryRewriteUdpEndpoints`; all three direct uses are tests at `FlowAndConfigurationTests.cs:484`, `:497`, and `:848`. The public API remains reachable to consumers of the Protocols assembly.
- **Regression coverage now present**: `ProtocolAuditTests.UdpIpv6RewriterRejectsShortPayloadWithoutMutating` covers direct IPv6 UDP payload lengths 0 through 7, rejects without throwing, and asserts byte-for-byte preservation. An equivalent short extension-chain matrix remains a coverage gap.

#### PRT-003 - IPv4 reserved fragment flag is accepted by parsers and endpoint rewriters

- **Severity**: Low
- **Anchors**: `src/WinForward.Protocols/IpTcpUdpPacket.cs:67-68`; `src/WinForward.Protocols/IpUdpPacket.cs:33-34`; `src/WinForward.Protocols/PacketChecksums.cs:48-49` and `:87-88`.
- **Evidence**: The IPv4 flags/fragment-offset field's high bit is the RFC 791 reserved bit and must be zero. The code rejects offset and MF but masks only `0x3fff`, or separately tests `0x1fff` and `0x2000`; neither form tests `0x8000`. Thus a packet with flags/offset word `0x8000` is accepted as unfragmented. The TCP and UDP endpoint rewriters then alter that malformed input and return `true`.

```csharp
var frame = CreateValidIpv4UdpFrame();
frame[20] = 0x80; // IPv4 byte 6: reserved flag set.

IpUdpPacket.TryParse(frame, out _); // true
PacketChecksums.TryRewriteUdpEndpoints(frame, newSource, 1, newDestination, 2); // true and mutates frame
```

RFC 791 identifies flag bit 0 as reserved and requires it to be zero. This conflicts with the parser documentation's malformed-header rejection claim in `IpTcpUdpPacket.cs:39-41` and with the task's reject-without-mutation rule.

- **Existing coverage**: `CapturePipelineTests.cs:50-57`, `FlowAndConfigurationTests.cs:454-477`, and `TcpEndpointRewriteTests.cs:123-134` exercise MF or offset, not the reserved flag. UDP rewriter rejection paths have no immutability tests.
- **Regression coverage now present**: `ProtocolAuditTests.ReservedIpv4FragmentFlagIsRejectedWithoutMutating` sets `0x8000` on IPv4 TCP and UDP frames, verifies both parsers and rewriters reject, and asserts both frames remain unchanged.

#### PRT-004 - SOCKS5 reply validators accept invalid fixed fields

- **Severity**: Medium
- **Anchors**: `src/WinForward.Protocols/Socks5State.cs:65-89`, `:154-173`; live reader at `src/WinForward.Runtime/Socks5Client.cs:189-227`.
- **Evidence**: RFC 1928 defines reply `RSV` as `X'00'` and ATYP as only `1`, `3`, or `4`. `TryParseReply` never reads `reply[2]`, and an unknown ATYP produces `addressLength == 0`, `portOffset == 4`, and `Success` for any eight-byte input. `TryParseReplyPrefix` claims to validate a five-byte prefix but accepts a two-byte span and checks only VER.

```csharp
var nonZeroReserved = new byte[] { 5, 0, 1, 1, 192, 0, 2, 53, 0x14, 0xe9 };
Socks5Messages.TryParseReply(nonZeroReserved, out _, out _, out _);
// Success. In Socks5ControlConnection, this also passes the live prefix/length/full-reply sequence.

var unknownAtyp = new byte[] { 5, 0, 0, 9, 0, 0, 0, 0 };
Socks5Messages.TryParseReply(unknownAtyp, out _, out _, out _);
// Success, although ATYP 9 is invalid.

Socks5Messages.TryParseReplyPrefix(new byte[] { 5, 0 }, out _);
// true despite the method's five-byte-prefix contract.
```

The live reader does reject unknown ATYP through `TryGetReplyLength` before it invokes the full parser (`Socks5Client.cs:203`), but accepts a nonzero `RSV` end-to-end. Failure REP values are intentionally treated as failure before address parsing (`Socks5State.cs:72-74`); that existing behavior is not included in this finding.

- **Existing coverage**: `FlowAndConfigurationTests.cs:275-299` covers valid IPv4 success, undersized failure, and truncated success. `:435-451` covers valid five-byte success/failure prefixes and a bad version. Neither suite asserts nonzero RSV, unknown ATYP, or undersized prefix behavior.
- **Regression coverage now present**: `ProtocolAuditTests.SocksReplyParsersRejectInvalidFixedFields` covers nonzero RSV, unassigned REP, unknown ATYP, and prefixes shorter than five bytes. Existing `FlowAndConfigurationTests` covers valid/failure/truncated reply behavior; a scripted malformed-success control-connection test remains a coverage gap.

#### PRT-005 - Public InternetChecksum overflows for ordinary-sized generic inputs

- **Severity**: Low
- **Anchors**: `src/WinForward.Protocols/PacketChecksums.cs:8-15`, `:169-181`.
- **Evidence**: `InternetChecksum` accumulates all 16-bit words in a `uint` and folds only after the loop. With no checked-overflow build setting in `Directory.Build.props`, a 65,538-word input overflows the accumulator. The method is public and has no input-length limit.

```csharp
var data = new byte[131_076]; // 65,538 words
Array.Fill(data, (byte)0xff);

PacketChecksums.InternetChecksum(data); // returns 0x0001
// Independent one's-complement arithmetic: any count of 0xffff words folds to 0xffff, so checksum is 0x0000.
```

After 65,537 `0xffff` words, `sum` is `uint.MaxValue`; adding the next word wraps to `0x0000fffe`, which `Finish` complements to `0x0001`. This does not affect current IPv4/IPv6/TCP/UDP call sites because their declared protocol lengths are at most 65,535 bytes, but it makes the general public API incorrect.

- **Existing coverage**: Small-header checksum validation exists at `UdpRelayTests.cs:40-58` and TCP validation uses independent `Sum`/`Finish` at `TcpEndpointRewriteTests.cs:243-294`. Neither covers a generic input beyond the accumulator threshold.
- **Regression coverage now present**: `ProtocolAuditTests.InternetChecksumFoldsLargeGenericInputWithoutOverflow` asserts the all-`0xff`, 131,076-byte vector yields zero. An odd-length large-input vector remains a coverage gap.

#### PRT-006 - UdpFrameBuilder.TryBuild can throw when an injected cap exceeds wire field widths

- **Severity**: Low
- **Anchors**: `src/WinForward.Protocols/UdpFrameBuilder.cs:23-77`, especially `:43-45`, `:84`, `:103`, and `:114`.
- **Evidence**: The configurable `maximumEthernetFrame` only limits the allocated Ethernet frame. It does not limit IPv4 Total Length or IPv6/UDP 16-bit length fields before construction. A cap of 65,570 permits a 65,528-byte IPv4 payload (`14 + 20 + 8 + 65,528`), but the computed UDP length is 65,536. The method allocates the frame and then throws `OverflowException` at the checked IPv4 total-length cast. IPv6 reaches the checked UDP-length cast instead.

```csharp
var payload = new byte[65_528];
UdpFrameBuilder.TryBuild(
    IPAddress.Parse("192.0.2.1"), 1,
    IPAddress.Parse("192.0.2.2"), 2,
    payload, sourceMac, destinationMac,
    out _, maximumEthernetFrame: 65_570);
// OverflowException, rather than false with an empty frame.
```

The default 1,514-byte cap and tested 9,014-byte cap prevent this in the current NDISAPI path (`src/WinForward.NdisApi/NdisApiAbi.cs:14`), but the public `TryBuild` API's documented boolean failure behavior does not hold for supported parameter values.

- **Existing coverage**: `UdpRelayTests.cs:130-149` checks 1,514 and 9,014 boundaries only. No test uses a cap beyond the IP/UDP wire-field limits.
- **Regression coverage now present**: `ProtocolAuditTests.FrameBuilderRejectsPayloadsBeyondWireLengthFields` verifies IPv4 and IPv6 payloads that would overflow the UDP/IP wire fields return `false` and an empty frame. Largest-valid-length and allocation-boundary cases remain a coverage gap.

#### PRT-007 - SOCKS connection attempt timeout ends at TCP connect, not authentication or command state reads

- **Severity**: Medium
- **Anchors**: `src/WinForward.Runtime/Socks5Client.cs:41-83`, `:94-143`, `:173-228`.
- **Evidence**: `ConnectOnceAsync` creates an `attemptCts` with `CancelAfter(timeout)` only around `socket.ConnectAsync` (`:122-124`). It then calls `AuthenticateAsync` with the caller's cancellation token (`:127`), and the greeting/authentication/command replies use asynchronous `NetworkStream.ReadExactlyAsync` (`:176-186`, `:191-206`) with no per-attempt deadline. `Socket.ReceiveTimeout` and `SendTimeout` configured at `:116-117` affect synchronous operations only, not the asynchronous reads used here, according to Microsoft documentation.

Deterministic state reproduction:

1. Start a loopback TCP listener that accepts a connection and never sends a SOCKS method-selection reply.
2. Call `Socks5ControlConnection.ConnectAsync` with that listener address and a small `perAttemptTimeout`.
3. TCP connect succeeds before the timeout; `AuthenticateAsync` sends its greeting and waits indefinitely in `ReadExactlyAsync` unless the external cancellation token is cancelled.

This contradicts the method's own per-attempt deadline description at `Socks5Client.cs:35-39`. `UdpProxyCoordinator.TrySendAsync` awaits session creation at `UdpProxyCoordinator.cs:49-60`, so a proxy-selected UDP flow can retain a setup task and capacity slot while its SOCKS state transition is stalled.

- **Existing coverage**: `FlowAndConfigurationTests.cs:358-419` covers the connect-attempt cap and disposal when socket creation or connection fails quickly. It does not cover a successful TCP connection that stalls during SOCKS greeting, RFC 1929 authentication, or command reply reading.
- **Regression coverage now present**: `Socks5ControlConnectionTests` covers resolver timeout, a stalled method-selection read, caller cancellation, and a stalled UDP ASSOCIATE command reply. Authentication-response stall coverage remains a gap; the implementation applies the same attempt token to authentication reads.

### Coverage Gaps, Not Confirmed Defects

| ID | Area | Evidence and boundary not currently covered |
|---|---|---|
| GAP-001 | `IpTcpUdpPacket` parser matrix | Existing parser tests are limited to baseline IPv4/IPv6 TCP/UDP, one MF frame, and short pre-header input (`CapturePipelineTests.cs:17-68`). No tests cover IPv4 IHL options, Total Length mismatch, UDP declared-length mismatch, TCP data offsets 0/4/over-available, IPv6 extension accept paths, 256-byte extension cap, or malformed extension lengths. PRT-001 is the one confirmed gap that throws. |
| GAP-002 | `IpUdpPacket` parser matrix | Tests cover normal IPv4/IPv6 payload extraction and IPv4 MF/IPv6 Fragment Header (`FlowAndConfigurationTests.cs:454-477`, `:830-876`). Direct tests are absent for short/mismatched UDP lengths, IPv4 IHL options, IPv6 accepted extension chains, malformed extension lengths, and the IPv4 reserved flag in PRT-003. |
| GAP-003 | UDP rewrite reject immutability | `TryRewriteUdpEndpoints` success is covered for IPv4 and IPv6 (`FlowAndConfigurationTests.cs:480-510`, `:843-866`), but no test asserts unchanged input for its reject paths. TCP has narrow reject-without-mutation coverage at `TcpEndpointRewriteTests.cs:97-199`; it does not cover the PRT-003 reserved flag. |
| GAP-004 | Independent UDP-rewrite checksum oracle | The IPv4/IPv6 UDP rewrite tests calculate a pseudo-header but use production `PacketChecksums.InternetChecksum` as the final oracle (`FlowAndConfigurationTests.cs:504-510`, `:859-866`). `UdpRelayTests.cs:55-58` and `:110-112` use independent `Sum`/`Finish`, but only for newly built frames, not endpoint rewrites. |
| GAP-005 | Exact TCP zero-checksum vector | The implementation correctly distinguishes TCP from UDP at `PacketChecksums.cs:147-149`, and TCP validation is otherwise independent. `TcpChecksumNeverInvertsZeroToFFFF` at `TcpEndpointRewriteTests.cs:225-235` does not construct a segment whose checksum is actually zero, so the exceptional stored-zero rule lacks a direct vector. |
| GAP-006 | Captured checksum acceptance policy | Neither IP parser validates IPv4 header, TCP, or UDP checksums (`IpTcpUdpPacket.cs:56-129`, `IpUdpPacket.cs:23-75`). This may be deliberate for capture checksum offload, and no stated contract requires validation, so it is not classified as a defect. The policy and its NIC-offload rationale have no direct test/documented assertion. |
| GAP-007 | SOCKS5 request/auth/reply breadth | Tests cover greeting bytes, one IPv4 UDP ASSOCIATE request, one IPv4 reply, short failure/success reply behavior, selected normalization, and PRT-004's invalid REP/RSV/ATYP/prefix rejections (`FlowAndConfigurationTests.cs:259-355`, `:435-451`; `ProtocolAuditTests.cs:71-85`). They do not cover IPv6 request bytes, username/password 0/255/256-byte boundaries, multibyte UTF-8 boundaries, all valid full-reply ATYP forms, or valid `TryGetReplyLength` layouts. |
| GAP-008 | SOCKS5 UDP decode matrix | Normal IPv6 and domain round trips, FRAG rejection, and IPv6 scope preservation are covered (`FlowAndConfigurationTests.cs:101-132`, `:347-355`). The codec source itself correctly checks RSV, FRAG, ATYP, domain length, and needed port bytes at `Socks5Udp.cs:38-61`; tests do not enumerate nonzero RSV, invalid ATYP, every truncation boundary, zero-domain length, maximum 255-byte domain, or zero-length payload. |
| GAP-009 | SOCKS5 UDP size budget | `Socks5UdpCodec.Encode` has no explicit frame-size cap (`Socks5Udp.cs:11-35`). Current NDIS capture caps make inbound payloads far smaller than UDP limits, and the receive buffer is 65,535 bytes (`UdpProxyCoordinator.cs:229-240`), so no current caller-level failure is confirmed. RFC 1928 requires a SOCKS-aware UDP interface to account for header overhead; no boundary test documents the intended cap for IPv4/IPv6/domain frames. |
| GAP-010 | Live SOCKS state tests | The `Socks5ControlConnection` parser/state machine has no scripted-stream or loopback protocol test. The current tests only exercise connection failures before any SOCKS bytes are read (`FlowAndConfigurationTests.cs:358-419`). PRT-004 and PRT-007 demonstrate the impact of this missing state coverage. |
| GAP-011 | UDP relay domain response behavior | `Socks5UdpCodec.TryDecode` supports a domain ATYP (`Socks5Udp.cs:51-58`), but `UdpProxySession.ReceiveLoopAsync` discards a response when `DestinationAddress` is null (`UdpProxyCoordinator.cs:236-240`). It is not a confirmed codec defect because reinjection needs an IP source address; there is no documented/tested behavior for a relay response using domain ATYP. |
| GAP-012 | IPv6 extension support boundary | All three packet paths recognize Hop-by-Hop (0), Routing (43), and Destination Options (60), cap accumulated extension bytes at 256, and reject Fragment (44). TCP rewrite proves the Hop-by-Hop accept path (`TcpEndpointRewriteTests.cs:201-223`). There is no equivalent parser/UDP-rewriter extension success test and no test that labels AH, ESP, Mobility, jumbograms, or extension chains over 256 bytes as intentionally unsupported. |

## Per-File Conclusions

| File | Result | Evidence |
|---|---|---|
| `src/WinForward.Protocols/IpTcpUdpPacket.cs` | PRT-001 and PRT-003 are fixed. Other IPv4/IPv6 protocol/length/fragment checks are bounds-first; IPv6 extension traversal is limited to 256 bytes and rejects Fragment Header. | `:43-131`; caller `CapturePacketProcessor.cs:36-44`; regressions `ProtocolAuditTests.cs:17-69`, baseline parser tests `CapturePipelineTests.cs:17-68`. |
| `src/WinForward.Protocols/IpUdpPacket.cs` | PRT-003 is fixed. IPv4 and IPv6 UDP payload extraction verifies IP and UDP declared lengths before copying payload; no checksum validation policy is specified. | `:10-75`; caller `NdisPacketActionExecutor.cs:58-82`; regressions `ProtocolAuditTests.cs:53-69`, baseline tests `FlowAndConfigurationTests.cs:454-477,830-876`. |
| `src/WinForward.Protocols/PacketChecksums.cs` | PRT-002, PRT-003, and PRT-005 are fixed. IPv4/IPv6 TCP and bounded UDP pseudo-header checksum construction is otherwise correct; UDP computed zero is transmitted as `0xffff`, and TCP zero is retained. | `:8-193`; TCP callers `TcpProxyCoordinator.cs:166`, `:226`; regressions `ProtocolAuditTests.cs:30-69,84-91`, rewrite tests `TcpEndpointRewriteTests.cs:20-235`. |
| `src/WinForward.Protocols/Socks5State.cs` | PRT-004 is fixed. Greeting, RFC 1929 username/password layout, request byte layout, reply REP/RSV/ATYP and port validation, wildcard normalization, mapped IPv4 conversion, and IPv6 scope propagation follow their tested contracts. | `:26-175`; caller `Socks5Client.cs:256-295`; regressions `ProtocolAuditTests.cs:71-85`, baseline tests `FlowAndConfigurationTests.cs:259-355,435-451`. |
| `src/WinForward.Protocols/Socks5Udp.cs` | No confirmed codec defect. Decoder enforces `RSV == 0`, `FRAG == 0`, ATYP in 1/3/4, domain length nonzero, and address/port bounds before constructing output. | `:11-84`; caller `Socks5Client.cs:310-325`; tests `FlowAndConfigurationTests.cs:101-132,347-355`. |
| `src/WinForward.Protocols/UdpFrameBuilder.cs` | PRT-006 is fixed. Default and configured 1,514/9,014 caps, family and MAC checks, Ethernet/IP/UDP field layout, and output checksums are correct within protocol field widths. | `:23-120`; caller `UdpResponseReinjector.cs:84-112`; regression `ProtocolAuditTests.cs:93-102`, cap tests `UdpRelayTests.cs:21-149`. |

## Caller Map

| Protocol API | Production callers | Observed contract |
|---|---|---|
| `IpTcpUdpPacket.TryParse` | `CapturePacketProcessor.cs:36` | `false` routes to non-flow policy; the PRT-001 short-TCP throw is fixed, though no direct `CapturePacketProcessor.ProcessAsync` regression exists. |
| `IpUdpPacket.TryParse` | `NdisPacketActionExecutor.cs:60` | `false` causes proxy-selected UDP frame to be consumed fail-closed. |
| `PacketChecksums.TryRewriteTcpEndpoints` | `TcpProxyCoordinator.cs:166`, `:226` | `false` blocks TCP redirection; caller copies the captured frame before rewrite. |
| `PacketChecksums.TryRewriteUdpEndpoints` | No production caller | Exposed public helper, exercised by Core tests; PRT-002/003 reject paths now fail without mutation. |
| `Socks5Messages` | `Socks5ControlConnection` at `Socks5Client.cs:256-295` | Encodes state transitions and parses server replies; PRT-004 fixed-field validation is enforced before endpoint construction. |
| `Socks5UdpCodec` | `Socks5UdpTransport` at `Socks5Client.cs:310-325` | Outbound frames encode numeric IP endpoints; inbound malformed codec frames throw `IOException` after decoder rejects them. |
| `UdpFrameBuilder.TryBuild` | `UdpResponseReinjector.cs:84-112` | `false` logs and drops a relay response; PRT-006 wire-field limits are now checked before allocation/casts, including arbitrary larger configured caps. |

## Protocol Verification Summary

| Topic | Verified behavior | Qualification |
|---|---|---|
| IPv4 declared length | Both parsers require header length, total length, and physical span consistency before payload parsing. | PRT-001 adds the TCP 20-byte minimum-header guard; direct capture-processor coverage remains a gap. |
| IPv4 fragmentation | Both parsers and rewriters reject the reserved flag, MF, and nonzero offset. | PRT-003 is fixed. |
| IPv6 declared length/extensions | Parsers/rewriters require physical `Payload Length`, traverse 0/43/60 with a 256-byte cap, and reject 44. | PRT-002 rejects a short UDP payload before any read or mutation; other extension types are intentionally unsupported but unlabelled in tests. |
| UDP length | Parsers require declared length 8 through available transport bytes. Builders write header-inclusive lengths. | No checksum validation on captured input, recorded as GAP-006. |
| Checksum arithmetic | UDP pseudo-headers use protocol 17 and write computed zero as `0xffff`; TCP uses protocol 6 and stores a computed zero directly; IPv4 headers are recomputed after address mutation. | PRT-005 folds generic `InternetChecksum` accumulation; the UDP rewrite oracle remains a coverage gap (GAP-004). |
| SOCKS5 greeting/RFC 1929 | Greeting bytes and UTF-8 byte-length check are consistent with the tested protocol layout; authentication reply requires version 1 and status 0. | PRT-007 records the runtime attempt deadline fix; authentication-response stall coverage remains a gap. |
| SOCKS5 request/reply | Requests write `VER=5`, reserved zero, numeric ATYP, and network-order port. | PRT-004 validates assigned REP values plus reply RSV and ATYP before accepting a success reply. |
| SOCKS5 UDP framing | Encoders leave RSV/FRAG zero; decoder rejects nonzero RSV/FRAG, unsupported ATYP, zero domain, and truncation before output allocation. | Header-overhead cap is untested (GAP-009). |
| Frame caps | Default NDIS target is 1,514 and 9,014 is tested through the injectable cap. | PRT-006 rejects payloads exceeding UDP or IPv4 wire field widths before allocation. |

## External References

- [RFC 1928, SOCKS Protocol Version 5](https://www.rfc-editor.org/rfc/rfc1928.html) - Sections 5-7 define ATYP, reply `RSV = X'00'`, UDP `RSV = X'0000'`, standalone `FRAG = X'00'`, and optional-fragment rejection behavior.
- [RFC 1929, Username/Password Authentication for SOCKS V5](https://www.rfc-editor.org/rfc/rfc1929.html) - Defines subnegotiation version 1 and one-byte username/password lengths in the 1-255 range.
- [RFC 791, Internet Protocol](https://www.rfc-editor.org/rfc/rfc791.html) - Defines IPv4 Total Length, reserved fragment flag bit, MF, fragment offset, and header checksum semantics.
- [RFC 768, User Datagram Protocol](https://www.rfc-editor.org/rfc/rfc768.txt) - Defines UDP's header-inclusive length, pseudo-header checksum, and computed-zero transmission as all ones.
- [RFC 8200, IPv6 Specification](https://www.rfc-editor.org/rfc/rfc8200.html) - Defines Payload Length as all bytes after the base header and specifies extension and Fragment Header structure.
- [Socket.ReceiveTimeout documentation](https://learn.microsoft.com/en-us/dotnet/api/system.net.sockets.socket.receivetimeout?view=net-10.0) - States that `ReceiveTimeout` applies only to synchronous `Receive` calls.
- [NetworkStream.ReadTimeout documentation](https://learn.microsoft.com/en-us/dotnet/api/system.net.sockets.networkstream.readtimeout?view=net-10.0) - States that the timeout does not affect asynchronous `ReadAsync`; relevant to PRT-007's `ReadExactlyAsync` calls.

## Related Specs

- `.trellis/tasks/08-10-audit-protocols/prd.md` - Audit requirements and acceptance criteria for the six Protocols files.
- `.trellis/tasks/08-10-audit-protocols/design.md` - States that parser/frame-builder bounds, mutation primitives, and independent checksum oracles are audit layers.
- `.trellis/tasks/archive/2026-08/08-07-winforward-proxy/design.md` - Release-1 contract: malformed or fragmented proxy-selected traffic is fail-closed; SOCKS UDP fragmentation is unsupported; native frame ABI is capped.
- `.trellis/spec/backend/quality-guidelines.md` - Referenced by the active task as the independent checksum and immutable-reject-path review checklist.

## Caveats / Not Found

- No dedicated `WinForward.Protocols` test project exists; Protocols tests are embedded in `WinForward.Core.Tests`, including task-specific `ProtocolAuditTests.cs`.
- No production caller of `PacketChecksums.TryRewriteUdpEndpoints` exists at this revision.
- No direct `CapturePacketProcessor.ProcessAsync` test exists, so PRT-001's capture workflow effect follows the source's explicit catch-and-rethrow path rather than an existing integration test.
- No full live SOCKS server test harness was found. PRT-004's malformed fixed-field paths are covered by pure tests and PRT-007's resolver/handshake/command timeout paths by loopback tests; authentication-response stall and malformed-success live-reader tests remain coverage gaps.

## Final Verification

- Focused Release protocol/runtime test filter: passed, 113/113.
- Release build: passed, 0 warnings, 0 errors.
- `git diff --check`: passed.
- Full `dotnet test -c Release`: passed, 215/215 tests, 0 skipped.
- Native AOT publish and Windows hardware behavior remain environment-gated; no Windows driver or Hyper-V host was available for this audit.
