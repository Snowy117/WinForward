# Plan: windows-ndisapi.md

> Read-only analysis, 2026-10-09. Target: `.trellis/spec/backend/windows-ndisapi.md` (566 raw /
> 412 effective — non-blank, non-heading). Every claim below was checked against the working tree;
> each row cites a `file:line` I read. Verification is static: this host has no Windows and no
> `ndisapi.dll`, so nothing here was re-derived by running tests or hardware smoke.
>
> **Duplicate verdict.** The heading *"Adapter-view self-healing — address fingerprints, periodic
> re-enumeration, health-triggered forced refresh (wired 2026-09-17, task 09-17-adapter-staleness-logging)"*
> appears twice: **L494–529** and **L532–566**. `diff` shows the two copies are the same content; the
> second has lost every backtick, both `→` arrows and both `≥` signs (e.g. `';'`→`';'`,
> `` `forced=true` ``→`forced=true`, `relaySetupFailed≥3`→`relaySetupFailed>=3`, `changed` → `→`
> `changed ->`). **The first copy (L494–529) is authoritative** — it is the only one that renders
> correctly and it carries the same words. **Delete L532–566 wholesale** (35 raw / 21 effective).

---

## 1. Stale claims (verified against code)

| # | Section (line) | Claim as written | What the code says now | Evidence (file:line) | Action |
|---|---|---|---|---|---|
| 1 | Batched capture reads §3 (412) | ETH_M_REQUEST managed shape carries `AssertManagedX64Layout` size/offset assertions "(24; 0/8/12/16)" | The assertion method is `AssertManagedLayout()`. It does assert `EthernetMultiRequest`=24 and offsets 0/8/12/16 — but also 5 other structs and a 64-bit-platform guard. | `src/WinForward.NdisApi/NdisApiAbi.cs:26,33,53-56` | Rename to `AssertManagedLayout`. Same stale name is quoted in `.trellis/spec/guides/cross-layer-thinking-guide.md:61` — fix in the same change. |
| 2 | Batched capture reads §6 (455) | Test `SynRewriteParseFailureLeavesFrameByteIdentical` | The test is `TruncatedSynRewriteFailureLeavesFrameByteIdentical` (parse-failure → frame byte-identical, Blocked, resources released). Class `TcpProxyCoordinatorRewriteTests` is correct. | `tests/WinForward.Runtime.TcpRedirect.Tests/TcpProxyCoordinatorRewriteTests.cs:223` | Rename. |
| 3 | Adapter list change refresh §2 (139) | `IUdpAdapterTargetSource { UdpAdapterTarget? Host; UdpAdapterTarget? Resolve(stableId); }` | `UdpAdapterTarget? Resolve(ushort slot)` — the source is **slot-keyed** (F4 interning), not stableId-keyed — and the interface has a third member, `IReadOnlyCollection<string> AdapterIds`. | `src/WinForward.Runtime/UdpProxy/UdpAdapterTargetSource.cs:27-39` (signature at `:33`, `AdapterIds` at `:39`) | Rewrite the signature; say "slot", not "stableId". |
| 4 | Adapter list change refresh §2 (142-143) | `LayeredCaptureRunner(enumerationProvider, generationFactory, changeSource, policy, logger, disposeDurableAsync, onScopeInstalled)` | The constructor takes 11 parameters: the four further optional ones are `minimumRefreshInterval`, `timeProvider`, `periodicRefreshInterval`, `interceptionHealthMonitor`. | `src/WinForward.Runtime/Capture/LayeredCaptureRunner.cs:71-82` | Either complete the signature or replace it with a prose list and a pointer to the health/scheduling bullets. |
| 5 | Batched capture reads §3 (412) | "Managed shape is `EthernetMultiRequest` … **24-byte fixed head**" | The upstream fixed header is 16 bytes; the managed struct is 24 bytes only because it also contains `FirstBuffer`, which *is* slot 0. A request for N buffers spans 16+8N (`MultiRequestByteCount(N) = 24 + 8(N-1)`). | `src/WinForward.NdisApi/NdisApiAbi.cs:118-124`; `src/WinForward.NdisApi/NdisApiDriver.cs:400-401`; formula confirmed by `stackalloc byte[16 + (8 * 2)]` for `count: 2` at `tests/WinForward.NdisApi.Tests/NdisApiBatchedSendAbiTests.cs:59` | Reword: `sizeof(EthernetMultiRequest)` is 24 (16-byte header + `FirstBuffer`); per-request size is 16+8N. |
| 6 | Adapter handles (95) | "Fix: `NdisCapturedPacket(buffer, _adapterHandle, buffer.DeviceFlags)` in the pump" | The pump calls `NdisCapturedPacket.FromCapture(_batchBuffers[index], _adapterHandle)`; the factory builds `new NdisCapturedPacket(buffer, enumerationAdapterHandle, buffer.DeviceFlags) { Flags = buffer.Flags }` — it also carries the NDIS metadata `Flags`, which the quoted call does not. | `src/WinForward.NdisApi/NdisCapture.cs:337` and `:10-13` | Update to the factory call. |
| 7 | Adapter handles (86) | "The queue query + batch read pair keeps sharing ONE lease of the adapter's gate." | Since F5 (2026-10-01) the steady state is **read-first**: one full-capacity `ReadPackets` per drain, and the queue query runs **only when that read did not succeed**. The "query + read pair" is no longer the normal shape (it survives only for an adapter the mismatch guard healed). The lease-granularity rule itself is correct and already stated at L417. | `src/WinForward.NdisApi/NdisApiDriver.cs:242-253` (one lease, read-first dispatch) and `:280-309` (`ReadSpeculatively`) | Delete the clause; keep the "one lease per drain" statement that L417 already owns. |
| 8 | Adapter list change refresh §2 (156-157) | `DurableCaptureBundle.DisposeAsync` "single-flight, order sweeper → udp → tcp" | Single-flight is right; the order is sweeper → **attribution pipeline** → udp (→ udp pools) → tcp (→ syn-copy/attribution/relay pools → wake owners). The attribution-pipeline step (F8) is missing entirely from the bullet, and so is the pipeline from the bullet's composition list. | `src/WinForward.Cli/DurableCaptureBundle.cs:431-470` (`:435` sweeper, `:439` attribution, `:447` udp, `:462` tcp) | Fix the order and add the attribution pipeline to the composition list. |
| 9 | Batched reinjection sends §3.1 (304) | "`DurableCaptureBundle.FlushPendingInjections(nint adapterHandle)` = `Executor.FlushPendingPasses` then the redirect flush." | The body is `ActivityClock.Tick()` → `Dispatcher.Attribution?.DeliverDecided(adapterHandle)` → `Executor.FlushPendingPasses` → `Tcp.FlushPendingRedirectInjections`. Decided attributions are delivered **first** (F8, 2026-10-01). | `src/WinForward.Cli/DurableCaptureBundle.cs:408-419` | Restate with the attribution delivery as the first step. (L416's separate F8 note is correct; this signature line contradicts it.) |
| 10 | Batched reinjection sends §3 (287) | "`DurableCaptureBundle.OnScopeInstalled` … composes `UpdateUdpTargets` + `Executor.RetireLanesExcept(<scope handles>)`" | Three steps: `UpdateUdpTargets(scope)` → `Executor.RetireLanesExcept(handles)` → `Tcp.UpdateRedirectTargets(handles)`. The third is described separately at L325 but missing from the lifecycle bullet that a reader lands on first. | `src/WinForward.Cli/DurableCaptureBundle.cs:391-398` | Add `Tcp.UpdateRedirectTargets`. |
| 11 | Batched capture reads §3 (413) | "Therefore the `ArrayPool<byte>.Shared.Return` lives in `ProcessAsync`'s outer `finally` — never on the lease completion callback." | The outer `finally` calls `lease.Release()`; the `ArrayPool<byte>.Shared.Return` is *inside* `PacketLease.Release()` (which also recycles the lease into the thread-static cache). The boundary rule is unchanged, but the named mechanism is not what the code does. | `src/WinForward.Runtime/Capture/CapturePacketProcessor.cs:103-106`; `src/WinForward.Core/PacketRuntime.cs:124-138` | Reword: the return point is `lease.Release()` in `ProcessAsync`'s outer `finally`. |
| 12 | Batched reinjection sends §2 (277) | "`NdisCapturePump` optional `onBatchCompleted` callback — … and from the run loop's `finally` before `ReleaseBatchBuffers()`" | It is `NdisCapturePumpOptions.OnBatchCompleted` (an option on the options record, not a constructor parameter). The loop-exit invocation is a post-`try`/`catch` block in `RunLoop`, not a `finally` clause — still before `ReleaseBatchBuffers()`, which is the part that matters. | `src/WinForward.NdisApi/NdisCapture.cs:54-55` (option) and `:250-269` (loop-exit invocation, release at `:264`) | Fix the member name and the "finally" wording. |
| 13 | Adapter-view self-healing §2 (508), vs refresh §3 (195) | L508: "consumption stays single-point (demand entry reads-and-clears; install clears)". L195: "the force flag clears on the next install". | There are two clearing sites, as `_forceRebuild = false` appears in both `ProcessRefreshDemandAsync` (demand entry) and `InstallGenerationAsync` (install). Calling that "single-point" is self-contradictory. | `src/WinForward.Runtime/Capture/LayeredCaptureRunner.cs:199-200` (entry) and `:297` (install) | State the two sites once, in one place. |

**Also verified clean** (checked, no change needed — recorded so the check phase need not redo it):
`AdapterSlotTable.TryIntern`/`TryResolve` semantics and the absence of an `Observe` seam
(`src/WinForward.Core/AdapterSlotTable.cs:67-123`); `Guid.TryParse`-normalized, case-insensitive
GUID correlation (`src/WinForward.Windows/AdapterIdentity.cs:66-104`); `ValidateRowCount`'s
fail-closed bounds (`src/WinForward.Windows/ProcessAttribution.cs:195-199`);
`UnicastTableFirstRowOffset = 8` (`src/WinForward.Windows/IPHelperAbi.cs:25`);
`MaxPacketsPerSendRequest = (1024-16)/8 = 126` (`src/WinForward.NdisApi/NdisApiDriver.cs:26,30`);
`MaxLanes = 64` / `MaxFramesPerLane = 126` (`src/WinForward.Runtime/TcpRedirect/RedirectInjectionLanes.cs:30,37`);
health thresholds 3/3/8/8, 30 s window, 60 s cooldown, 5 min degraded spacing
(`src/WinForward.Runtime/InterceptionHealthMonitor.cs:57-69`); the 1 s storm guard, `87`, and
`MaxConsecutiveStartupRecoveries = 3` (`src/WinForward.Runtime/Capture/LayeredCaptureRunner.cs:35-45`);
the transient table 21/170/1237/995/1167/31 and the 5×/100 ms/1.6 s retry budget
(`src/WinForward.NdisApi/NdisNativeCallStatus.cs:50-56`; `src/WinForward.NdisApi/NdisCapture.cs:116-118`);
the gate map's `ConcurrentDictionary.GetOrAdd` (`src/WinForward.NdisApi/NdisNativeCallGate.cs:69-81`);
DLL resolution from `AppContext.BaseDirectory` only (`src/WinForward.NdisApi/NdisApiAbi.cs:163-182`);
all 14 `[LibraryImport]` declarations carry `SetLastError = true`
(`src/WinForward.NdisApi/NdisApiAbi.cs:184-245`); every log event name quoted in the doc exists
(`src/WinForward.Runtime/Logging/CaptureLog.cs:9-99`, `RuntimeLog.cs:35`, `TcpRedirectLog.cs:57-63`);
and every test-class name it lists exists.

---

## 2. Verbosity cuts

| # | Lines | What | Why it can go |
|---|---|---|---|
| 1 | 532-566 | The mangled second copy of the self-healing section. | Byte-identical content to L494-529 modulo stripped markdown; the first copy is authoritative (see the duplicate verdict above). 21 effective lines. |
| 2 | 102-119 | The 18-line blockquote retelling the 2026-09-07 incident: four adapters degraded, "87 degrade → refresh in 77 ms", R-2 auto-reset verification, R-1 shutdown ordering, the headless-Ctrl+C caveat. | The rule it justifies is two sentences: Windows rebuilds the bound list on plug/unplug/enable/disable/standby-resume/Wi-Fi-Direct churn; every handle goes stale at once; 87 stays permanent, so recover instead of retry. The rest is incident narration plus a verification caveat the rule does not depend on. Keep ~4 lines. |
| 3 | 91-96 | "Symptom / Cause / Fix (recorded as a verified bug class)" — repeats the L82 warning (captured `m_hAdapter` → error 87) and adds harness counts (97/97, 100/100). | Every line restates L82 or the L95 fix that is already folded into it. The counts are evidence records, not rules; the one-line fix belongs next to the warning. |
| 4 | 11-15, 120-125, 266-271, 396-399 | The four "### 1. Scope / Trigger" blocks. | Each restates its H2 heading and the blockquote above it, in the template's wording ("Trigger: any change to …"). The refresh and read ones also restate their own §2 signature lists. Collapse each to two lines (what breaks if you touch this) or drop it. |
| 5 | 280-290 vs 347-365 | The batched-send §4 matrix restates the §3 contract bullets as table rows (append-then-flush, one batch per lane, flush on exit, rebuild to 2N, in-place vs pooled staging, ordering). | A table must add condition→result pairs the prose does not already give. Keep only the rows that do: batch failure, redirect cross-adapter scope refusal, overflow refusal, per-frame tail. Drop the restatements. |
| 6 | 293-295, 304-307 | The §3.1 preamble ("Research F1 of `09-29-tcp-udp-path-structural-perf`: … used to inject one frame per IOCTL with a rented buffer plus a full-frame copy") plus a Signatures list whose items are then re-explained in the bullets below. | The preamble is a before/after changelog; the signatures are exactly the bullets' subject matter (`FlushPendingInjections` ordering is restated at L308-311, the in-place staging at L312-317, the caps at L340-345). Fold the names into the bullets; keep one "wired 2026-09-29" provenance tag. |
| 7 | 199-204 | "Shutdown order (R-1 deviation, documented)" — explains a deviation from a capture-loop wrapper removed on 2026-09-07 and concludes the deviation "is believed benign". | The rule is "generation cleanup runs before durable disposal". The comparison against a deleted wrapper informs nothing a reader can act on — and the sentence is now stale anyway (table row 8). Two lines. |
| 8 | 278 | The telemetry row narrating a spec-row deletion: "The stale `BatchedSendFlushCount`/`BatchedSendPacketCount` row was removed 2026-10-01 (F5; `rg -ni 'flushcount\|packetcount' src/` finds neither member)." | Changelog narration about this document. (The claim is true — no such member exists — but the quoted `rg` is imprecise: it hits locals `queuedPacketCount`/`requestedCount` in `NdisApiDriver.cs:263-301`.) State it positively: the driver exposes no batched-send counters; the nearest are `RedirectDegradedFlushCount`, `RedirectOverflowCount`, `ImmediateSendLaneOverflowCount`. |
| 9 | 371-373 | Measurement narration inside "Tests Required": "measured 96 frames → 6 calls = 16× reduction vs single sends"; "failed at 264 B per (adapter, direction) per iteration before the lane spare pool". | Historical measurements with no decision attached. Keep the assertion and the evidence pointer; move the numbers to the task artifact. |
| 10 | 509 | Six lines retelling the pre-fix read-back bug — a `consecutive=0` log sample, "recorded pre-fix", why the ordering guarantee no longer holds — to justify a one-line rule. | The rule is "log the handed `ForcedRefreshTrigger`; never read the monitor back". The rule plus the test name is enough; the archaeology is already in the task journal. |
| 11 | 403-418 | The read doc's §2 and §3. §2 already states the read-first shape and the `INdisReadPacketCalls` seam; L418 restates both at length, and L417 restates the lease rule a third time (also at L86). | One home per rule: merge §2's signatures with §3's contracts, keep the read-shape rule once, and delete the third copy of the lease-granularity rule. |
| 12 | 42-47, 53-74 | Identity §5 Good/Base/Bad + §7's 22-line Wrong/Correct C# block (a hand-written copy of `MatchAdapter`), both restating L25's "GUID-primary, MAC is a sanity fallback". | The code block re-implements `src/WinForward.Windows/AdapterIdentity.cs:66-81`, which is the authority and is cited on the very next line. Keep the two facts that are *not* in the code (the `Ethernet` friendly-name example, the ~6 cloned-MAC interfaces) as two sentences; delete the sample. |
| 13 | 494-496 | The self-healing blockquote's ground-truth retelling (last `adapter.refresh` 78 minutes before the outage, only a process restart cured it). | The load-bearing clause is "the NDISRD change event cannot see address changes". One sentence keeps the "why"; the outage timeline belongs to the incident record. |
| 14 | 205-207, 220-222 | "Mode restore across refresh" and the old-handle degrade bullet restate rules already carried by the matrix rows and by L210. | Same rule, two homes. Keep the contract sentence, let the matrix row carry the condition. |
| 15 | 458-488 | The read doc's §7 Wrong/Correct: 30 lines of C# whose two rules (pooled-frame return, read-before-write on in-place rewrite) are both stated in §3. | Duplicated rule statements in code form; if the frame-lifetime rules move to `hot-path.md` (see §5.3) these samples go with them, otherwise keep one 4-line pair. |

Rough total: **~35 raw** from the duplicate, **~85 raw** from cuts 2–15 → the file lands near
**300 effective lines** with every rule, threshold and evidence pointer preserved.

---

## 3. Split proposal

Five files. The `windows-ndisapi.md` name survives and keeps the cross-cutting driver-seam rules that
every child depends on, plus the topic map. Section-title preservation is called out where live code
cites a title (R4/AC5).

Line estimates are raw → effective (non-blank, non-heading), measured from the current file and the
cuts above.

### windows-ndisapi.md (hub, ≈55 raw → ≈33 effective)

- **Purpose**: the driver seam itself — which handle is valid for what, how native calls are
  serialized and resolved, and the request ABI both batching paths share — plus the topic map for
  the family.
- **Sections moved**: intro + provenance (L1-6, rewritten); **"## Adapter Handles: Enumeration vs
  Captured"** (L80-89 minus the pump-performance note, which moves to the capture child); the
  `[LibraryImport]`/`Marshal.GetLastWin32Error()` diagnostics bullets (L88-89); **"## Native DLL
  resolution"** (L388-391); the ETH_M_REQUEST ABI bullet (L412); new: the topic map, a short
  vocabulary list (`generation`, `scope`, `durable layer`, `lane`, `drain`/`flush`), and a
  "where things moved" column for the frozen citations in `benchmarks/results/**`.
- **Notes**: keeps the heading text **"Adapter Handles: Enumeration vs Captured"** verbatim — it is
  the section `src/WinForward.NdisApi/NdisCapture.cs:336` sends readers to. `L91-96` is cut
  (verbosity 3) and its one-line fix folded into the L82 warning. The hub's topic-map table must
  carry "old section → new file" so the dated result files that cite `windows-ndisapi.md` (adapter
  identity) stay resolvable by a human.

### ndis-adapter-identity.md (≈72 raw → ≈42 effective)

- **Purpose**: how an NDISAPI runtime adapter is correlated with a stable Windows identity, and how
  a flow key names an adapter without carrying a string.
- **Sections moved**: **"## Adapter Identity Contract"** (L7-78) in full, all seven subsections and
  the `Related` pointer.
- **Notes**: owns `WindowsAdapterInventory`, `IPAdapterInfo`, `WindowsAdapter`, the correlation
  matrix, `AdapterSlotTable`/`AdapterMetadata`/`FlowKey` interning, and `ValidateRowCount`. Add one
  forward link to `ndis-capture-refresh.md` for `AddressFingerprint` (a diff input, defined there)
  and one to the hub for `RuntimeHandle` lifetime. `hot-path.md:1565` and
  `benchmarks/results/2026-09-30-flow-key-parse-once/README.md:179` both label this content
  "`windows-ndisapi.md` (adapter identity)" — the hub's move table covers them.

### ndis-capture-refresh.md (≈165 raw → ≈130 effective)

- **Purpose**: when and why the capture view is rebuilt — bound-list change, periodic
  re-enumeration, address fingerprints, and health-triggered forced refresh.
- **Sections moved**: **"## Adapter list change refresh — layered capture generations"** (L102-261)
  and **"## Adapter-view self-healing — address fingerprints, periodic re-enumeration,
  health-triggered forced refresh"** (L494-529, the authoritative copy only).
- **Notes**: the largest child and the one with the most live cross-references; it owns
  `LayeredCaptureRunner`, `IAdapterListChangeSource`/`NdisAdapterListWatcher`,
  `CaptureAdapterScopeResolver.ResolveForRefresh`, `AdapterEnumerationDiff`, the generation
  lifecycle, and `InterceptionHealthMonitor`. If it later grows past ~250 effective lines, the
  clean second seam is "demand producers (watcher / timer / health monitor)" vs "generation swap,
  recovery and teardown" — not a line-range cut. Keep the H3 **"Redirect deferred-injection
  lanes"**-style title discipline: nothing here is cited by title today.

### ndis-batched-capture.md (≈88 raw → ≈58 effective)

- **Purpose**: the batched read ABI, the read-shape self-heal, pump batching, the arrival-signal
  idle wait, and batch-buffer ownership.
- **Sections moved**: **"## Batched capture reads and buffer pooling"** (L394-491), merged down to
  the §2 signatures + §3 contracts + §4 matrix + §6 tests; plus the pump-performance sentence at
  L98 (batch capacity 32, idle arrival wait vs 1 ms poll, `HighResolutionTimerScope`).
- **Notes**: owns `TryReadPackets`, `ReadSpeculatively`/`ReadQueryFirst`,
  `INdisReadPacketCalls`/`NdisNativeReadPacketCalls`, `NdisReadDiagnostics`,
  `NdisReadShapeMismatch`, `INdisPacketArrivalSignal`/`NdisPacketArrivalSignal`,
  `MultiAdapterCaptureLoop`'s arrival-signal list, and `NdisPacketBufferPool`/`NdisPacketBuffer`.
  Links to the hub for the handle rule and the ETH_M_REQUEST layout. Decision pending on the
  frame-lifetime rules (see §5.3).

### ndis-batched-send.md (≈108 raw → ≈85 effective)

- **Purpose**: batched reinjection — the pass lanes and the TCP-redirect lanes, their flush points,
  ordering guarantees, capacity backstops and failure posture.
- **Sections moved**: **"## Batched reinjection sends"** (L264-385), including its H3
  **"### 3.1 Redirect deferred-injection lanes"**, plus the send-side ABI sentence from L406.
- **Notes**: **preserve both titles verbatim** — `src/WinForward.Runtime/TcpRedirect/RedirectInjectionLanes.cs:22`
  cites `windows-ndisapi.md, "Batched reinjection sends"` and `.trellis/spec/backend/hot-path.md:102`
  cites `windows-ndisapi.md § "Redirect deferred-injection lanes"` as the authority. Owns
  `SendPacketsToMstcp`/`SendPacketsToAdapter`, `FlushPendingPasses`, `FlushPendingRedirectInjections`,
  `RetireLanesExcept`, `RedirectInjectionLanes`, and the four redirect counters/event names.

**Arguably another document's business**

| Section | Case for moving | Recommendation |
|---|---|---|
| Read doc §3 "Frame lifetime boundary", "Copy-out points are mandatory", "In-place rewrite ordering" (L413-415), plus the pooling samples in §5/§7 (L440-444, L458-488) | `backend/index.md` already assigns "native lease lifetime, in-place reinjection" to `hot-path.md`, and `PacketLease` is a `WinForward.Core` type, not an NDISAPI one. | Defer. `hot-path.md` is 1404 effective lines and is itself being split; moving these now couples two revisions. Keep them in `ndis-batched-capture.md` for this change, add a forward link from `hot-path.md`, and settle ownership during the hot-path split. |
| Self-healing §2 "iphlpapi table row alignment" (L505), plus the owner-PID `ScopeId`/port byte-order rule (L29) and `ValidateRowCount` (L30) | These are Windows IP Helper ABI rules, not NDISAPI rules; they are already scattered across two would-be children and the family name is wrong for them. | Create `windows-ip-helper.md` **later** (it has no home today and three rules already want it), or accept them where their rule is used. Not a blocker for this split. |
| §3.1 "Redirect deferred-injection lanes" (L291-345) | It is about the TCP redirect injector, so `tcp-local-redirect.md` has a claim. | Keep in `ndis-batched-send.md`: it shares the lane table, the flush callback and the caps with the pass path, and both code citations name its title. Add a one-line pointer from `tcp-local-redirect.md`. |

Family result: **5 files, largest ≈130 effective lines**, every file well under the 400 cap and each
owning one decision surface.

---

## 4. Cross-references that break

**Index and sibling links (must change)**

1. `.trellis/spec/backend/index.md:15` — the "Windows NDISAPI Interop" row describes content that
   now spans five files; rewrite the description for the hub and add four rows (`ndis-adapter-identity.md`,
   `ndis-capture-refresh.md`, `ndis-batched-capture.md`, `ndis-batched-send.md`).
2. `.trellis/spec/backend/index.md:24` — the history note ("the former 470-line `windows-ndisapi.md`
   monolith was split … into `windows-ndisapi.md` + `tcp-local-redirect.md` + `udp-relay.md` +
   `traffic-policy-lifecycle.md`") must record this second split.
3. `.trellis/spec/backend/windows-ndisapi.md:3` — the sibling-links blockquote must add the four
   children.
4. `.trellis/spec/guides/cross-layer-thinking-guide.md:61` — quotes the stale `AssertManagedX64Layout`;
   fix in the same change (stale row 1).

**Live code and sibling docs that cite a section by title (AC5)**

5. `src/WinForward.Runtime/TcpRedirect/RedirectInjectionLanes.cs:22` — cites
   `windows-ndisapi.md, "Batched reinjection sends"`. Title preserved in `ndis-batched-send.md`;
   the filename in the comment must be updated.
6. `.trellis/spec/backend/hot-path.md:102` — cites `windows-ndisapi.md § "Redirect deferred-injection
   lanes" as the authority`. Title preserved; filename must be updated.
7. `.trellis/spec/backend/hot-path.md:1565` — spec-row map entry labelled "`windows-ndisapi.md`
   (adapter identity)" → retarget to `ndis-adapter-identity.md`.
8. `.trellis/spec/backend/hot-path.md:93` — "`m_hAdapter` (see windows-ndisapi.md)". Still resolves
   (the handle rule stays in the hub); worth adding the section anchor.
9. `src/WinForward.NdisApi/NdisApiDriver.cs:18` — points at "`windows-ndisapi.md`: the
   mutable-OVERLAPPED concern". Still resolves (the gate topology stays in the hub) but the cited
   sentence is being rewritten for stale row 7 — re-read it after the edit.
10. `src/WinForward.NdisApi/NdisCapture.cs:336` — "See spec/backend/windows-ndisapi.md" for the
    captured-handle rule. Still resolves (hub keeps that section).

**Intra-file pointers that become cross-file links**

11. L98 "see 'Batched capture reads and buffer pooling' below" → `ndis-batched-capture.md`.
12. L283 "the sibling table described under 'Redirect deferred-injection lanes' below" →
    `ndis-batched-send.md#redirect-deferred-injection-lanes`.
13. L406 "See 'Batched reinjection sends' below" → `ndis-batched-send.md`.
14. L222 matrix row's "F4" (defined in L26) → `ndis-adapter-identity.md`.
15. L404 "(see below)" / L417 / L418 "see below" stay intra-file inside `ndis-batched-capture.md`.

**Frozen citations (cannot be edited; hub must map them)**

16. `benchmarks/results/2026-09-30-flow-key-parse-once/README.md:179` — cites `windows-ndisapi.md`
    (adapter identity) against four core facts. The hub's move table must point a human at
    `ndis-adapter-identity.md`; the fact names themselves all still exist
    (`tests/WinForward.Core.Tests/AdapterSlotTableTests.cs:19,38,57,90`).

**Language (AC6)**

17. L105 carries `本地连接* N` in the refresh preamble. The English form `Local Area Connection* N`
    already appears at L44. Since verbosity cut 2 rewrites this preamble down to ~4 lines, resolve it
    there (gloss or drop) rather than keeping a second localized name.

---

## 5. Open questions / judgement calls

1. **Does this file actually need splitting?** Deleting the duplicate alone takes it from 412 to
   **391 effective lines** — already under the 400 cap — and the R2 cuts land it near 300. So the
   split is justified by *topic separation and headroom* (five unrelated decision surfaces in one
   file; no room for the next incident's rules), not by the cap. If the lead wants minimal churn, the
   defensible alternative is "delete the duplicate, apply R2, stop". My recommendation is to split
   anyway: the file is the most-cited in the library and the seams are clean.
2. **Does identity belong in the hub?** Proposed: four children, with the hub carrying the driver
   seam (handles, gates, ABI, DLL) + the map. Alternative B: fold `ndis-adapter-identity.md` into the
   hub (~80 effective lines), leaving three children. B saves a hop for the single most-quoted rule
   in the library and keeps `index.md`'s existing one-line description closer to true; A keeps the hub
   thin and the children single-purpose. **Lead's call.**
3. **Frame lifetime / in-place rewrite ordering** (L413-415, L440-444, L458-488). `index.md` assigns
   this to `hot-path.md`, but `hot-path.md` is 1404 effective lines and is being split in the same
   revision. Moving it now means two revisions touching one rule set; leaving it means temporary
   duplicate ownership. Recommendation: keep it with the NDISAPI family this round and add a
   forward link, then settle it in the hot-path split. **Confirm.**
4. **`windows-ip-helper.md`** — three rules (`ValidateRowCount` bounds, `ReadRow`/`Table[0]` offsets,
   `ScopeId` host-order vs network-order ports) are IP Helper ABI rules, not NDISAPI ones, and they
   would otherwise land in two different children. Recommendation: create the file now if the lead
   wants the family names to be honest; otherwise they stay where their rule is used and the hub's map
   notes the mismatch. **Confirm.**
5. **Could not verify either way** (recorded rather than guessed):
   - The hardware smoke numbers and production ground truth (L9, L96, L113-118, L496 — 77 ms
     refresh, 97/97 and 100/100 harness counts, 78-minute gap, 2026-09-17 link-state change) are
     historical observations. The artifact they point at exists
     (`.trellis/tasks/archive/2026-09/09-07-adapter-list-refresh/smoke-evidence.md`) but I did not
     re-derive them, and this host cannot.
   - "the send IOCTLs pass `lpOutBuffer=NULL, nOutBufferSize=0` and both IOCTLs are `METHOD_BUFFERED`"
     (L282) is asserted by the driver's own doc comment and the task's research artifact, not by
     anything executable here. It is consistent with the code's all-or-nothing behaviour, so I left
     it standing; `src/WinForward.NdisApi/NdisApiDriver.cs:429-437`.
   - "96 frames → 6 calls = 16×" and "264 B per (adapter, direction) per iteration" (L371-373) are
     recorded measurements, not reproducible from source.
   - "the factory's exclusion/log loop has no test (Windows-only, real driver required)" (L222):
     `rg -F AdapterSlotExhausted tests` returns nothing, so the claim still holds — but the absence of
     a test is not positive proof it can never be unit-tested.
6. **`EthernetMultiRequest` "24-byte fixed head"** (stale row 5) is misleading rather than false —
   `sizeof(EthernetMultiRequest)` really is 24. If the lead reads the sentence as accurate, drop that
   row; I recommend rewording because the same sentence also states "16 + 8N total", and the code's
   own comment says the fixed header is 16 bytes.
7. **Title preservation vs readability.** Two headings are cited verbatim by live files
   ("Batched reinjection sends" by `RedirectInjectionLanes.cs:22`, "Redirect deferred-injection lanes"
   by `hot-path.md:102`). I have kept both, but "Batched reinjection sends" is a weak title for a
   file that owns the whole send path. If the lead prefers a better title
   (`ndis-batched-send.md` → "Batched reinjection"), the two comments must be updated in the same
   change — the plan assumes preservation because AC5 prefers it.
