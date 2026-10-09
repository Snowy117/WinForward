# Plan: udp-relay.md

> Target: `.trellis/spec/backend/udp-relay.md` (1029 lines, 12 H2 sections, 8 seven-section
> contract blocks). Verified against the tree at the current HEAD. Every `file:line` below was
> read, not inferred. Section 3 proposes a 6-child family plus a hub; per `design.md` §3.3 the
> hub carries a **Where things moved** table so the numbered citations frozen in
> `benchmarks/results/**` (`2026-09-30-warm-path-lock-chain/README.md:201` cites `udp-relay.md
> §3/§4`) stay resolvable without rewriting dated evidence.

## 1. Stale claims (verified against code)

| # | Section (line) | Claim as written | What the code says now | Evidence (file:line) | Action |
|---|---|---|---|---|---|
| 1 | UDP relay wiring, H2 heading (7) | "UDP relay wiring (wired 2026-08-09, **hardware pass pending**)" | The hardware pass happened and is recorded by this doc's own L14/L16 (2026-08-27, origin-adapter binding on real hardware). The startup fallback path it was pending on is implemented and locked by a test. | `src/WinForward.Runtime/UdpProxy/UdpAdapterTargetSource.cs:71`; doc L14, L16 | Drop the parenthetical; keep the date only on rules whose date still justifies them. |
| 2 | Wiring (12) | host adapter MAC "from NDISAPI `CurrentAddress`" | No such member. The ABI field is `TcpAdapterList.CurrentAddresses`; the runtime surfaces it as `NdisAdapter.MacAddress`. | `src/WinForward.NdisApi/NdisApiAbi.cs:82`, `src/WinForward.NdisApi/NdisAdapter.cs:3` | Rename to `CurrentAddresses` / `NdisAdapter.MacAddress`. |
| 3 | Wiring (13) | "plumbed through `IUdpResponseSink.InjectAsync(..., byte[]? clientMac, ...)`" | The parameter is `MacAddress clientMac` (inline 6-byte value type), not `byte[]?`. | `src/WinForward.Runtime/UdpProxy/UdpResponseReinjector.cs:18` | Fix the signature in prose. |
| 4 | Wiring (13) | executor "extracts the Ethernet src MAC from bytes 6..11 of the **first** proxied frame" | It extracts on **every** proxied UDP datagram dispatch (the coordinator only consumes it on the admission path, so the session keeps the first). | `src/WinForward.Runtime/Capture/NdisPacketActionExecutor.cs:475` | Reword: "of each proxied frame; the session records the first". |
| 5 | Wiring (13) | "`UdpProxySession.ClientMac` is set once, get-only" | It is a **private** get-only `MacAddress` auto-property; it is not part of the session's surface. | `src/WinForward.Runtime/UdpProxy/UdpProxySession.cs:175` | Mark it private in the prose (or drop the member name). |
| 6 | Wiring, activity bullet (38) | "`_lastActivityTicks` … (Interlocked, **exact** — idle-expiry decisions in `TryBeginExpiry` read this exact value)" | The field is `_lastActivityBucket`, a 500 ms bucket published with `Volatile.Write`; `LastActivityUtc` is quantised **down** and expiry compares bucket cutoffs, so retirement is deliberately up to one bucket late and never early. The doc is describing a property the code explicitly disclaims. | `src/WinForward.Runtime/UdpProxy/UdpProxySession.cs:76`, `:123`, `:271-276`; `src/WinForward.Core/ActivityBucket.cs:24` | Rewrite: bucket stamp, never early, ≤1 bucket late. |
| 7 | Wiring (38) | "throttled to at most once per `ActivityPropagationInterval` (**100ms**)" | No such symbol. The constant is `ActivityPropagationBuckets = 1` bucket = **500 ms** (`ActivityBucket.TicksPerBucket`). | `src/WinForward.Runtime/UdpProxy/UdpProxySession.cs:44`; `src/WinForward.Core/ActivityBucket.cs:24` | Rewrite with the real name and the real quantum. |
| 8 | Wiring (38) | table "serves reverse-leg classification and **sweep pruning** on seconds-scale timeouts" | `UdpAssociationTable.RemoveExpired` has **no production caller** (its own doc-comment says "tests only"); the sweeper's UDP leg calls only `UdpProxyCoordinator.RemoveExpiredAsync`. Sweep pruning of associations is not a live production path. | `src/WinForward.Runtime/UdpProxy/UdpAssociations.cs:132-137`; `src/WinForward.Runtime/IdleExpirySweeper.cs:202-203` | Drop "sweep pruning"; keep "reverse-leg classification". |
| 9 | Cross-family §2 (99) | "`Socks5UdpCodec.TryEncode/Encode`" | Only `TryEncode` exists; the `Encode` overloads were removed (L297 of the same doc says so). The two statements contradict each other. | `src/WinForward.Protocols/Socks5Udp.cs:20` (no `Encode` in the file) | Drop `/Encode`. |
| 10 | Bounded setup memory, structure note (162) | "`UdpProxyLogging` (static event formatting)" | The type is `UdpProxyLog`, `internal static partial`, in `Logging/UdpProxyLog.cs` — a different namespace path and a different name. | `src/WinForward.Runtime/Logging/UdpProxyLog.cs:7` | Rename; also fix L826 and L862. |
| 11 | Bounded setup memory (163) | "`UdpSessionSetup` (dial/claim/construct/flush pipeline **via ctor delegates**)" | The pipeline takes one slot seam (`IUdpSessionSlotHost host`) plus plain collaborators; "ctor delegates" (plural) no longer describes it. | `src/WinForward.Runtime/UdpProxy/UdpSessionSetup.cs:19-28` | Reword: "…via the `IUdpSessionSlotHost` gate seam". |
| 12 | Bounded setup memory (176) | "`SetupQueueGlobalByteBudget` (8 MiB default; **coordinator internal ctor override** for tests)" | The budget default is right, but the seam is `UdpProxyOptions.SetupQueueGlobalByteBudget` (internal `init` property) — there is no ctor parameter for it. | `src/WinForward.Runtime/UdpProxy/UdpProxyOptions.cs:49`; `src/WinForward.Runtime/UdpProxy/UdpProxyCoordinator.cs:78` | Reword to name the options seam. |
| 13 | Bounded setup memory (180) | "Diagnostics surface as coordinator `PendingSetupBytesForDiagnostics` / `SetupBudgetRejectionCount`" | Neither member exists. The coordinator exposes one `internal UdpProxyDiagnostics Diagnostics` record with `PendingSetupBytes` / `SetupBudgetRejectionCount` fields. | `src/WinForward.Runtime/UdpProxy/UdpProxyCoordinator.cs:210`; `src/WinForward.Runtime/UdpProxy/UdpProxyDiagnostics.cs:9-14` | Rewrite to the record. |
| 14 | Bounded setup memory (181) | "`SetupQueueDatagramTtl` (5 s), counters surfaced as **coordinator** `SetupTtlExpiredCount` / `SetupStampsRefreshedCount`" | The TTL is `private static readonly TimeSpan s_setupQueueDatagramTtl`; the counters are `UdpSessionSetup.TtlExpiredCount` / `.StampsRefreshedCount`, surfaced through `UdpProxyDiagnostics` — not on the coordinator. | `src/WinForward.Runtime/UdpProxy/UdpSessionSetup.cs:39`, `:66`, `:69` | Rewrite name, owner and surface. |
| 15 | Bounded setup memory (189) | "diagnostics surface as coordinator `SetupCooldownCountForDiagnostics`" | No such member; it is `UdpProxyDiagnostics.SetupCooldownCount`, read off `_cooldowns.Count`. | `src/WinForward.Runtime/UdpProxy/UdpProxyCoordinator.cs:210`; `src/WinForward.Runtime/UdpProxy/UdpProxyDiagnostics.cs:10` | Rewrite. |
| 16 | Bounded setup memory §2 (190-194) | "`BoundedSetupQueue` … entry shape is `(ReadOnlyMemory<byte>, DateTimeOffset)`; timestamp overloads `TryEnqueue(frame, enqueuedAt)` / `TryDequeue(out frame, out enqueuedAt)`; the **timestamp-less overloads forward with a default stamp**" | Nothing in the queue is `ReadOnlyMemory<byte>`. The entry is `Entry(NativeLease Lease, int Length, DateTimeOffset EnqueuedAt)` (inline single-slot fast path + `Queue<Entry>`); the API is `TryEnqueue(NativeLease, int, DateTimeOffset)` and `TryDequeue(out NativeLease, out int[, out DateTimeOffset])`. There is no timestamp-less **enqueue** overload and no default-stamp forwarding. | `src/WinForward.Core/BoundedSetupQueue.cs:19`, `:44`, `:69`, `:71` | Rewrite the whole bullet against the real entry shape and API. |
| 17 | Zero-alloc §2 (291) and Ownership §2 (581) | factory signature "…`IRuntimeLogger? logger = null`…" | The parameter is `ILogger? logger = null`. `IRuntimeLogger` exists nowhere in the tree. | `src/WinForward.Runtime/Socks5/Socks5UdpTransport.cs:52` (`rg IRuntimeLogger` → no hits) | Fix both listings. |
| 18 | Zero-alloc §6 (322) | "`Assert.Equal(IPAddress, IPAddressValue)` still compiles elsewhere (e.g. **`UdpPacketView`** assertions)" | The type is `UdpPacketSpanView`; `UdpPacketView` does not exist. The inference-participation assertions the note means are real and still compile. | `src/WinForward.Protocols/IPUdpPacket.cs:15`; `tests/WinForward.Protocols.Tests/UdpPacketParsingTests.cs:129`, `:131` | Rename the type. |
| 19 | Session lifetime §2/§3 (357-362) | "**Per-session lifetime token**: `UdpProxySession` owns `_lifetime = CancellationTokenSource.CreateLinkedTokenSource(context.Shutdown)` … `_receiveFailure` is written only by the generic catch …" | Deleted by the very next section (L441-447) and absent from the tree. The owner is a `QuiescenceScope` (`_scope`) that owns the CTS; the failure representation is `_scope.Fault`; admission is a `WorkLease`. | `src/WinForward.Runtime/UdpProxy/UdpProxySession.cs:65`, `:184`, `:203`, `:379` | Delete the mechanisms; keep only the teardown-reason and fail-closed-drop reasoning the section itself says to keep. |
| 20 | Session lifetime §3 (376-378) | "the coordinator tracks in-flight receive-failure teardowns and awaits them in its `DisposeCoreAsync` (`DrainInFlightTeardownsAsync`)" | No such field or method. The teardown is a scope child (`_scope.Run(...)`) joined by `_scope.DrainAsync()` inside `DisposeCoreAsync`. | `src/WinForward.Runtime/UdpProxy/UdpProxyCoordinator.cs:416`, `:536` | Delete; the 09-21 section already states the live rule. |
| 21 | Session lifetime §4 (384-392) | matrix rows asserting `_lifetime cancelled`, `_receiveFailure set`, `Session teardown after _lifetime disposal`, "`_disposed`" | Every row names a symbol that no longer exists; the surviving rows are restated verbatim in the next section's §4 (L500-513). | `src/WinForward.Runtime/UdpProxy/UdpProxySession.cs:65`, `:203`; `UdpProxyCoordinator.cs:45` | Delete the table; the superseding section owns it. |
| 22 | Zero-alloc §3 (300) | "**Composition single source of truth** (`Cli/UdpProxyComposer.cs`): one hoisted `NdisApiAbi.MaximumEthernetFrame`" | The hoist is `const int maximumFrameSize = NdisApiAbi.MaximumEthernetFrame;` in `DurableCaptureBundle`, not in `UdpProxyComposer`; the composer consumes `composition.MaximumFrameSize` and fans it out. | `src/WinForward.Cli/DurableCaptureBundle.cs:221`; `src/WinForward.Cli/UdpProxyComposer.cs:65-86` | Move the attribution to `DurableCaptureBundle`; keep the fan-out list. |
| 23 | Ownership §2 (840) | "`UdpSessionSetup`'s alias claim (`UdpSessionSetup.cs:102-110`)" | The `RelayAlias` claim block is at 104-118 (construction at 104, claim/collision checks through 118). | `src/WinForward.Runtime/UdpProxy/UdpSessionSetup.cs:104` | Refresh the range. |
| 24 | UoT §2 (859) | "`UdpProxyCoordinator.TeardownReasonFor` (`UdpProxyCoordinator.Send.cs:214-220`)" | Actual definition is 215-221. | `src/WinForward.Runtime/UdpProxy/UdpProxyCoordinator.Send.cs:215-221` | Refresh the range. |
| 25 | UoT §2 (860) | "`RemoveReceiveFailedSessionCoreAsync` (`UdpProxyCoordinator.cs:563-575`)" | Actual definition is 568-583; the classification the sentence is about (`TeardownReasonFor(fault)`) is at 579, outside the cited range. | `src/WinForward.Runtime/UdpProxy/UdpProxyCoordinator.cs:568`, `:579` | Refresh the range. |
| 26 | UoT §2 (862-864) | "`UdpProxyLogging.UdpTransportOf` (`UdpProxyLogging.cs:42-46`): `"uot"` for a **`Socks5Server`** with `UdpOverTcp`, …" | The method is `private static string? UdpSessionSetup.UdpTransportOf(ProxyTarget target)` — wrong type name, wrong owner, and the file `UdpProxyLogging.cs` does not exist (there is no such file at all). It takes a `ProxyTarget`, not a `Socks5Server`. | `src/WinForward.Runtime/UdpProxy/UdpSessionSetup.cs:186-190` | Fix name, owner, file and parameter type. |
| 27 | UoT §3 residual 2 (935) | "`UdpSessionSetup.HandleSetupFailureAsync`, `UdpSessionSetup.cs:152-167`" | Actual definition is 156-166. | `src/WinForward.Runtime/UdpProxy/UdpSessionSetup.cs:156` | Refresh the range. |
| 28 | UoT §3 residual 2 (938) | "only `RemoveSlotAsync`'s `SetupFailure` branch arms the cooldown (`UdpProxyCoordinator.cs:515-518`)" | The branch is 518-521 (`if (reason == … && !_scope.IsSealed) { _cooldowns.Write(...) }`). | `src/WinForward.Runtime/UdpProxy/UdpProxyCoordinator.cs:518-521` | Refresh the range. |
| 29 | UoT §3 residual 2 (939) | "still traces `udp.setup.cooldown` (`UdpProxyCoordinator.Send.cs:66`)" | The `UdpSetupCooldown` trace call is at 67 (inside the `TryHit` branch opened at 65). | `src/WinForward.Runtime/UdpProxy/UdpProxyCoordinator.Send.cs:65-67` | Refresh the line. |

**Adjudication of the seed list** (all confirmed against the tree):

- Simply **gone** (removed with association sharing; no replacement symbol): `UdpAssociationPool`,
  `UdpAssociationLease`, `UdpAssociationEvidence`, `UdpControlAssociation`, `UdpServerCapability`,
  `UdpAssociationCapabilitySampler`, `UdpAssociationReuseMode`. They survive only in dated
  `benchmarks/results/2026-09-28-udp-reuse|2026-09-29-benchmark-coverage|2026-09-30-expiry-sweep-bounded-pause/README.md`
  and in `.trellis/tasks/archive/**`; those are frozen and must not be rewritten. The spec's own
  L574-577 already lists them as removed — correct, keep.
- **Renamed**, then gone: `UdpPacketView` → `UdpPacketSpanView` (`IPUdpPacket.cs:15`).
- **Renamed, still live**: `UdpProxyLogging` → `UdpProxyLog` (`Logging/UdpProxyLog.cs:7`);
  `CurrentAddress` → `CurrentAddresses` (`NdisApiAbi.cs:82`) / `NdisAdapter.MacAddress`;
  `_lastActivityTicks` → `_lastActivityBucket`; `ActivityPropagationInterval` →
  `ActivityPropagationBuckets`; `SetupQueueDatagramTtl` → `s_setupQueueDatagramTtl`;
  `PendingSetupBytesForDiagnostics` / `SetupCooldownCountForDiagnostics` →
  `UdpProxyDiagnostics.{PendingSetupBytes, SetupCooldownCount}`.
- **Gone by rewrite**: `_activeSends` → `QuiescenceScope`/`WorkLease`; `_lifetime` → `_scope`
  (`QuiescenceScope`) owning the CTS.
- **False positive**: `OverflowException` is BCL and still correct — the `checked` cast it names
  exists (`src/WinForward.Protocols/Socks5Udp.cs:83`).
- `analyze.py` / `tables.md` / `verdict.json`: **not present** in this document (checked).

## 2. Verbosity cuts

| # | Lines | What | Why it can go |
|---|---|---|---|
| 1 | 16 | Four-line Win11 hardware-proof retelling (two Hyper-V adapters, GUID sort, pktmon drop code `0xE0004136`, 0/20 → 20/20) | The rule it justifies is already in the table directly above (L18-23) and the Bad case below (L25). Evidence belongs in `benchmarks/results/**`. |
| 2 | 14, 16, 27 | The F4 slot-indexed-array rule stated three times (bullet, hardware proof, "Required tests" prose) | One statement plus a test list; the second and third add no new fact. |
| 3 | 18-36 | Matrix (4 rows) + Good/Base/Bad + Required tests + a Wrong/Correct code block that repeats the matrix's second row | Three restatements of one four-row table. The "Wrong" snippet (`reinjector.SendToMstcp(scope[0]…`) is not writable against any real API — `scope[0]` is not a `UdpAdapterTarget`. |
| 4 | 44-49 | "UDP relay hardware findings (Win11, 2026-08-09)" — a dated incident retelling wrapping two rules | Rules 1-2 (reverse-datagram pass-through, self-traffic wildcard) are relay contracts; rule 3 (a local SOCKS5 server's own sockets get re-captured) is a **test-harness** artifact, not a relay contract. |
| 5 | 54-58 | "### 1. Scope / Trigger" that restates the H2 heading | A block whose only content is its heading (design.md §2.3). |
| 6 | 60-85 | §2 Signatures mixes a real signature list with two long prose digressions (the 2026-10-05 seam move, the "two implementations" tour) | The seam-move paragraph is changelog narration; the two-implementation tour belongs in one contract bullet. |
| 7 | 117-124 | §5 Good/Base/Bad restates the L108-115 matrix | Duplicate. |
| 8 | 126-135 | §6 "Preserve coordinator same-flow reuse, relay collision, capacity, failure, response routing, and matching-family coverage" | A vague regression wish, not a required assertion. |
| 9 | 137-147 | §7 Wrong vs Correct restating L92-94 | Both snippets are non-compiling pseudo-code (`new Socket(flowFamily, …)` with an undefined `flowFamily`); violates design.md §2.7. |
| 10 | 153-156 | Pre-fix worst-case arithmetic ("16,384 flows × 32 KiB = 512 MiB held for hours…") | Narration ahead of a bound the next bullet simply states. |
| 11 | 158-165 | "Structure note" refactor diary + "Terminology" word-retirement note | Records that a type was split ("mirroring the TCP 1158→5 coordinator split") and that the word "tombstone" is retired from UDP — neither is a rule. |
| 12 | 167-171, 280-285, 335-338, 437-438, 653-657 | Five "### 1. Scope / Trigger" blocks of "any change to X" boilerplate | One "Touch points" line per document replaces all five. |
| 13 | 211-225 | 15 lines for the rule "TTL ages from dial start": pre-fix maths, the 93.75 % figure, the benchmark path, an acceptance-gate instruction | Compress to the rule + the reason clause + the acceptance-gate pointer (design.md §2.2). |
| 14 | 246-253 | §5 Good/Base/Bad restates the L238-244 matrix | Duplicate. |
| 15 | 255-264 | §6 lists test names with a parenthetical explaining what one of them asserts | The test name carries it; the parenthetical is the test's own doc-comment. |
| 16 | 268-278 | Pre-fix allocation inventory ("3 heap allocations per forwarded datagram", "2 per relay response", …) before the rule | Narration; also ends with a broken-indent line (L278 opens with a tab). |
| 17 | 313-318 | §5 Tests restates names and cites "(463/463 at landing)" | A historical suite count is meaningless now. |
| 18 | 320-322 | §6 "Migration note" (xUnit generic inference and the `IPAddress` → `IPAddressValue` conversion) | A test-authoring rule, not a relay contract → `test-stability.md`. |
| 19 | 326-431 | **Whole superseded section** (self-declared at L328-333): §3 mechanisms, §4 matrix, §5, §6 and §7 all describe code deleted on 2026-09-21 | ~100 lines for ~10 lines of surviving reasoning (teardown-reason-as-data + fail-closed send drop), which the next section already states. |
| 20 | 394-401 | §5 Good/Base/Bad restates the §4 matrix again | Duplicate. |
| 21 | 403-413 | §6 Tests: 11 lines, of which 8 are a benchmark retelling (`272 B → 0`, `4,184 B → 0`, "Actual: 88") | The identical table lives at `benchmarks/results/2026-09-30-expiry-sweep-bounded-pause/README.md:159,170,172`. Keep the gate names only. |
| 22 | 415-430 | §7 Wrong vs Correct repeating §3's admission rule | The "Wrong" snippet names `_expiring` and `_receiveFailure`; the "Correct" snippet omits `_scope.Fault` compared to the real `UdpProxySession.cs:203`. |
| 23 | 514-521 | §5 Good/Base/Bad restates the §4 matrix again | Duplicate. |
| 24 | 557-563 | Ownership preamble re-argues the removed sharing design (4–7 of 48 churn flows, 3 of 48 burst) | The same argument is repeated at L615 and again at L753-759; one half-line suffices (design.md §2.9). |
| 25 | 626-647 | §7 Wrong vs Correct: four pseudo-snippets (`_pool.RentAsync`, `_lease.IsFaulted`) | Restates L588/L590 and names symbols that no longer exist anywhere in the tree. |
| 26 | 611-615 | §5 Good/Base/Bad restates the L599-609 matrix | Duplicate. |
| 27 | 692-698 | "Pre-change state" paragraph narrating pre-fix behaviour at length | The rule arrives at L715; the narration adds no constraint. |
| 28 | 728-732 | "A mismatch is not an error" re-derives RFC 1928 relay-source validation | Already stated at L11 and implemented at `Socks5UdpTransport.cs:415-422`; a pointer suffices. |
| 29 | 743-752 | Ten lines justifying "the throttle is per session, deliberately" | Compress to the rule + "a global throttle would lose per-session attribution". |
| 30 | 787-807 | §6 Wrong vs Correct restating count-then-deliver | The contract is already prose at L715-720. |
| 31 | 813-819 | UoT preamble narrating the rationale (setup round trip disappears, reply-source question disappears) | §3/R2 and the reply-source bullet state both again. |
| 32 | 929-943 | "Carried residuals": item 1 is a byte-count post-mortem ("an earlier estimate assumed a 22-byte FQDN"); item 2 is a 10-line counter-gap diagnosis | Item 1 is compressible to the constant; item 2's diagnosis is the one piece worth keeping (it tells a diagnoser to read the cooldown trace, not the counter). |
| 33 | 944-952 | "**Interop status. [to confirm]** … the memo's on-box probe has not run" | A literal `[to confirm]` open status is task-document material; the wire pin itself (sing-box `testing` @`2ff3985c`) can stay as one provenance clause. |
| 34 | 990-1000 | "### 6. Measured evidence" retelling the benchmark column | The same numbers live at `benchmarks/results/2026-10-06-uot-per-flow/README.md:203,224` — keep the pointer, drop the transcription. |
| 35 | 1002-1029 | §7: two Wrong/Correct blocks; the second (self-traffic registration) is a different rule from the heading and duplicates the native path at L244-247 | Split the rule out or drop the block. |

Net: ~250 lines of narration, duplicated matrices, superseded mechanisms and restated
Good/Base/Bad blocks; the seven-section blocks alone account for ~120 of them.

## 3. Split proposal

Family of **one hub + six children**, 1029 → ≈ 820 lines total. Hub keeps the filename and is
listed alone in `backend/index.md`; children are reachable from the hub (design.md §1). Every
child opens with a one-line scope and links back to the hub.

### udp-relay.md (hub, ≈ 135 lines)

Content: scope and when to read it; "The datagram path" (the compressed L9-11 + L37 wiring
bullets: parse → `TrySendSpanAsync` → frame consumed; relay-source validation port+family vs the
local target's exact rule; per-flow self-traffic tuple registered before the first datagram;
reverse-datagram pass-through); skip classes and the receive-fault posture (from L39-40, minus
the S2 retelling); the "Where things moved" table mapping every old H2/H3 (with its numbers,
e.g. the ownership section's `§3/§4` cited from `benchmarks/results/2026-09-30-warm-path-lock-chain/README.md:201`)
onto its child; topic map with one line per child.

### udp-response-reinjection.md (≈ 130 lines)

- Purpose: how a relayed reply is validated, owned, rebuilt into an Ethernet frame, and injected
  toward the right adapter — or dropped fail-closed.
- Sections moved: wiring L10-11's reinjection half, L12, L13, L14 (trimmed of #1/#2 above), L16
  (compressed), L18-27 (table + tests only); and the whole H2 "Reply-ownership observability: the
  foreign-source counter" (L690-808) minus its §5/§6 duplication.
- Notes: absorbs the `ClientMac`/adapter-target/MAC-slot rules and the
  `udpResponseSourceMismatch` / `udp.response.foreign_source` contract, because both answer the
  same reader question ("what happens to a reply from the relay?"). Keeps the scope-tolerance
  rule; drops the duplicate RFC 1928 derivation (#28).

### udp-relay-transport.md (≈ 115 lines)

- Purpose: the SOCKS5 UDP relay transport itself — ASSOCIATE across families, relay socket setup,
  and the endpoint/buffer sizing that keeps the send path allocation-free.
- Sections moved: H2 "Cross-family SOCKS5 UDP relay setup" (L52-148, trimmed: drop boilerplate §1,
  collapse §5-§7) and H2 "UDP endpoint zero-allocation + frame-cap buffer sizing" (L268-323, minus
  the migration note, which moves to `test-stability.md`).
- Notes: both halves are about constructing and sizing this one transport; the module-boundary
  bullet (L68-85) is kept compressed to one paragraph because it is the seam every other file
  names.

### udp-flow-setup.md (≈ 95 lines)

- Purpose: what happens between a flow's first datagram and a live session — the 8-wide dial
  limiter, the bounded setup queue with its 8 MiB global budget and 5 s TTL, and the 1 s setup
  cooldown.
- Sections moved: H2 "Bounded UDP setup memory: global budget + datagram TTL + cooldown bound"
  (L151-265), plus the setup-admission sentences from the wiring section.
- Notes: keeps §3 Contracts + §4 matrix (the exactly-once charge/credit contract is genuinely
  cross-layer) and drops §5/§6 duplicates; **see §5 — this file and `udp-association-ownership.md`
  are the two merge candidates.**

### udp-session-lifecycle.md (≈ 150 lines)

- Purpose: one UDP flow's session — its state vocabulary, scope-owned lifetime, teardown reasons,
  fail-closed send drop, receive-failure signal, and its two-class retention with the sweep
  cadence derived from it.
- Sections moved: H2 "UDP session lifetime, teardown reason, and fail-closed send drop" (L326-431,
  reduced to the surviving reasoning only — delete §3's mechanisms, §4's table and §5/§7);
  H2 "Scope-owned UDP lifetime and the receive-failure signal/join split" (L434-554, minus its §5
  and duplicated §7); H2 "UDP session retention and the relay receive buffer (two-class
  retention)" (L651-687).
- Notes: L326-431's title is cited by `benchmarks/results/2026-09-30-warm-path-lock-chain/README.md:201`
  as "`§3/§4`" — the hub's Where-things-moved table must record that the surviving §3/§4 content
  now lives here, not in the deleted section. Also keeps the relay-receive-buffer default
  (64 KiB, 16..1024) with its owner named (`Socks5UdpTransport.DefaultRelaySocketReceiveBufferSize`).

### udp-association-ownership.md (≈ 90 lines)

- Purpose: one flow, one authenticated association — what the dial produced, who owns it, how the
  watchdog declares it dead, how disposal is ordered, and what evidence the transport keeps.
- Sections moved: H2 "UDP association ownership: one flow, one authenticated association"
  (L557-648) minus §5/§6/§7 duplication; plus the wiring bullet L37's factory sentence.
- Notes: keeps the removed-feature list (L574-577) as the reason the rule exists — it is the
  live constraint the design.md §2.9 exception allows. Keeps the alias-registry summary (L570-573)
  as three lines with a pointer to `traffic-policy-lifecycle.md`, which already owns the
  alias-collision guarantee (its L50) and association retention (its L52).

### udp-over-tcp.md (≈ 150 lines)

- Purpose: the opt-in UoT v2 connect-mode carriage — one flow, one stream connection; framing,
  pipelined establishment, typed faults, descriptor budget, synthesized reply source.
- Sections moved: the whole H2 "UDP over TCP per flow: one flow, one stream connection"
  (L811-1029), minus the measured-evidence transcription (#34) and with the interop status
  compressed (#33).
- Notes: the largest child even after trimming; it is self-contained (it explicitly says the
  native path is untouched) and shares no rule with the other children except the descriptor-floor
  comparison, which stays here as a cross-link to `udp-association-ownership.md`.

## 4. Cross-references that break

| Where | What it says | Fix |
|---|---|---|
| `backend/traffic-policy-lifecycle.md:52` | "See **'UDP association ownership'** in [udp-relay.md](./udp-relay.md)." | Retarget to `./udp-association-ownership.md` (the quoted title survives as that child's title). |
| `backend/error-handling.md:14` | "see **the bounded-setup-memory scenario** in [udp-relay.md](./udp-relay.md)" | Retarget to `./udp-flow-setup.md`. |
| `backend/error-handling.md:15` | "See **'UDP association ownership'** in [udp-relay.md](./udp-relay.md)." | Retarget to `./udp-association-ownership.md`. |
| `backend/hot-path.md:812-815` | "The ownership contract itself … is in [udp-relay.md](./udp-relay.md)." | Retarget to `./udp-association-ownership.md`. |
| `backend/hot-path.md:1566` | "`udp-relay.md` (host-flow response adapter binding + Required tests)" in the rewrite ledger | Retarget to `./udp-response-reinjection.md` (the ledger row is a claim map, not dated evidence — keep the row, change the target). |
| `backend/index.md:17` | The single `[UDP Relay](./udp-relay.md)` row | Keep exactly one row for the hub and update its description to name the family (design.md §1: "`backend/index.md` lists the hub"). |
| `backend/index.md:24` | The split-history note (the 2026-08-29 monolith split produced `udp-relay.md`) | Extend with the 2026-10-09 family split so the lineage stays readable; the older paths named there stay valid. |
| `backend/windows-ndisapi.md:3`, `backend/traffic-policy-lifecycle.md:3` | Sibling links to `[udp-relay.md](./udp-relay.md)` in the header notes | No change needed — the hub keeps the filename; verify the one-line description still matches the hub's narrowed scope. |
| `src/WinForward.Runtime/UdpProxy/UdpProxyCoordinator.cs:27` | Live-code comment: "the pre-existing outstanding-lease window of `udp-relay.md`" | The cited content is the admission/expiry race, which moves to `udp-session-lifecycle.md` → update the comment to that title in the same change (design.md §3.1). |
| Hub-internal links | L3 → `windows-ndisapi.md`; L98 → `traffic-policy-lifecycle.md`; L675 → `error-handling.md`; L592/L872 → `hot-path.md` | Each must be carried into whichever child takes the sentence — they will silently disappear if the host bullet is rewritten rather than moved. |

Also required by design.md §3.3, not a break but a dependency: the hub's Where-things-moved table
must resolve the frozen numbered citations
`benchmarks/results/2026-09-30-warm-path-lock-chain/README.md:201` (`udp-relay.md §3/§4`) and
`benchmarks/results/2026-09-30-flow-key-parse-once/README.md:180` (host-flow response adapter
binding). Those two files must not be edited.

## 5. Open questions / judgement calls

1. **Merge `udp-flow-setup.md` (≈95) into `udp-association-ownership.md` (≈90)?** They are the two
   smallest children and both answer "how does a flow get its relay". Merged they make one
   ≈185-line `udp-association-lifecycle.md`. I kept them apart because "queued-datagram memory
   admission" and "who owns the association" are different rules a reader looks up separately; the
   lead should settle whether a 6-child family is too fragmented for a 1029-line source.
2. **Does the hub keep the per-datagram cost/buffer-sizing table, or does it move wholly into
   `udp-relay-transport.md`?** I left the skip classes and the buffer ceiling in the hub (they are
   the "what one datagram costs" rules a reader expects on the front page) and moved the
   construction-time sizing there. The design's "hub is a document, not a table of contents"
   argues for keeping it; the "one topic per document" rule argues for moving it. My estimate
   assumes it stays.
3. **Not verified either way:** whether the 2026-09-06 burst-TTL acceptance matrix at
   `benchmarks/results/2026-09-06-udp-burst-ttl-fix/` still holds the exact `128/128` and
   `timeToLast ≈ ceil(N/8)×D` figures the doc quotes (L217-219) — I confirmed the directory exists
   but did not read its tables, because the numbers are dated evidence the revision should reduce
   to a pointer rather than re-verify.
4. **Not verified either way:** L114's matrix row "relay response family differs from original flow
   | do not throw from `FlowKey.Create`". `FlowKey.Create` *does* throw on a family mismatch
   (`src/WinForward.Core/Domain.cs:116`), so the row is a requirement on the caller, not a
   description of the callee. It reads as a stale derivation of an old bug and is a candidate for
   deletion, but I could not tell whether it is load-bearing for the cross-family contract.
5. **Judgement call the lead may want to overrule:** I recommend deleting the entire 2026-09-20
   section (L326-431) rather than keeping its reasoning, because the reasoning (teardown reason is
   data; only `SetupFailure` arms the cooldown; a fail-closed send returns `false` instead of
   throwing) is already stated in the superseding 2026-09-21 section at L370-378 and L490-496. That
   will make `benchmarks/results/2026-09-30-warm-path-lock-chain/README.md:201`'s `§3/§4` pointer
   resolve only through the hub table — which is exactly what design.md §3.3 provides for.
