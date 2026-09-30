# Design — F4 flow keys, parse-once, atomic trackers

Scope: the four sites the PRD numbers — (1) `FlowKey` (string → interned adapter slot, 128 B → one cache
line), (2) the per-packet header walks (`IsTcpSyn`, the sequence observation, the checksum rewriter's
revalidation, and the reverse-leg `RecordServerSynAck` the research list omits), (3) the
`TcpRedirectAssociation._sequenceGate` trackers, (4) the per-packet `FlowContext`/`CapturedFlowPacket`
copies. Nothing about routing, policy decisions, rewrite mutation, injection, retention or the flow table's
concurrency architecture changes; what changes is the *representation* of identity, of the parsed frame, of
two `uint?` trackers, and of the strings a packet carries.

Deliberately out of scope (PRD Notes): the §A4 sharded slab/index rebuild with a canonical key and
incremental rehash (roadmap item 7); F2's warm cache, activity bucket, self-traffic split and redirect/UDP
caches (they are the acceptance surface, not the target); F3's `_liveStates`/`SweepHoldProbe` sweep shape;
the sharded `FlowTable`; the tombstone/cooldown/pending-SYN representations.

Explicitly in scope because requirement 1 cannot be met without it: **`FlowKey`'s endpoint storage is
repacked**, and **`TransportTuple`/`FlowHash` gain a packed entry point** so F2's warm path never
materializes an `Endpoint`. §2 and §3 carry the layout and the equivalence arguments; §2.5 and §8 carry the
F2 re-proof.

Ground truth for every anchor and number quoted below: `research/implementation-notes.md`, produced from
tree `ca8d999`.

## 1. Chosen mechanism per site

| # | Site | Mechanism | Before → after |
|---|---|---|---|
| 1 | `FlowKey` (`Domain.cs:76-118`) | `string? OriginAdapterId` → one **`ushort OriginAdapterSlot`** from a process-lived `AdapterSlotTable` (**`OriginAdapterGeneration` stays a key field**, operator decision, PRD Notes: only the stable id is interned, so the table is bounded by the adapter count); the two `Endpoint`s → two packed `(ulong low, ulong high, uint scope, ushort port)` groups; `Local`/`Remote` stay `Endpoint`-typed **computed** properties | 128 B → **64 B** (exactly the contract); two `Endpoint` copies per hash/probe → none; `string.Equals` in `Equals` → gone; slot **and** generation compared, i.e. today's semantics |
| 2 | header walks (`CapturePacketProcessor.cs:67`, `TcpFrameRewriter.cs:69`, `TcpSequenceObservation.cs:55` + `:37`, `PacketChecksums.cs:105-176`) | the classifier's `PacketView` is *derived once* into a 16-byte **`PacketLayout`** carried on `CapturedFlowPacket`; `IsTcpSyn`, both sequence observations and the checksum rewriter consume it | 4 walks → **1** per redirected packet; the rewriter keeps only `Transport == Tcp`, the span bound and the argument-family check |
| 3 | `TcpRedirectAssociation` trackers (`TcpRedirectTable.cs:122-156`) | two `uint?` under `_sequenceGate` → two **`long`** (−1 = unobserved) with a CAS-max writer and `Volatile.Read` readers; `_sequenceGate` deleted | 2 lock entries/packet → **0**; one `Lock` allocation per association → 0 |
| 4 | `FlowContext` (`Domain.cs:167-174`) | 5 references + a redundant port → **two interned metadata references** (adapter metadata fetched from the slot table at classification, process metadata created at claim); `RemotePort` becomes `Key.RemotePort` | 168 B → **80 B**; `CapturedFlowPacket` 224 B → **152 B**; zero per-packet allocations added |
| 5 | UDP reinjection target map (`UdpAdapterTargetSource.cs:32/:38/:56`) | the per-response `Dictionary<string, UdpAdapterTarget>` → a **slot-indexed array** built from the slot table; `Resolve(string)` → `Resolve(ushort)` | one string hash + dictionary probe per UDP response → **one array index**; §3.6 |
| 6 | *(optional commit, §6)* IPv6 reverse-leg address delta (`PacketChecksums.cs:157-173`) | the `stackalloc` pre-image snapshot + second pass → one pass reading the old words directly; wide (`u64`) loads | adopted **only if** the post-Step-5 re-run of `tcp-redirect-data-path` shows a real IPv6 reverse win; never a gate |

The `PacketLease`, the native-frame handle, the in-place redirect lanes, the executor's disposition switch,
the gates, the sweep, the policy engine and every coordinator are untouched.

## 2. `FlowKey`: layout, size, hash and equality contract

### 2.1 The layout

```csharp
// src/WinForward.Core/Domain.cs — FlowKey's storage (public surface keeps the property names)
private readonly ulong _localLow;        // IPAddressValue.Bits low half
private readonly ulong _localHigh;       // IPAddressValue.Bits high half
private readonly ulong _remoteLow;
private readonly ulong _remoteHigh;
private readonly long _adapterGeneration; // WindowsAdapter.Generation of the parsing enumeration — KEPT
private readonly uint _localScopeId;     // Endpoint.Address.ScopeId
private readonly uint _remoteScopeId;
private readonly ushort _localPort;
private readonly ushort _remotePort;
private readonly ushort _adapterSlot;    // AdapterSlotTable.NoSlot == 0 when there is none
private readonly byte _addressFamily;    // AddressFamilyKind
private readonly byte _protocol;         // TransportProtocol
private readonly byte _origin;           // FlowOriginKind
```

Layout arithmetic: `32 (4 × ulong) + 8 (generation) + 8 (2 × uint scope) + 4 (2 × ushort port) + 2 (slot)
+ 3 (family/protocol/origin) = 57` → alignment 8 (no `UInt128` field — that is the point of splitting Bits
into two `ulong`s) → **64 B**.

**The key lands exactly on the contract, with no margin.** 57 rounds up to 64, so *no further field fits*:
any addition — a second slot, an extra flag byte, a wider generation — pushes the struct to 72 B and fails
`FlowKeyFitsOneCacheLine`. The implementer must therefore treat 64 as a hard ceiling and re-check the size
assertion before adding anything to the type, including a "temporary" diagnostic field. This is why the
generation is carried as the existing `long` rather than being split or checksummed, and why
`PacketLayout`'s `Family` byte (which does fit, §4.1) is the only other field this task adds anywhere near
the key.

`AddressFamily` and `Protocol` stay as `byte` fields rather than being re-derived, because `GetHashCode` and
`Equals` read them on every probe and both are also read directly by logging and policy. They are **not**
hashed with the slot or the generation (§2.3): the hash's field set is exactly today's transport-only set.

**Shape note (the implementer will hit this immediately).** With only private readonly fields there is
nothing for a `with` expression to set: `FlowKey.Reverse()` and `TransportTuple.Reverse()` become explicit
private-constructor calls instead of `this with { Local = Remote, Remote = Local }`, and the test sites that
build keys with `with { OriginAdapterId = … }` (`WarmPathGateTests.cs:277-278`) move to
`FlowKey.Create(…, slot, generation)`. Either keep `FlowKey` a `readonly record struct` (its
compiler-generated `Equals`/`GetHashCode`/`ToString` are then overridden/suppressed by the hand-written ones)
or downgrade it to `readonly struct : IEquatable<FlowKey>` — the plan does not care which, but the choice is
recorded in Step 5, and dropping `record` must not drop `IEquatable<FlowKey>`.

### 2.2 The size contract and its proof

The PRD's "fits one cache line (≤64 B)" is discharged by an **exact** test, not a measurement:

```csharp
Assert.True(Unsafe.SizeOf<FlowKey>() <= 64, $"FlowKey is {Unsafe.SizeOf<FlowKey>()} bytes");
Assert.DoesNotContain(typeof(FlowKey).GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public),
    field => !field.FieldType.IsValueType);          // no reference-typed field ⇒ no string comparison is possible
```

The second assertion is the *structural* form of requirement 1's "no string comparison on any lookup path":
with no reference-typed field, no `string.Equals` can exist in `Equals` or `GetHashCode`, on any path, in
product code or in a friend assembly. It is stronger than counting comparisons and cannot drift. The first
assertion is satisfied **at exactly 64 B**, not below it (§2.1) — the test states the contract, the layout
note states the ceiling.

### 2.3 Equality and hashing

`Equals` compares, in the same cheapest-first order, **exactly today's field set** with the adapter id
replaced by the slot:

```
Protocol → AddressFamily → Origin → OriginAdapterSlot → OriginAdapterGeneration →
LocalPort → RemotePort → LocalLow/High → RemoteLow/High →
LocalScopeId → RemoteScopeId
```

The generation is compared as it is today (`Domain.cs:106`), so a key rebuilt from a later adapter
enumeration is still unequal to the stored one, and the flow is served through the origin-agnostic transport
index exactly as before. The slot narrows the comparison; it does not change which keys compare equal.

`GetHashCode` keeps delegating to the shared `FlowHash` expression, now with a packed entry point. **The
hashed field set is unchanged and contains neither the slot nor the generation** — the hash stays
transport-only so origin variants keep sharing a `_states` bucket (`Domain.cs:92-98`), and
`TransportTuple.GetHashCode` keeps delegating to the same expression (`FlowTable.cs:484`), which the packed
entry point must preserve:

```csharp
internal static class FlowHash
{
    internal static int Combine(AddressFamilyKind family, TransportProtocol protocol, Endpoint local, Endpoint remote)
        => CombinePacked((byte)family, (byte)protocol,
            (ulong)local.Address.Bits, (ulong)(local.Address.Bits >> 64), local.Port,
            (ulong)remote.Address.Bits, (ulong)(remote.Address.Bits >> 64), remote.Port);

    internal static int CombinePacked(byte family, byte protocol,
        ulong localLow, ulong localHigh, ushort localPort,
        ulong remoteLow, ulong remoteHigh, ushort remotePort)
        => HashCode.Combine(localLow, localHigh, remoteLow, remoteHigh, localPort, remotePort, family, protocol);
}
```

`(ulong)Bits` and `(ulong)(Bits >> 64)` **are** the packed halves, so `Combine` and `CombinePacked` are the
same eight values in the same order — the identity is by construction, and a randomized test
(`PackedAndMaterializedHashesAgree`, over IPv4/IPv6 keys and both orientations) pins it against future
drift. Scope ids are deliberately *not* hashed, exactly as today (`Domain.cs:129-137`).

`CombineCanonical` keeps its total order but gains a packed twin whose ordering must match `OrdersBefore`
(`Domain.cs:155-159`, which compares `UInt128.CompareTo`, i.e. **high half first, then low, then port**):

```csharp
internal static int CombineCanonicalPacked(byte family, byte protocol,
    ulong firstLow, ulong firstHigh, ushort firstPort,
    ulong secondLow, ulong secondHigh, ushort secondPort)
{
    var firstBefore = firstHigh != secondHigh ? firstHigh < secondHigh
                    : firstLow  != secondLow  ? firstLow  < secondLow
                    : firstPort <= secondPort;
    return firstBefore
        ? CombinePacked(family, protocol, firstLow, firstHigh, firstPort, secondLow, secondHigh, secondPort)
        : CombinePacked(family, protocol, secondLow, secondHigh, secondPort, firstLow, firstHigh, firstPort);
}
```

`CanonicalSlotIsOrderIndependent` (randomized: a key and its `Reverse()` select the same slot for IPv4 and
IPv6), `PackedAndMaterializedHashesAgree` and `PackedAndMaterializedCanonicalHashesAgree` are the facts that
keep F2's slot function honest. Their corpora are specified, because a weak corpus passes while the packing
is wrong (§2.5): every hashing corpus must contain

- a key whose **high-half order differs from its low-half order** (the `OrdersBefore` drift detector — with
  low-half-first ordering the two entry points still agree on a symmetric corpus, but the canonical slot
  stops matching `CombineCanonical`);
- the **equal-endpoint tie** (`Local == Remote`, which exercises `OrdersBefore`'s `<=` arm);
- both families, both orientations, and at least one IPv6 key with a **nonzero `ScopeId`**.

### 2.4 `Local`/`Remote` are computed properties, and what that costs

```csharp
public Endpoint Local => Endpoint.From(new IPAddressValue(new UInt128(_localHigh, _localLow), Family, _localScopeId), _localPort);
```

Consumers and their frequency: policy matching (`Policy.cs:22`, claim), `SelfTrafficRegistry.SelfTrafficKey.From`
(`:70`, **per warm packet**), `FlowDispatcher.IsReverseOf` (`:376-380`, **per warm UDP proxy packet**),
`TcpRedirectAssociation`'s constructor (`TcpRedirectTable.cs:33-42`, claim), `ClientResetInjector` (`:52-53`,
cold), logging (cold).

**Two hot consumers do get worse if left alone**, and the design names them rather than claiming otherwise:

- `SelfTrafficKey.From(context)` (`SelfTrafficRegistry.cs:70`) builds a `(TransportProtocol, Endpoint,
  Endpoint)` value. That already copies two `Endpoint`s today, so the change is **neutral** — it reads
  `Key.Local`/`Remote` exactly as before.
- `FlowDispatcher.IsReverseOf(stored, observed)` (`:376-380`) today compares four `Endpoint` *fields*; with
  computed properties it would materialize **four** `Endpoint`s (192 B) per call on the warm UDP proxy path.
  That is a genuine regression if implemented literally.

  Resolution: `FlowKey` gains a **packed** `public bool IsReverseOf(FlowKey other)` (family, protocol, ports,
  both address halves, both scopes — no materialization) and `FlowDispatcher.IsReverseOf` delegates to it.
  That is one small public member on a public type, needs no new friend-assembly edge, and is asserted by
  `IsReverseOfMatchesTheEndpointComparison` against the materialized form. `DispatcherBenchmarks`'
  `WarmProxyProductionAsync`/`WarmProxyDisabledTraceAsync` rows are the series that would show an
  accidental materialization (§11).

`FlowKey`'s packed primitives (`LocalLow`, `LocalHigh`, `LocalPort`, `LocalScopeId`, `Remote*`, `AdapterSlot`,
`AdapterGeneration`) are reachable from `WinForward.Core` (`FlowTable.SlotOf`, `TransportTuple.From`,
`FlowKey.GetHashCode`) and, for `IsTcpSyn`-style consumers, need no cross-assembly access at all. Where
`WinForward.Runtime` needs a packed read it uses the public `IsReverseOf`; nothing in Runtime reads the
individual halves, so **no new `InternalsVisibleTo` edge is added** (`WinForward.Core.csproj:8-9` friends
`Core.Tests` and `Benchmarks` only, and this task keeps it that way).

### 2.5 The F2 re-proof (the invariant that must be re-established, not recompiled)

F2's contracts are `hot-path.md:1277-1363`; `research/implementation-notes.md` §6 enumerates the five
load-bearing pieces. Each is re-established as follows.

| F2 piece | what changes | how it is re-proven |
|---|---|---|
| `FlowTable.SlotOf` = `FlowHash.CombineCanonical(key.AddressFamily, key.Protocol, key.Local, key.Remote) & mask` (`FlowTable.cs:209-210`) | the call becomes the packed twin; `key.AddressFamily`/`key.Protocol` are unchanged in meaning | `PackedAndMaterializedCanonicalHashesAgree` + `CanonicalSlotIsOrderIndependent` over the corpus of §2.3; the existing `WarmCacheHitServesTheValidatedView` and `FlowTableCollidingFlowsFallBackToTheGatedPath` stay green untouched |
| `TryResolveWarm`'s validation chain (`:183-195`): slot read → `TrySnapshot` → `Matches` | `TransportTuple` becomes packed (two ulong pairs + ports + scopes + family + protocol = **48 B**, no slot and no generation) with the same field set; `Matches` and `Reverse()` keep their shape | `TransportTupleEqualsTheKeysTransportFields` (randomized, both orientations) and `FlowTableTransportTupleIsUniqueAcrossOrigins` (`WarmPathGateTests.cs:274`) unchanged. Note the failure mode is bounded: `Matches` corroborates by **tuple equality**, so packed-hash drift can only cost a warm slot, never a wrong answer |
| "at most one state per transport tuple" (`FlowTable.cs:197-202`, enforced at `:459`) | nothing | unchanged; a new `TwoAdapterSlotsOnTheSameTupleStillAliasThroughTheTransportIndex` fact pins that distinct slots still alias (the origin-agnostic behaviour F2 depends on) |
| `TcpRedirectTable.TryResolveByOriginal`'s full-key compare (`:353`) and slot (`:510`) | the hash gains the packed path; `Equals` compares the **slot and the generation** instead of the string and the generation, so the key's equality semantics are unchanged | the existing redirect warm-path facts (`TcpRedirectWarmPathGateTests`) stay green; a new `OriginalKeyCacheStillValidatesTheWholeKey` asserts a *different-slot* key on the same tuple is not served from a populated original slot, and `FlowKeyEqualityKeepsTheGeneration` pins the retained generation compare |
| `FlowTableWarmResolveAllocatesNoManagedBytes` (`WarmPathGateTests.cs:175`) | the packed path must not allocate (no `Endpoint` boxing, no closure) | the gate re-runs **unchanged** and is this task's acceptance gate for the key change |
| `TransportTuple.GetHashCode` delegates to the one shared expression (`FlowTable.cs:480-484`) | the expression gains a packed entry point | `Combine` delegates to `CombinePacked` (one body), so "one shared expression" is a construction property, not a convention |

**The packing itself must round-trip.** `PackedAndMaterializedHashesAgree` compares the packed hash against
the hash of the *unpacked* endpoints, so a self-consistent low/high swap, or a scope id written to the wrong
half, passes every fact above while `Policy` (`Policy.cs:22`), `SelfTrafficKey.From` (`:70`), `IsReverseOf`
(`FlowDispatcher.cs:376-380`), the association constructor (`TcpRedirectTable.cs:33-42`) and every log line
silently change. The PRD's criterion 1 therefore names two additional exact facts, and they are the ones a
reviewer should attack first:

- `FlowKeyPackedRoundTripsEndpoints` — for IPv4, IPv6, and IPv6 with a **nonzero `ScopeId`**:
  `key.Local == source && key.Remote == destination` (and `Address.Family`, `Address.ScopeId`, `Port` each
  equal), i.e. pack → unpack is the identity.
- `ReverseSwapsEndpointsAndScopes` — `key.Reverse()` yields `Local == original.Remote`,
  `Remote == original.Local`, with both scope ids travelling with their endpoints (and, for the canonical
  slot, still selecting the same slot).

A key-shape change that merely compiles would leave (a) the slot function's order-independence unproven under
the new ordering code, (b) the hash-equality identity asserted nowhere, and (c) the packing itself
unverified; all three are named facts above.

## 3. The interned adapter slot table

### 3.1 Shape and lifetime

```csharp
// src/WinForward.Core/AdapterSlotTable.cs
public sealed class AdapterSlotTable
{
    public const ushort NoSlot = 0;                   // the UDP relay-alias key and adapter-less test keys

    public bool TryIntern(string stableId, long generation, string? friendlyName, out ushort slot);  // idempotent per stableId
    public void Observe(ushort slot, long generation, string? friendlyName);        // refresh, called per enumeration
    public bool TryResolve(ushort slot, out AdapterMetadata metadata);              // slot → strings, one array index
    public int CountForDiagnostics { get; }
    public bool ExhaustedForDiagnostics { get; }
}

public sealed class AdapterMetadata          // immutable; Observe publishes a REPLACEMENT instance
{
    public ushort Slot { get; }
    public string StableId { get; }
    public string FriendlyName { get; }
    public long Generation { get; }          // the generation of the last Observe
}
```

- **Ownership and lifetime**: created **once** in `DurableCaptureBundle` (composition), process lifetime,
  injected into the durable `CapturePacketProcessor` (`Program.cs:284`) alongside the dispatcher. It is
  deliberately *not* owned by the capture generation (`NdisCaptureGenerationFactory`), because a key must
  stay meaningful across an adapter-list refresh (`AdapterListWatcher`); the durable processor is the one
  object that already outlives every generation (`NdisCaptureGeneration.cs:72`).
- **Interning point**: once per `WindowsAdapter` instance, i.e. per adapter per enumeration — never per
  packet. `MultiAdapterCaptureLoop` resolves each adapter's slot when the generation is built
  (`NdisCaptureGeneration.cs:97-108`) and the loop passes the `(WindowsAdapter, slot)` pair into
  `CapturePacketProcessor.ProcessAsync`. `PacketFlowClassifier.ClassifyFlow` receives the slot **and the
  adapter's `Generation`** and builds the key with them: no lookup, no dictionary, no string.
- **Refresh**: `Observe(slot, generation, friendlyName)` **publishes a new `AdapterMetadata` instance** into
  the slot array with a `Volatile.Write`; the old instance is never mutated, so a reader that already holds
  it keeps a stable triple. `AdapterMetadata`'s properties are get-only for exactly that reason. The
  **slot value never changes for a given `StableId`**, so keys minted before a refresh keep resolving to the
  same flow.
- **Unregistered adapter**: `TryResolve` returns false and `ClassifyFlow` keeps `NoSlot`; policy matching for
  an adapter-qualified rule then sees `AdapterId == null` exactly as it would for an adapter-less key today.
  The table is *not* the place that decides traffic fate. `NoSlot` is unreachable from a real capture-scope
  adapter — see §3.3.

### 3.2 Slot allocation and the ABA argument

Slots are allocated **monotonically and never reused** (`1, 2, 3, …`). The reason is the interning ABA: if a
retired slot were reissued to a different adapter, an old `FlowKey` still resident in `_states`,
`_byOriginal`, a tombstone, a cooldown or the F2 warm cache would compare **equal** to a key for a different
adapter — a wrong-flow decision, i.e. fail-open routing. Monotone allocation makes that impossible by
construction: a slot value identifies exactly one adapter identity for the whole process.

The cost is a ceiling: 65,535 interning identities per process (slot 0 is `NoSlot`). Since a slot is minted
per *distinct `StableId`*, not per enumeration or per generation, the practical bound is "adapters this
process has ever seen" — a handful on a workstation, tens on a gateway. The generation is **not** part of the
interned identity: it stays a key field (§3.4), which is what keeps the table bounded by the adapter count.

### 3.3 Exhaustion and retirement, fail-closed

- **Retirement** marks the metadata's generation as stale but keeps the slot resolvable and keeps the
  `StableId`. A flow on a removed adapter must still resolve (its traffic is a transient), and a stale key
  must never become equal to a new one.
- **Exhaustion** (`TryIntern` would need slot 65,536): the call returns `false`. **The adapter is refused at
  the composition point, not absorbed into the key space.** `MultiAdapterCaptureLoop`'s generation build
  excludes an adapter it cannot intern, logs one rate-limited `adapter.slot-exhausted` error, and the
  adapter never enters the capture scope, so **no captured packet ever carries `NoSlot` for a real
  adapter**.
  This replaces the earlier "collapse onto `NoSlot`" sketch, which was **fail-open and is rejected**: two
  different adapters' keys would compare equal whenever their 5-tuples match (host and forwarded traffic on
  the same VM pair — precisely the multi-VM case `OriginAdapterId` exists for), and the second flow would
  resolve the first's state. Refusing the adapter instead keeps "no identity is guessed" true at the cost of
  not capturing on an adapter this process could not name, which is the safe direction and is bounded by the
  65,535-identity ceiling being unreachable in practice.
  Pinned by `AnAdapterThatCannotBeInternedIsRefusedNotAliased`: with the slot space forced full, the
  composition refuses the adapter and the dispatcher never observes a `NoSlot` key for it.
- **`NoSlot` semantics**: `NoSlot` is a real, compare-equal value (two adapter-less keys are equal), so the
  UDP relay-alias keys keep aliasing exactly as today. Its only producers are the adapter-less
  `FlowKey.Create` overloads (`UdpSessionSetup.cs:103-107` and tests), never `CapturePacketProcessor`.

### 3.4 `OriginAdapterGeneration` stays a key field (operator decision)

The PRD's Notes settle this: **only the stable id is interned, the generation stays a key field with integer
equality**, and the design's earlier alternative (intern the `(StableId, Generation)` pair and drop the
generation from `Equals`) is **rejected** because it weakens the adapter-recreation semantics the field
exists for. `WindowsAdapterInventory.GetCurrentAdapters` bumps the generation for every adapter on every
enumeration (`AdapterIdentity.cs:46`), so interning the pair would mint a new slot per adapter per refresh —
unbounded growth — while interning the id alone keeps the table bounded by the adapter count (§3.2).

The key's generation is **the parsing enumeration's `WindowsAdapter.Generation`**, carried straight from
`PacketFlowClassifier`'s `adapter.Generation` (`PacketFlowClassifier.cs:21`) into the packed field, exactly
as today. Equality compares slot **and** generation, so the observable semantics are unchanged:

1. `FlowTable.TryResolveLocked` (`FlowTable.cs:442-454`) probes `_states[key]` and then, origin-agnostically,
   `_transportIndex[tuple]`. A key built after a refresh has a new generation and therefore still misses
   `_states[kind+slot+generation]` and is served by the transport index, exactly as it is today.
2. The cost of the retained field is 8 bytes (the layout's `long`), which is why the key lands at 64 B rather
   than 56 (§2.1). That is the size budget the operator accepted.
3. The only *new* thing about the field is that it now tracks the same enumeration its slot came from; a
   captured packet's key can never mix a slot from one enumeration with another enumeration's generation,
   because both are read from the same `WindowsAdapter` instance at classification.
   `Domain.cs:106` (its own `Equals`), `BenchmarkShared`/`FlowTableBenchmarks` and two test assertions
   (`PacketParsingTests.cs:79`, `WarmPathGateTests.cs:277-278`). The live generation is available from the
   side table for logs and diagnostics.

### 3.5 Construction API and the test surface

```csharp
public static FlowKey Create(Endpoint local, Endpoint remote, TransportProtocol protocol, FlowOriginKind origin);                       // NoSlot, generation 0
public static FlowKey Create(Endpoint local, Endpoint remote, TransportProtocol protocol, FlowOriginKind origin, ushort adapterSlot, long adapterGeneration);
public static FlowKey Create(Endpoint local, Endpoint remote, TransportProtocol protocol, FlowOriginKind origin, AdapterSlotTable table, string? stableId, long adapterGeneration);  // cold/tests
```

The generation is an explicit parameter on the adapter-carrying overloads (the operator's decision keeps it a
key field, §3.4), and `PacketFlowClassifier` passes `adapter.Generation` from the same `WindowsAdapter`
instance it read the slot from. The existing 4-argument overload is preserved, so the majority of the 215
`FlowKey` references across 47
test files compile untouched; the 19 `AdapterContext` sites (10 files) switch to the table overload through
one helper in `tests/WinForward.Core.Tests/TestHelpers/FlowBuilders.cs`. `AdapterContext` is deleted or reduced to
`(ushort Slot, long Generation)` — the implementer picks one at Step 4 and records it; leaving a
`string?`-carrying `AdapterContext` in the tree is forbidden because it re-introduces exactly the field the
requirement removes from the key. `prd.md` is not edited.

### 3.6 The UDP reinjection target map is re-keyed by slot

`UdpAdapterTargetSource` (`UdpAdapterTargetSource.cs:32/:38/:56`) resolves a reinjection target by
`string stableId` from an immutable snapshot, and `UdpResponseReinjector` consults it **per UDP response**
(`UdpResponseReinjector.cs:172`, `:189`) plus in two logging paths (`:252`, `:276`). Once the key carries a
slot, the string is no longer available at the call sites without a table lookup, and the lookup itself is
what should go away.

The map becomes a **slot-indexed array** (`UdpAdapterTarget?[]`), rebuilt on each capture refresh:

- `DurableCaptureBundle.UpdateUdpTargets` (`DurableCaptureBundle.cs:311-323`) already walks exactly the
  currently-enumerated scope and builds `Dictionary<string, UdpAdapterTarget>` keyed by `adapter.StableId`;
  it now writes `targets[slot] = new UdpAdapterTarget(adapter.Adapter.RuntimeHandle, adapter.Mac)` using the
  slot the generation build resolved, and `UdpTargets.Update(host, targets)` publishes the new array with
  one `Volatile.Write` (the snapshot discipline `UdpAdapterTargetSource.cs:50-66` already has).
- `IUdpAdapterTargetSource.Resolve(ushort slot)` becomes an array index plus a null check — no hash, no
  string compare — and `AdapterIds` (used by one diagnostic log, `UdpResponseReinjector.cs:253`) resolves
  each non-null entry's `StableId` through the slot table.
- Semantics are unchanged: an adapter absent from the current refresh has no entry, so a forwarded response
  still drops fail-closed and a host response still takes the rate-limited `scope[0]` fallback
  (`udp-relay.md:14`'s table, unchanged). The slot→target map and the flow key now share one identity
  source, so they cannot disagree about which adapter a flow came from.
- Facts: `UdpTargetMapResolvesBySlot` (slot → the same target the string map returned) and the existing
  `HostFlowResponseInjectsTowardItsOriginAdapter` / `HostFlowWithUnresolvedOriginAdapterUsesFallbackAndWarns`
  / forwarded-response tests, unchanged.

## 4. Parse once: the `PacketView` → `PacketLayout` split

### 4.1 The view gains two fields, the packet carries a 16-byte layout

`PacketView` (`IPTcpUdpPacket.cs:19-27`, 80 B, stack-local) gains:

```csharp
int TransportLength,   // IPv4: totalLength − IHL; IPv6: payloadLength − extension bytes  (both already computed at :71 and :97)
byte TcpFlags,         // frame[transportOffset + 13] for TCP, 0 for UDP   (read once at :123-128)
```

`CapturedFlowPacket` carries a derived, address-free layout (the addresses already live in the key, packed).
It **must carry the family**, or the layout-driven checksum overload cannot choose the IPv4
(`ipOffset + 12` / `+16` address fields) versus IPv6 (`ipOffset + 8` / `+24`) geometry and §4.3's retained
family-equality reject is unperformable — a literal implementation would happily write 16 bytes into an IPv4
frame and return `true`:

```csharp
public readonly record struct PacketLayout(byte Transport, byte TcpFlags, byte Family, int IPHeaderLength, int TransportHeaderLength, int TransportLength)
{
    public static PacketLayout From(in PacketView view) => new((byte)view.Transport, view.TcpFlags, (byte)view.SourceAddress.Family, view.IPHeaderLength, view.TransportHeaderLength, view.TransportLength);
    public int TransportOffset => 14 + IPHeaderLength;
    public int TransportEnd => TransportOffset + TransportLength;
}
```

`3 + 1 pad + 4 + 4 + 4 = 16 B` — the `Family` byte fits the padding the record struct already had, so the
layout stays 16 B and `PacketLayoutFitsSixteenBytes` asserts it (a sixth field would push it to 24).
Non-flow packets (no view) carry `default`, and every consumer checks `Transport` first.

**Why not carry the `PacketView` itself**: it holds two `IPAddressValue`s (32 B each) that the key already
stores, packed, and it would add 80 B to a struct that is copied 2–3 times per packet. The layout adds 16.

### 4.2 What each consumer does

| consumer | today | after |
|---|---|---|
| `TcpFrameRewriter.IsTcpSyn` (`:67-76`) | re-parses, derives the flags offset, reads the byte | `IsTcpSyn(in PacketLayout layout)` = `layout.Transport == (byte)PacketTransport.Tcp && (layout.TcpFlags & 0x02) != 0 && (layout.TcpFlags & 0x10) == 0`. **Compare against the enum, never the literal `1`** — `PacketTransport.Tcp` is `0` and `Udp` is `1` (`IPTcpUdpPacket.cs:7-11`), so a literal `1` silently disables SYN detection entirely; the fact `LayoutSynTestMatchesTheSpanTest` covers both transports and would catch it |
| `TcpSequenceObservation.TryReadTcpSequenceAdvance` (`:52-71`) | re-parses, re-derives the transport length with an etherType branch, reads seq + flags | takes `in PacketLayout`; `advance = layout.TransportLength - layout.TransportHeaderLength` (+SYN, +FIN); reads the 4 sequence bytes at `layout.TransportOffset + 4` guarded by `frame.Length >= layout.TransportOffset + 8` |
| `TcpSequenceObservation.RecordServerSynAck` (`:35-43`) | re-parses per reverse packet | takes `in PacketLayout`; SYN-ACK test from `layout.TcpFlags`, sequence read at the view offset under the same `+ 8` bound |
| `TcpSequenceObservation.RecordClientSyn` (`:18-29`) | re-parses (cold) | takes `in PacketLayout` (cold; consistency, not speed) |
| `PacketChecksums.TryRewriteTcpEndpoints` (`:46-56`) | full inline revalidation + the IPv6 extension-header walk | **new overload** taking `in PacketLayout`; skips version/IHL/protocol/fragment/length revalidation, the `dataOffset` bound and `TryFindIpv6Transport`; keeps only what the layout cannot prove — `layout.Transport == (byte)PacketTransport.Tcp`, `frame.Length >= layout.TransportEnd`, and the **family** check (the argument addresses' family must equal `layout.Family`, because the geometry written depends on it and the addresses come from the association, not the frame) |

### 4.3 What the view proves, and what the consumers still check

The parse (`IPTcpUdpPacket.cs:43-129`) already proved: Ethernet II framing, IP version, header length in
range, TCP protocol, no IPv4 fragmentation, no IPv6 extension chain beyond the bound, `totalLength` /
`payloadLength` inside the frame, TCP header present, and `20 <= dataOffset <= availableLength`. The
consumers therefore do **not** repeat any of it. What they still check is only what the *span* demands:

- `frame.Length >= layout.TransportOffset + 8` — the **only** frame read the layout-driven consumers make,
  and only where a 4-byte sequence number at `TransportOffset + 4` is needed
  (`TryReadTcpSequenceAdvance`, `RecordServerSynAck`, `RecordClientSyn`). The bound covers the whole read,
  not just its start: `+ 4` would let `Slice(offset + 4, 4)` throw instead of returning `false`, which is
  the reject-not-throw contract the rewriter family carries. `IsTcpSyn` needs no frame access at all once
  `TcpFlags` is in the layout;
- `frame.Length >= layout.TransportEnd` in the rewriter — the same one-check rule the PRD states ("no path
  re-validates what the view already proves" does not mean "no bounds checks"), and it is the *only* frame
  check the layout overload makes besides `Transport == Tcp`;
- the **family** check in the rewriter (`sourceAddress.Family == destinationAddress.Family == layout.Family`)
  — it selects the IPv4 or IPv6 write geometry, and the addresses to write come from the association, so the
  view says nothing about them;
- nothing else: the version, IHL, protocol byte, fragment bits, total length, extension chain,
  `tcpLength >= 20` and the `dataOffset` bound are the view's proofs and are **not** repeated.

⚠️ **Frame identity.** The layout describes the *captured* frame. The redirect lanes stage a copy
(`Injections.cs:249`, `:253`) and the rewrite is same-length (endpoints + checksums only), so geometry is
preserved; the sequence observations must still run **before** `TryRewriteForwardLeg` / the reverse
`TryRewriteTcpEndpoints` (they read the pre-rewrite sequence — `tcp-local-redirect.md:87`). Both are already
the case today (`Injections.cs:256` then `:259`; `:321-322` then `:323`) and the order is unchanged.

### 4.4 The counter and the exact proof

Diagnostics-only, F2's `GateHoldProbe` shape (`FlowTable.cs:129-136`: read once into a local, plain null
check, null in production) — but **thread-scoped**, because a process-wide static would be unsound here:

```csharp
// src/WinForward.Protocols/PacketPathProbe.cs — Protocols already friend-lists Core.Tests
// (`WinForward.Protocols.csproj`), so the tests reach `internal` probes with no new InternalsVisibleTo.
internal static class PacketPathProbe
{
    [ThreadStatic] private static Action? t_parseWalk;      // the IDE1006 t_ marker + localized pragma, as PacketLease does
    internal static Action? ParseWalk { get => t_parseWalk; set => t_parseWalk = value; }
    // RevalidateWalk and ViewRewrite have the same shape.
}
```

**Why thread-scoped.** This test project has **no `xunit.runner.json` and no `[CollectionBehavior]`** (verified
repository-wide), so xUnit runs test *collections* in parallel by default. A process-wide counter attached to
`IPTcpUdpPacket.TryParse` would be inflated by every sibling parser test running concurrently, and the
assertion would flake in the same family as the recorded host-lump gates. A `[ThreadStatic]` probe attaches
and detaches on the driving thread only; the fact asserts the counts on that thread and that
`Environment.CurrentManagedThreadId` is unchanged across the drive (the window-contract discipline of
`hot-path.md:989-994`). Serialising the assembly (`CollectionBehavior(DisableTestParallelization = true)`)
would also work but taxes the whole suite for one fact; it is the fallback if a future async drive migrates
threads mid-packet, and the fact's thread check makes that failure loud rather than silent.

**The harness must start at the processor.** The processor's `TryParse` is the first of today's four walks, so
a harness that starts at the coordinator cannot record the red: `HotPathAllocationGateTests` drives
`HandlePacketAsync`/`HandleReverseAsync` directly (`:41`, `:104`) and `TcpRedirectInjectionBatchingTests`
drives `HandlePacketAsync`, so neither sees walk #1. The fact therefore composes
`CapturePacketProcessor.ProcessAsync` → `FlowDispatcher` (Proxy policy resolving to a known server) → the
real `TcpProxyCoordinator` wired as both `reverseHandler` and executor, with a pump-owned
`NdisPacketBuffer` — the `ProcessAsync` front end of `FlowDispatcherExecutorTests.cs:296` and
`BatchedPassReinjectionE2eTests.cs:35-43`, plus the coordinator fixture of `HotPathAllocationGateTests:41`
(forward, mid-flow with a claimed association) and `:104` (reverse SYN-ACK).

Facts:

- `RedirectedForwardPacketWalksHeadersExactlyOnce`: N warm mid-flow packets through
  `CapturePacketProcessor.ProcessAsync` ⇒ `ParseWalk == N`, `RevalidateWalk == 0`, `ViewRewrite == N`
  **on the driving thread**.
- `RedirectedReversePacketWalksHeadersExactlyOnce`: N warm reverse packets through the same entry ⇒ the same
  counts. Recorded per-leg before-counts from that harness, red on the unmodified tree:
  **forward 4** (processor, `IsTcpSyn`, `TrackClientSequence`, checksum revalidation) and **reverse 4**
  (processor, `RecordServerSynAck`, `TrackServerSequence`, checksum revalidation).
- `WorkCountsOnTheDrivingThreadOnly`: a sibling thread drives an unrelated parse while the probe is attached
  and the driving thread's counts are unaffected (pins the thread scoping itself; red against a
  process-wide probe).

### 4.5 Malformed and truncated frames

The rejection path is unchanged by construction: the layout is produced only from a successful `TryParse`,
and a packet without one is dispatched through `DispatchNonFlowAsync` (`CapturePacketProcessor.cs:69-71`)
exactly as today. The acceptance criterion is discharged by the *existing* parser test corpus
(`PacketParsingTests`, `ProtocolAuditTests`) being extended with a property-style table: for every
truncation of an IPv4 and an IPv6 TCP frame (each length from 0 to the full frame, plus the extension-header
variants), `TryParse` returns the same result as before the change and `PacketLayout` is produced only when
it returns true. `ViewDrivenRewriterRejectsWhatTheSpanRewriterRejects` drives both rewriter entry points over
the same corpus and asserts identical `bool` results and byte-identical output for the accepted ones.

## 5. The atomic sequence tracker

### 5.1 Representation

```csharp
private long _clientNextSeq = Unobserved;    // -1
private long _serverNextSeq = Unobserved;

public uint? ClientNextSeq { get { var value = Volatile.Read(ref _clientNextSeq); return value < 0 ? null : (uint)value; } }
```

`_sequenceGate` is deleted. `ObserveClientSequence` no longer takes a lock, and the association no longer
allocates a `System.Threading.Lock`.

### 5.2 The CAS-max protocol

```csharp
internal void ObserveClientSequence(uint sequenceNext)
{
    while (true)
    {
        var current = Volatile.Read(ref _clientNextSeq);
        if (current >= 0 && !IsSequenceAhead(sequenceNext, (uint)current)) return;
        if (Interlocked.CompareExchange(ref _clientNextSeq, sequenceNext, current) == current) return;
    }
}
```

- `current < 0` (unobserved) always writes — today's "first observation wins" (`TcpRedirectTable.cs:141`
  `is not { } current` short-circuit).
- Otherwise the loop applies the **same predicate the lock applied**, `IsSequenceAhead`
  (`:156`, RFC 793-style wrap-aware comparison), against a freshly read `current`. Two racing observations
  serialise on the CAS: the loser re-reads and re-tests, which is exactly what the lock provided.
- Storing the `uint` in a `long` keeps `0xFFFFFFFF` a legal tracked value, so `-1` is unreachable from real
  data (the `-1 = unobserved` sentinel cannot collide).

### 5.3 Memory ordering

`Interlocked.CompareExchange` is a full barrier on every supported platform, and `Volatile.Read` is an
acquire read; the tracker is a **single aligned 64-bit word**, so no reader can observe a torn value. The
reader's only requirement is that the value it sees was written by some completed observation — which
`Interlocked` provides. The cold readers (`ClientResetInjector.cs:46-47`) tolerate a weakly-consistent value
by construction: the RST's ack/seq stay in the client's window under *any* observed prefix of the advance
sequence (the value only ever moves forward within the comparison window), which is why the PRD
(`prd.md:37-38`) calls the RST builders cold and tolerant. No ordering between the two trackers is required
— they are independent words.

### 5.4 The exact proof

- `RedirectPacketTakesZeroSequenceGateEntries` — red **2** before, **0** after, counted by a diagnostics-only
  probe on the association (`SequenceGateEntryProbe`, attached like F2's `GateHoldProbe`). The probe is
  landed in Step 1 (before the change), records the 2-per-packet count, and is **deleted with the gate** in
  the step that removes it; the artifact keeps the red text and the command.
- `TcpRedirectAssociationHoldsNoLockField` — structural: no instance field of a reference type on the
  association.
- `ConcurrentSequenceObservationsKeepTheLargerValue` — two threads advancing the same leg with
  barrier-released observation bursts; the final value equals the wrap-aware maximum of all observed values,
  and the reader never observes a decrease. Modelled on the repository's concurrency-test rule
  (`quality-guidelines.md:41`: genuinely overlapping tasks).
- The RST suite (`ClientResetInjector`/`TcpResetBuilderTests`, `TcpRelayEndResetTests`) stays green
  untouched, and `TcpProxyCoordinatorRewriteTests`' `ClientNextSeq == ISN + 1 + payloadLen` assertion
  (`:402`) is the behavioural pin for the SYN-with-payload case (`tcp-local-redirect.md:49`).

## 6. The IPv6 reverse-leg delta (optional commit, evidence-gated)

**What the recorded 2.0× actually contains.** The `ReverseLegForwarded` row
(`TcpRedirectDataPathBenchmarks.cs:99-104`) is `memcpy + TrackServerSequence + TryRewriteTcpEndpoints`. It has
**no key and no dictionary probe**, and the planning measurement that called the parse "family-neutral and
therefore not the cause" missed one term: `TrackServerSequence` takes `_sequenceGate`, measured at
**22.9–29.0 ns** uncontended on this host, and it is paid by both legs. So the row decomposes roughly as
`copy (≈23) + lock (≈25) + rewriter (IPv4 ≈20 / IPv6 ≈68)`, and **Step 2 (the atomic trackers) removes a
term from the row before any checksum work is considered**.

The PRD now settles the criterion: acceptance is **per-leg no-regression**, and the improvement figure and the
IPv6:IPv4 ratio are **readings, not gates** (this host's noise floor makes a 10 % delta unmeasurable —
`hot-path.md` contract 9). Accordingly:

- **Re-take the series after Steps 2–5 land**, with the mechanism named per step: Step 2 removes two lock
  entries per packet (both legs), Step 3 removes three header walks (both legs), Steps 4–5 shrink the
  per-packet copies. The IPv6 reverse row is expected to move on Step 2 alone; the planning claim that
  "requirements 1–4 cannot move it" was wrong, and this section replaces it.
- **Step 6** (the single-pass, wide-load IPv6 address delta below) is a genuinely **optional commit**, not a
  Step-0 decision: it lands only if the post-Step-5 re-run shows a win on the IPv6 reverse rows with every
  other row unmoved and 0 B everywhere, and it is dropped otherwise with the reading recorded either way.

**The mechanism, if adopted.** Replace the IPv6 address pre-image construction with a single pass that reads
each old word directly instead of snapshotting 32 bytes onto the stack and walking them twice
(`PacketChecksums.cs:157-173`), and read the 32 bytes as four `ReadUInt64BigEndian` loads whose 16-bit halves
are extracted by shift. The arithmetic (`acc += (ushort)(old ^ 0xFFFF) + @new` per word, folded once at the
end) is **identical integer arithmetic**, proven by the probe at 2,000,000 random 32-byte pairs with **0
mismatches**, and worth 22.1–22.6 vs 27.6–28.3 ns on the isolated delta. The larger half — deleting the
`stackalloc` snapshot and its second pass — cannot be measured without editing product code, which is why the
step is evidence-gated rather than assumed.

**Proof obligations if it lands** (unchanged semantics, per `quality-guidelines.md:14`, `:43`):
`ViewRewrittenChecksumsMatchTheFullRecomputeOracle` (the existing
`TryRewriteTcpEndpointsFullRecompute` twin, now also parametrized by family), the mutable-offset assertion
(only the endpoint/checksum bytes change), and `Ipv6AddressDeltaMatchesTheNarrowReference` (the 2,000,000-pair
randomized equivalence, landed as a test at a smaller but still decisive N). `prd.md` is not edited either
way; the outcome is recorded in `implement.md`'s session record and the artifact README.

## 7. The slim per-packet context

```csharp
public readonly record struct FlowContext(FlowKey Key, AdapterMetadata? Adapter, ProcessMetadata? Process)
{
    public string? AdapterId => Adapter?.StableId;
    public string? AdapterName => Adapter?.FriendlyName;
    public string? ProcessName => Process?.ProcessName;
    public string? ProcessPath => Process?.ProcessPath;
    public ushort RemotePort => Key.RemotePort;          // packed accessor, no Endpoint materialization
}
```

- **`AdapterMetadata`** is the slot table's own per-slot instance: fetched by slot at classification with a
  **plain array index** (`_slots[slot]`, no dictionary, no string, no allocation) and attached to the
  context, so the cold consumers that read `Context.AdapterId` today (`NdisPacketActionExecutor.cs:121`) keep
  working on every packet, warm or not. `Observe` publishes a *replacement* instance (§3.1), so an attached
  reference is always a coherent triple.
- **`ProcessMetadata`** is a two-string record created at claim time in `AttributeProcessAsync`
  (`FlowDispatcher.cs:281-291`), **once per flow**, only when attribution produced an identity. The claim
  path already allocates inside the attributor (`IProcessAttributor.FindAsync`), so this adds no new class of
  allocation to a gated path, and no 0 B gate drives the attributed claim path (the dispatcher gate's first
  claim runs before the measured window, `HotPathAllocationGateTests.cs:361-363`; the flow-table claim gate
  drives `FlowTable.TryClaimResolved` directly with a pre-built context, `:418`).
- **`RemotePort`** becomes `Key.RemotePort` (all three construction sites passed exactly that,
  `PacketFlowClassifier.cs:27`, `:40`).
- **Warm packets keep `Process == null`**, exactly as today (the classifier never sets a process identity and
  warm hits do not re-attribute), so trace logs are unchanged.

Measured effect (with the generation kept in the key, so the key is 64 B): `FlowContext` 168 → **80 B**,
`CapturedFlowPacket` 224 → **152 B** (including the 16-byte `PacketLayout`), `FlowStateView` 160 → **96 B**.
With the 2–3 `packet with { … }` copies per packet (`FlowDispatcher.cs:188`, `:194`, `:218`, `:246`) plus the
160-byte view copy in `TryResolveWarm`, the warm path copies roughly 350 B less per packet.

## 8. Contracts

### Public — unchanged

`FlowKey.Create`'s 4-argument overload, `FlowKey.AddressFamily`/`Protocol`/`Local`/`Remote`/`Origin`,
`FlowKey.Reverse()`, `FlowContext`'s property names (`AdapterId`, `AdapterName`, `ProcessName`,
`ProcessPath`, `RemotePort`), `CapturedFlowPacket`'s property names and the `with`-expression surface,
`TcpRedirectAssociation.ClientNextSeq`/`ServerNextSeq` (still `uint?`),
`TcpFrameRewriter.IsTcpSyn(ReadOnlySpan<byte>)`, `TcpFrameRewriter.TryRewriteForwardLeg`,
`PacketChecksums.TryRewriteTcpEndpoints(Span<byte>, IPAddressValue, ushort, IPAddressValue, ushort)` and its
`IPAddress` overload, `IPTcpUdpPacket.TryParse`.

### Changed

- `FlowKey`'s field set and size (128 → **64 B**); `OriginAdapterId` leaves the key, `OriginAdapterSlot`
  (ushort) arrives, `OriginAdapterGeneration` **stays**; `Local`/`Remote` become computed and
  `FlowKey.IsReverseOf(FlowKey)` is added.
- `PacketView` gains `TransportLength` and `TcpFlags` (80 → 88 B, stack-local); `PacketLayout` gains `Family`
  (still 16 B).
- `CapturedFlowPacket` gains `PacketLayout Layout` (16 B) and its `FlowContext` shrinks.
- `TcpSequenceObservation`'s and `TcpFrameRewriter`'s per-packet entries take `in PacketLayout`;
  `PacketChecksums` gains the layout overload (the span overload keeps its validation and stays the oracle
  for callers without a layout — `FrameRewriterBenchmarks`, `BenchmarkFrameBuilderTests`).
- `AdapterSlotTable` is new public surface in `WinForward.Core`; `AdapterContext` is deleted or reduced to
  `(ushort Slot, long Generation)`.
- `IUdpAdapterTargetSource.Resolve(ushort)` replaces `Resolve(string)` (§3.6) and `AdapterIds` resolves
  through the slot table.
- `DurableCaptureBundle` creates the slot table and threads it into the processor; `MultiAdapterCaptureLoop`
  passes the resolved slot per adapter and refuses an adapter it cannot intern (§3.3).

### New internal members (diagnostics-only, null in production)

- `PacketPathProbe.ParseWalk` / `RevalidateWalk` / `ViewRewrite` (`[ThreadStatic]`, §4.4).
- `AdapterSlotTable.CountForDiagnostics`, `AdapterSlotTable.ExhaustedForDiagnostics`.
- `FlowKey`'s packed accessors (Core-internal; Runtime uses the public `IsReverseOf`).
- `PacketLayout.From(in PacketView)`.

### Accepted semantic deltas

1. **No generation delta.** `OriginAdapterGeneration` keeps its key role (operator decision, §3.4): a key
   rebuilt after a capture refresh compares unequal to the stored one exactly as today, and is served by the
   transport index.
2. **`FlowContext`/`CapturedFlowPacket` equality is not a product behaviour.** Both are record structs, so
   their generated `Equals` now compares two metadata references instead of four strings — but nothing in
   `src/` compares either by equality (repository-wide, the only `Equals`/`==` uses are on `FlowKey`,
   `Endpoint` and `FlowDecision`). The change is observable only in hand-written test comparisons, and a
   rebuilt context for the same adapter still compares equal because the metadata instance is the slot
   table's singleton (or a fresh `AdapterMetadata` for the *same* slot — the record's own value equality
   applies to the metadata too, so equality is by `(Slot, StableId, FriendlyName, Generation)`).
3. **`FlowKey.Create(…, AdapterSlotTable, …)` on an unregistered adapter yields `NoSlot`** — that overload is
   for cold edges and tests only; capture-scope adapters are interned at generation build and an adapter that
   cannot be interned is refused rather than keyed (§3.3), so no captured packet carries `NoSlot`. The
   adapter-less keys alias through the transport index exactly as origin-variant keys do today.

## 9. Semantics and risk table

| # | Condition | What can go wrong | Bound / control |
|---|---|---|---|
| 1 | A retired slot is reissued | old keys compare equal to a different adapter's key → wrong-flow decision | impossible by construction: slots are monotone and never reused (§3.2); `SlotValuesAreNeverReused` asserts it |
| 2 | Slot exhaustion | an alias between two adapters' keys when their 5-tuples match — the multi-VM case `OriginAdapterId` exists for | **refused at composition, not absorbed** (§3.3): the adapter is excluded from the capture scope and logged, so no captured packet carries `NoSlot`; `AnAdapterThatCannotBeInternedIsRefusedNotAliased` pins it |
| 3 | An adapter refresh changes identity | keys minted before the refresh stop resolving | the intern key is the GUID-primary `StableId` (`windows-ndisapi.md:25`) and the slot is stable across refreshes; `AdapterSlotSurvivesARefresh` pins it. The generation still moves with each refresh, exactly as today (§3.4) |
| 4 | `AdapterSlotTable` reached per packet | a dictionary probe per packet, slower than the string compare it replaced | interning happens once per adapter per enumeration (§3.1); a per-packet `Intern` cannot compile because the classifier takes a `ushort`, and the parse-layout gate would not catch it — named as a review item |
| 5 | Packed hash diverges from the materialized hash | F2's slot function and the dictionaries' buckets drift | one expression, two entry points (§2.3) + `PackedAndMaterializedHashesAgree` + `PackedAndMaterializedCanonicalHashesAgree` over the specified corpus. The failure mode is bounded: `Matches` corroborates by **tuple equality**, so drift can only cost a warm slot, never a wrong answer |
| 5b | The packing itself is wrong (low/high swap, scope id misplaced) | every hash fact still passes while `Policy`, `SelfTrafficKey.From`, `IsReverseOf`, the association constructor and the logs change | `FlowKeyPackedRoundTripsEndpoints` (IPv4 / IPv6 / IPv6 + nonzero scope) and `ReverseSwapsEndpointsAndScopes` (§2.5) — the facts the PRD's criterion 1 now names |
| 6 | Canonical ordering changes | forward and reverse packets select different slots → the warm cache misses for reverse traffic (perf), and `Matches`' orientation form still resolves it (correctness preserved) | `CanonicalSlotIsOrderIndependent` over both families, plus the corpus's high-vs-low half-order pair (§2.3) |
| 7 | The generation is dropped or mis-sourced | a flow served from `_states` instead of `_transportIndex` returns a different state; or a key mixes one enumeration's slot with another's generation | the generation **stays** and is read from the same `WindowsAdapter` instance as the slot (§3.4); `FlowKeyEqualityKeepsTheGeneration` pins the compare |
| 8 | A layout is used with a different frame | the rewriter writes at wrong offsets | the layout is produced only by `PacketLayout.From(view)` on the packet's own frame; the rewrite is same-length; the rewriter keeps its bounds checks and rejects without mutating (§4.3) |
| 8b | `PacketLayout.Family` is missing or ignored | the IPv6 geometry is written into an IPv4 frame and the overload returns `true` — silent wire corruption | `Family` is a layout field (§4.1) and the family-equality reject is retained (§4.3); `ViewDrivenRewriterRejectsWhatTheSpanRewriterRejects` drives a cross-family call and asserts a `false` with the span unmutated |
| 8c | `IsTcpSyn` compares `Transport` to the literal `1` | `PacketTransport.Tcp == 0`, so every TCP packet reads as non-SYN and redirect setup silently stops | compare against `(byte)PacketTransport.Tcp` (§4.2); `LayoutSynTestMatchesTheSpanTest` covers TCP and UDP |
| 8d | A sequence read is guarded by `TransportOffset + 4` instead of `+ 8` | a short frame throws out of `Slice` instead of taking the reject path | the bound covers the whole 4-byte read (§4.3); the truncation corpus drives every length |
| 9 | `TransportLength` is taken from the frame length instead of the IP length | Ethernet padding counted as payload → a too-large sequence advance → the RST ack leaves the client's window | the layout carries the parse's `availableLength`, the exact value today's `TryReadTcpSequenceAdvance` derives (`TcpSequenceObservation.cs:57-59`); `SequenceAdvanceIgnoresEthernetPadding` re-pins it for both families |
| 10 | IPv6 extension headers | the IPv6 transport length must deduct them | `PacketView`'s IPv6 arm already computes `payloadLength − extensionBytes`; the layout inherits it; `ExtensionHeaderFramesProduceTheSameAdvance` pins it against the narrow reference |
| 11 | CAS-max loop livelock | repeated CAS failure under heavy contention | the loop has no side effects and each retry re-reads; at most two writers per leg (the two directions are separate words) |
| 12 | A cold reader sees a stale `-1` after a write | the RST degrades to ISN+1 | today's documented degradation (`ClientResetInjector.cs:42`, `tcp-local-redirect.md:81`); `Interlocked`/`Volatile` make the reverse (a value that was never written) impossible |
| 13 | Process metadata allocation on the claim path | an exact 0 B gate over a claim with attribution | no such gate exists (`HotPathAllocationGateTests.cs:418` drives `FlowTable.TryClaimResolved` with a pre-built context, not the dispatcher; the dispatcher gate's claims run before its measured window, `:361-363`). Recorded so a future gate author sees the constraint |
| 14 | The optional delta rewrite changes a checksum | silent wire corruption | bit-identical arithmetic (§6) + the full-recompute oracle + the mutable-offset assertion, all re-run |
| 15 | Instrumentation left in production | a per-packet counter write; or a probe shared across xUnit's parallel collections | every probe is null in production, read once into a local (F2's `NoteGateHold` shape, `FlowTable.cs:136`), and `[ThreadStatic]` so a sibling test collection cannot inflate a count (§4.4) |
| 16 | `Local`/`Remote` materialization on a warm path | `IsReverseOf` would copy four `Endpoint`s per warm UDP proxy packet | `FlowKey.IsReverseOf(FlowKey)` is packed (§2.4); `SelfTrafficKey.From` already materialized and is unchanged; `DispatcherBenchmarks`' proxy rows are the watch series |
| 17 | The UDP target map and the key disagree about an adapter | a response reinjected toward the wrong adapter's handle/MAC | both derive from the same slot (§3.6), and the map is rebuilt from the same `scope` the slots were interned from (`DurableCaptureBundle.cs:311-323`) |
| 18 | A test-only `AdapterContext` survives | the string stays in the key's construction path and creeps back | §3.5 forbids it; a repository-wide `rg 'AdapterContext'` is part of the Step-8 evidence |

## 10. Acceptance mapping (each PRD criterion → an exact proof or a named series)

| PRD criterion | Proof | Kind |
|---|---|---|
| **AC 1 key shape**: size ≤64 B, no string comparison on a lookup, the table round-trips and refuses an unregistered adapter, **the packed key round-trips its endpoints and the reverse swap** | `FlowKeyFitsOneCacheLine` (`Unsafe.SizeOf<FlowKey>() <= 64` — satisfied at exactly 64, §2.1), `FlowKeyHasNoReferenceTypedFields` (reflection: no reference-typed instance field — the structural form of "no string comparison on any lookup"), `AdapterSlotTableRoundTripsStableId` (slot → StableId/FriendlyName/Generation), `AdapterSlotTableRefusesAnUnregisteredAdapter` (`TryResolve(NoSlot)` false; `TryIntern` unknown → `false`), `SlotValuesAreNeverReused`, `AdapterSlotSurvivesARefresh`, `FlowKeyEqualityKeepsTheGeneration`, `FlowKeyPackedRoundTripsEndpoints` (IPv4, IPv6, IPv6 + nonzero ScopeId: `key.Local == source && key.Remote == destination`), `ReverseSwapsEndpointsAndScopes`, `PackedAndMaterializedHashesAgree`, `PackedAndMaterializedCanonicalHashesAgree`, `CanonicalSlotIsOrderIndependent`, `IsReverseOfMatchesTheEndpointComparison`, `TransportTupleEqualsTheKeysTransportFields` | exact |
| **AC 2 parse-once**: a **thread-scoped** counter driven through the **real processor → dispatcher → coordinator** path proves one header walk per redirected packet on the warm path, same results IPv4/IPv6, malformed/truncated frames take the same rejection path | `RedirectedForwardPacketWalksHeadersExactlyOnce` and `RedirectedReversePacketWalksHeadersExactlyOnce` driven through `CapturePacketProcessor.ProcessAsync` (§4.4) with `ParseWalk == N`, `RevalidateWalk == 0`, `ViewRewrite == N` on the driving thread; **red before recorded at 4 and 4 per leg**; `WorkCountsOnTheDrivingThreadOnly` pins the `[ThreadStatic]` scoping against xUnit's parallel collections; `LayoutSynTestMatchesTheSpanTest`; `PacketLayoutFitsSixteenBytes`; `ViewDrivenRewriterRejectsWhatTheSpanRewriterRejects` (every truncation of IPv4/IPv6 TCP frames, plus a cross-family call that must return `false` with the span unmutated, byte-identical output on the accepted ones); `ExtensionHeaderFramesProduceTheSameAdvance`; `SequenceAdvanceIgnoresEthernetPadding`; the existing `PacketParsingTests`/`ProtocolAuditTests` corpora unchanged | exact |
| **AC 3 sequence trackers**: zero `_sequenceGate` entries per packet, CAS-max pinned, RST paths green | `RedirectPacketTakesZeroSequenceGateEntries` (**red: 2** before, 0 after, counted by Step 1's probe which is deleted with the gate), `TcpRedirectAssociationHoldsNoLockField` (structural), `ConcurrentSequenceObservationsKeepTheLargerValue` (two barrier-released threads, wrap-aware max, never decreasing), `UnobservedTrackerReadsNullAndObservedZeroReadsZero`, the RST suites green untouched | exact |
| **AC 4 IPv6 forwarded-leg series (one line, recorded reading)**: the row's 3-run median improves against the re-taken before-series **with the mechanism named**; no other row regresses beyond noise; discharge is **per-leg no-regression**, the improvement figure and the IPv6:IPv4 ratio are **readings, not gates** | the 16-row series re-run 3× after Steps 2–5, per-row medians. Named mechanisms: Step 2 removes two lock entries per packet (both legs, ≈23–29 ns uncontended each — the term the planning pass initially missed in this row), Step 3 removes three walks (both legs), Steps 4–5 shrink the copies. **No threshold**: this host's noise floor makes a 10 % delta unmeasurable (`hot-path.md` contract 9). Step 6 is an **optional commit** adopted only on a real reading (§6); `research/implementation-notes.md` §7's "requirements 1–4 cannot move the row" is **withdrawn** | series / recorded reading |
| **AC 5 allocation**: the per-packet 0 B gates including the dispatcher warm path and `FlowTableWarmResolveAllocatesNoManagedBytes` | `HotPathAllocationGateTests` re-run **unchanged** (11 facts, Release), `WarmPathGateTests.FlowTableWarmResolveAllocatesNoManagedBytes` re-run unchanged, plus a new `SlimContextCopiesAllocateNoManagedBytes`-style check only if the implementer finds the existing gates do not cover a new copy site (they do: the dispatcher gate drives the warm shape end to end) | exact |
| **AC 6 gates**: full suite, `HotPathAllocationGateTests`, the F3 sweep matrix, the F2 warm-path facts, gc-soak anchors, UDP/TCP scenarios | the validation block; F3's sweep code is untouched by every step; F2's facts are the re-proof table of §2.5. Every class this task grows re-records its padded total in Step 0 and Step 8 (§11.9) | exact |
| Release zero-warning, full suite green, `dotnet format` empty, `jb inspectcode` zero | the validation block at every rollback point | exact |
| Benchmark data recorded and cited before archive | `benchmarks/results/2026-09-30-flow-key-parse-once/` (README with the verdict tables, the readings, the spec-row map, before/after series, the parse-walk and gate-count logs, the struct-size table) | artifact |

**Spec-row → proof map.** Every row below is a binding contract in `.trellis/spec/backend/` that this design
touches; the plan's Step 8 updates each row in the same commit as the change that invalidates it.

| Spec row | Today | After this task | Proof / where |
|---|---|---|---|
| `hot-path.md:103-105` (contract 8, "flow keys hash flat") | "compares every field (including `OriginAdapterId` and `ScopeId`)" | compares `OriginAdapterSlot`, `OriginAdapterGeneration` and both `ScopeId`s; no reference-typed field; packed hash entry point whose hashed field set is still transport-only (no slot, no generation) | §2.2/§2.3 + the size, reflection, round-trip and agreement facts |
| `hot-path.md:513-526` (F2 flow-table warm cache) | "`_warm` … validated by the seqlock snapshot + the exact tuple corroboration" | unchanged contract; the slot function reads packed fields; `TransportTuple` is packed (48 B) with the same field set | §2.5 + `PackedAndMaterializedCanonicalHashesAgree` + the unchanged F2 facts |
| `hot-path.md:1308-1312` (F2 "warm resolves take no global gate") | — | unchanged; the slot table is read at classification, never on the warm resolve | §2.5 |
| `hot-path.md:76-78` (contract 4, "packets are structs") | `FlowContext`, `CapturedFlowPacket`, `NativeFrameHandle` are structs | add `PacketLayout` (16 B); record the new measured sizes (`FlowContext` 80, `CapturedFlowPacket` 152, `FlowStateView` 96) | §7 + the artifact's size table |
| `hot-path.md:106-107` (contract 9, measurement discipline) | — | the key/parse claims are exact counts and sizes; the AC-4 improvement is a **reading**, not a threshold | §10/§11 |
| `quality-guidelines.md:14` (endpoint rewrite is a protocol-layer primitive; reject-without-mutating) | the span entry point validates fully | the layout overload keeps exactly `Transport == (byte)PacketTransport.Tcp`, `frame.Length >= TransportEnd` and the family equality — the `dataOffset` bound, version, IHL, protocol byte, fragment bits, total length and the extension chain are the **view's** proofs and are skipped (§4.3) — and it still rejects without mutating; the span entry point remains the oracle | §4.2/§4.3 + `ViewDrivenRewriterRejectsWhatTheSpanRewriterRejects` |
| `quality-guidelines.md:36` (hard invariant linkages: `FlowKey` and `TransportTuple` hash through one shared expression) | one `FlowHash.Combine` | one `FlowHash.CombinePacked` that `Combine` and both packed callers delegate to; `TransportTuple.GetHashCode` still delegates | §2.3 + the two agreement facts |
| `quality-guidelines.md:43` (rewrite tests need an independent checksum oracle + mutable-offset assertions) | `TryRewriteTcpEndpointsFullRecompute` | unchanged; extended over the layout overload and both families | §6 (the optional delta commit) |
| `tcp-local-redirect.md:49` (TFO SYN handling names `IsTcpSyn` and `TryReadTcpSequenceAdvance`) | both re-parse | both consume `PacketLayout` with identical predicates (`Transport` compared to the enum, sequence bound `+ 8`) | §4.2 + `LayoutSynTestMatchesTheSpanTest` + `SynWithPayloadIsRedirectedLikeBareSyn` / `RetransmittedSynWithPayloadReusesAssociation` unchanged |
| `tcp-local-redirect.md:87` (client-reset sequence tracking: "wrap-aware advance-only") | `uint?` under two locks | `long` (−1 unobserved) with a CAS-max writer and `Volatile.Read` readers; the predicate is unchanged | §5 + `ConcurrentSequenceObservationsKeepTheLargerValue` |
| `windows-ndisapi.md:19`, `:25` (adapter identity, GUID-primary) | `WindowsAdapter(StableId, …)` used directly in keys | the slot table interns on the same GUID-primary `StableId` and the key stores the slot plus the generation; a capture-scope adapter that cannot be interned is refused at generation build | §3 + `AdapterSlotSurvivesARefresh` + `AnAdapterThatCannotBeInternedIsRefusedNotAliased` |
| **`udp-relay.md:14`** (host-flow response adapter binding: "must resolve `FlowKey.OriginAdapterId` through the capture-scope `UdpAdapterTarget` map") | `Resolve(string stableId)` over a `Dictionary<string, UdpAdapterTarget>` snapshot, per response | `Resolve(ushort slot)` over a slot-indexed array built from the same `scope` the slots came from; `AdapterIds` resolves through the slot table; the inject/fallback/drop matrix is unchanged | §3.6 + `UdpTargetMapResolvesBySlot` + the existing `HostFlowResponseInjectsTowardItsOriginAdapter` / `HostFlowWithUnresolvedOriginAdapterUsesFallbackAndWarns` / forwarded-response tests |
| **`udp-relay.md:27`** (that row's `Required tests`) | names the three response-target tests | unchanged names, plus `UdpTargetMapResolvesBySlot` for the new key type | §3.6 |
| `traffic-policy-lifecycle.md:9-11` (policy domains) | `Policy` reads `context.AdapterId`/`AdapterName`/`ProcessName`/`ProcessPath`/`RemotePort` | the same properties, now backed by the two metadata references and the key | §7 + the policy suites unchanged |

Nothing in the table is a wall-clock maximum. The new *countable* families are header walks, sequence-gate
entries and slots; the wall-clock rows are report-only and quoted beside their recorded baseline.

## 11. Measurement plan

1. **Instrument first (F3/F2 discipline).** Land the three probes, the sequence-gate-entry probe and the
   struct-size test on the unmodified product, run the full before-series, and record
   `benchmarks/results/2026-09-30-flow-key-parse-once/`. The before-numbers must be taken on **this** tree
   (`ca8d999`): F2 removed the per-hit clock read and the warm-path gates after the
   `2026-09-29-benchmark-coverage` artifacts were written, so those rows are not comparable.
2. **Record the red-before counts** the exact criteria move: header walks per forward/reverse redirected
   packet (4 / 4), sequence-gate entries per packet (2), `Unsafe.SizeOf<FlowKey>()` (128),
   `Unsafe.SizeOf<FlowContext>()` (168), `Unsafe.SizeOf<CapturedFlowPacket>()` (224),
   `Unsafe.SizeOf<FlowStateView>()` (160), `Unsafe.SizeOf<PacketView>()` (80), `Unsafe.SizeOf<Endpoint>()`
   (48). All eight are exact and re-derivable by one test run; the after-values are **64 / 80 / 152 / 96**
   for the first four (the key lands exactly on the 64 B contract, §2.1).
3. **Per-step re-record.** Each step re-runs the *specific* rows it claims to move immediately before its own
   change, so the delta is attributable rather than cumulative: Step 2 (trackers) re-runs
   `TcpRedirectDataPath`; Step 3 (parse) re-runs `TcpRedirectDataPath` + `Parser`; Steps 4–5 (key) re-run
   `FlowTableProductionShape` + `DispatcherBenchmarks`; Step 7 (context) re-runs `DispatcherBenchmarks`.
4. **Attribution discipline.** Before quoting any timing delta, state which rows could not have moved
   because the mechanism is not on them (e.g. `tcp-redirect-data-path` contains no key and no dictionary
   probe — `research/implementation-notes.md` §7.2 — so the key steps must *not* be credited there). Each
   exact claim carries its own count or size; a timing row is only ever a companion. The reverse rows'
   `TrackServerSequence` lock is a *named* term (§6), so Step 2 is the step credited with that row.
5. **Three runs per series**, median quoted, each run its own `--output` (the runner truncates per process;
   concatenate from temp files as F3 did), full command lines and the metadata row in the artifact README.
   Timing rows carry `gated: false`. **The AC-4 improvement and the IPv6:IPv4 ratio are recorded as
   readings, with no threshold** — the operator restated the criterion, and this host's noise floor
   (`hot-path.md` contract 9: sub-2× deltas are noise) makes a 10 % figure unmeasurable as a gate.
6. **Discrimination re-proof.** After each step, inject the regression the step prevents and record the exact
   failure: a per-packet string intern (must fail the walk-count or the warm gate), a re-parse in
   `IsTcpSyn` (must fail `RedirectedForwardPacketWalksHeadersExactlyOnce`), `IsTcpSyn` comparing
   `Transport` to the literal `1` (must fail `LayoutSynTestMatchesTheSpanTest`), a low/high swap in the
   packing (must fail `FlowKeyPackedRoundTripsEndpoints` while every hash fact stays green — the point of
   that fact), a non-atomic tracker (must fail `ConcurrentSequenceObservationsKeepTheLargerValue`), and a
   65-byte `FlowKey` (must fail `FlowKeyFitsOneCacheLine`).
7. **Must-not-move series**: the F2 `WarmPathGateTests` structural classes, `SweepAllocationGateTests`,
   `udp-ready-path-contention`, `gc-soak` shape anchors and the UDP retention/burst scenarios — recorded,
   not gated, except where an existing gate already covers them.
8. **The gate-stability procedure is not re-derived.** The per-gate process proof
   (`hot-path.md:1234-1260`) is host noise, not product behaviour; this task re-runs the four allocation
   classes once and records the totals, and does not repeat the 20-run loop unless a gate actually fails.
9. **Re-record the per-class totals.** Facts added to existing classes change their padded totals, and
   `hot-path.md:1236-1240`'s proof loop asserts the totals string (F2 recorded `12 + 4 + 2 + 7 = 25` for the
   four structural classes in `warm-path-gate-counts.txt`). Step 0 and Step 8 record the totals for **every**
   class this task grows — at minimum `WarmPathGateTests`, `HotPathAllocationGateTests`,
   `TcpRedirectWarmPathGateTests`, `UdpWarmPathGateTests`, `SelfTrafficWarmPathGateTests` and the new
   `FlowKeyShapeTests` — and the artifact README carries the mapping so a later reader is not comparing
   against a stale string.

## 12. Rollback shape

No feature flag; every step is independently revertable and the observable contract changes are confined to
the key's field set, the layout on the packet, the tracker representation and the context's field set.

- Step 1 (probes, size test, before-artifact) is product-neutral apart from the diagnostics hooks; it stays
  on any revert and is what makes the red-before counts re-derivable.
- Step 2 (trackers) reverts alone: restore `_sequenceGate` + two `uint?` fields; nothing else references the
  representation.
- Step 3 (parse-once) reverts alone: the view-taking overloads and the `PacketLayout` field can be deleted
  while the span-taking entry points remain, so a revert is behaviour-zero.
- Step 4 (interned slot) and Step 5 (packed layout) are the key change; each reverts alone in that order
  (Step 5 depends on Step 4's field set, not the reverse). A revert of both restores the 128-byte key and
  `AdapterContext` exactly. §3.6's target-map re-keying rides with Step 4 and reverts with it.
- Step 6 (the optional IPv6 delta commit) reverts alone to the current two-pass delta; dropping it entirely
  is the expected outcome unless the re-run reads a win (§6).
- Step 7 (context) reverts alone: restore the four string fields.
- The probes and the tests are left in place on any revert; the artifact README records which half of each
  series is post-change.

## 13. Recorded discrepancies (code wins; full list in `research/implementation-notes.md` §12)

| # | PRD / research claim | Code reality | Disposition |
|---|---|---|---|
| D1 | `FlowKey` is "~100+ bytes" with "two `Endpoint`s (each ~20 B)" and a ≤48 B target (`research.md:193`, `:211`) | measured `Endpoint` = 48 B, `FlowKey` = **128 B**, `FlowContext` = 168 B, `CapturedFlowPacket` = 224 B | the ≤64 B contract requires repacking the endpoints, not just the string (§2); the research's sizing model is corrected |
| D2 | "`Equals` runs `string.Equals` on **every** dictionary probe" (`prd.md:15`) | true on the gated path; F2's warm path does not probe a dictionary. The full-key compare (string included) runs per warm **forward TCP** packet in `TcpRedirectTable.TryResolveByOriginal` (`:353`) | the requirement is restated structurally (no reference-typed field) and the one warm full-key site is named as the affected path (§2.2, §2.5) |
| D3 | the string comparison is a per-packet cost | on the hit path both sides are usually the same `string` instance, and `string.Equals(…, Ordinal)` short-circuits on reference equality (`research/implementation-notes.md` §1.5) | no timing claim is attributed to the comparison; the win claimed for requirement 1 is the **64-byte** struct (128 → 64) plus the removal of a reference field. **Operator decision, PRD Notes** |
| D4 | "a side table mapping slot ↔ stable id / **generation** / friendly name" (`prd.md:29`) | the inventory bumps the generation on every enumeration for every adapter (`AdapterIdentity.cs:46`), so `(StableId, Generation)` interning cannot be bounded | **settled by the operator:** intern the stable id only; `OriginAdapterGeneration` **stays a key field** with integer equality, because dropping it weakens the adapter-recreation semantics the field exists for. The table is bounded by the adapter count, the exhausted path is unreachable in practice, and the ≤64 B contract is met *with* the generation (§3.4). The design's earlier "drop the generation" alternative is withdrawn |
| D5 | "the same frame is walked up to four times: … `IsTcpSyn`, `TryReadTcpSequenceAdvance`, and the checksum rewriter's full revalidation" (`prd.md:16`) | on the **reverse** leg the second walk is `RecordServerSynAck` (`TcpSequenceObservation.cs:35-43`, called per reverse packet at `Injections.cs:321`), not `IsTcpSyn` | the layout feeds four consumers per leg, `RecordServerSynAck` included; `RecordClientSyn` (cold) is converted for consistency |
| D6 | "the classifier's parse produces a small view (IP header length, transport offset, protocol, flags-byte position) **carried on the packet**" (`prd.md:32-34`) | `PacketView` exists but carries two 32-byte addresses the key already stores; carrying it would add 80 B to a struct copied 2–3×/packet | carry a derived 16-byte `PacketLayout` instead, and state why; the view stays stack-local (§4.1). **Operator-sanctioned, PRD Notes** |
| D7 | AC 4's original "ratio moves toward parity" | the planning pass concluded "requirements 1–4 cannot move the row" from the parse being family-neutral — but the row is `copy + TrackServerSequence + rewriter`, and `TrackServerSequence`'s `_sequenceGate` measures 22.9–29.0 ns uncontended, so **Step 2 alone removes a term from it** | **withdrawn and replaced.** The operator restated AC 4: discharge is **per-leg no-regression**, the improvement figure and the ratio are **recorded readings** (this host's noise floor), the series is re-taken after Steps 2–5, the mechanism is named per step, and the IPv6 checksum delta is an **optional commit** adopted only on a real reading (§6) |
| D8 | "the rewriter consume the view instead of re-walking headers" (`prd.md:33`) | `PacketChecksums.TryRewriteTcpEndpoints` is a public primitive with an internal oracle twin and direct benchmark/test callers | additive overload; the span entry point keeps its validation and its oracle role (§4.2) |
| D9 | "Process name/path and adapter names move behind **an** interned metadata reference created at claim time" (`prd.md:39-41`) | adapter identity is known at classification and is read on cold paths from every packet (e.g. `NdisPacketActionExecutor.cs:121`), while process identity exists only at claim | two references (per-adapter metadata + claim-time process metadata) instead of one; the single-reference alternative would allocate per packet or lose the adapter id on warm packets (§7) |
| D10 | "`FlowContext` … stacks more strings (`AdapterId`, `AdapterName`)" (`prd.md:18`) | `RemotePort` is a third redundancy — all three construction sites pass exactly `Key.Remote.Port` | `RemotePort` becomes `Key.Remote.Port`; 2 bytes and a copy slot leave the struct |
| D11 | "the two `_sequenceGate` entries per packet disappear" (`prd.md:38`) | the gate is also one `System.Threading.Lock` **allocation** per claimed association (`TcpRedirectTable.cs:127`) | adopted; the allocation removal is recorded as a per-association bonus, not a per-packet claim |
| D12 | research F4.2 "the classifier already produced a `PacketView`" (`research.md:214`) | true, but it carries neither the transport length nor the TCP flags `IsTcpSyn` and the observation need | `PacketView` gains `TransportLength` + `TcpFlags`; the packet carries the derived `PacketLayout` (§4.1) |
| D13 | "the transport tuple index" as the F2 artefact to re-prove (`prd.md:46`) | `_transportIndex` is gated and off the per-packet path; the per-packet corroboration is `Matches` over the same `TransportTuple` shape | the re-proof targets `CombineCanonical`, `SlotOf`, `Matches`/`TransportTuple` and the redirect table's `OriginalKey` cache — each with a named fact (§2.5) |
| D14 | "the key fits one cache line (≤64 B) **with all-integer equality**" (`prd.md:31`) | `Local`/`Remote` must stay `Endpoint`-typed for policy, self-traffic, redirect and logging consumers; with computed properties, `FlowDispatcher.IsReverseOf` would materialize four `Endpoint`s per warm UDP proxy packet | equality is all-integer over the packed fields; the `Endpoint` surface stays as computed properties; `IsReverseOf` is served by a new packed `FlowKey.IsReverseOf(FlowKey)`, and `SelfTrafficKey.From` is neutral because it already materialized (§2.4, risk 16) |
| D15 | PRD Notes: only the stable id is interned | the design's earlier alternative interned `(StableId, Generation)` | withdrawn; the generation stays a key field, which costs 8 bytes and puts the key at exactly 64 B (§3.4) |
| D16 | review: "the ≤64 B key lands at exactly 64, so the 56 B / 8 B margin is invalid" | `32 + 8 + 8 + 4 + 2 + 3 = 57` → 64 with 8-byte alignment | corrected: the key is 64 B with **no** headroom, `FlowContext` 80, `CapturedFlowPacket` 152, `FlowStateView` 96 (§2.1, §7, §11.2), and no further field fits |
| D17 | review: `PacketLayout` cannot choose IPv4 vs IPv6 geometry, so §4.3's family reject is unperformable | the layout as first drafted carried no family | corrected: `PacketLayout` gains `byte Family` (still 16 B, asserted) and the argument-family reject is retained (§4.1, §4.3, risk 8b) |
| D18 | review: `IsTcpSyn(in PacketLayout)` written as `Transport == 1` | `PacketTransport.Tcp == 0`, `Udp == 1` (`IPTcpUdpPacket.cs:7-11`) — the literal would disable SYN detection | corrected to compare against `(byte)PacketTransport.Tcp`, with `LayoutSynTestMatchesTheSpanTest` and a discrimination re-proof (§4.2, risk 8c) |
| D19 | review: exhaustion described as fail-closed but was fail-open | collapsing onto `NoSlot` makes two adapters' keys equal when their 5-tuples match | corrected: an adapter that cannot be interned is **refused at generation build**, so no captured packet carries `NoSlot` (§3.3, risk 2) |
| D20 | review: the AC-2 counter would be inflated by xUnit's parallel collections; and neither named harness reaches `CapturePacketProcessor.ProcessAsync` | no `xunit.runner.json` / `[CollectionBehavior]` exists (verified), so collections run in parallel; `HotPathAllocationGateTests` drives the coordinator directly and `TcpRedirectInjectionBatchingTests` drives `HandlePacketAsync` | corrected: `[ThreadStatic]` probe + driving-thread assertion, and the harness starts at `CapturePacketProcessor.ProcessAsync` → dispatcher → coordinator for both legs (§4.4) |
| D21 | design omitted the UDP target-map re-keying, which Step 4 forces | `IUdpAdapterTargetSource.Resolve(string)` is consulted per UDP response (`UdpResponseReinjector.cs:172`, `:189`) | added §3.6 + the `udp-relay.md:14`/`:27` spec rows + `UdpTargetMapResolvesBySlot` |
