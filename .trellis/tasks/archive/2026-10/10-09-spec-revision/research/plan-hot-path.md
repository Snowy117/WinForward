# Plan: hot-path.md

Read in full (1 655 lines, 1 404 effective). Every claim below was checked against the working tree
at the revision this report was written on; `file:line` is the line I read, not a line the doc cites.

Mechanical findings from `research/verify-specs.py --doc backend/hot-path.md`:

```
1655  1404  backend/hot-path.md  <-- OVER
? backend/hot-path.md: `IsReverseCandidate`
? backend/hot-path.md: `ResidualLumpProbe`
? backend/hot-path.md: `_servers`
0 broken relative link(s), 0 unexplained CJK line(s)
```

The three unresolved identifiers are rows 1, 14 and 15 below; the other twelve rows are stale
prose/defaults/signatures that the identifier check cannot see.

---

## 1. Stale claims (verified against code)

| # | Section (line) | Claim as written | What the code says now | Evidence (file:line) | Action |
|---|---|---|---|---|---|
| 1 | Contracts §3 (63) | "a resolved proxy decision whose `TargetName` hits `_servers`" | the resolver field is `_targets` (`IReadOnlyDictionary<string, ProxyTarget>`), read at the proxy branch | `src/WinForward.Runtime/FlowDispatcher.cs:110` (declared), `:207` (read) | rename to `_targets`; this is the only mechanically-flagged row that is a real code drift |
| 2 | Contracts §2 (22) | "`IPPrefix.PrefixMask(prefixLength, family)`: IPv4 = `0xFFFFFFFF << (32 - len)`" — cited as a callable member | it is `private static UInt128 PrefixMask(int, AddressFamilyKind)`, reachable only through `Contains`/`Normalize`; the arithmetic stated in the doc is exactly right | `src/WinForward.Core/IPPrefix.cs:55` (definition), `:48`/`:69` (callers) | keep the arithmetic, cite it as the private mask helper behind `IPPrefix.Contains`; `IPv4PrefixesMatchOnlyTheirPrefix` is real (`tests/WinForward.Core.Tests/EndpointAndPolicyTests.cs:261`) |
| 3 | Native pool §2 (554) | "`NativeBufferPool(int byteSize, int capacity = 256, Action<bool>? accountingSink = null)`" | real signature is `NativeBufferPool(int bufferSize, int capacity = DefaultCapacity)` with `DefaultCapacity = 256`; the sink is a settable property `Action<bool>? AccountingSink { get; set; }`, never a ctor parameter | `src/WinForward.Core/NativeBufferPool.cs:34` (ctor), `:22` (`DefaultCapacity`), `:55` (`AccountingSink`) | fix the signature; a reader copying it does not compile |
| 4 | Native pool §2 (563) | "`DefaultRingCapacity = 1024`" | `public const int DefaultRingCapacity = 2_048` | `src/WinForward.Runtime/SetupExecutor.cs:146` | update to 2 048 |
| 5 | Native pool §2 (564) | "`SetupExecutor` … `StartPendingSetup`/`LaunchSetup` replace per-flow `Task.Run`" — the two names are presented as `SetupExecutor` members | both are `TcpProxyCoordinator` members; `SetupExecutor` exposes `RentItem`/`TryEnqueue` only | `src/WinForward.Runtime/TcpRedirect/TcpProxyCoordinator.cs:171` (`StartPendingSetup`), `:213` (`LaunchSetup`), `src/WinForward.Runtime/SetupExecutor.cs:119`/`:122` | move the two names to the redirect/coordinator sentence or drop them |
| 6 | Native pool §2 (567) | "`FlowState.Reset(FlowKey, FlowDecision, long)` re-initializes in place" | `internal void Reset(FlowKey key, FlowDecision decision, long generation, long activityBucket)` — four parameters; the bucket is what `TouchBucket` reads | `src/WinForward.Core/Domain.cs:422` | add the bucket parameter |
| 7 | Native pool §3 (587) | "every successful `TryResolve` calls `Touch` before returning" | the member is `FlowState.TouchBucket(long)`, called from `TryResolveLocked` (not the public `TryResolve`); `Touch` no longer exists | `src/WinForward.Core/FlowTable.cs:480` (`state.TouchBucket(ActivityClock.Current)`), `:227` (`TryResolve` wrapper) | rename to `TouchBucket(ActivityClock.Current)` and name `TryResolveLocked` |
| 8 | Closure-hoisting §3 (413) | "`ConfigureAwait(false)` throughout (`NdisPacketActionExecutor.cs:360,450` and the send tails)" | the file is `src/WinForward.Runtime/Capture/NdisPacketActionExecutor.cs` (a `Capture/` subdirectory the doc omits), and its `await … ConfigureAwait(false)` sites are 371, 379, 407, 480 — 360 and 450 are unrelated code | `src/WinForward.Runtime/Capture/NdisPacketActionExecutor.cs:371` | fix the path; drop the line numbers (they rot) and cite the three await sites by symbol |
| 9 | Closure-hoisting §5 (472) | "the suite is 980 Core.Tests + 18 Analyzers = 998 as of 2026-09-30" | contradicted by this same document 890 lines later ("the suite total is `1,141 + 18` on the 2026-10-01 F6 tree"), and the gates no longer live in `Core.Tests` at all — e.g. the flow-table gate is in `WinForward.Performance.Tests` | `.trellis/spec/backend/hot-path.md:1365` (the later total), `tests/WinForward.Performance.Tests/HotPathAllocationGateTests.cs:421` (a gate in a non-Core project) | delete the number; keep "the baseline for comparison is the run's own recorded total" |
| 10 | Relay pump §2 (290) | "`PacketChecksums.TryRewriteIpv4Tcp/TryRewriteIpv6Tcp` use RFC 1624 … (`InternalsVisibleTo("WinForward.Protocols.Tests")`)" | both entry points are `private static`; the csproj grants internals to **two** test assemblies (`WinForward.Protocols.Tests` *and* `WinForward.Integration.Tests`) | `src/WinForward.Protocols/PacketChecksums.cs:120` and `:167` (private), `src/WinForward.Protocols/WinForward.Protocols.csproj:10-11` | mark them private helpers; add the second grant |
| 11 | Closure-hoisting §2 (352) | "`UdpProxyCoordinator` is a `partial class` split into `UdpProxyCoordinator.cs` … and `UdpProxyCoordinator.Send.cs`" | there is a third part, `UdpProxyCoordinator.Sweep.cs` (all three are ≤400 effective lines: 362 / 150 / 51) | `src/WinForward.Runtime/UdpProxy/UdpProxyCoordinator.Sweep.cs:10` | state "three parts" and name the sweep part |
| 12 | F4 map (1544) | "Task record: `.trellis/tasks/09-30-flow-key-parse-once/`" | no such directory: `.trellis/tasks/` holds only `08-30-proxy-perf-stability`, `09-06-local-mux-transport`, `10-06-e2e-competitor-benchmark`, `10-07-tcp-close-drain`, `10-09-spec-revision` + `archive/`, and `archive/2026-09/` has no `flow-key` entry either | surviving artifact: `benchmarks/results/2026-09-30-flow-key-parse-once/README.md:1` (the artifact itself repeats the dead path at `:3`) | drop the `.trellis` pointer or repoint at the benchmark artifact |
| 13 | Residual-lump §4 (1379) | the hard-coded proof map `…SweepAllocationGateTests:12 … FlowAttributionPipelineTests:19` | the classes now hold 11 and 23 `[Fact]`s respectively (no `[Theory]`/`[InlineData]` in either), so the loop's `Total: *N` guard prints `VACUOUS MATCH` and stops on the first run | `tests/WinForward.Performance.Tests/SweepAllocationGateTests.cs:44` (class, 11 facts in file), `tests/WinForward.Runtime.Flow.Tests/FlowAttributionPipelineTests.cs:19` (class, 23 facts in file) | re-derive the totals before the family ships, or make the loop read the total from the class instead of a literal |
| 14 | Warm-path §2 (1485) | "`IsReverseCandidate` is gone" | the prose is correct — the pre-X1 method is gone — but the backticked name resolves nowhere in the tree, so AC4 fails. The live member is `TcpRedirectTable.IsReverseCandidatePort(ushort)`; `TcpProxyCoordinator` keeps the candidate filter under the same name | `src/WinForward.Runtime/TcpRedirect/TcpRedirectTable.cs:368`, `src/WinForward.Runtime/TcpRedirect/TcpProxyCoordinator.cs:359` | reword to `` `IsReverseCandidatePort` `` (or "the pre-X1 `IsReverseCandidate` probe") |
| 15 | Residual-lump §2 (1252) | "A temporary in-assembly reproducer (`ResidualLumpProbe`, deleted after the measurement …)" | correct as history, but the backticked type exists nowhere in the tree, so AC4 fails; the surviving seam the probe drove is `NdisCapturePump.RunIterationForTests`, still `internal` | `src/WinForward.NdisApi/NdisCapture.cs:213` | drop the backticks ("a temporary reproducer, deleted after the measurement") — do not allowlist a name that only exists in prose |
| 16 | UDP per-flow §3 (865-872) | "the 2026-10-01 artifact … re-ran the loss/burst/churn anchors … (churn inside its **≤8,200 B/session** band)" and, nine lines later, "the ≤8,200 B/session **framework ladder**, and the ≤14,500/≤14,300/≤17,500 churn anchors" | the artifact applies the ≤8,200 band to the `udpChurn --churn-waves 0 --duration 120` row (7,436.8 B/session), i.e. to churn, not to the framework ladder | `benchmarks/results/2026-10-01-udp-session-footprint/README.md:141` | give the ≤8,200 band exactly one owner and link the ladder/churn anchors to their own artifact rows (see §5.4) |
| 17 | Contracts §11 (132) | "`PacketTransport.Tcp` is `0`" + "the layout carries an explicit validity stamp that only `PacketLayout.From(in PacketView)` writes" | verified: `Tcp` is the first enum member (value 0) and `ParsedStamp = 0x5a` is written only by the private ctor `From` calls (`IsTcp`/`IsValid` both gate on it) | `src/WinForward.Protocols/IPTcpUdpPacket.cs:9`, `src/WinForward.Protocols/PacketLayout.cs:31`/`:52`/`:65`/`:72` | none — recorded here because it is the highest-consequence claim in the file and it still holds exactly |

Checked and **not** stale (recorded so the revision agent does not "fix" them): the 12 contract
numbers themselves, the struct sizes (`FlowKey` 64 / `FlowContext` 80 / `CapturedFlowPacket` 152 /
`FlowStateView` 96 / `PacketView` 96 / `Endpoint` 48 / `PacketLayout` 16 — asserted verbatim by
`tests/WinForward.Core.Tests/FlowKeyShapeTests.cs:22-27` and `tests/WinForward.Protocols.Tests/PacketLayoutTests.cs:28`),
`PacketLease.TakeNative` (`src/WinForward.Core/PacketRuntime.cs:101`), `PrepareForReinjection`
(`src/WinForward.NdisApi/NdisPacketBuffer.cs:121`, retargets the adapter handle and zeroes
`UnionPadding`), `Socks5UdpDatagram.DestinationAddress` as `IPAddressValue?`
(`src/WinForward.Protocols/Socks5Udp.cs:11`), `Socket.Blocking = false`
(`src/WinForward.Runtime/Socks5/Socks5UdpTransport.cs:238`), `FlowAction {Proxy, Pass, Block}`
(`src/WinForward.Core/Domain.cs:24-29`), `FlowHash.CombinePacked`/`Combine`/`CombineCanonical`
(`src/WinForward.Core/Domain.cs:238`/`:254`/`:273`), `FlowKey.Equals` comparing every field including
both scope ids and the interned adapter pair (`src/WinForward.Core/Domain.cs:190-202`),
`TcpRedirectAssociation`'s two `long` trackers with the CAS-max loop and `IsSequenceAhead`
(`src/WinForward.Runtime/TcpRedirect/TcpRedirectTable.cs:146-181`), the candidate-port
`int[65_536]` with Inc in `TryClaim` and Dec in `RemoveUnderGate`
(`TcpRedirectTable.cs:210`/`:321`/`:460`), `ActivityBucket.TicksPerBucket = 500 ms` and the strict-`<`
cutoff (`src/WinForward.Core/ActivityBucket.cs:24`/`:48`), `NeverPropagated = long.MinValue`
(`src/WinForward.Runtime/UdpProxy/UdpProxySession.cs:51`), `TryBeginExpiry` still taking
`_activityGate` while `SendSpanAsync`/`TouchActivity` do not (`UdpProxySession.cs:268`/`:533`),
`ReceiveWindowSize = frame + 22 + 1` (`UdpProxyCoordinator.cs:12-13`/`:217`), `OneShotIdleTimeout = 5 s`
(`UdpProxyCoordinator.cs:267`), the GC-posture csproj properties
(`src/WinForward.Cli/WinForward.Cli.csproj:38-40`/`:56`), `<TieredCompilation>false</TieredCompilation>`
(`tests/Directory.Build.props:25`), the owner-table cache's coalescing/reuse rules (TCP reuses, UDP
never; 300 ms) (`src/WinForward.Windows/ProcessOwnerTableCache.cs:26`/`:94`/`:96`), the real-dial
harness (`--serve-socks5-udp` child mode, `--socks5-external`, `WINFORWARD_BENCH_EXTERNAL_SERVER=1`,
`ExternalLoopbackSocks5UdpServer.StartAsync(flows, associateDelay, ct)`, the one-line handshake)
(`benchmarks/WinForward.Benchmarks/Stability/ExternalLoopbackSocks5UdpServer.cs:18`/`:39`/`:41`,
`Socks5UdpServerMode.cs:18-20`), and every test-fact name cited in contracts 4/8/10/11/12 that I
spot-checked (all resolve; none renamed).

One name near-miss worth a row even though it is not a code symbol: line 1607 says
"`WinForwardProcessAttributor.FindAsync`'s 2 ms retry". The type is `WindowsProcessAttributor`; the
2 ms retry itself is real. Evidence: `src/WinForward.Windows/ProcessAttribution.cs:12` (class),
`:42-51` (retry). Same fix class as rows 1/3/7: rename in the doc.

---

## 2. Verbosity cuts

| # | Lines | What | Why it can go |
|---|---|---|---|
| 1 | 56-68 | F2/X1/C2b change history inside contract 3: measured `352 B → 160 B`, `596 ns → 258 ns`, "a removed always-false gate taught this lesson", benchmark-class provenance | the rule is one clause ("a resolved proxy decision whose `TargetName` hits the target map stays on the warm entry"); the deltas belong to the artifact, which is linked two lines above |
| 2 | 69-75 | Windows IOCP→thread-pool mechanism story behind the non-blocking socket ("the mechanism behind a 3.5× loopback pps gap") | the rule ("sync `SendTo` first, overlapped fallback on `SocketException`, release exactly once") is what a reader must obey; the mechanism is a one-clause reason at most |
| 3 | 94-102 | the "Since 2026-09-29 …" redirect-lane retelling, ending with "the redirect contract in windows-ndisapi.md … is the authority" | it states a whole contract that another doc owns; keep one sentence: an unmaterialized pump packet is rewritten on its capture slot, retained only until the lane flush, and never treated as read-only afterwards |
| 4 | 112-116, 1555 | contract 8's five-fact list repeated verbatim as an F4 proof-map row | duplication inside one document; the fact list belongs next to the rule (once) |
| 5 | 158-175 | the SOCKS5 block's §1 Scope / §2 Signatures, which restate the four signatures already introduced by §3 | template ritual: 18 lines of headings and one-line restatements for four bullets |
| 6 | 178-190 | the forwarded-shape cost story ("4.8× slower", "2,887.9 ns vs 616.7 ns @1400 B", "before C2") | conclusion is "cache the raw address at the cold edge"; the numbers are the artifact's job |
| 7 | 185-206 | the UDP bookkeeping budget bullet: four generations of numbers in one bullet (`≤1 KB` → `2.1 KB → ~0.4 KB` → `1,433 B` → decomposition 489/829/≤172/2,503/1,459) with "predate … and are superseded" marking | the live anchor plus "re-derive, never relax" is the contract; the superseded ladder is the archaeology of one number |
| 8 | 207-232 | framework-socket bullet: the isolated path split (7,952 = 3,792 + 959 + 576 + 160 + 2,465), the churn wave matrix, the 09-28 pooled series, the "reusing control connections was the only structural lever … then removed again" retelling inside a *budget* bullet | ~26 lines; the surviving rules are "measure out of process", "framework cost is anchored, not untracked", "no anchor is re-based onto the pooled series" |
| 9 | 243-268 | §4 Validation matrix (6 rows), §5 Good/Base/Bad, §6 Tests Required — three restatements of the same four §3 bullets | matrix rows that repeat the contract text verbatim add no decision surface; keep only the rows that state an *outcome* (fail-closed on null, drop-oldest, tombstone) |
| 10 | 285-291, 306-317, 319-322, 324-335 | relay block: §1/§2 template, a 9-row matrix, tests-required, and a Wrong/Correct whose "Wrong" comment restates the fold rule | the four real contracts (CTS reuse, re-arm throttle, no per-chunk CTS, incremental precondition) fit in ~12 bullets; the matrix/test/code sections repeat them |
| 11 | 296, 303 | X8a motivation ("~100–200 ns per timer op, ≈5–10 % of a core at 10 Gbps single-flow") and the two-harness ns readings plus "quote only the recorded clean harness" | the throttle rule is "never disarms, never recreates, ≤1 s drift"; the cost estimate is not a decision input any more |
| 12 | 383-420 | the readiness incident: the "earlier explanation was wrong" correction (12 lines of meta-narration), the reconciliation parenthesis, the 40-loop thread-id evidence, the four failing `Actual:` values, and the `SynchronizationContext` bullet | the conclusion — "fix the window, not the threshold; probe direct admission and 0-byte stability; assert the thread and the call count" — is stated in full in §"Allocation-gate stability" and is the only part a gate author needs |
| 13 | 461-472 | Tests Required ending in a fourth dated suite total ("678 tests green on this task; … 998 as of 2026-09-30") | row 9 of §1; a fifth total (1 141) appears later in the same file, and a sixth today |
| 14 | 596-640 | the FlowTable warm-cache bullet and the live-slot/sweep bullet, which state in full what §"Warm-path lock-free resolve" states again (slot validation, false-miss-only, `ReferenceEquals`-guarded clear, `ActivityBucket < cutoff`, chunk bound) | one rule, two homes; the pool section should keep only the pool-facing half (state recycling, `ReturnState` order) and link to the warm-path doc |
| 15 | 671-711 | the 17-row Validation matrix, Good/Base/Bad, Tests Required for the pool family | rows duplicate the §3 bullets almost one-to-one (rent/return/dispose, double release, `Memory` after release, overflow, overflow growth, post-dispose enqueue) |
| 16 | 712-740 | three full Wrong/Correct code fences (EndPoint serialization, zero-GC assertion, Outstanding equality) | each is a one-line rule already stated in §3; keep one fence, or convert the three to three bullets |
| 17 | 742-806 | the whole Real-Dial harness block: 7 template sections for 4 facts (child mode + opt-in, handshake line, lifetime, default), with §4-§7 restating §3 | a 65-line section that a 15-line contract carries; the artifact link is the evidence |
| 18 | 834-843 | "the established-datagram path gains three allocation-free operations", a numbered retelling of the §2 signatures and the §3 counters | the three operations are already stated where they are measured |
| 19 | 873-881 | the pooling-series archaeology (13,066.5 → 7,556.7/7,564.7/7,577.5, "1.06–1.95 descriptors", "Sharing was removed … those numbers are history") | the live rule is one sentence: no recorded anchor may be re-based onto the removed pooled series |
| 20 | 885-918, 920-943 | matrix + Good/Base/Bad + three Wrong/Correct fences for the UDP counters | restates §3; one fence (record after the kernel accepted, nothing on skip paths) is enough |
| 21 | 1032-1048, 1070-1073 | the tiering evidence dump (7,336–7,360 B in 12/20 and 17/20 runs; 1,880 B in 2/20; 8,008 B at iteration 65; 136 B/824 B first-use; four 20-run arms) and the "+3 s per suite run" paragraph | keep the conclusion (tiering lumps are one-time, `TieredCompilation=false` removes the family, the price is ~3 s) and the run-count rule; the raw counts are in the task record |
| 22 | 1146-1208 | the recorded outcome of the proof runs (100/100, 29 runs/4 failures, Wilson intervals, "the ≥40-consecutive-green criterion is not met", the 47-run hang hunt, the bash loop) | a task result log: the surviving rules are §4's procedure ("per gate, own process, N runs, record totals/hash, failure stops the loop") and the code loop, which is repeated at 1376-1394 |
| 23 | 1257-1308 | the 13-row census table, the per-arm multiplicity paragraph, the thread-pool inline note, the "suite-process-conditional" argument and the `dotnet-trace` sampling derivation | the durable conclusions are three lines: zero lumps in isolation, the residual is suite-process-conditional, a budget-sampled profiler cannot see a ≤8 KB one-shot |
| 24 | 1309-1349 | "the per-window control is priced and rejected", min-of-K power arithmetic, the sensitivity floor table, the signature predicate, "do not re-test" list | the live rules are the signature predicate, "no tolerant gate shape", "one reproducer per process", and one line of "these families are excluded, do not re-derive" |
| 25 | 1411-1442 | §6 "Addendum (2026-10-06 flaky sweep)" as an append rather than an edit | it contradicts §2's size predicate ("the size predicate in §2 is incomplete") and adds two new victim sizes; it must be merged into §2, not kept as a dated tail |
| 26 | 1542-1586 | the F4 spec-row → proof map (13 rows), "Accepted semantic deltas", "Residuals" | task-archive material: it maps rows of *this* and three other documents onto test names; the durable rules live in contracts 4/8/10/11 and the facts live in the tests |
| 27 | 1588-1655 | the owner-table epoch coalescer | not packet-path at all (a cold cache behind attribution); see §3's recommendation |
| 28 | 156-1655 | nine `### 1. Scope / Trigger` … `### 7. Wrong vs Correct` template blocks (at 156, 280, 337, 544, 742, 808, 1015, 1234, 1444, 1588) | per design §2.3 only the sections that carry information are written; roughly 60 lines are pure heading overhead and ~250 more are §4/§5/§6 restating §3 |

---

## 3. Split proposal

Family: one hub + eight children, all in `.trellis/spec/backend/`. Total ≈ 1 190 lines (from 1 655);
no file above ~200. Two pieces leave the family entirely (§3.9, §3.10).

### hot-path.md (hub, 130 lines)

- **Purpose**: the entry point for anyone touching per-packet code — scope, the invariants that hold
  across the whole packet path, and the topic map that routes to the children.
- **Sections moved**: nothing moves *in* except the cross-cutting contracts; it keeps
  Scope/Trigger (6-10), contract 1 (14-21, raw addresses), contract 2 (22-25, IPv4 masks, with the
  `IPPrefix` fix from §1 row 2), contract 4 (76-81, struct shapes and sizes), contract 7 (103-105,
  span codecs), contract 9 (117-118, measurement discipline), plus the "any new per-packet stage
  follows the warm/slow split" sentence from contract 3.
- **Notes**: carries a **Where things moved** table (design §1) mapping every old section title and
  old section number — `Contracts` 1-12, `SOCKS5 Path Contracts` §1-§7, `Relay pump…` §1-§6,
  `Closure-hoisting…` §1-§6, `Native pool family…` §1-§7, `Real-Dial…` §1-§7, `UDP per-flow…` §1-§7,
  `Measurement self-checks`, `Allocation-gate stability` §1-§5, `The residual exact-gate lump` §1-§6,
  `Warm-path lock-free…`, `F4 … proof map`, `The owner-table epoch coalescer` — onto the children, so
  the numbered citations frozen in `benchmarks/results/**` stay resolvable. Also carries the
  "Where the rules live" one-line index (one row per child). Every child links back here.

### warm-path-dispatch.md (165 lines)

- **Purpose**: the synchronous warm entry and everything it resolves — dispatcher shape, reverse
  diversion prefilter, self-traffic split, warm caches, activity bucket, sequence trackers.
- **Sections moved**: contract 3's dispatcher/reverse/self-traffic/flow-resolve bullets (28-68);
  contract 12 (147-154); the FlowTable warm-cache bullet (596-620); the whole
  `## Warm-path lock-free resolve, the activity bucket and the self-traffic split` block (1444-1541);
  the reverse/self-traffic rows of the SOCKS5 matrix (245-248).
- **Notes**: this is the natural home for the gate-count table ("Counts are the proof"), which is the
  section's most useful artifact. Contract 8 (flow keys) goes to `packet-shape-and-flow-identity.md`,
  referenced from here with a one-line link.

### packet-shape-and-flow-identity.md (120 lines)

- **Purpose**: the per-packet value types and the parse's proofs — struct sizes, the packed flow key,
  the one parse, the 16-byte layout, and the defaulted-layout refusal.
- **Sections moved**: contract 8 (106-116); contract 10 (119-131); contract 11 (132-146); the F4
  proof-map rows that belong to contracts 10/11 and the layout/identity facts (1552-1568, compressed
  to a single "F4 deltas" table); the "Accepted semantic deltas" minus its task prose (1569-1575);
  the residuals that are live constraints (`TransportTuple`'s field-set equality has no direct fact;
  the non-flow arm carries `default` by design) (1577-1586).
- **Notes**: this is the only child that holds `<400`-line material from a *protocol* border; if the
  lead prefers, contracts 10/11 can instead live in `quality-guidelines.md` (which already owns the
  rewrite primitive) with this child keeping identity only. The F4 task narrative itself (row 12 of
  §1) is deleted, not moved.

### native-lease-and-pool-lifetime.md (150 lines)

- **Purpose**: the lifetime rules of everything rented — native leases, in-place reinjection, the
  pool family, pooled flow/setup state, and the GC-off posture.
- **Sections moved**: contract 5 (82-88); contract 6 (89-102); all of
  `## Native pool family, pooled flow/setup state, and GC-off posture` (544-741) **except** the
  FlowTable warm-cache bullet (→ `warm-path-dispatch.md`) and the sweep-granularity bullet (→
  `warm-path-dispatch.md`); the EndPoint-trap bullet (662-667) and its fence (714-722); the gc-soak
  contract (651-661).
- **Notes**: the lease/`Memory`/stale-copy rules and their matrix rows are the same rule stated
  twice — one bullet each plus one matrix row. `EndPoint`-serialization is a *transport* trap; it
  stays here because it is how the pool's cached `SocketAddress` is protected, with a link to
  `udp-datagram-path.md`.

### allocation-gates.md (140 lines)

- **Purpose**: what an exact zero-allocation gate is and how to write one that can fail — closure
  hoisting, the per-thread counter's limits, the window contract, the injected-allocation check.
- **Sections moved**: the closure-hoisting rule (356-364) and the "allocation gates must discriminate
  the exact seam" bullet (365-373); the readiness/thread contract (374-411) **compressed to ~12
  lines**; the `SynchronizationContext` note (412-416) as one sentence; the approved cold-path
  materialization (421-424); the tiering host contract (1028-1077) **compressed to ~20 lines**; the
  window-shape bullets (1105-1124); the matrix rows that are about gate shape (453-459, 1136-1144);
  the four-property bullet (1110-1115).
- **Notes**: the "Sibling gate, same shape" paragraph (417-420) and the `DispatcherWarmFastPath`
  flake history become one provenance line. Everything about *why the suite flakes* moves to the next
  child.

### allocation-gate-host-lumps.md (165 lines)

- **Purpose**: the one failure mode of an otherwise correct gate — a host lump inside an exact window
  — and the procedures that bound it.
- **Sections moved**: the residual's trigger/scope (1236-1247); "the residual is not a property of
  the window bodies" with the census table compressed from 13 rows to 3 + one paragraph (1251-1283);
  the thread-pool-inline gotcha (1284-1287); multiplicity and the suite-process-conditional argument
  (1288-1299); the profiler limitation (1300-1308); the priced-and-rejected control (1309-1314); the
  no-tolerant-shape rule with the injected-allocation check (1315-1324); disposition, sensitivity and
  the signature predicate (1325-1344); the "do not re-test" list (1345-1349); the 2026-10-06 addendum
  **merged into the body** (1411-1442); the per-gate proof loop (1362-1394) and its `totals` map
  (§1 row 13).
- **Notes**: this is the one child whose subject is arguably `test-stability.md`'s
  (`test-stability.md` §4 already owns the repeat-run soak proof and already cites "`hot-path.md` §6"
  for this residual). Recommendation: **one owner**. Either move the whole child's content to
  `test-stability.md` §"Host-residual lumps" and leave a two-line pointer here, or keep the child and
  have `test-stability.md` link to it. Duplicating it in both is the failure mode to avoid.

### udp-datagram-path.md (150 lines)

- **Purpose**: the per-datagram path end to end — encode/send, the disposal guard, the per-flow
  association and its counters, and the recorded UDP anchors.
- **Sections moved**: contract 3's socket-send paragraph (69-75); the UDP half of
  `## SOCKS5 Path Contracts` — `BoundedSetupQueue` (172-173), the session
  bookkeeping budget (183-206), the framework-socket anchors (207-232), the throughput anchor (233-236),
  the UDP matrix rows (249-250); the disposal-guard bullet (425-442); the whole
  `## UDP per-flow association: counters, evidence cost, and the recorded anchors` block (808-944),
  trimmed of the pooling archaeology (§2 row 19).
- **Notes**: the TCP half of `## SOCKS5 Path Contracts` (165-171, 178-182, 254-259, 272-278) is
  `tcp-local-redirect.md`'s business — see §3.10. The anchors (≤5,400 / ≤8,200 / ≤14,500 / ≤14,300 /
  ≤17,500) each get exactly one owner line here, with the artifact path beside them.

### relay-pump-and-checksums.md (75 lines)

- **Purpose**: the TCP relay pump loop and the checksum primitive it feeds.
- **Sections moved**: all of `## Relay pump and checksum contracts` (280-336), trimmed of rows 10/11
  of §2.
- **Notes**: small but genuinely self-contained — a reader changing `PacketChecksums` or
  `TcpProxyRelay` has no other door. Alternative if the lead prefers fewer files: the pump half folds
  into `tcp-local-redirect.md` (470 lines today, itself a split candidate) and the checksum half into
  `quality-guidelines.md`, which already names the rewrite primitive. I recommend keeping the child.

### benchmark-measurement-discipline.md (95 lines)

- **Purpose**: how to build a benchmark row/stability scenario that measures the path it names, and
  which numbers may be quoted as gates.
- **Sections moved**: `## Measurement self-checks` (945-1013); `## Real-Dial Measurement Harness`
  (742-806, compressed to its four facts); contract 9 (117-118) as its opening rule.
- **Notes**: this is explicitly another document's subject — `measurement-harness.md` owns the
  harness contract (and is itself 607 lines, assigned to another wave-1 agent). Recommendation:
  land this child now, and when `measurement-harness.md` is split, fold the benchmark-instrument
  rules into its family and reduce this file to a pointer. Do **not** copy the content into both.

### §3.9 Moved out of the family: the owner-table epoch coalescer (68 lines)

`## The owner-table epoch coalescer (F8, 2026-10-01)` (1588-1655) is a cold process-attribution
cache: it allocates, it awaits, and it never runs on the packet path. It belongs to
`traffic-policy-lifecycle.md` (53 lines, has room) — which already owns process attribution and the
idle sweep — as a new "Owner-table snapshot cache" section. Nothing on the packet path changes if
this cache is slow; only attribution accuracy and the fail-open/fail-closed split do. If the lead
wants it in this family instead, it becomes a tenth child (`process-attribution-cache.md`, ~55
lines); my recommendation is the move.

### §3.10 Returned to existing documents

- The TCP-redirect half of `## SOCKS5 Path Contracts` (165-171 `ForwardLocalAddress`,
  169-171 `SwapEthernetMacs`, 178-182 forwarded-shape cost, 254-259, 272-278) → `tcp-local-redirect.md`.
  It is the redirect transform's contract, and that doc already owns the rewrite shape.
- The F4 task record and its spec-row → proof map (1542-1568) → deleted from the spec family; the
  durable rules move to `packet-shape-and-flow-identity.md` as a short "F4 deltas" table and the
  archive stays in `benchmarks/results/2026-09-30-flow-key-parse-once/`.

---

## 4. Cross-references that break

Live code, by policy (design §3.2: numbered citations are converted to the section title in the same
change):

- `hot-path.md #3` — `src/WinForward.Runtime/TcpRedirect/TcpProxyCoordinator.Injections.cs:244`,
  `:297`, `:362`; `src/WinForward.Runtime/Socks5/Socks5UdpTransport.cs:267`;
  `src/WinForward.Runtime/Socks5/Socks5UotTransport.cs:150`;
  `src/WinForward.Runtime/UdpProxy/LocalUdpTransport.cs:220`. The rule lands in
  `warm-path-dispatch.md`; the comments must say so (e.g. "hot-path.md → warm-path-dispatch.md,
  'no async state machine on the steady-state path'").
- `hot-path.md contract 4` — `tests/WinForward.Core.Tests/FlowKeyShapeTests.cs:11`. The struct sizes
  stay in the hub or move to `packet-shape-and-flow-identity.md`; a number is not a stable identifier
  across a split.

Live tests, by section title (the title must survive verbatim in whichever child receives it, or the
comment must be repointed):

- `hot-path.md` §"Allocation-gate stability" — `tests/WinForward.Performance.Tests/SweepAllocationGateTests.cs:24`,
  `:72`; `tests/WinForward.NdisApi.Tests/CapturePumpReadCallTests.cs:112`;
  `tests/WinForward.NdisApi.Tests/NdisCapturePumpTests.cs:309`;
  `tests/WinForward.NdisApi.Tests/NdisCapturePumpIdleWaitTests.cs:151`;
  `tests/WinForward.NdisApi.Tests/NdisApiReadShapeTests.cs:176`;
  `tests/WinForward.NdisApi.Tests/CompositePacketArrivalSignalTests.cs:12`, `:89`;
  `tests/WinForward.Performance.Tests/UdpAdaptiveSweepAllocationGateTests.cs:23`. If the shape
  material moves to `allocation-gates.md`, that section keeps the title "Allocation-gate stability"
  or all nine comments change.
- "An allocation gate must open only after its path is ready" —
  `tests/WinForward.Performance.Tests/HotPathAllocationGateTests.cs:376`.
- "hot-path.md's window contract" — `tests/WinForward.Performance.Tests/WarmPathGateTests.cs:186`.
- `hot-path.md` gate rules unqualified — `tests/WinForward.E2E.Tests/Lanes/LaneTransportAllocationGateTests.cs:12`,
  `:41`; `tests/WinForward.E2E.Tests/Lanes/LaneEngineAllocationGateTests.cs:9`, `:35`.

Dated artifacts (never rewritten; the hub's "Where things moved" table is what keeps them
resolvable):

- `benchmarks/results/2026-09-28-udp-reuse/README.md:102`, `:141`, `:171`, `:197` — `hot-path.md` §3.
- `benchmarks/results/2026-09-30-expiry-sweep-bounded-pause/README.md:176`, `:207` — §4.
- `benchmarks/results/2026-09-30-warm-path-lock-chain/README.md:193`, `:194` — §3 and §"FlowTable pooling".
- `benchmarks/results/2026-10-01-attribution-off-pump/gate-stability.txt:43`, `:49`, `:123` —
  `hot-path.md:1057-1060`; `:157` — "residual-lump section".
- `benchmarks/results/2026-10-06-aot-instruction-set/README.md:57`, `:61` — "the standing
  `hot-path.md` contract".
- Inside the document: line 393/471 point at sibling sections that move
  ("Allocation-gate stability", "An allocation gate must open only after its path is ready");
  line 1125 points at §"Native pool family"; the F4 table's rows point at "this file, contract
  4/8/10/11/12". All become child-to-child links.

Index: `verify-specs.py` AC3 requires **every** `.md` under `backend/` to appear by name in
`backend/index.md`, so all eight children must be added to the index table (design §1 says children
are reachable through the hub — the checker is stricter than the design; see §5.1).

---

## 5. Open questions / judgement calls

1. **Index vs hub reachability.** `check_structure` in `verify-specs.py` fails unless every child's
   filename appears in `backend/index.md`, while `design.md` §1 says the index lists the hub and the
   children are reached through it. One of the two must give: either list all eight children in the
   index (my recommendation — the index table is the fastest way to land on `allocation-gates.md`),
   or relax the checker to "listed, or linked from a listed hub".
2. **Who owns the gate-flake material.** `test-stability.md` §4 already carries the loaded-soak proof
   and cites "`hot-path.md` §6" for the residual. If `allocation-gate-host-lumps.md` also carries it,
   the library has two owners for one rule (design §2.6 forbids this). My recommendation: keep the
   *shape* in `allocation-gates.md` (it is a packet-path convention), move the *host residual* to
   `test-stability.md`, and leave a two-line pointer here. The lead should settle this before wave 2,
   because it changes two documents, not one.
3. **Where the benchmark-instrument rules live.** `measurement-harness.md` (607 lines) is the natural
   owner, and it is being analysed by another agent this wave. Landing
   `benchmark-measurement-discipline.md` now risks a second move; landing nothing risks losing the
   content. Recommendation: write the child, and record the eventual fold in its header.
4. **The ≤8,200 band has two claimants.** `hot-path.md` calls it the framework ladder;
   `benchmarks/results/2026-10-01-udp-session-footprint/README.md:141` applies it to the churn row.
   I could not tell from the artifacts which reading the recorded band was derived from. The lead (or
   the wave-2 agent for `udp-datagram-path.md`) should re-derive it from the archived
   `09-22-session-creation-cost-redo` numbers before assigning an owner.
5. **Test totals are a moving target.** The document carries five different suite totals
   (386 → 431 → 678 → 998 → 1 141+18), and the proof loop hard-codes per-class totals that no longer
   match (`SweepAllocationGateTests` 12→11, `FlowAttributionPipelineTests` 19→23, §1 row 13). I did
   not run the suite, so I cannot state today's total; my counts come from `[Fact]`/`[Theory]`/
   `[InlineData]` in the class files, which is exact for the two classes I flag but is not a
   substitute for a run. Recommendation: the spec states the *rule* ("re-derive the expected total
   from the class before running the loop") and carries no literal.
6. **Two mechanically-flagged names are legitimately dead.** `IsReverseCandidate` (the prose says it
   was deleted) and `ResidualLumpProbe` (the prose says it was deleted after measurement). I
   recommend rewording the prose rather than allowlisting them — the allowlist is for names the
   *tree* owns elsewhere, and neither of these exists anywhere.
7. **F4 proof map.** My recommendation is deletion (its durable rules are contracts 4/8/10/11 and its
   facts are the tests it names). The alternative is a ~12-line "F4 deltas" table in
   `packet-shape-and-flow-identity.md`. The lead should pick one, because the same decision applies to
   the other oversized docs' proof maps.
8. **Claims I could not verify either way** (listed rather than guessed): the Roslyn hoisting claim
   that a capturing lambda's display class is allocated at method entry (`IL_0000: newobj …`, line
   358-364) — it is consistent with the measured 184 B/op but I did not compile a probe; the
   `TransportTuple` "packed 48 B" size (line 611) — no size fact asserts it and I did not run
   `Unsafe.SizeOf`; and every ns/pps figure, which the doc itself declares dev-box-noisy.
9. **The `2 MB at the shipped 65,536 default` warm-cache figure** (line 598) checks out
   (`clamp(65 536 × 64, 4 096, 262 144)` = 262 144 slots × 8 B = 2 MB,
   `src/WinForward.Core/FlowTable.cs:66-67`), but it is a derived number with no fact behind it. It
   is exactly the kind of threshold design §2.5 wants anchored to its owner; keep the clamp
   expression and let the MB figure go.
