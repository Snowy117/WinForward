# Hot-Path Conventions (Zero-Allocation Packet Pipeline)

> Established 2026-08-28 by task 08-28-perf-hotspots. Verified by the WinForward.Benchmarks harness:
> allocation/GC counters are exact, ns/pps on the dev box carry ±50 % noise.

**Scope**: any code on the per-packet path — capture processing, parsing, classification, dispatch,
pass/block execution, flow-table probes, SOCKS5 UDP encode. Cold edges (config, process attribution,
socket setup, logging, tests) are exempt. Read this hub first; each child owns one topic.

## The pipeline's shape

capture pump → one parse per frame → classify → `FlowDispatcher.DispatchAsync` (the synchronous warm
shape, else `DispatchSlowAsync`) → pass/block/proxy execution → in-place reinjection or the SOCKS5 UDP
send. Two rules give the family its name:

- **No async state machine on the steady-state path, and no allocation on it.** A fat async method
  heap-allocates per call even when it completes synchronously (~193 B/op measured). Every new
  per-packet stage either runs inline on the warm entry or falls to the slow path — there is no third
  shape. See [warm-path-dispatch.md](./warm-path-dispatch.md).
- **Zero is a gate, not a goal.** Every steady-state path carries an exact 0 B gate that must be able
  to fail, in a host that makes its reading meaningful. See
  [allocation-gates.md](./allocation-gates.md).

## Packets are structs with pinned shapes

`FlowContext`, `CapturedFlowPacket`, `NativeFrameHandle` and `PacketLayout` are `readonly record
struct` with `[StructLayout(LayoutKind.Auto)]`; completion is enum-driven (`PacketAction`), never
closures or delegates. The F4 (2026-09-30) sizes, asserted exactly by
`FlowKeyShapeTests.StructSizesForDiagnostics`:

| Type | Bytes |
|---|---|
| `FlowKey` | 64 |
| `FlowContext` | 80 (key + two interned metadata references) |
| `CapturedFlowPacket` | 152 (incl. the 16-byte layout) |
| `FlowStateView` | 96 |
| `PacketView` | 96 |
| `Endpoint` | 48 |

`PacketLayout` is 16 (`PacketLayoutTests.PacketLayoutFitsSixteenBytes`). The key's shape and the
parse's proofs are [packet-shape-and-flow-identity.md](./packet-shape-and-flow-identity.md).

## Raw addresses only on the hot path

`IPAddress` (class) never appears in parse/classify/flow-key code; use `IPAddressValue` (UInt128 bits,
IPv4 in the low 32 bits, family + scope), converting through `ToIPAddress()`/`From(IPAddress)` on cold
edges only. `Endpoint` stores `IPAddressValue` by value, and since 2026-08-30 (task
08-30-udp-alloc-jumbo) the SOCKS5 UDP product type follows: `Socks5UdpDatagram.DestinationAddress` is
`IPAddressValue?` and `IUdpProxyTransport.SendSpanAsync` takes an `Endpoint` — no framework addresses
remain on any UDP datagram path.

## IPv4 masks stay inside the low 32 bits

`IPPrefix.Contains`/`Normalize` mask through the private helper
`IPPrefix.PrefixMask(int prefixLength, AddressFamilyKind family)`: IPv4 = `0xFFFFFFFF << (32 - len)`
(/0 → 0, /32 → `0xFFFFFFFF`); IPv6 = left-aligned 128-bit. A left-aligned mask over low-32 IPv4 bits
matches everything — `IPv4PrefixesMatchOnlyTheirPrefix` is the regression lock.

## Span-writing codecs and lifetime

- Datagram encode uses `TryEncode(..., Span<byte>, out written)` into a reusable buffer
  (`Socks5UdpTransport._sendBuffer`); allocating overloads exist for tests only.
- Every rent is released exactly once and never retained past its owning scope; `lease.Memory` is
  valid only until release. The lease, pool, reinjection and GC-posture rules are
  [native-lease-and-pool-lifetime.md](./native-lease-and-pool-lifetime.md).
- Benchmark gates are stated in allocation bytes and GC counts; ns/pps deltas under ~2× are noise on
  the dev box. Which numbers may be quoted, and how a row proves it measured the path it names, is
  [benchmark-methodology.md](./benchmark-methodology.md).

## Where the rules live

| Child | Subject | Read it when |
|---|---|---|
| [warm-path-dispatch.md](./warm-path-dispatch.md) | The synchronous entry, reverse diversion, self-traffic split, warm resolves, activity bucket, sequence trackers | You change the dispatcher's warm shape, a reverse/original probe, or the activity stamp |
| [packet-shape-and-flow-identity.md](./packet-shape-and-flow-identity.md) | The packed key, the one parse, the 16-byte layout and its validity stamp | You touch `FlowKey`, `FlowHash`, `PacketLayout`, `TryParse` or the layout-driven rewriter |
| [native-lease-and-pool-lifetime.md](./native-lease-and-pool-lifetime.md) | Native leases, in-place reinjection, pooled flow/setup state, GC-off posture, `gc-soak` | You add a rent site or change a pool, the injection lane, or the CLI GC config |
| [allocation-gates.md](./allocation-gates.md) | Closure hoisting, the exact window, the tiering host contract, injected-allocation checks | You write or touch an exact 0 B gate |
| [allocation-gate-host-lumps.md](./allocation-gate-host-lumps.md) | The residual host lump, its signature, the per-gate proof procedure | An exact gate failed with an unexplained delta |
| [udp-datagram-path.md](./udp-datagram-path.md) | The per-datagram send, the disposal guard, exchange counters, recorded UDP anchors | You change the established-datagram path or a UDP anchor |
| [relay-pump-and-checksums.md](./relay-pump-and-checksums.md) | The relay pump loop, the stall window, `PacketChecksums` | You change the byte pipe or the checksum arithmetic |
| [benchmark-methodology.md](./benchmark-methodology.md) | Rows that measure the path they name, the out-of-process real-dial harness | You add a benchmark row, scenario or gate |

## Where things moved

Every old top-level section, its `### 1..7` block where it had one, and every old `Contracts` item
number, mapped onto the children. Only the sections that carried information survived; a template
section whose only content was its heading (`Scope / Trigger` … `Wrong vs Correct`) is gone, and the
rule it restated lives in the child named here.

| Old section / number | Now in |
|---|---|
| `Contracts` 1 (raw addresses) | hub, "Raw addresses only on the hot path" |
| `Contracts` 2 (IPv4 masks) | hub, "IPv4 masks stay inside the low 32 bits" |
| `Contracts` 3 (no async state machines; socket sends) | warm-path-dispatch.md, "No async state machines on the steady-state path"; socket sends in udp-datagram-path.md, "The socket send" |
| `Contracts` 4 (struct shapes and sizes) | hub, "Packets are structs with pinned shapes" |
| `Contracts` 5, 6 (lease lifetime, in-place pass reinjection) | native-lease-and-pool-lifetime.md |
| `Contracts` 7 (span codecs) | hub, "Span-writing codecs and lifetime" |
| `Contracts` 8 (flow keys hash flat) | packet-shape-and-flow-identity.md, "Flow keys hash flat" |
| `Contracts` 9 (measurement discipline) | benchmark-methodology.md, "Measurement discipline" |
| `Contracts` 10, 11 (parse once; defaulted layout refused) | packet-shape-and-flow-identity.md |
| `Contracts` 12 (sequence trackers) | warm-path-dispatch.md, "Sequence trackers are atomic, not locked" |
| `SOCKS5 Path Contracts` §1–§2 (signatures; TCP redirect / UDP session triggers) | [tcp-redirect-transform.md](./tcp-redirect-transform.md), "Cold-edge addresses are cached once"; udp-datagram-path.md, "Transport defaults and the setup queue" |
| `SOCKS5 Path Contracts` §3 (contracts: forwarded cost, UDP budgets, framework cost, throughput, benchmark gate) | warm-path-dispatch.md (proxy branch, forwarded cost); udp-datagram-path.md, "The recorded setup anchors (per-flow shape)"; relay-pump-and-checksums.md (throughput anchor) |
| `SOCKS5 Path Contracts` §4 (matrix) | udp-datagram-path.md, "Outcomes a reader must be able to predict" (outcome rows only) |
| `SOCKS5 Path Contracts` §5–§7 | the owning children above |
| `Relay pump and checksum contracts` §1–§6 | relay-pump-and-checksums.md |
| `Closure-hoisting and allocation-gate contracts` §1–§6 | allocation-gates.md |
| `Native pool family, pooled flow/setup state, and GC-off posture` §1–§7 | native-lease-and-pool-lifetime.md; §"FlowTable warm cache" and §"FlowTable live-slot registry + chunked sweep" in warm-path-dispatch.md |
| `Real-Dial Measurement Harness` §1–§7 | benchmark-methodology.md, "The real-dial harness: the server must not share the measured process" |
| `UDP per-flow association: counters, evidence cost, and the recorded anchors` §1–§7 | udp-datagram-path.md |
| `Measurement self-checks` §1–§3 | benchmark-methodology.md, "Rows must measure the path they name" |
| `Allocation-gate stability…` §1–§5 | allocation-gates.md, "Allocation-gate stability: the tiering host contract, the gate shape, and the repeat-run proof"; its residual half and the repeat-run proof in allocation-gate-host-lumps.md |
| `The residual exact-gate lump…` §1–§6 | allocation-gate-host-lumps.md (its §6 is now "Addendum (2026-10-06 flaky sweep)") |
| `Warm-path lock-free resolve, the activity bucket and the self-traffic split` §1–§3 | warm-path-dispatch.md |
| `F4 flow identity, parse-once and slim context — spec-row → proof map` | **deleted** as a proof map: its durable rules are hub `Contracts` 4, packet-shape-and-flow-identity.md and warm-path-dispatch.md; its evidence archive is `benchmarks/results/2026-09-30-flow-key-parse-once/` |
| `The owner-table epoch coalescer` §1–§4 | [traffic-policy-lifecycle.md](./traffic-policy-lifecycle.md), "Process Attribution: The Owner-Table Cache" |
| old line ranges `hot-path.md:1057-1060` (the residual-lump disposition) | allocation-gate-host-lumps.md |

## Frozen citations under `benchmarks/results/**`

Dated evidence is never rewritten, so these citations resolve through the table above:

- `2026-09-28-udp-reuse/README.md` — `hot-path.md` §3 (the churn/Noop anchors) is now
  udp-datagram-path.md, "The recorded setup anchors (per-flow shape)"; its §6 bar for out-of-process
  real-dial runs is the same section.
- `2026-09-30-expiry-sweep-bounded-pause/README.md` — `hot-path.md` §4 (the suite-level loop is not
  the criterion; the stale suite total) is allocation-gate-host-lumps.md, "The per-gate proof
  procedure".
- `2026-09-30-warm-path-lock-chain/README.md` — `hot-path.md` §3 (warm shape) and §"FlowTable
  pooling" / §"FlowTable warm cache" / §"FlowTable live-slot registry + chunked sweep" are
  warm-path-dispatch.md and native-lease-and-pool-lifetime.md, "Pooled flow state".
- `2026-10-01-attribution-off-pump/gate-stability.txt` — `hot-path.md:1057-1060` and the
  "residual-lump section" are allocation-gate-host-lumps.md.
- `2026-10-06-aot-instruction-set/README.md` — "the standing `hot-path.md` contract" (zero
  allocation on the checksum path) is relay-pump-and-checksums.md plus this hub's pipeline rules.
