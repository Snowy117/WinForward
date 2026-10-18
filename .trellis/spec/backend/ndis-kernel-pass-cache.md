# Kernel Pass Cache: the leased static-filter mirror

> How the opt-in kernel bypass mirrors policy-pass TCP flows into the NDISAPI driver's static filter
> table: the ownership and crash-safety rules, the exact rule encoding, the lease lifecycle, the
> failure posture, and the measured Windows cost. Read before touching
> `NdisKernelPassCache`, `NdisStaticFilterAbi`, `INdisKernelFilterTable`, `PacketPassKind`, or the
> `KernelPassCache` configuration key.

## Scope / trigger

The feature is a bounded mirror of decisions the user-mode policy has **already made**, not a second
policy. It covers a TCP flow whose stored decision is `FlowAction.Pass` (host or forwarded): after
the flow is observed in the user-mode pass path, the cache installs two oriented
`FILTER_PACKET_PASS` records in the driver's static table, so later packets of that flow take the
driver's normal network path instead of being read into the pump and reinjected. It does not decide,
block, redirect, or proxy anything.

## Ownership and crash safety

- **The static filter table is driver-global.** The docs describe one active rule set in
  `ndisrd.sys`; `STATIC_FILTER.m_Adapter` scopes a rule to an adapter, not to a process or a client
  handle. Enabling the feature claims the whole table: `Start` calls `ResetPacketFilterTable`
  before installing, replacing any other WinpkFilter client's rules. This is why the feature is
  opt-in and documented as exclusive; a second WinForward instance resets the first's table, and the
  first's next read-back sees `TableMismatch` and disables its bypass (capture keeps working).
- **PASS only.** No DROP, REDIRECT, PASS_RDR, or DROP_RDR action may ever be encoded. A crash then
  leaves at worst a benign pass-through rule, never a blackhole. The next **enabled** start clears
  the table before installing; with the feature off, WinForward deliberately does not touch the
  table (another client may own it).
- **The cache never survives a foreign table.** `ReadRulePacketCounts` re-encodes the recorded
  installation and compares the read-back except `LastReset` and the statistics block. A size,
  order, or field mismatch is `TableMismatch`, not a native error; the cache disables its bypass
  rather than overwrite an unknown table.

## ABI summary

- `STATIC_FILTER` is 184 bytes under `#pragma pack(push,1)`; the `STATIC_FILTER_TABLE` header is 8
  bytes (`TableSize`, `Padding`); a table of N records spans `8 + 184 × N` bytes. `NdisStaticFilterAbi`
  pins every size/offset and the encoder; the independent `StaticFilterTableOracle` in the test
  support duplicates the documented offsets so an encoder bug cannot agree with itself.
- One flow rule becomes two oriented records: `(A→B, A.port→B.port)` and `(B→A, B.port→A.port)`.
  Both carry `Adapter = 0`, `DirectionFlags = ON_SEND | ON_RECEIVE`, `FilterAction = FILTER_PACKET_PASS`,
  `ValidFields = NETWORK_LAYER_VALID | TRANSPORT_LAYER_VALID`, protocol TCP, exact addresses/ports,
  and all-ones masks. The header tuple selects the direction; the flag need not.
- IPv4 address fields are the raw four header bytes read as a little-endian DWORD (what the legacy
  sample gets from `in_addr`'s byte fields); ports are host-order `ushort`. IPv6 uses the 16-byte
  `IN6_ADDR` with an all-ones mask. IPv6 link-local addresses, and any address with a non-zero scope
  id, are refused because the native record has no scope id.
- **Never call `SetPacketFilterTable` with zero entries.** The user-mode SDK computes
  `sizeof(STATIC_FILTER_TABLE) + (m_TableSize - ANY_SIZE) * sizeof(STATIC_FILTER)` with `ANY_SIZE == 1`,
  so a zero-entry table underflows the unsigned byte count. Clearing is `ResetPacketFilterTable`.
- The deployed sidecar (`ndisapi.dll` 3.6.1) exports `SetPacketFilterTable`,
  `ResetPacketFilterTable`, `GetPacketFilterTableSize`, and `GetPacketFilterTable`. A missing export
  is `Unavailable`, never an exception out of the driver; `EnablePacketFilterCache` exists on 3.6.1
  but the product does not enable it (see "Measured behavior").

## Eligibility: `PacketPassKind`

`NdisPacketActionExecutor.PassAsync` is reached by several semantically different passes. Only the
dispatcher's claimed policy `FlowAction.Pass` may be mirrored; the dispatcher tags the packet:

| Origin | `PacketPassKind` | Cached? |
|---|---|---|
| Inline/warm policy pass, and the deferred attribution pass | `Flow` | yes (TCP only) |
| `SelfTrafficRegistry` pass | `SelfTraffic` | no (out of MVP) |
| Non-flow frames (ARP/ICMP/other L2/L3) | `NonFlow` | no |
| Reverse leg of a proxied UDP flow | `Reverse` | **never** |
| `TcpRedirectOutcome.NotRelevant` fallback on a proxy decision | `None` | **never** |

The reverse-pass row is the load-bearing one: that packet reaches `PassAsync` while the flow's stored
decision is `Proxy`, and a rule for it would bypass the proxy in the client→server direction. The
executor calls the observer only for `PassKind.Flow`; the cache additionally accepts only TCP with
non-zero ports. Do not infer eligibility from a bare `PassAsync` call or a flow-table lookup.

## Lease lifecycle

- **Observation path (pump thread, 0 B).** `OnPassPacket` canonicalizes the tuple, test-and-sets a
  marker in a fixed 4,096-slot table (FNV-1a, linear probe, `Interlocked`), and appends
  `(rule, seenAt)` to a preallocated 1,024-entry ring under `_ringGate`. No driver call, no flow
  lookup, no logging, no allocation. A marker hit is a no-op; a full ring/probe chain drops the
  observation and clears the marker (the flow keeps today's path; a later packet retries).
- **Worker (one `PeriodicTimer` every 250 ms, `QuiescenceScope`).** Read the per-rule counters,
  renew `LastActive` on a positive delta, accumulate `Bypassed`, expire rules idle for 2 s, drain
  candidates older than 1 s, evict the least-recently-active rule at the 128-flow cap, and apply at
  most one whole-table `Replace`/`Reset` per tick. Every driver call runs on this worker under the
  driver's control gate; a pump never issues a filter IOCTL.
- **A rule is a lease, not a permanent mirror.** Its next packet after removal re-enters user mode,
  is observed again, and re-arms the rule. The idle lease bounds the window in which a recycled TCP
  5-tuple could inherit a rule. A failed apply leaves the old table in place; the desired set stays
  dirty and retries next tick.
- **Pause/resume.** An empty capture scope pauses: the cache stops observing, clears its state, and
  resets the table. A non-empty scope resumes with an empty table; the durable flow table is not
  consulted. Generation refreshes keep the rules (adapter-agnostic, `m_Adapter = 0`).
- **Start/dispose.** `Start` resets the table (claiming it) and starts the loop. `DisposeAsync`
  stops the loop and resets the table while the driver is still open; if `Start`'s reset failed,
  the cache never claimed the table and dispose does not retry the reset (no foreign-table clear).
  The bundle disposes the cache first, before the sweeper/UDP/TCP teardown.

## Failure posture

| Condition | Result |
|---|---|
| Missing export or a refused initial reset | `Unavailable`; one warning; feature disabled; capture unchanged; no install attempted. |
| `Replace` refused | throttled warning; `_tableMatchesDesired=false`; retry next tick; 8 consecutive failures → best-effort reset + disable. |
| Read-back refused/mismatched | throttled warning; skip activity/expiry for that tick; 40 consecutive → best-effort reset + disable. |
| Counters move backwards | treat as a driver reset; rebaseline, no activity. |
| Ring/marker overflow | drop the observation, clear the marker; the flow keeps the user-mode path. |
| Cap reached | evict the least-recently-active rule; the evicted flow re-enters user mode. |
| Empty scope | pause + reset. |
| Crash | PASS-only rules remain until the next enabled start's reset; benign by construction. |

The cache is observational-adjacent, not a correctness dependency: every failure degrades to today's
capture path.

## Observability

- Per summary interval (10 s) and at dispose, `kernel.passCache` (Information) logs rules, active,
  bypassed, captured, reinjected, and two derived per-mille ratios:
  `crossings = 1000 × (captured + reinjected) / (captured + bypassed)` and
  `calls = 1000 × (readCalls + sendCalls) / (captured + bypassed)`. A zero denominator omits the
  ratios. No tuple, address, or port is logged.
- `kernel.passCache.reset` (Information) makes the start/pause/shutdown reset observable.
  `kernel.passCache.unavailable`, `.fault`, and `.disabled` are the warn/error arms.
- `RuntimeCounters` mirrors `kernelPassCache.{bypassed,applies,evictions,rejected,markerOverflow,
  readFailures,applyFailures,disabled}` into the heartbeat. The counters never influence a
  disposition.

## Configuration

`WinForward.KernelPassCache` is a boolean, default **false**. Enabled, it claims the driver's static
filter table (see ownership); disabled, no cache object exists, the executor observer is null, and
the driver table is never touched. The README key table and `appsettings.example.json` carry the
key. The strict reader rejects wrong casing/types like any other key.

## Measured behavior (Windows, 2026-10-18)

Evidence: `benchmarks/results/2026-10-18-ndis-filter-table-bypass/README.md`.

- Exports present; tables of 1/64/128/256 rules accepted; read-back and reset verified.
- THRU 4-stream/60 MB/s proxy arms: all on arms report 4 rules and ~0.88–0.90 M bypassed packets;
  the crossing ratio is 1.004–1.006 versus the uncached ≈2.0; sustained throughput reached
  479.85–479.93 Mbps in every on arm, against 346.98–469.74 Mbps off.
- **CPU did not improve.** Per delivered GB the on arms are 26.5–27.8 s/GB against 24.0 s/GB for
  the best off arm (30.2/34.7 for the two off arms that could not reach the target). The
  sequential static-filter evaluation for the non-matching proxied packets is the recorded
  hypothesis. The driver's `EnablePacketFilterCache` side probe raised on-arm CPU and depressed the
  off arm's throughput; the product does not enable it.
- Soak (feature on): THRU 479.87 Mbps/0 loss, DNS 3,001/3,001 answered, LOSS 9,001/9,001 arrived,
  0 foreignConnection.
- **Default stays false.** The feature is a transition-count and sustained-throughput win, not a CPU
  win at this table size and workload. A default flip requires a CPU-positive measurement.

## Open items

- UDP pass caching, `SelfTrafficRegistry` mirroring, and non-flow static classes are separate
  follow-ups; the current eligibility deliberately excludes them.
- The driver's filter-evaluation cost at larger tables (and whether `EnablePacketFilterCache` has
  safe invalidation semantics) is unresolved; do not enable it without a measurement and an
  invalidation test.
- IPv6 link-local/scoped flows are refused; a scope-aware rule would need a different native
  mechanism.
- The table is global; multi-client deployments need an ownership protocol (the current contract is
  "one WinForward instance owns the table").
- 32-bit WOW64 is not supported by the product's ABI (`AssertManagedLayout` requires 64-bit).

## Tests

`NdisStaticFilterAbiTests` (layout/encoder/oracle), `NdisKernelFilterTableTests` (driver seam,
read-back, error mapping, counters), `NdisKernelPassCacheTests` (lease lifecycle, eviction,
pause/resume, fault escalation, summary, 0 B observation), `NdisPacketActionExecutorPassKindTests`
and `FlowDispatcherPassKindTests` (the eligibility table), `KernelPassObservationGateTests` (0 B),
`KernelPassCacheConfigurationTests`/`AppSettingsExampleTests` (the key), and
`KernelPassCacheCompositionTests` (wiring, scope, dispose order). Keep the ABI tests and the
independent oracle in lockstep with `Common.h`.
