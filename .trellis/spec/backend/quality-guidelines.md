# Quality Guidelines

> Code quality standards for backend development.

---

## Overview

<!--
Document your project's quality standards here.

Questions to answer:
- What patterns are forbidden?
- What linting rules do you enforce?
- What are your testing requirements?
- What code review standards apply?
-->

## Current Conventions

- Build with nullable analysis, `TreatWarningsAsErrors`, Native AOT/trim analyzers, and the centrally pinned analyzer packages.
- Keep packet/ABI hot paths allocation-conscious, but preserve explicit bounds checks and ownership guards.
- Use localized `#pragma warning` suppression only when the behavior is intentional and documented (for example, rollback cleanup that must continue after one restore failure).
- Process selectors are exact: a selector without a directory separator matches an executable filename, while a selector containing `/` or `\\` matches a normalized full path.
- UDP routing uses the original local/remote endpoint tuple plus protocol, address family, and origin context. PID and DNS transaction IDs are metadata/payload, never association keys.
- Packet endpoint rewrite is a protocol-layer primitive shared by UDP and TCP redirect. `PacketChecksums.TryRewriteUdpEndpoints` / `TryRewriteTcpEndpoints` rewrite ONLY IP src/dst addresses, transport src/dst ports, and checksums (IPv4 header + transport). They must NEVER touch TCP seq/ack/flags/window/options/payload or UDP length/payload — this invariant is the bedrock of transparent local redirect (design §8 step 3) and is enforced byte-for-byte by the mutable-offset test assertion. Allocation-free `Span<byte>` in/out; bounds-check fully before any write; reject-without-mutating on every malformed input.
- TCP checksum has NO optional-zero-checksum provision (RFC 9293): `WriteTcpChecksum` stores the folded result verbatim, never inverting a computed 0x0000 to 0xFFFF. This deliberately diverges from `WriteUdpChecksum`, which MUST invert 0→0xFFFF per RFC 768. Do not unify these two helpers behind a flag — the divergence is load-bearing.
- IPv4 fragment rejection uses mask `0x3fff` on the TCP path (matching the canonical `IpTcpUdpPacket` TCP parser), not the stricter `0x1fff|0x2000` used by the UDP-only `IpUdpPacket` parser. When adding a new transport rewrite, mirror the canonical parser for that transport, not a sibling parser with different strictness.
- Proxy coordinators (UDP `UdpProxyCoordinator`, TCP `TcpProxyCoordinator`) mirror one structural shape: a flow-keyed session dict + Lock + shutdown CTS + capacity + `DisposeAsync` teardown. A flow is claimed exactly once in its association table; retransmitted/duplicate initial packets reuse the existing association (touch + re-inject), never allocate a second listener/transport. Every setup, rewrite, injection, or relay failure fails closed and releases exactly the resources acquired so far in acquisition order — a proxy-selected flow is never silently passed (design §8, R8).
- Concurrent initial packets for the same flow race through the coordinator's pre-claim fast path (resolve-by-original returns empty for all racers before any `TryClaim` runs). The association table's `TryClaim` is the single exactly-once arbiter: later racers receive the existing association, not a new one. The coordinator MUST detect this (compare the returned association's translated tuple to the just-allocated listener's) and release the redundant listener + fall back to the re-inject path. Failing to do so causes a duplicate-key crash on the session dict. Expose an internal concurrent-loser counter (e.g. `ConcurrentLoserCount`) so a test can deterministically assert the defense branch fired. The `ConcurrentSynBurstWithAsyncListenerStaysExactlyOnce` test gates `CreateAsync` on a `TaskCompletionSource` released only when all N callers have arrived (proving every caller passed the empty-table fast path before any claim), then asserts `ConcurrentLoserCount == N-1`. A `Task.Yield()`-only fake is scheduler-dependent and NON-load-bearing — it does not reliably reproduce the race. When testing proxy-coordinator concurrency, gate the allocation seam on a barrier/TCS so all racers provably pass the fast path before any claim lands, and assert via an instrumented counter rather than relying on scheduler interleaving.
- Association tables (`UdpAssociationTable`, `TcpRedirectTable`) use a single instance `_gate` lock for ALL mutating and reading methods, including internal lookup helpers. Do NOT lock the `Dictionary` object itself in a helper while other methods lock `_gate` — that diverges from the reference, breaks the documented "single gate lock" contract, and risks a future lock-ordering deadlock if a method that holds one lock calls another that acquires the other.
- `FlowTable.TryResolve` is the packet-observation lookup: every successful exact, reverse, origin-flipped, or adapter-agnostic resolution MUST call `FlowState.Touch` before returning, so active flows cannot expire at their pre-lookup deadline. `TryGet` is a non-observing lookup and intentionally does not refresh activity.
- Boundary constructors and parsers must be intentional about null inputs: `Endpoint.From(IPAddress, ushort)` rejects a null address with `ArgumentNullException`, while `IpPrefix.TryParse(string?, out IpPrefix)` returns `false` without throwing. Configuration validation must turn null JSON array elements into indexed diagnostics rather than allowing a `NullReferenceException`.
- Normalized remote-port intervals are sorted by start/end and merged when overlapping or adjacent. Downstream rule matching receives the canonical disjoint interval list, never user ordering or duplicate ranges.

## Testing Requirements

- Every flow or association ownership change needs a regression for same-key reuse, distinct-key isolation, and deterministic collision/failure behavior.
- Concurrency tests must use genuinely overlapping tasks, not only sequential repeated calls.
- Pure tests run on any host; NDISAPI and Windows attribution tests must remain behind ABI/platform seams.
- Packet-rewrite tests must validate checksums with INDEPENDENTLY reimplemented `Sum`/`Finish` helpers (not the production routines under test), assert only the expected mutable bytes changed via an explicit offset set, prove round-trip rewrite-back-to-original is byte-identical, and assert every reject path leaves the input span unchanged.
- Flow lookup regressions must verify that an observation refreshes `LastActivityUtc` before an idle-expiry boundary; also preserve a non-observing lookup test where applicable.
- Configuration tests must cover null DTO array entries, merged adjacent/overlapping port ranges, unknown JSON field paths, and paired credential limits at both 255-byte accepted and 256-byte rejected UTF-8 boundaries.

---

## Forbidden Patterns

<!-- Patterns that should never be used and why -->

(To be filled by the team)

---

## Required Patterns

<!-- Patterns that must always be used -->

(To be filled by the team)

---

## Testing Requirements

<!-- What level of testing is expected -->

(To be filled by the team)

---

## Code Review Checklist

<!-- What reviewers should check -->

(To be filled by the team)
