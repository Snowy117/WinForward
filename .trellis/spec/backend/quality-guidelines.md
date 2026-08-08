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

## Testing Requirements

- Every flow or association ownership change needs a regression for same-key reuse, distinct-key isolation, and deterministic collision/failure behavior.
- Concurrency tests must use genuinely overlapping tasks, not only sequential repeated calls.
- Pure tests run on any host; NDISAPI and Windows attribution tests must remain behind ABI/platform seams.
- Packet-rewrite tests must validate checksums with INDEPENDENTLY reimplemented `Sum`/`Finish` helpers (not the production routines under test), assert only the expected mutable bytes changed via an explicit offset set, prove round-trip rewrite-back-to-original is byte-identical, and assert every reject path leaves the input span unchanged.

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
