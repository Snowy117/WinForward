# Implement — reusable owner-table slots

Ordered; every step keeps the solution buildable and the cache facts green.

1. **Row types and `OwnerTable` storage** (`src/WinForward.Windows/IProcessOwnerTableReader.cs`) —
   done: `UdpOwnerRow.Address` is an `IPAddressValue`; `OwnerTable` is a reusable slot with
   `BeginUdpFill`/`BeginTcpFill`/`CompleteFill` and doubling growth; the two predicates are
   unchanged in rule and now compare values.
2. **Reader** (`src/WinForward.Windows/IPHelperOwnerTableReader.cs`) — done, with the decode split
   into `IPHelperOwnerTableParser` so the fill is exercisable (and allocation-gated) without
   `iphlpapi`: one slot per kind, refilled in place, no framework address, no per-scan array. The
   size probe, `ERROR_INSUFFICIENT_BUFFER` handling, `ValidateRowCount` and every throw are
   unchanged.
3. **Cache** (`src/WinForward.Windows/ProcessOwnerTableCache.cs`) — done: searches run under the
   per-kind gate, the reuse/negative/coalescing rules are the same statements as before, `takenUtc`
   is still sampled after the read, and only successful publishes count and reach the new
   `ReadSink`.
4. **Attributor + composition** — done: `WindowsProcessAttributor(…, ownerTableReadSink)`,
   `RuntimeCounters.AttributionOwnerTableScans`, wired in `DurableCaptureBundle`.
5. **Allocation gate** — done: `tests/WinForward.Windows.Tests/IPHelperOwnerTableParserTests.cs`
   (decode facts + the 0 B gate). Failability proved by injecting `IPAddress.Parse` into the TCP4
   fill loop: the gate failed; reverted, it passed.
6. **Local full verification** — done: solution build zero-warning, full test suite green,
   `dotnet format --verify-no-changes` exit 0 with empty output, `jb inspectcode` clean after
   removing the two findings it raised in the new code (a redundant `using`/`unsafe`) and the
   `Endpoint(AddressFamilyKind, IPAddress, ushort)` constructor that lost its last caller.
7. **On-host measurement (VM)** — done: 20 cps REL, allocation 0.546 MB/s and 2401 scans; memory
   shape before/after/fuse-32; `gc-verbose` trace. Evidence in `research/after-20cps.md`.
8. **Campaign row** — *narrowed*: the REL-arm A/B on the same plan and upstream replaced the
   `wf-fdd-opt` full-plan row, because the local stand-in SOCKS5 upstream speaks CONNECT only and a
   full plan would fail its UDP/UoT arms for a reason unrelated to this change. Rationale recorded
   in the PRD (AC5); the real sing-box row stays available for the campaign axis.
9. **Gates and close-out** — spec updated (`.trellis/spec/backend/hot-path.md` scope note and the
   reusable-slot rule in `native-lease-and-pool-lifetime.md`), commit carries the task id.
10. **Review follow-up** — the first commit skipped the workflow's quality-check step, so an external
   review of `9c6aff7` was run and folded in (`research/review-9c6aff7.md`):
   - the cache's owner spec (`traffic-policy-lifecycle.md`) still described the old snapshot
     implementation and its "it allocates" claim — rewritten around the reusable slot, the new seam
     ownership and the current predicate snippet;
   - `IPHelperOwnerTableParserTests` registered as the Windows entry of the per-gate lump proof
     (the previous Windows entry carries no exact gate);
   - added the fill-protocol facts (a begun fill is unsearchable, a rejected row count leaves the
     contents answering, the `Unavailable` constant refuses a fill) and the UDP4/UDP6/TCP6 row-image
     decode facts, including IPv6 scope ids;
   - inlined the one-line IPv6 forwarder, renamed the gate to the `*AllocateNoManagedBytes` family,
     renamed `ProcessOwnerTableCache.ReadCount` to `ScanCount`, replaced the last
     `Marshal.AllocHGlobal`/`FreeHGlobal` pair in `src/` with `NativeMemory`, and tightened the
     cache's failure-timing wording;
   - the design's native-buffer retention sketch is recorded as decided against, and the review's
     S11–S13 trade-offs are recorded in the design.
