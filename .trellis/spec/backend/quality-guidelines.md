# Quality Guidelines

> Code quality standards for backend development: build/analyzer posture, behavior-zero refactor discipline, and testing requirements.

---

## Current Conventions

- Build with nullable analysis, `TreatWarningsAsErrors`, Native AOT/trim analyzers, and the centrally pinned analyzer packages.
- Keep packet/ABI hot paths allocation-conscious, but preserve explicit bounds checks and ownership guards.
- Use localized `#pragma warning` suppression only when the behavior is intentional and documented (for example, rollback cleanup that must continue after one restore failure).
- Process selectors are exact: a selector without a directory separator matches an executable filename, while a selector containing `/` or `\\` matches a normalized full path.
- UDP routing uses the original local/remote endpoint tuple plus protocol, address family, and origin context. PID and DNS transaction IDs are metadata/payload, never association keys.
- Packet endpoint rewrite is a protocol-layer primitive shared by UDP and TCP redirect. `PacketChecksums.TryRewriteUdpEndpoints` / `TryRewriteTcpEndpoints` rewrite ONLY IP src/dst addresses, transport src/dst ports, and checksums (IPv4 header + transport). They must NEVER touch TCP seq/ack/flags/window/options/payload or UDP length/payload — this invariant is the bedrock of transparent local redirect (design §8 step 3) and is enforced byte-for-byte by the mutable-offset test assertion. Allocation-free `Span<byte>` in/out; bounds-check fully before any write; reject-without-mutating on every malformed input.
- TCP checksum has NO optional-zero-checksum provision (RFC 9293): `WriteTcpChecksum` stores the folded result verbatim, never inverting a computed 0x0000 to 0xFFFF. This deliberately diverges from `WriteUdpChecksum`, which MUST invert 0→0xFFFF per RFC 768. Do not unify these two helpers behind a flag — the divergence is load-bearing.
- IPv4 fragment rejection uses mask `0xbfff` (reserved bit + MF + fragment offset; DF allowed) in both the canonical `IPTcpUdpPacket` parser and the UDP-only `IPUdpPacket` parser. When adding a new transport rewrite, mirror the canonical parser for that transport.
- Proxy coordinators (UDP `UdpProxyCoordinator`, TCP `TcpProxyCoordinator`) mirror one structural shape: a flow-keyed session dict + Lock + shutdown CTS + capacity + `DisposeAsync` teardown. A flow is claimed exactly once in its association table; retransmitted/duplicate initial packets reuse the existing association (touch + re-inject), never allocate a second listener/transport. Every setup, rewrite, injection, or relay failure fails closed and releases exactly the resources acquired so far in acquisition order — a proxy-selected flow is never silently passed (design §8, R8).
- Coordinator teardown is single-flight: the first `DisposeAsync` marks the coordinator closed, cancels shared setup, snapshots and releases owned sessions, and all concurrent disposal callers await that same cleanup task. New sends after teardown begins fail with `ObjectDisposedException`; a canceled waiter never cancels shared setup owned by other callers.
- TCP reverse routing is keyed by its full pre-rewrite wire tuple (`client-address:proxy-port` to `server-address:original-client-port`), never a listener port alone. A reverse probe that is not an exact association must not touch activity or rewrite the frame. A caller cancellation on an existing TCP retransmit/rewrite propagates only to that caller; it must not tear down the shared redirect association.
- Concurrent initial packets for the same flow race through the coordinator's pre-claim fast path (resolve-by-original returns empty for all racers before any `TryClaim` runs). The association table's `TryClaim` is the single exactly-once arbiter: later racers receive the existing association, not a new one. The coordinator MUST detect this (compare the returned association's translated tuple to the just-allocated listener's) and release the redundant listener + fall back to the re-inject path. Failing to do so causes a duplicate-key crash on the session dict. Expose an internal concurrent-loser counter (e.g. `ConcurrentLoserCount`) so a test can deterministically assert the defense branch fired. The `ConcurrentSynBurstWithAsyncListenerStaysExactlyOnce` test gates `CreateAsync` on a `TaskCompletionSource` released only when all N callers have arrived (proving every caller passed the empty-table fast path before any claim), then asserts `ConcurrentLoserCount == N-1`. A `Task.Yield()`-only fake is scheduler-dependent and NON-load-bearing — it does not reliably reproduce the race. When testing proxy-coordinator concurrency, gate the allocation seam on a barrier/TCS so all racers provably pass the fast path before any claim lands, and assert via an instrumented counter rather than relying on scheduler interleaving.
- Association tables (`UdpAssociationTable`, `TcpRedirectTable`) use a single instance `_gate` lock for ALL mutating and reading methods, including internal lookup helpers. Do NOT lock the `Dictionary` object itself in a helper while other methods lock `_gate` — that diverges from the reference, breaks the documented "single gate lock" contract, and risks a future lock-ordering deadlock if a method that holds one lock calls another that acquires the other.
- `FlowTable.TryResolve` is the packet-observation lookup: every successful exact, reverse, origin-flipped, or adapter-agnostic resolution MUST call `FlowState.Touch` before returning, so active flows cannot expire at their pre-lookup deadline. `TryGet` is a non-observing lookup and intentionally does not refresh activity.
- Boundary constructors and parsers must be intentional about null inputs: `Endpoint.From(IPAddress, ushort)` rejects a null address with `ArgumentNullException`, while `IPPrefix.TryParse(string?, out IPPrefix)` returns `false` without throwing. Configuration validation must turn null JSON array elements into indexed diagnostics rather than allowing a `NullReferenceException`.
- Normalized remote-port intervals are sorted by start/end and merged when overlapping or adjacent. Downstream rule matching receives the canonical disjoint interval list, never user ordering or duplicate ranges.
- Capture composition owns proxy coordinators inside the capture-loop disposal boundary: stop the sweeper and capture pumps, then dispose UDP/TCP sessions, and only afterward restore adapter modes. Active `StopAsync` must cancel and await the capture run before releasing those resources.
- Structural refactors are behavior-zero and gate on the test baseline: prefer mechanical line-range moves (script-assisted) over retyping, never modify assertion semantics in test moves, and require `dotnet test` totals to equal the recorded baseline (344 as of 2026-08-29) plus a zero-warning build before committing each batch. See directory-structure.md for file-size and split conventions.

## Testing Requirements

- Every flow or association ownership change needs a regression for same-key reuse, distinct-key isolation, and deterministic collision/failure behavior.
- Concurrency tests must use genuinely overlapping tasks, not only sequential repeated calls.
- Pure tests run on any host; NDISAPI and Windows attribution tests must remain behind ABI/platform seams.
- Packet-rewrite tests must validate checksums with INDEPENDENTLY reimplemented `Sum`/`Finish` helpers (not the production routines under test), assert only the expected mutable bytes changed via an explicit offset set, prove round-trip rewrite-back-to-original is byte-identical, and assert every reject path leaves the input span unchanged.
- Flow lookup regressions must verify that an observation refreshes `LastActivityUtc` before an idle-expiry boundary; also preserve a non-observing lookup test where applicable.
- Configuration tests must cover null DTO array entries, merged adjacent/overlapping port ranges, unknown JSON field paths, and paired credential limits at both 255-byte accepted and 256-byte rejected UTF-8 boundaries.
- Lifecycle tests must assert coordinator disposal precedes mode restoration on normal completion, capture failure, and concurrent stop, using an ordered event seam rather than scheduler timing.

## Scenario: Bounded Pooled SOCKS5 UDP Receive Storage

### 1. Scope / Trigger

- Trigger: changing UDP relay receive storage, SOCKS5 UDP decoding, response reinjection, or the pinned NDIS maximum frame size.

### 2. Signatures

- `UdpProxyCoordinator(..., int maximumFrameSize = UdpFrameBuilder.DefaultMaximumEthernetFrame)` owns the receive-window bound.
- `UdpResponseReinjector(..., int maximumFrameSize = UdpFrameBuilder.DefaultMaximumEthernetFrame, ...)` owns the rebuilt-frame bound.
- `Socks5UdpTransport.ReceiveAsync(Memory<byte> buffer, CancellationToken)` returns a discriminated `Socks5UdpReceiveResult`; a receive whose byte count fills the supplied buffer reports the `Oversized` skip instead of a datagram.

### 3. Contracts

- Composition passes the same pinned `maximumFrameSize` to the coordinator and reinjector. A relay response that cannot fit the reinjection cap must never be accepted into a larger independent receive contract.
- Per active UDP session, rent one buffer sized `maximumFrameSize + 22 + 1`: maximum Ethernet frame, maximum SOCKS5 UDP header, and one oversize sentinel byte.
- The receive loop owns the rented array for its full lifetime and returns it exactly once in `finally`, including cancellation, socket disposal, malformed input, immediate receive failure, and normal session teardown.
- `receivedBytes >= buffer.Length` means the datagram may be truncated — it is a **skip**, not a session failure (superseded 2026-08-28, task 08-28-udp-loss-design-flaws D2).
- **Per-datagram anomalies skip, never tear down** (supersedes the earlier throw-based matrix): `Socks5UdpTransport.ReceiveAsync` returns a discriminated `Socks5UdpReceiveResult` — a valid datagram, or a skip with reason `UnexpectedSource` / `Oversized` / `Malformed`. The session receive loop counts skips, emits a per-session rate-limited (>=5s) summary log, and continues; only socket-level exceptions (`SocketException`, `ObjectDisposedException`, shutdown cancellation) are fatal via `_receiveFailure`. Per-response sink (`InjectAsync`) failures are likewise isolated per response. A single >=1537B or malformed relay datagram must NOT stop response delivery for the flow.
- Decoded payloads are owner-bound `ReadOnlyMemory<byte>` slices and must be consumed before the next receive or buffer return; they must not escape the awaited response-sink call.

### 4. Validation & Error Matrix

| Condition | Required result |
| --- | --- |
| `maximumFrameSize <= 0` | Constructor throws `ArgumentOutOfRangeException` |
| Receive result is smaller than the sentinel window and decodes successfully | Await the response sink before reusing the buffer |
| Receive result fills the sentinel window | Skip (`Oversized`); do not decode or reinject; do NOT tear down the session |
| Malformed SOCKS5 UDP header/payload | Skip (`Malformed`); rate-limited summary log; session survives |
| Datagram source port/family does not match the relay | Skip (`UnexpectedSource`); session survives |
| Socket-level receive failure / disposal / shutdown cancellation | Fatal: `_receiveFailure` -> session teardown (existing path) |
| Response sink (`InjectAsync`) throws non-cancellation | Rate-limited warn, skip that one response, loop continues |
| Receive, decode, sink, cancellation, or disposal exits the loop | Return the rented array exactly once |
| Rebuilt Ethernet frame exceeds the same pinned cap | Drop fail-closed (rate-limited log) without native injection |

### 5. Good/Base/Bad Cases

- Good: a 1514-byte frame cap rents a 1537-byte receive window; a valid smaller relay datagram is decoded as a view, awaited through the sink, then the buffer is reused.
- Base: cancellation or socket disposal ends the receive loop and returns the pool rental.
- Bad: rent 65,535 bytes per session, or accept `receivedBytes == buffer.Length` as complete; both defeat the memory bound and can silently process a truncated datagram.

### 6. Tests Required

- Assert coordinator and reinjector receive the same non-default frame cap from composition.
- Inject a tracking `ArrayPool<byte>` and assert one rent/one return after normal shutdown and immediate receive failure.
- Assert a receive that fills the sentinel window is rejected before decode/sink invocation.
- Preserve malformed datagram, oversized rebuilt frame, host/forwarded reinjection direction, client-MAC, cancellation, expiry, and coordinator single-flight disposal tests.

### 7. Wrong vs Correct

```csharp
// Wrong: independent unbounded receive storage can retain ~64 KiB per session
// and gives no reliable truncation signal.
var buffer = new byte[65_535];
var received = await transport.ReceiveAsync(buffer, cancellationToken);

// Correct: share the pinned frame cap, reserve one sentinel byte, and always return the rental.
var buffer = pool.Rent(maximumFrameSize + MaximumSocks5UdpHeaderSize + 1);
try
{
    var received = await transport.ReceiveAsync(buffer.AsMemory(0, receiveBufferSize), cancellationToken);
    await sink.InjectAsync(flow, source, received.Payload, clientMac, cancellationToken);
}
finally
{
    pool.Return(buffer);
}
```

