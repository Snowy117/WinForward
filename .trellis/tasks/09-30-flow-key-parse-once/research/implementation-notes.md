# Implementation notes — F4 flow keys, parse-once, atomic trackers

Task `09-30-flow-key-parse-once` (parent `08-30-proxy-perf-stability`). These notes are the code-grounded
census behind `design.md` and `implement.md`: every claim carries a `file:line` anchor and a quoted line, so
a reviewer can re-derive it without re-searching. Where the research sketch and the code disagree, the code
wins and the discrepancy is recorded here (§12) and in `design.md` §12.

Tree: `ca8d999ab4352c98da10a9d374762ef4cc834d18` (`ca8d999`), `master`, before any edit by this task.
Predecessors landed on this tree: `09-30-expiry-sweep-bounded-pause` (F3) and `09-30-warm-path-lock-chain`
(F2). F2 changed the warm path materially, so every "before" number in this task is re-taken on this tree
(§11).

---

## 0. How these notes were produced

Everything below comes from reading the tree plus three short, non-mutating measurements run outside the
repository (`/tmp/wfprobe`, a throwaway console project referencing `src/WinForward.Core`,
`src/WinForward.Protocols` and `src/WinForward.Runtime`; deleted after use — no repository file was touched,
and no product/test/benchmark/spec file was edited by this task's planning pass):

1. **Struct sizes** via `System.Runtime.CompilerServices.Unsafe.SizeOf<T>()` (§5, §7) — exact, not estimated.
2. **Component timings** on a Release-built 1400-byte mid-flow TCP frame, interleaved rounds, min of 9 ×
   300,000 iterations, `baseline-empty` = 1.1 ns (§7). These are attribution numbers, not acceptance
   numbers: they exist to decide *which* mechanism can move the recorded 2.0× and which cannot.
3. **A randomized equivalence check** for the IPv6 address-delta rewrite candidate (§7.3): 2,000,000 random
   32-byte pairs, narrow (16 × `u16`) vs wide-load (8 × `u64`, shift-extracted) accumulation, **0
   mismatches** after folding.

Everything else is quoted source. `rg -n --heading` was used throughout, per the repository's search
convention.

---

## 1. `FlowKey`: definition, construction, comparison, hashing, storage

### 1.1 Definition and measured size

`src/WinForward.Core/Domain.cs:76-118`:

```csharp
public readonly record struct FlowKey(
    AddressFamilyKind AddressFamily,
    TransportProtocol Protocol,
    Endpoint Local,
    Endpoint Remote,
    FlowOriginKind Origin,
    string? OriginAdapterId,
    long OriginAdapterGeneration)
```

Measured (`Unsafe.SizeOf<T>()`): **`FlowKey` = 128 bytes** — exactly two cache lines, not "~100+ bytes
spanning several cache lines" as the research estimated. The task's packed replacement (two 24-byte endpoint
groups + the adapter slot + the retained generation) measures **57 → 64 bytes**, i.e. exactly the PRD's
one-cache-line contract with no headroom. The estimate's error is in `Endpoint`:

| type | measured | why |
|---|---:|---|
| `UInt128` inside `IPAddressValue` | — | 16-byte alignment forces the padding |
| `IPAddressValue` (`Domain`-adjacent, `src/WinForward.Core/IPAddressValue.cs:15-35`) | **32 B** | `UInt128 Bits` (16) + `AddressFamilyKind Family` (4) + `uint ScopeId` (4) = 24 → padded to 32 |
| `Endpoint` (`Domain.cs:31-72`) | **48 B** | `IPAddressValue` (32) + `ushort Port` (2) = 34 → padded to 48 |
| `FlowKey` | **128 B** | 48 + 48 + 4 + 4 + 4 + 8 (`string?` ref) + 8 (`long`) = 124 → padded to 128 |

The research's "two `Endpoint`s (each ~20 B)" (`research.md:193`) understates each endpoint by 2.4×, which is
why its "≤48 B" target (`research.md:211`) is unreachable without repacking the endpoint storage — see
§9.1 and `design.md` §2. The design's own size contract and its proof are in `design.md` §2.2.

### 1.2 Construction sites (all of `src/`)

`FlowKey.Create` is called in exactly three places in production code:

- `src/WinForward.Runtime/PacketFlowClassifier.cs:26` — the per-packet classifier:
  ```csharp
  var key = FlowKey.Create(local, remote, protocol, origin, adapterContext);
  ```
  where `local`/`remote` are `Endpoint.From(view.SourceAddress, view.SourcePort)` /
  `Endpoint.From(view.DestinationAddress, view.DestinationPort)` (`:23-24`) and `adapterContext` is
  `new AdapterContext(adapter.StableId, adapter.Generation)` (`:21`). **This is the only per-packet
  construction site in the product.**
- `src/WinForward.Runtime/PacketFlowClassifier.cs:39` — the non-flow classifier (`ClassifyNonFlow`), also
  per packet on the non-flow path.
- `src/WinForward.Runtime/UdpProxy/UdpSessionSetup.cs:103-107` — the UDP relay alias key (cold, per UDP
  session setup), built with no `AdapterContext`:
  ```csharp
  var relayAlias = new RelayAlias(FlowKey.Create(
      Endpoint.From(transport.LocalEndpoint.Address, checked((ushort)transport.LocalEndpoint.Port)),
      Endpoint.From(transport.RelayEndpoint.Address, checked((ushort)transport.RelayEndpoint.Port)),
      TransportProtocol.Udp,
      flow.Origin));
  ```

The positional record constructor is used directly only in test/benchmark code (e.g.
`tests/WinForward.Core.Tests/UdpRelayTests.cs:371`).

`FlowKey.Create` itself (`Domain.cs:85-90`) validates that both endpoints share a family and stores
`adapter?.StableId` / `adapter?.Generation ?? 0`.

### 1.3 Comparison and hashing

- `Domain.cs:99` — `GetHashCode()` delegates to `FlowHash.Combine(AddressFamily, Protocol, Local, Remote)`
  (transport-only: origin kind, adapter id and generation are deliberately excluded so origin variants share
  a `_states` bucket).
- `Domain.cs:102-115` — `Equals` compares, in order: `Protocol`, `AddressFamily`, `Origin`,
  `OriginAdapterGeneration`, both ports, both `Address.Bits`, both `Address.Family`, both `Address.ScopeId`,
  then
  ```csharp
  string.Equals(OriginAdapterId, other.OriginAdapterId, StringComparison.Ordinal);
  ```
  (`:115`).
- `Domain.cs:117` — `Reverse()`. **No production caller**; only `FlowTable`'s `TransportTuple.Reverse()` is
  used (`FlowTable.cs:478`).
- `Domain.cs:127-160` — `FlowHash`: `Combine` (`:129-137`) is `HashCode.Combine` over both endpoints' two
  64-bit halves, both ports, family and protocol; `CombineCanonical` (`:148-152`) orders the pair by address
  bits then port and delegates to `Combine`; `OrdersBefore` (`:155-159`) is the total order.

### 1.4 Storage sites (every structure keyed by `FlowKey` or by the transport tuple)

| structure | anchor | key | reached per packet? |
|---|---|---|---|
| `FlowTable._states` | `src/WinForward.Core/FlowTable.cs:20` | `FlowKey` | gated path + claim |
| `FlowTable._transportIndex` | `FlowTable.cs:21` | `TransportTuple` | gated path only |
| `FlowTable._warm` | `FlowTable.cs:30` | canonical transport hash, `FlowState?[]` | **warm path** |
| `TcpRedirectTable._byOriginal` | `src/WinForward.Runtime/TcpRedirect/TcpRedirectTable.cs:169` | `FlowKey` | forward redirect path |
| `TcpRedirectTable._warmOriginal` | `TcpRedirectTable.cs:199` | `originalKey.GetHashCode()` slot (`:510`) | **warm forward TCP path** |
| `TcpRedirectTombstoneTable._byForward` | `TcpRedirectTombstoneTable.cs:21` | `FlowKey` | reverse-miss path |
| `TcpResetCooldownTable._expiryByTuple` / `_insertionOrder` | `TcpResetCooldownTable.cs:16-17` | `FlowKey` | capacity-reset path (cold) |
| `TcpRedirectSessionStore._sessions` | `TcpRedirectSessionStore.cs:32` | `FlowKey` | setup/teardown |
| `TcpPendingSynSetup._pending` / `_setupCooldowns` | `TcpPendingSynSetup.cs:71-72` | `FlowKey` | setup |
| `UdpSetupCooldownTable._retryAtByFlow` | `src/WinForward.Runtime/UdpProxy/UdpSetupCooldownTable.cs:21` | `FlowKey` | admission |
| `UdpProxyCoordinator._sessions` | `src/WinForward.Runtime/UdpProxy/UdpProxyCoordinator.cs:16` | `FlowKey` | admission |
| `UdpAssociationTable` (three dictionaries) | `src/WinForward.Runtime/UdpProxy/UdpAssociations.cs:51-105` | `FlowKey` | setup |
| `SelfTrafficRegistry` | `src/WinForward.Runtime/SelfTrafficRegistry.cs:9-10` | `SelfTrafficKey` (protocol + two `Endpoint`s, **no adapter**) | **warm path** (wildcard half) |
| `TcpRedirectTable._warmReverse` | `TcpRedirectTable.cs:198` | `HashCode.Combine(source, destination)` (`:508`) | **warm reverse path** |
| `UdpAdapterTargetSource` | `src/WinForward.Runtime/UdpProxy/UdpAdapterTargetSource.cs:48-56` | `string stableId` | UDP response path |

`rg -l 'FlowKey' src/` = **26 files**; `rg -n 'FlowKey' src/` = **124 hits**. The construction surface is
three sites; the *consumption* surface is wide but almost entirely reads of `Key.Local`, `Key.Remote`,
`Key.Protocol`, `Key.Origin`, `Key.AddressFamily` and `Key.OriginAdapterId`.

### 1.5 Where the key is actually read on the per-packet path (current tree)

- **Warm dispatcher entry** (`src/WinForward.Runtime/FlowDispatcher.cs:164-200`):
  `packet.Context.Key` → `_selfTraffic.IsWildcardOwned(packet.Context)` (`:175`, reads
  `context.Key.Protocol/Local/Remote` via `SelfTrafficRegistry.SelfTrafficKey.From`, `:70`) →
  `_flows.TryResolveWarm(packet.Context.Key, out var existing)` (`:176`) → `packet.Context.Key.Protocol`
  (`:187`) → `packet with { FlowGeneration = existing.Generation }` (`:188`, `:194`).
- **`FlowTable.TryResolveWarm`** (`FlowTable.cs:183-195`): `TransportTuple.From(key)` (`:185`), `SlotOf(key)`
  (`:186` → `FlowHash.CombineCanonical(key.AddressFamily, key.Protocol, key.Local, key.Remote)`, `:209-210`),
  `candidate.TrySnapshot` (`:187`), `Matches(tuple, view.Key)` (`:187` → `:203-207`).
- **`TcpRedirectTable.TryResolveByOriginal`** (`TcpRedirectTable.cs:349-372`) — **every mid-flow forward
  redirected packet** goes through `TcpProxyCoordinator.HandlePacketAsync`
  (`src/WinForward.Runtime/TcpRedirect/TcpProxyCoordinator.cs:406`) into this probe:
  ```csharp
  var cached = Volatile.Read(ref _warmOriginal[slot]);
  if (cached is not null && cached.OriginalKey.Equals(originalKey))
  ```
  (`:352-353`) — i.e. the **full-key `Equals`, including the string comparison**, plus
  `originalKey.GetHashCode()` (`:510`, the 128-bit endpoint mix) run per packet today.
- **`FlowDispatcher.IsReverseOf`** (`FlowDispatcher.cs:376-380`) — UDP proxy reverse detection, compares
  `stored.Local == observed.Remote` (an `Endpoint` comparison, `Domain.cs:61`).
- **`TcpProxyCoordinator.HandleReverseAsync`** (`TcpProxyCoordinator.Injections.cs:300-310`) reads
  `key.Protocol`, `association.OriginalKey` (`.Origin`, `.Remote`, `.Local`).
- **Policy** (`src/WinForward.Core/Policy.cs:20-23`) reads `context.Key.Protocol`, `context.Key.AddressFamily`,
  `context.Key.Remote` — once per flow at claim.

**String-comparison nuance (important for attribution).** `string.Equals(a, b, StringComparison.Ordinal)`
short-circuits on reference equality, and the only per-packet key builder uses
`adapter.StableId` from the *same* `WindowsAdapter` record instance for the lifetime of a capture generation
(`PacketFlowClassifier.cs:21` ← `MultiAdapterCaptureLoop.cs:28`). So the string comparison on a *hit* is
almost always a reference compare (~2 ns); it becomes a real `memcmp` only when a key built from a refreshed
adapter list meets a key built from the previous one, or on a miss. Requirement 1's "no string comparison on
any lookup path" is therefore a **structural** win (a reference field and 8 bytes leave the key, the field
becomes comparable on every path including friend-assembly and test code) and a **near-zero per-packet
win on the hit path**. The design must not attribute a measurable per-packet gain to it (§7.2, §10).

---

## 2. The four parse sites (and two more the research list misses)

The frame is an Ethernet II frame; every derived offset is `14 + IPHeaderLength + n`. `IPTcpUdpPacket`
(`src/WinForward.Protocols/IPTcpUdpPacket.cs:43-129`) produces a `PacketView`
(`IPTcpUdpPacket.cs:19-27`, measured **80 B**):

```csharp
public readonly record struct PacketView(
    PacketTransport Transport,
    IPAddressValue SourceAddress,
    IPAddressValue DestinationAddress,
    ushort SourcePort,
    ushort DestinationPort,
    int IPHeaderLength,
    int TransportHeaderLength);
```

`IPHeaderLength` is **Ethernet-relative** in both families: IPv4 = IHL (`:60`), IPv6 = 40 + extension bytes
(`:97`, `transportOffset - EthernetHeaderLength`). `TransportHeaderLength` is the TCP data offset or 8 for
UDP (`:119`, `:125-127`).

### Site 1 — the classifier (the one walk that must survive)

`src/WinForward.Runtime/Capture/CapturePacketProcessor.cs:67`:

```csharp
if (!IPTcpUdpPacket.TryParse(frameSpan, out var view))
```

then `PacketFlowClassifier.ClassifyFlow(view, adapter, isOnSend)` (`:74`) or `ClassifyNonFlow` (`:69`). The
parse is bounds-checked and allocation-free; a failure routes the packet to `DispatchNonFlowAsync` and never
into a flow.

What `TryParse` proves for a **TCP** frame, family by family:

| proof | IPv4 (`:56-72`) | IPv6 (`:74-98`) |
|---|---|---|
| Ethernet II ethertype | `0x0800` (`:47-50`) | `0x86dd` (`:47-50`) |
| minimum frame length | `>= 34` (`:46`) | `>= 54` (`:77`) |
| IP version | `>> 4 == 4` (`:61`) | `>> 4 == 6` (`:77`) |
| header length in range | `IHL >= 20` and inside the frame (`:61`) | 40 fixed + ext chain (`:84-92`) |
| transport protocol | TCP or UDP (`:63`) | TCP or UDP after the ext chain (`:93`) |
| total/payload length inside the frame | `:64-65` | `:78-79` |
| no fragmentation | `(fragment & 0xbfff) == 0` (`:66-67`) | n/a (ext header 44 rejected, `:86`) |
| transport header present | `availableLength >= 20`, inside frame (`:111`, `:123`) | same |
| TCP data offset sane | `20 <= dataOffset <= availableLength` (`:125-126`) | same |

### Site 2 — `TcpFrameRewriter.IsTcpSyn` (per proxy-decided TCP packet)

`src/WinForward.Runtime/TcpRedirect/TcpFrameRewriter.cs:67-76`:

```csharp
if (!IPTcpUdpPacket.TryParse(frame, out var view) || view.Transport != PacketTransport.Tcp) return false;
var tcpFlagsOffset = 14 + view.IPHeaderLength + 13;
if (frame.Length <= tcpFlagsOffset) return false;
var flags = frame[tcpFlagsOffset];
const byte syn = 0x02;
const byte ack = 0x10;
return (flags & syn) != 0 && (flags & ack) == 0;
```

Caller: `src/WinForward.Runtime/TcpRedirect/TcpProxyCoordinator.cs:398` —
`var syn = TcpFrameRewriter.IsTcpSyn(packet.InspectionSpan);`, evaluated **unconditionally before the reverse
branch** on every proxy-decided TCP packet (forward *and* reverse).

### Site 3 — `TcpSequenceObservation` (per packet on both legs)

`src/WinForward.Runtime/TcpRedirect/TcpSequenceObservation.cs:52-71` (`TryReadTcpSequenceAdvance`) re-parses
and then **re-derives the transport length from the IP length field**, with a family branch:

```csharp
if (!IPTcpUdpPacket.TryParse(frame, out var view) || view.Transport != PacketTransport.Tcp) return false;
var etherType = BinaryPrimitives.ReadUInt16BigEndian(frame.Slice(12, 2));
var transportLength = etherType == 0x0800
    ? BinaryPrimitives.ReadUInt16BigEndian(frame.Slice(16, 2)) - view.IPHeaderLength
    : BinaryPrimitives.ReadUInt16BigEndian(frame.Slice(18, 2)) - (view.IPHeaderLength - 40);
if (transportLength < view.TransportHeaderLength) return false;
var tcpOffset = 14 + view.IPHeaderLength;
var sequence = BinaryPrimitives.ReadUInt32BigEndian(frame.Slice(tcpOffset + 4, 4));
var flags = frame[tcpOffset + 13];
```

Note the IPv6 arm subtracts `IPHeaderLength - 40` (the extension bytes) from the payload length — the
convention `PacketView.IPHeaderLength` already carries. Callers:

- `TcpSequenceObservation.cs:77-80` `TrackClientSequence` ←
  `TcpProxyCoordinator.Injections.cs:256` (per forward mid-flow packet, on the staged pre-rewrite frame).
- `TcpSequenceObservation.cs:83-86` `TrackServerSequence` ← `Injections.cs:322` (per reverse packet, on the
  staged pre-rewrite frame).

### Site 4 — the checksum rewriter's own revalidation (per redirected packet)

`PacketChecksums.TryRewriteTcpEndpoints` (`src/WinForward.Protocols/PacketChecksums.cs:46-56`) re-derives
everything from the frame:

- IPv4 (`:105-118`): family check, `frame.Length >= 34`, `IHL >= 20`, `version == 4`, `protocol == 6`,
  `totalLength >= IHL + 20`, frame covers `totalLength`, no fragment, `tcpLength >= 20`, `dataOffset` sane.
- IPv6 (`:146-155`): family check, `frame.Length >= 54`, `version == 6`, frame covers
  `40 + payloadLength`, then `TryFindIpv6Transport` (`:244-259`) **walks the extension-header chain again**
  (bounded to 256 bytes, header 44 rejected), then the same `tcpLength`/`dataOffset` checks.

For IPv6 it additionally builds the old address pre-image on the stack and walks it twice
(`:157-173`):

```csharp
Span<ushort> oldAddressWords = stackalloc ushort[16];
FillAddressWords(frame, ipOffset, oldAddressWords);
...
var wordIndex = 0;
for (var offset = ipOffset + 8; offset < ipOffset + 40; offset += 2)
{
    tcpDelta += WordDelta(oldAddressWords[wordIndex++], ReadWord(frame, offset));
}
```

vs IPv4 (`:120-142`), which keeps four words in locals. This asymmetry is the whole 2.0× (§7).

Callers on the per-packet path: `TcpFrameRewriter.cs:25` and `:27` (forward leg, via
`TryRewriteForwardLeg`), and `TcpProxyCoordinator.Injections.cs:323` (reverse leg, called directly).

### Site 5 (research misses) — `RecordServerSynAck` re-parses on every reverse packet

`TcpSequenceObservation.cs:35-43`:

```csharp
if (!IPTcpUdpPacket.TryParse(frame, out var view) || view.Transport != PacketTransport.Tcp) return;
var flagsOffset = 14 + view.IPHeaderLength + 13;
if (frame.Length <= flagsOffset || (frame[flagsOffset] & 0x12) != 0x12) return;
var sequenceOffset = 14 + view.IPHeaderLength + 4;
```

Caller `TcpProxyCoordinator.Injections.cs:321` — **per reverse packet**, before `TrackServerSequence`. The
flags test rejects all but the SYN-ACK, so the *record* happens once, but the *walk* happens every time.

### Site 6 (research misses) — `RecordClientSyn` (cold) and `TcpResetBuilder` (cold)

`TcpSequenceObservation.cs:18-29` re-parses at redirect setup (`TcpRedirectSetup.cs:129`), and
`TcpResetBuilder.TryBuildResetFromSyn` (`src/WinForward.Protocols/TcpResetBuilder.cs`) walks the frame to
build a capacity-reset RST on the cold path (`ClientResetInjector.cs:95`). Cold, but they consume the same
`PacketView` and should be converted for consistency, not for speed.

### Count per redirected packet, today

| packet shape | walks |
|---|---|
| warm forward mid-flow (`ReinjectExistingFlowDataAsync`) | processor `TryParse` (1) + `IsTcpSyn` (2) + `TrackClientSequence` (3) + `TryRewriteTcpEndpoints` revalidation (4) |
| warm reverse (`HandleReverseAsync`) | processor (1) + `RecordServerSynAck` (2) + `TrackServerSequence` (3) + `TryRewriteTcpEndpoints` revalidation (4) |
| SYN setup (cold, `TcpRedirectSetup`) | processor (1) + `IsTcpSyn` (2) + `RecordClientSyn` (3) + `TryRewriteForwardLeg` revalidation (4) |

The PRD's "four header walks" is confirmed, with the correction that on the reverse leg the fourth is
`RecordServerSynAck` rather than `IsTcpSyn` — but both legs do four.

---

## 3. Sequence trackers: shape and every reader/writer

`src/WinForward.Runtime/TcpRedirect/TcpRedirectTable.cs:116-156`, on `TcpRedirectAssociation`:

```csharp
public uint? ClientNextSeq { get { lock (_sequenceGate) return _clientNextSeq; } }
public uint? ServerNextSeq { get { lock (_sequenceGate) return _serverNextSeq; } }

private readonly Lock _sequenceGate = new();
private uint? _clientNextSeq;
private uint? _serverNextSeq;
```

Writers:

```csharp
internal void ObserveClientSequence(uint sequenceNext)
{
    lock (_sequenceGate)
    {
        if (_clientNextSeq is not { } current || IsSequenceAhead(sequenceNext, current)) _clientNextSeq = sequenceNext;
    }
}
```
(`:137-143`; `ObserveServerSequence` is the mirror at `:146-152`; `IsSequenceAhead` at `:156` is
`candidate != current && (int)(candidate - current) > 0`.)

| member | kind | gate | frequency |
|---|---|---|---|
| `ObserveClientSequence` | writer | takes `_sequenceGate` | **per forward packet** (`Injections.cs:256` → `TcpSequenceObservation.cs:79`) |
| `ObserveServerSequence` | writer | takes `_sequenceGate` | **per reverse packet** (`Injections.cs:322` → `:85`) |
| `ClientNextSeq` getter | reader | takes `_sequenceGate` | cold (`ClientResetInjector.cs:47`) |
| `ServerNextSeq` getter | reader | takes `_sequenceGate` | cold (`ClientResetInjector.cs:46`) |
| `ClientNextSeq`/`ServerNextSeq` | reader | — | tests (`TcpProxyCoordinatorRewriteTests.cs:402`) |

So the gate is entered **twice per packet** on the redirect data path (once per leg) — the PRD's "two lock
entries per packet" (`prd.md:17`) is exact — while both readers are cold teardown/RST paths:

```csharp
var serverSequenceNext = association.ServerNextSeq ?? serverInitialSeq + 1;
var clientSequenceNext = association.ClientNextSeq ?? clientInitialSeq + 1;
```
(`src/WinForward.Runtime/TcpRedirect/ClientResetInjector.cs:46-47`)

`_sequenceGate` is `System.Threading.Lock`, a **reference type**, so it is also **one managed allocation per
claimed TCP redirect association** (`TcpRedirectTable.cs:270` constructs the association).

Not in scope but adjacent: `ClientInitialSeq` / `ServerInitialSeq` (`:82`, `:114`) are plain `uint?` written
at setup (`TcpSequenceObservation.cs:23`, `:42`) and read by the same cold RST builders. `ServerInitialSeq`
is written from the per-packet reverse path (`Injections.cs:321` → `:42`) without a gate — a pre-existing
benign race (the value is written once, on the SYN-ACK) that this task does not change.

---

## 4. `FlowContext` / `CapturedFlowPacket`: shape and every copy site

### 4.1 Definitions and measured sizes

```csharp
public readonly record struct FlowContext(
    FlowKey Key,
    string? ProcessName,
    string? ProcessPath,
    string? AdapterId,
    string? AdapterName,
    ushort RemotePort);
```
(`src/WinForward.Core/Domain.cs:167-174`)

```csharp
public readonly record struct CapturedFlowPacket(
    PacketLease Lease,
    FlowContext Context,
    PacketCaptureMetadata Metadata = default,
    long PacketSequence = 0,
    long FlowGeneration = 0,
    NativeFrameHandle NativeFrame = default)
```
(`src/WinForward.Runtime/FlowDispatcher.cs:34-51`)

Measured: `FlowContext` = **168 B**, `CapturedFlowPacket` = **224 B**, `PacketCaptureMetadata` = 24 B,
`NativeFrameHandle` = 8 B, `FlowStateView` = **160 B** (`Domain.cs:181-182`).

### 4.2 Copy sites (per packet)

| anchor | copy | size |
|---|---|---|
| `CapturePacketProcessor.cs:70`, `:75` | `new CapturedFlowPacket(...)` into `DispatchAsync`/`DispatchNonFlowAsync` (by value) | 224 B |
| `FlowDispatcher.cs:164` | `DispatchAsync(CapturedFlowPacket packet)` — by-value parameter | 224 B |
| `FlowDispatcher.cs:188` | `packet = packet with { FlowGeneration = existing.Generation };` (warm proxy) | 224 B |
| `FlowDispatcher.cs:194` | same (warm pass/block) | 224 B |
| `FlowDispatcher.cs:218` | same (slow-path resolve hit) | 224 B |
| `FlowDispatcher.cs:246` | `packet = packet with { Context = context, FlowGeneration = claimed.Generation };` | 224 B |
| `FlowDispatcher.cs:236` | `context = await AttributeProcessAsync(context, ...)` (by value in, by value out) | 168 B ×2 |
| `FlowDispatcher.cs:290` | `return context with { ProcessName = ..., ProcessPath = ... };` (claim only) | 168 B |
| `FlowTable.cs:183-195` | `TryResolveWarm(FlowKey, out FlowStateView)` — 128 B key in, **160 B view out** | 288 B |
| `FlowTable.cs:442-454` | `TryResolveLocked(FlowKey, out FlowState?)` | 128 B |
| `FlowDispatcher.cs:269`, `:304`, `IPFragment.IsFragment(packet.InspectionSpan)` | `CapturedFlowPacket` by value into coordinator entry points | 224 B each |

`InspectionSpan` (`FlowDispatcher.cs:50`) is a property, not a copy; it reads the native buffer or the
lease frame.

### 4.3 Consumers of the string fields

| field | consumer | frequency |
|---|---|---|
| `ProcessName`, `ProcessPath` | `src/WinForward.Core/Policy.cs:16` (`ProcessSelectorMatcher.IsMatch`) | **claim only** |
| `ProcessName`, `ProcessPath` | `FlowDispatcher.cs:439-440`, `:452-453` (`LogPacketStage`, `FlowFields`) | trace/debug only |
| `AdapterId`, `AdapterName` | `Policy.cs:18-19` | **claim only** |
| `AdapterId` | `NdisPacketActionExecutor.cs:121` (pass-reinject failure log) | cold failure |
| `RemotePort` | `Policy.cs:23` (`RemotePorts` matcher) | **claim only** |

All three construction sites pass exactly `key.Remote.Port` as `RemotePort`
(`PacketFlowClassifier.cs:27`, `:40`; `UdpSessionTransfer`-free callers in tests and
`BenchmarkShared.cs:193`), so the field is redundant with the key.

`Context.AdapterId` / `AdapterName` are **not** read anywhere else in `src/`; `FlowKey.OriginAdapterId` (a
different member) is read at:
`TcpRedirectSetup.cs:100` (forward-local address resolution, cold), `UdpResponseReinjector.cs:172`, `:189`
(per UDP response), `:252`, `:276` (logging), and by tests/benchmarks.

---

## 5. Adapters: what exists, and who knows their stable ids

- `src/WinForward.Windows/WindowsAdapter.cs:3-8`:
  ```csharp
  public sealed record WindowsAdapter(string StableId, string FriendlyName, string InternalName, nint RuntimeHandle, long Generation);
  ```
- `WindowsAdapterInventory.GetCurrentAdapters` (`src/WinForward.Windows/AdapterIdentity.cs:42-55`) mints
  `StableId` as the Windows IP-Helper `NetworkInterface.Id` (the adapter GUID) when it can match one, else
  the NDISAPI internal name; `FriendlyName` is the IP-Helper name; **`Generation` is `++_generation` on the
  inventory, so a single enumeration bumps the generation for *every* adapter** (`:46`, `:52`).
- The `WindowsAdapter` instances live for one capture generation: `MultiAdapterCaptureLoop`
  (`src/WinForward.Runtime/Capture/MultiAdapterCaptureLoop.cs:28`) takes
  `IReadOnlyList<WindowsAdapter> adapters`; `NdisCaptureGenerationFactory.Create`
  (`src/WinForward.Runtime/Capture/NdisCaptureGeneration.cs:97-108`) builds one loop per enumeration from
  `scope.Select(item => item.Adapter).ToArray()`.
- The **packet processor is durable across generations**: one `CapturePacketProcessor` is constructed in
  `src/WinForward.Cli/Program.cs:284` and handed to every generation (`NdisCaptureGeneration.cs:72`, `:103`).
  A slot table owned there has process lifetime and survives adapter-list refreshes, which is what a key
  needs.
- Capture scope is resolved by `CaptureAdapterScopeResolver.TryResolve`
  (`src/WinForward.Runtime/Capture/CaptureAdapterScopeResolver.cs:18-53`), which already sorts by
  `StableId` (`:53`) — the same identity the interning key must use.
- Who resolves a stable id back to something usable:
  - `TcpRedirectSetup.ResolveForwardLocalAddress` (`TcpRedirectSetup.cs:97-105`) →
    `localAddresses.SelectLocalAddress(originAdapterId, family, localAddress)` — cold.
  - `UdpAdapterTargetSource.Resolve(string stableId)` (`UdpAdapterTargetSource.cs:56`) — a
    `Volatile.Read` + `Dictionary<string, UdpAdapterTarget>` probe, **per UDP response packet**
    (`UdpResponseReinjector.cs:172`, `:189`); the whole map is rebuilt on each refresh (`:61-66`).
  - `NdisPacketActionExecutor.cs:121` — a failure log.
  - `Policy.cs:18-19` — the `AdapterIds`/`AdapterNames` matcher, once per claim.
  - `UdpResponseReinjector.cs:252`, `:276` — failure logging.
  - `SelfTrafficRegistry` does **not** consult the adapter at all (`SelfTrafficRegistry.cs:68-71`).

---

## 6. F2's regression surface: what a key change must re-prove

F2 landed (tree `02fee48` + `cee7063`) and its contracts are in `.trellis/spec/backend/hot-path.md:1277-1363`.

1. **`FlowHash.CombineCanonical` is the warm slot function.**
   `FlowTable.SlotOf` (`FlowTable.cs:209-210`):
   ```csharp
   private int SlotOf(FlowKey key) =>
       FlowHash.CombineCanonical(key.AddressFamily, key.Protocol, key.Local, key.Remote) & (_warm.Length - 1);
   ```
   It reads `key.Local`/`key.Remote` — i.e. it **materializes both `Endpoint`s (96 B) per warm packet**
   today. Any repacking of the key must either keep this call valid or provide a packed equivalent that is
   *proven identical*, because the slot function must stay the same function the dictionaries bucket by
   (`Domain.cs:127-160`, `FlowTable.cs:480-484`).
2. **The warm cache's validation.**
   `TryResolveWarm` (`FlowTable.cs:183-195`) = `TransportTuple.From(key)` → slot read → `TrySnapshot` →
   `Matches(tuple, view.Key)` → `TouchBucket`. `Matches` (`:203-207`) compares **transport tuples in either
   orientation**, not full keys:
   ```csharp
   var storedTuple = TransportTuple.From(stored);
   return storedTuple.Equals(queried) || storedTuple.Reverse().Equals(queried);
   ```
   `TransportTuple` (`FlowTable.cs:470-497`) is `(AddressFamilyKind, TransportProtocol, Endpoint Local,
   Endpoint Remote)` with `GetHashCode() => FlowHash.Combine(...)` (`:484`).
3. **The invariant the cache rests on.** The doc comment at `FlowTable.cs:197-202` states it: "equivalent to
   the OR of the two dictionary probes because the table holds at most one state per transport tuple",
   enforced by `AddToTransportIndex`'s `Dictionary.Add` (`:459`) and by `TryResolveLocked`
   (`:442-454`) resolving a second claim of the same tuple to the existing state. Pinned by
   `tests/WinForward.Core.Tests/WarmPathGateTests.cs:274` `FlowTableTransportTupleIsUniqueAcrossOrigins`.
4. **The redirect table's own warm cache validates with the full key.**
   `TcpRedirectTable.TryResolveByOriginal` (`:349-372`) validates by `cached.OriginalKey.Equals(originalKey)`
   (`:353`) — the one place on the warm path where the *full* `FlowKey.Equals` (including the string
   comparison) runs per packet, plus `originalKey.GetHashCode()` for the slot (`:510`).
5. **F2's landed facts that must stay green** (from `.trellis/spec/backend/hot-path.md:1308-1340` and
   `benchmarks/results/2026-09-30-warm-path-lock-chain/`):
   `WarmResolveCompletesWhileFlowTableGateIsHeld`, `ReverseResolveCompletesWhileRedirectGateIsHeld`,
   `FlowTableWarmResolveAllocatesNoManagedBytes` (`WarmPathGateTests.cs:175`),
   `WarmCacheHitServesTheValidatedView`, `FlowTableCollidingFlowsFallBackToTheGatedPath`,
   `FlowTableRemovedFlowIsNeverServedFromItsOldSlot`, `FlowTableTransportTupleIsUniqueAcrossOrigins`,
   `WarmHitTakesZeroExactTupleGuardProbes` (`SelfTrafficWarmPathGateTests.cs:11`).

The consequence for this task: the key change is only safe if (a) `CombineCanonical`'s value for a given
logical key is unchanged or a packed variant is proven equal to it, (b) `TransportTuple`'s field set and
equality are unchanged in meaning, and (c) the two warm-cache validation paths above are re-exercised by
named facts — not merely recompiled.

---

## 7. The IPv6-versus-IPv4 asymmetry behind the recorded 2.0×

### 7.1 The recorded numbers

`benchmarks/results/2026-09-29-benchmark-coverage/tcp-redirect-data-path.csv`, `--job short`,
`IterationCount=3`, 1400-byte mid-flow frame:

| row | IPv4 | IPv6 | ratio |
|---|---:|---:|---:|
| `ReverseLegForwarded` | 58.03 ns | 112.02 ns | **1.93×** |
| `ReverseLegHost` | 68.63 ns | 96.22 ns | 1.40× |
| `ForwardLegHost` | 118.60 ns | 111.20 ns | 0.94× |
| `ForwardLegForwarded` | 102.90 ns | 91.52 ns | 0.89× |

So the 2.0× is the **reverse leg only**, and it is the *forward* leg that is family-symmetric. The rows are
defined at `benchmarks/WinForward.Benchmarks/Perf/TcpRedirectDataPathBenchmarks.cs:88-104`
(`ReverseLegHost`/`ReverseLegForwarded` both end in `PacketChecksums.TryRewriteTcpEndpoints`; the forward
rows end in `TcpFrameRewriter.TryRewriteForwardLeg`).

### 7.2 Measured decomposition (probe, 1400-byte frame, min of 9 × 300,000)

| component | ns |
|---|---:|
| `baseline-empty` | 1.1 |
| `memcpy1400` (`pristine → scratch`, what every row pays) | 23–25 |
| `parse ipv4` / `parse ipv6` (`IPTcpUdpPacket.TryParse`) | 13.4 / 12.2–15.0 |
| `rewrite ipv4` / `rewrite ipv6` (`PacketChecksums.TryRewriteTcpEndpoints`) | 20.3–20.8 / **63.7–69.8** |
| `copy + parse + rewrite` ipv4 / ipv6 | 42.7–50.0 / 86.1–92.8 |
| `copy + rewrite (no parse)` ipv4 / ipv6 | 35.2–38.3 / 77.5–84.1 |
| uncontended `lock`/`unlock` pair | 22.9–29.0 |
| `delta narrow` (16 × `u16`, 2 spans) / `delta wide` (8 × `u64` loads) | 27.6–28.3 / 22.1–22.6 |

Three conclusions, each load-bearing for the design:

1. **The parse is family-neutral** (13.4 vs 12.2–15.0 ns). Removing one walk from both legs removes
   ~10–15 ns from *both*, so the IPv6/IPv4 **ratio** barely moves — `(112 − 13) / (58 − 13) = 2.2×`.
   **This is a statement about the ratio, not about whether the row moves**: the ratio is no longer an
   acceptance criterion (the PRD restates AC 4 as per-leg no-regression with the ratio as a reading), and
   the *absolute* IPv6 reverse row does move, for a reason the decomposition below names.
2. **The family-specific cost is entirely inside `PacketChecksums.TryRewriteIpv6Tcp`** — measured +43 to
   +49 ns over `TryRewriteIpv4Tcp`. The code reason is `:157-173` (§2 site 4): a 32-byte `stackalloc`
   snapshot plus two passes over the 32 address bytes (16 `FillAddressWords` reads + 16 delta reads), where
   IPv4 keeps four words in locals (`:120-142`).
3. **The 2.0× row is not "the key/parse path"** — it is `copy + track + rewriter`, with no dictionary probe
   and no key at all. But it is **not** untouched by this task either: `TrackServerSequence`
   (`TcpRedirectDataPathBenchmarks.cs:100-104` → `TcpSequenceObservation.cs:83-86`) takes
   `TcpRedirectAssociation._sequenceGate`, and this probe measures an uncontended lock/unlock pair at
   **22.9–29.0 ns**. **Step 2 (the atomic trackers) therefore removes a measured term from this very row on
   both legs, before any checksum work is considered.** The planning pass initially missed that term and
   concluded "requirements 1–4 cannot move the row"; that conclusion is **withdrawn** — see §12 D7.

### 7.3 Candidate for the reverse-leg residual (optional commit)

If the row still reads slow **after** Step 2 removed the lock and Step 3 removed three walks, then — and only
then — `delta narrow` can be replaced by a **single-pass, wide-load** form that keeps the exact narrow
arithmetic (`acc += (u16 old ^ 0xFFFF) + u16 new` per word, extracted from `ReadUInt64BigEndian` loads). It
measured 22.1–22.6 vs 27.6–28.3 ns on the isolated delta and was proven **bit-identical over 2,000,000 random
32-byte pairs, 0 mismatches**. Removing the `stackalloc` snapshot and the second pass is the larger structural
half and is not separately measurable without editing product code (out of bounds for this planning pass).

The repository already owns the exact oracle for proving such a change:
`PacketChecksums.TryRewriteTcpEndpointsFullRecompute` (`PacketChecksums.cs:181-221`, the "independent
protocol oracle") plus the mutable-offset assertions required by `quality-guidelines.md:43`. The step is an
**optional commit** in `implement.md`, adopted only on a reading.

---

## 8. Hazards of each candidate mechanism

| # | mechanism | hazard | control |
|---|---|---|---|
| H1 | adapter intern table in the key | **slot reuse ABA**: a retired slot reissued to a different adapter makes an old `FlowKey` compare equal to a new one — a wrong-flow decision (fail-open) | allocate slots monotonically and **never reuse** within the process; document the ceiling |
| H2 | interning keyed by `(StableId, Generation)` | `WindowsAdapterInventory` bumps the generation for **every** adapter on **every** enumeration (`AdapterIdentity.cs:46`), so the slot space would grow by *adapters × refreshes* — minutes-to-hours exhaustion on a long-running gateway | key the intern on `StableId` alone and keep `OriginAdapterGeneration` as a **key field** (operator decision): the table is bounded by the adapter count, the ceiling is unreachable in practice, and the key's generation semantics are unchanged (`design.md` §3.4) |
| H2b | slot exhaustion | if an un-internable adapter's traffic were keyed with `NoSlot`, two adapters' keys would compare equal whenever their 5-tuples match (the multi-VM case `OriginAdapterId` exists for) and the second flow would resolve the first's state — **fail-open** | **refuse the adapter at generation build** (exclude it from the capture scope, log once), so no captured packet ever carries `NoSlot`; `AnAdapterThatCannotBeInternedIsRefusedNotAliased` pins it (`design.md` §3.3) |
| H3 | per-packet intern lookup | a string-keyed dictionary probe per packet would be **slower** than the reference-equality-biased `string.Equals` it replaces (§1.5) | intern once per `WindowsAdapter` instance (per capture generation), never per packet; `FlowKey.Create` takes the resolved slot |
| H4 | stale slot after an adapter disappears | a key for a gone adapter must still resolve its flow (traffic on a removed adapter is a transient) | the side table keeps the slot alive for the process lifetime and marks it retired; the key value is untouched |
| H5 | a **dropped** `OriginAdapterGeneration` (the design's withdrawn alternative) | changes which dictionary serves a lookup and weakens the adapter-recreation semantics the field exists for | not adopted: the generation stays and is compared (`design.md` §3.4); `FlowKeyEqualityKeepsTheGeneration` pins it |
| H6 | repacking `Endpoint` inside `FlowKey` | `Local`/`Remote` are consumed as `Endpoint` by `Policy` (`Policy.cs:22`), `SelfTrafficRegistry` (`:70`), `TcpRedirectTable` (`:33-42`, `:500`), `ClientResetInjector` (`:52-53`), `IsReverseOf` (`FlowDispatcher.cs:376-380`) and logging | keep the `Endpoint`-typed properties and make them computed over packed fields; never change `Endpoint`/`IPAddressValue` themselves (22 and 30 `src/` files reference them) |
| H6b | the computed `Local`/`Remote` on a warm path | `FlowDispatcher.IsReverseOf` would materialize **four** `Endpoint`s (192 B) per warm UDP proxy packet | add the packed `public bool IsReverseOf(FlowKey)` and delegate; `SelfTrafficKey.From` is neutral (it already materialized two). Watch `DispatcherBenchmarks`' proxy rows (`design.md` §2.4) |
| H6c | a self-consistent packing error (low/high swap, scope id in the wrong half) | every hash-agreement fact still passes (both sides pack the same way), while `Policy`, `SelfTrafficKey.From`, `IsReverseOf`, the association constructor and the logs silently change | `FlowKeyPackedRoundTripsEndpoints` (IPv4 / IPv6 / IPv6 + nonzero scope) and `ReverseSwapsEndpointsAndScopes` (`design.md` §2.5) |
| H7 | changing `PacketChecksums.TryRewriteTcpEndpoints`'s signature | it is a **public protocol-layer primitive** with an internal full-recompute oracle twin and direct callers in benchmarks/tests (`FrameRewriterBenchmarks.cs:63`, `BenchmarkFrameBuilderTests.cs:148`), and `quality-guidelines.md:14` makes its reject-without-mutating contract bedrock | add a layout-taking overload; keep the span-taking entry point and its validation for callers without a layout |
| H7b | the layout-driven rewriter cannot choose the write geometry | without an address family on the layout it would write 16 bytes into an IPv4 frame and return `true` — silent wire corruption | `PacketLayout` carries `byte Family` and the family-equality reject is retained for the argument addresses (`design.md` §4.1/§4.3) |
| H8 | layout consumed on a frame that is not the parsed frame | the redirect lanes stage frames into rented buffers (`TcpProxyCoordinator.Injections.cs:249`, `:253`) and rewrite in place | the layout's offsets are byte-identical for a staged copy of the same frame; the rewrite is same-length (endpoints + checksums only, `quality-guidelines.md:14`); the layout-driven rewriter still bounds-checks `frame.Length >= TransportEnd` and fails closed |
| H8b | a sequence read guarded by `TransportOffset + 4` | the 4-byte read spans `[+4, +8)`, so a short frame would throw out of `Slice` instead of rejecting | guard with `frame.Length >= TransportOffset + 8` (`design.md` §4.3) |
| H8c | `IsTcpSyn` comparing the transport to a literal | `PacketTransport.Tcp == 0` and `Udp == 1` (`IPTcpUdpPacket.cs:7-11`), so a literal `1` reads every TCP packet as non-SYN and silently stops redirect setup | compare against `(byte)PacketTransport.Tcp`; `LayoutSynTestMatchesTheSpanTest` covers both transports |
| H9 | view-based `TrackClient/ServerSequence` on a rewritten frame | the trackers must read the **pre-rewrite** sequence (`TcpSequenceObservation.cs:74-76`) | read the sequence from the frame **at the layout's offset before** `TryRewriteForwardLeg`, exactly as today; the layout only removes the re-parse and the length re-derivation |
| H10 | atomic tracker loses the "first write wins" rule | `Interlocked.CompareExchange` loops can write a *later* value | the CAS-max loop re-tests `IsSequenceAhead` against the freshly read current value inside the loop, exactly the lock's predicate (`TcpRedirectTable.cs:141`, `:156`) |
| H11 | `-1` sentinel collides with a real sequence | `uint` 0xFFFFFFFF is a legal sequence | store the tracker as `long` and widen the `uint` into it (`0xFFFFFFFF` → 4294967295), so `-1` is unreachable from a real value |
| H12 | slim context loses a string a cold path needs | `NdisPacketActionExecutor.cs:121` logs `Context.AdapterId` on **every** packet including warm ones | keep an interned **adapter** metadata reference on the context (known at classification, one instance per adapter, fetched by array index, no per-packet allocation) in addition to the claim-time **process** metadata |
| H13 | parse counter instrumentation on the production path | a per-packet counter write would violate the 0 B / hot-path posture | diagnostics-only `Action?` probe, read once into a local behind a null check, exactly F2's `FlowTable.GateHoldProbe` shape (`FlowTable.cs:129-136`) |
| H13b | a **process-wide** static probe under a parallel test runner | no `xunit.runner.json` / `[CollectionBehavior]` exists (verified), so sibling test collections would inflate the count and the fact would flake | make the probe `[ThreadStatic]`, assert on the driving thread, and assert the thread id is unchanged across the drive (`design.md` §4.4) |
| H13c | a walk-count harness that starts at the coordinator | the processor's `TryParse` is walk #1, so the red-before cannot be recorded | drive `CapturePacketProcessor.ProcessAsync` → dispatcher → coordinator, both legs (`design.md` §4.4) |
| H14 | the UDP reinjection target map and the key disagree about an adapter | a response reinjected toward the wrong adapter's handle/MAC | both derive from the same slot; the map is an array built from the same `scope` the slots were interned from (`DurableCaptureBundle.cs:311-323`, `design.md` §3.6) |

---

## 9. Dead ends (recorded so they are not re-explored)

1. **Keep `Endpoint` as-is, replace only the string.** 48 + 48 + 4 + 4 + 4 + 2 (slot) + 8 (generation) =
   118 → 120 B. Fails the ≤64 B requirement by 56 B; the interesting part of requirement 1 is unreachable
   without repacking (§1.1).
2. **Repack `Endpoint` itself** to 24 B (`ulong Low/High` + `uint ScopeId` + `ushort Port`) and drop its
   redundant `Family`. `Endpoint` appears in 22 `src/` files and is the element type of
   `TcpRedirectTable._byTranslatedListener` (`:170`), `_byReverse` (`:171`), `SelfTrafficKey` (`:68`),
   `Policy` matching (`Policy.cs:22`) and every log line; `IPAddressValue` (32 B, 16-byte alignment) would
   have to be reconstructed on every `Endpoint.Address` access. Blast radius far exceeds the win.
3. **`(StableId, Generation)` interning.** See H2 — unbounded growth across refreshes, and the operator
   keeps the generation as a key field for the adapter-recreation semantics.
3b. **Adding anything to `FlowKey` beyond the packed field set of §2.1.** The struct is 57 → **64** bytes:
   it already sits exactly on the contract, so a second slot, an extra flag byte or a wider generation
   pushes it to 72 and fails `FlowKeyFitsOneCacheLine`.
4. **A process-wide ambient intern table reached from `FlowKey.Create`.** Would keep every test call site
   compiling untouched (215 `FlowKey` references in 47 test files), but makes a mutable static part of the
   key's meaning and turns an accidental
   per-packet intern into a silent correctness surface. Rejected in favour of an explicit table threaded
   from composition, with a test-only default for the positional-constructor call sites.
5. **Counting header walks with a fake collaborator, or from the wrong entry point.** The F2 review's own
   lesson (`hot-path.md:568-573`): a gate built on a fake cannot see the real collaborator's cost. The probe
   must be attached to the real parser and driven through `CapturePacketProcessor.ProcessAsync` — a harness
   that starts at the coordinator cannot see walk #1 (`design.md` §4.4).
6. **Attributing the 2.0× to the key or the parse, or declaring the row immovable.** Both are measured
   false: the row has no key or dictionary probe, and it *does* contain `TrackServerSequence`'s lock, which
   Step 2 removes (§7.2, §12 D7).

---

## 10. What the PRD asks for that the code does not currently support

Summarised here, itemised with dispositions in §12 and `design.md` §13. Several were settled by the
operator's amended PRD (Notes) during the review pass:

- `FlowKey ≤ 64 B` needs a repacked key, not just a slot (§1.1) — **and it lands exactly on 64**, so nothing
  else may be added to the type (§2.1 of `design.md`).
- `OriginAdapterGeneration` must stay a key field for the adapter-recreation semantics the operator names;
  only the stable id is interned (H2, D4 below).
- `FlowContext.RemotePort` is redundant with `Key.Remote.Port` (all three construction sites, §4.3).
- PRD acceptance criterion 4 is now a **recorded reading** whose discharge is per-leg no-regression; the
  earlier "requirements 1–4 cannot move the row" conclusion is withdrawn (§7.2, D7 below).
- The research's four-site list omits `RecordServerSynAck` (per reverse packet) (§2 site 5).

---

## 11. Reproduce-before commands (current tree, `ca8d999`)

Every "before" number for this task must be re-taken on this tree because F2 changed the warm path
(`02fee48`) after the `2026-09-29-benchmark-coverage` artifacts were recorded. Budget: the three BDN jobs
below are `--job short`; the stability scenarios are the only long ones.

The walk-count harness is the one named in `design.md` §4.4 — `CapturePacketProcessor.ProcessAsync` →
`FlowDispatcher` → the real `TcpProxyCoordinator` (as both `reverseHandler` and executor) — because the
processor's `TryParse` is walk #1 and neither `HotPathAllocationGateTests` (coordinator entry) nor
`TcpRedirectInjectionBatchingTests` (`HandlePacketAsync`) reaches it. The probe is `[ThreadStatic]`: attach,
drive and assert on one thread.

```bash
# 0. tree identity + suite baseline
git rev-parse --short HEAD && git write-tree
dotnet test WinForward.slnx -c Release 2>&1 | tail -5          # record the padded summary line

# 1. the composed data-path rows (PRD AC 4's series; 16 rows, per-leg no-regression, readings only)
dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- --filter '*TcpRedirectDataPath*' --job short

# 2. the key/parse series (PRD AC 1/2's report-only companions)
dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- --filter '*FlowTableProductionShape*' '*FlowTableMiss*' '*FlowTableHit*' --job short
dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- --filter '*Parser*' --job short

# 3. the dispatcher warm shape (the 160 B gate's benchmark companion)
dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- --filter '*Dispatcher*' --job short

# 4. the exact gates that must stay green (Release only; Debug fails by design)
dotnet test WinForward.slnx -c Release --filter "FullyQualifiedName~HotPathAllocationGateTests"
dotnet test WinForward.slnx -c Release --filter "FullyQualifiedName~WarmPathGateTests"
dotnet test WinForward.slnx -c Release --filter "FullyQualifiedName~SweepAllocationGateTests"
```

Recorded baselines to compare against (same host class, `2026-09-29-benchmark-coverage`):
`tcp-redirect-data-path.csv` (the 16 rows of §7.1), `parser.csv`
(`Ipv4UdpTryParse` 11.5–12.4 ns, `Ipv6UdpTryParse` 10.6–11.6 ns — `IPTcpUdpPacket.TryParse` is *not* on the
TCP path rows, so this series is a shape witness, not a target), and `flow-table-production-shape.md`
(`ResolveSameOrientationHit` 92.7–117.3 ns, `ResolveReverseAliasHit` 134.8–138.4 ns; F2 later added
`ResolveWarmHit` and retired `ReadActivityClock`).

Host rule that governs every reading (`benchmarks/results/2026-09-29-benchmark-coverage/README.md:12-14`):
allocation bytes and counts are exact gates; timing and throughput are series comparisons and sub-2× deltas
are noise.

---

## 12. Discrepancy register (source document vs code; code wins)

| # | source claim | code reality | disposition |
|---|---|---|---|
| D1 | `FlowKey` is "~100+ bytes: two `Endpoint`s (each ~20 B)" (`research.md:193`) and "~100+ bytes … spans several cache lines" (`prd.md:15`) | measured `Endpoint` = **48 B**, `FlowKey` = **128 B** (two full cache lines), `FlowContext` = **168 B**, `FlowStateView` = **160 B**, `CapturedFlowPacket` = **224 B** | the research's ≤48 B target (`research.md:211`) and the PRD's ≤64 B (`prd.md:31`) both require repacking the endpoint storage inside the key, not merely replacing the string; `design.md` §2 carries the layout |
| D2 | "`Equals` runs `string.Equals(..., Ordinal)` on **every** dictionary probe" (`prd.md:15`, `research.md:195`) | true for the gated dictionaries, but F2's warm path does **not** probe a dictionary: `TryResolveWarm` does a slot read + seqlock + `TransportTuple` corroboration (`FlowTable.cs:183-207`). The full-key compare (string included) **does** run per warm forward TCP packet in `TcpRedirectTable.TryResolveByOriginal` (`:353`) | the requirement is restated as "no reference-typed field and no string comparison in `FlowKey`", proven structurally (reflection over the fields) rather than by poking at a probe count; the one warm full-key compare is named as the per-packet site the change actually touches |
| D3 | "`Equals`… plus `string.Equals`… and the struct spans several cache lines" implies the string compare is expensive per packet | on the hit path both sides are usually the **same `string` instance** (`adapter.StableId` from the live `WindowsAdapter` record), and `string.Equals(..., Ordinal)` short-circuits on reference equality (§1.5) | requirement 1's per-packet win is the **72-byte struct shrink**, not the comparison; the design attributes no timing claim to the comparison and the acceptance proof is structural |
| D4 | "a side table mapping slot ↔ stable id / **generation** / friendly name" (`prd.md:29`) implies the slot identifies a (stable id, generation) pair | `WindowsAdapterInventory` bumps the generation for **every** adapter on **every** enumeration (`AdapterIdentity.cs:46`), so `(StableId, Generation)` interning grows without bound on a long-running gateway | **settled by the operator** (PRD Notes): intern the stable id only, and `OriginAdapterGeneration` **stays a key field with integer equality** — the table is bounded by the adapter count, the ceiling is unreachable in practice, and the adapter-recreation semantics the field exists for are preserved. The design's "drop the generation" alternative is withdrawn (`design.md` §3.4, §13 D4) |
| D5 | "a small integer slot" (`prd.md:28`) | no slot type exists anywhere in the tree | new `AdapterSlotTable` in `WinForward.Core`, created once in `DurableCaptureBundle` and threaded into the durable `CapturePacketProcessor` (`Program.cs:284`), so it survives adapter-list refreshes |
| D6 | "the same frame is walked up to **four times**: the processor's `TryParse`, `IsTcpSyn`, `TryReadTcpSequenceAdvance`, and the checksum rewriter's full revalidation" (`prd.md:16`) | confirmed for the forward mid-flow path; on the **reverse** path the second walk is `RecordServerSynAck` (`TcpSequenceObservation.cs:35-43`, called at `Injections.cs:321`), which the research's list omits | the view must feed four consumers, and `RecordServerSynAck` is one of them; `TcpSequenceObservation.cs:20` (`RecordClientSyn`) and `TcpResetBuilder` are cold extras |
| D7 | (planning pass) "requirements 1–4 cannot move the IPv6 reverse row, so AC 4 is unreachable by them" | **withdrawn by measurement + code**: the row is `copy + TrackServerSequence + TryRewriteTcpEndpoints`, and `TrackServerSequence` takes `TcpRedirectAssociation._sequenceGate`, measured at **22.9–29.0 ns** uncontended — a term Step 2 removes from **both** legs. The family-specific residue (the parse is family-neutral at 13.4 vs 12.2–15.0 ns; the gap is `PacketChecksums.TryRewriteIpv6Tcp`'s 32-byte stack snapshot + two address-byte passes, `:157-173` vs IPv4's four local words at `:120-142`) is what remains afterwards | **replaced, and settled by the operator:** AC 4 is a **recorded reading** — discharge is per-leg no-regression, the improvement figure and the IPv6:IPv4 ratio are readings (this host's noise floor), the series is re-taken after Steps 2–5 with the mechanism named per step, and the single-pass IPv6 delta is an **optional commit** adopted only on a real reading (`design.md` §6, `implement.md` Step 6) |
| D8 | "the rewriter consume the view instead of re-walking headers" (`prd.md:33`) | `PacketChecksums.TryRewriteTcpEndpoints` is a public protocol-layer primitive with an internal oracle twin and direct callers in benchmarks and tests (`FrameRewriterBenchmarks.cs:63`, `BenchmarkFrameBuilderTests.cs:148`); `quality-guidelines.md:14` makes its validation contract bedrock | the view arrives through an **additive overload**; the span-only entry point keeps its validation and stays the oracle for tests |
| D9 | "`FlowContext` stacks more strings (`AdapterId`, `AdapterName`)" (`prd.md:18`) | correct, and `RemotePort` is a third redundancy — every construction site passes exactly `Key.Remote.Port` (`PacketFlowClassifier.cs:27`, `:40`) | `RemotePort` becomes `Key.Remote.Port`; the string fields move behind interned metadata (`design.md` §7) |
| D10 | "Process name/path and adapter names move behind **an** interned metadata reference created at claim time" (`prd.md:39-41`) | adapter identity is known at **classification** and is read on cold paths from *every* packet (e.g. `NdisPacketActionExecutor.cs:121` on a warm pass failure), while process identity genuinely appears only at claim | two references instead of one: a per-adapter metadata instance fetched from the slot table at classification (no per-packet allocation) and a claim-time process metadata; recorded as a deliberate deviation from the sketch, with the alternative (one reference) losing the adapter id on warm packets |
| D11 | "`uint?` → a single `long` per leg (−1 = unobserved)" (`prd.md:36-38`) | matches the code shape (`TcpRedirectTable.cs:128-129`), and the gate is also one `Lock` allocation per association (`:127`) | adopted as specified; the allocation removal is a per-association bonus, not a per-packet claim |
| D12 | research F4.2 "the classifier already produced a `PacketView`" (`research.md:214`) | true (`IPTcpUdpPacket.cs:19-27`), but it carries no `TransportLength` and no TCP flags, which the sequence observation and `IsTcpSyn` need | `PacketView` gains `TransportLength` (int) and `TcpFlags` (byte); the TCP sequence stays a 4-byte frame read at a view-derived offset (it must be read pre-rewrite anyway, H9) |
| D13 | "the transport tuple index" (`prd.md:46`) as the thing to re-prove | `_transportIndex` is a **gated** `Dictionary<TransportTuple, FlowState>` (`FlowTable.cs:21`, `:444`) consulted only on the gated path; the per-packet corroboration is `Matches` (`:203-207`) over the same `TransportTuple` shape | the re-proof is: `TransportTuple`'s field set and equality meaning unchanged, `CombineCanonical`'s slot value unchanged (or a packed variant proven equal), and the two warm validations re-exercised by named facts |
