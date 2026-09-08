# Design Review — WinForward.Core + WinForward.Configuration

> Sub-agent report (explore), 2026-09-08. Read-only review; no files modified.
> Scope: src/WinForward.Core (IPAddressValue, IPPrefix, PacketRuntime, Policy, ProcessSelectors, Domain)
> + src/WinForward.Configuration (ConfigurationModels).
> Conventions checked against: .trellis/spec/backend/directory-structure.md, error-handling.md.

## 1. Module map (interface size vs implementation complexity)

| Type | Location | Public members (approx) | Depth verdict |
|---|---|---|---|
| `IPAddressValue` | Core/IPAddressValue.cs:15 | ~17 | **Deep** — hides dual-family bit layout, scope semantics, zero-allocation conversion |
| `IPPrefix` | Core/IPPrefix.cs:15 | ~8 | **Deep** — constructor normalization + two-op mask matching + no-throw parsing |
| `IFrameSource` | Core/PacketRuntime.cs:18 | 2 | **Perfectly narrow seam** (dependency inversion) |
| `PacketLease` | Core/PacketRuntime.cs:25 | ~10 | **Medium-deep** — hides pooled lazy copy, exactly-once completion, thread-local recycling; but heavy contract (Release on same thread, reads finished before callback) |
| `BoundedSetupQueue` | Core/PacketRuntime.cs:156 | ~8 | **Deep** — inline first packet + double bound + in-place timestamp overwrite |
| `PacketDisposition` | PacketRuntime.cs:5 | enum | Flat (enums need no depth) |
| `RuleMatcher` | Core/Policy.cs:3 | 9 | **Medium** — data-driven matcher |
| `PolicyRule` | Policy.cs:28 | 2 | Shallow (deliberately named record pair, reasonable) |
| `PolicySnapshot` | Policy.cs:30 | 4 | **Deep** — hides iteration, RuleIndex stamping, fallback, forwarded restriction |
| `ProcessSelectorMatcher` | Core/ProcessSelectors.cs:9 | 2 | **Deep (relative to size)** — hides filename/path/directory-tree three-state semantics |
| `Endpoint` | Core/Domain.cs:32 | ~8 | Medium-deep, handwritten equality |
| `AdapterContext` / `FlowDecision` / `FlowContext` | Domain.cs:73/121/127 | data vocabulary | Flat (legitimate) |
| `FlowKey` | Domain.cs:75 | ~6 | **Deep value type** — hand-tuned hash/Equals ordering |
| `FlowState` | Domain.cs:135 | 5 | Medium |
| `FlowTable`(+`TransportTuple`) | Domain.cs:153/306 | ~9 | **Deep core + bloated surface** (see issue 1) |
| `WinForwardConfigDto`/`RuleDto`/`Socks5ServerDto` | Configuration/ConfigurationModels.cs:8-52 | DTO | Shallow (serialization boundary, as it should be) |
| `ConfigurationLoader` | ConfigurationModels.cs:93 | **2 methods + 4 constants** | **Deepest in the set** — ~330 lines of validation behavior behind 2 entries |
| `ValidatedConfiguration` | ConfigurationModels.cs:79 | 5 | Medium |

## 2. Deletion test

No *type* is a pure pass-through. *Method-level* findings (verified across src+tests):

- `FlowTable.TryGet` (Domain.cs:167) — **zero callers** (src+tests all empty). Should be deleted per the project's own dead-public-surface rule.
- `FlowTable.TryClaim` (Domain.cs:222) — **behaviorally equivalent** to `TryClaimResolved` (Domain.cs:197) (both touch+claim+capacity check; TryClaim merely inlines `TryResolveLocked`'s dictionary check), used only by tests (CoreFlowStructuresTests.cs:68,70). Merge candidate.
- `FlowTable.Claim` (Domain.cs:216) — test-only (CoreFlowStructuresTests.cs:78,94), zero production calls.
- `BoundedSetupQueue.TryEnqueue(frame)` / `TryDequeue(out frame)` (PacketRuntime.cs:181/208) — one-line default-param forwarding; spec says inline (precedent `ReinjectExistingSynAsync`), but as overload convenience this is minor.
- `NormalizePath` (ProcessSelectors.cs:37) — public with no external callers (only this file lines 23-24).

Production actually uses only 3 FlowTable entries: `TryResolve` (FlowDispatcher.cs:174), `TryClaimResolved` (FlowDispatcher.cs:196), `RemoveExpired` (FlowDispatcher.cs:113).

## 3. Seam inventory (adapter counts)

| Seam | src/ adapters | tests/ adapters | Notes |
|---|---|---|---|
| `IFrameSource` | 1 (`NdisPacketBuffer`, NdisApi/NdisPacketBuffer.cs:13) | 0 | Cross-project dependency-inversion seam (Core unaware of NDIS), valid; but zero test fakes — tests never fake a frame source |
| `Func<FlowDecision> decide` (FlowTable factory) | 1 (FlowDispatcher.cs:196 lambda) | several test lambdas | Delegate seam |
| `Func<FlowKey,bool>? isHeld` (RemoveExpired) | 1 (FlowDispatcher.cs:113 ← coordinator.HoldsFlow) | tests | Delegate seam |
| `RuleMatcher` data-driven policy | 1 (ConfigurationLoader constructs) | constructed directly | Data seam not interface seam — good choice |
| `ConfigurationJsonContext` source-gen serialization | 1 | — | |

## 4. Domain.cs / file-type convention check

- **Domain.cs: 10 top-level types** (4 enums + Endpoint + AdapterContext + FlowKey + FlowDecision + FlowContext + FlowState + FlowTable), 264 effective lines. File name matches no type (no `Domain` type exists). `FlowTable`+`TransportTuple` (~180-line concurrency core) is the natural extraction candidate → `FlowTable.cs`. Closest violation of "second unrelated top-level type is a split signal" (directory-structure.md:66).
- **PacketRuntime.cs: 4 types**, file name likewise points at no type; `BoundedSetupQueue` (UDP setup buffering) is unrelated to packet lease — an "unrelated roommate" in the same file.
- **Policy.cs: 3 strongly-related types**, acceptable (cohesive domain), but again no `Policy` type.
- **ConfigurationModels.cs: 8 types / 367 effective lines** — spec explicitly exempts it (directory-structure.md:60 "达标后保持内聚不拆"). **Not a violation.**

## 5. Error-handling spec compliance

| Spec clause | Result | Evidence |
|---|---|---|
| JsonException → path + fixed template, no Message/value leakage | ✓ | ConfigurationModels.cs:126-132 |
| null collection element → indexed path | ✓ | `ValidateNonEmpty` reports `{path}[{index}]` (:342); null server/rule report `socks5Servers[i]` (:250) / `rules[i]` (:275) |
| **invalid** value element → indexed path | **✗ partial** | `ParseNetworks`(:355), `ParseSet`(:368), `ParsePorts`(:383,390) report the collection path not `[i]` (message contains the raw value as compensation, but path not indexed — spec line 21 asks "invalid or null ... at their indexed field paths") |
| fail-closed failure action | ✓ | only "block" (:328); fallback forbids proxy (:169 `allowProxy:false`) |
| Valid JSON never escapes as NRE | ✓ | logLevel uses JsonElement to distinguish Undefined; `NormalizeSet`'s `value!.Trim()`(:346) relies on :301 prior bail — an implicit invariant: correct but brittle |

**TryParse vs throw consistency**: consistent and layered by trust boundary — `IPPrefix.TryParse` (no-throw, untrusted config strings) vs `Endpoint.From`/constructors (ArgumentNullException/ArgumentException, cold-edge programming errors). Convention holds but is **implicit** (undocumented).

## 6. Top 3 strengths (with evidence)

1. **`ConfigurationLoader` is a textbook deep module** — 2 public methods + 4 derived-comment constants (:95-105) hiding all validation and diagnostic accumulation, warnings vs errors separated (:137-141).
2. **Narrow seam for frame lifetime** — `IFrameSource` only 2 members (PacketRuntime.cs:18-23) realizes Core←NdisApi dependency inversion; `PacketLease` hides lazy pooled copy, exactly-once completion (Interlocked, :138), thread-local single-slot recycling (:91-108).
3. **Value-type layer's constructor-as-invariant + why-comment culture** — IPv4 high-96-bits-zero enforced in constructor (IPAddressValue.cs:31); IPPrefix constructor normalization makes `Contains` a two-op mask compare (IPPrefix.cs:53-58,77-81); `FlowKey.Equals` "cheapest discriminator first" (Domain.cs:102), `BoundedSetupQueue` inline-first-packet rationale (PacketRuntime.cs:158-160).

## 7. Ranked issues (each with file:line + one-line impact)

1. **FlowTable dead/duplicate public surface** — Domain.cs:167 (TryGet dead), :222 (TryClaim ≡ TryClaimResolved), :216 (Claim test-only). 3 of ~6 near-identical lookup methods unused by production; callers forced to understand subtle differences; violates own dead-surface rg rule.
2. **Domain.cs junk drawer** — whole file. 10 types / filename matches nothing, violates file=main-type convention; FlowTable is the clear split candidate.
3. **PacketRuntime.cs same** — BoundedSetupQueue unrelated roommate; filename points at no type.
4. **Invalid array-value diagnostics not index-pathed** — ConfigurationModels.cs:355,368,383,390. Spec line 21 requires indexed paths for invalid elements too; multi-element arrays make users guess which element failed.
5. **logLevel double validation + message fork** — TryParse:116-118 ("must be a string") vs ParseLogLevel:196-220 (full enum message); LogLevel uses bare `JsonElement` (:11) inconsistent with other `string?` field typing — same wrong-type input gives asymmetric diagnostics vs fallbackAction's JsonException-path-only message.
6. **`NormalizePath` over-exposed** — ProcessSelectors.cs:37. public with no external callers.
7. **FlowKey hash/Equals asymmetry undocumented** — GetHashCode (Domain.cs:92-100) omits the three Origin fields while Equals (:103-116) compares them; legal and seemingly deliberate (aligning with TransportTuple hash) but no why-comment; also ~30 lines structural parallel duplication with TransportTuple (:315-335) (semantically necessary: two equivalence relations) — worth cross-referencing comments.

## 8. Spec violation summary (effective lines = wc minus comments/blanks)

| File | Effective/total lines | Violation |
|---|---|---|
| Domain.cs | 264/337 | line count ✓; **file=main-type ✗** (heaviest) |
| PacketRuntime.cs | 183/272 | line count ✓; file=main-type ✗ (filename has no matching type) |
| IPAddressValue.cs | 79/117 | ✓ |
| IPPrefix.cs | 60/82 | ✓ |
| Policy.cs | 53/63 | line count ✓; minor (3 types but cohesive) |
| ProcessSelectors.cs | 50/66 | ✓ |
| ConfigurationModels.cs | 367/436 | ✓ (explicit spec exemption precedent) |
| Dead public surface | — | `FlowTable.TryGet` zero call sites, meets deletion condition (directory-structure.md:72) |
