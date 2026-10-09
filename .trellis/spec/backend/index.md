# Backend Development Guidelines

> Coding guidelines for WinForward — a single-repo .NET solution with no frontend or database layer,
> so this is the only package/layer spec directory besides `guides/`.

**Read before you code.** Each document states its own scope and trigger in its first lines; start
with the hub of the family you are touching, then open the child that owns your rule. A hub is a real
document (scope, cross-cutting invariants, topic map), never an empty table of contents.

---

## Cross-Cutting

| Guide | Description |
|-------|-------------|
| [Directory Structure](./directory-structure.md) | Project graph, layout, the 400-effective-line ceiling, file/type relationship, naming, TestSupport rules |
| [Quality Guidelines](./quality-guidelines.md) | Code standards, forbidden patterns, analyzer-suppression policy, the two commit gates, testing requirements |
| [Error Handling](./error-handling.md) | Fail-closed semantics, the complete list of bounded exemptions, failure classification, configuration diagnostics |
| [Logging Guidelines](./logging-guidelines.md) | `Microsoft.Extensions.Logging` `[LoggerMessage]` logging, levels, console format, event identity, privacy rules |
| [Async Lifetime](./async-lifetime.md) | The `QuiescenceScope` contract: admission, seal/join drain, work leases, the `Run` door, WF fire-and-forget rules |
| [Test Stability](./test-stability.md) | Flake-proof test contracts: locked fake snapshots, real synchronization points, staged interleavings, process-wide counters and budgets |
| [Traffic Policy](./traffic-policy-lifecycle.md) | The two policy domains, non-flow pass, gateway-LAN authoring, loop prevention and self-traffic registration |
| [Idle Expiry Sweep](./idle-expiry-sweep.md) | The single expiry timer: wiring, two cadences, chunked retirement, activity buckets and retention bounds |

## Packet Path — [hot-path.md](./hot-path.md) family

The zero-allocation pipeline: what a packet is made of, what it may cost, and how that is proven.

| Guide | Description |
|-------|-------------|
| [Hot-Path Conventions](./hot-path.md) | **Hub.** The pipeline's shape, the rules that hold across it, and the topic map |
| [Warm-Path Dispatch](./warm-path-dispatch.md) | The dispatcher's warm entry: resolution, classification, the sync fast path and its gates |
| [Packet Shape And Flow Identity](./packet-shape-and-flow-identity.md) | The per-packet value types, what the flow key is made of, and why neither is re-derived per stage |
| [Native Lease And Pool Lifetime](./native-lease-and-pool-lifetime.md) | Leases, in-place reinjection, the pool family, pooled flow/setup state, and the GC-off posture |
| [Allocation Gates](./allocation-gates.md) | What an exact zero-allocation gate is and how to write one that can actually fail |
| [Allocation-Gate Host Lumps](./allocation-gate-host-lumps.md) | The one failure mode of a correct gate — a host lump inside a measured window — and how it is bounded |
| [UDP Datagram Path](./udp-datagram-path.md) | The SOCKS5 UDP path end to end, its disposal guard, and its allocation anchors |
| [Relay Pump And Checksums](./relay-pump-and-checksums.md) | The TCP relay's per-direction pump loop and the checksum primitive it feeds |
| [Benchmark Methodology](./benchmark-methodology.md) | How to build a benchmark row or gate that measures what it claims, and which numbers may be quoted |

## TCP Local Redirect — [tcp-local-redirect.md](./tcp-local-redirect.md) family

| Guide | Description |
|-------|-------------|
| [TCP Local Redirect](./tcp-local-redirect.md) | **Hub.** The redirect pipeline, the two wire shapes, the cross-cutting invariants |
| [Redirect Transform](./tcp-redirect-transform.md) | The host IP-swap transform, the forwarded DNAT-to-local shape, the reverse hook, mid-flow data, accept-loop identity |
| [Client Close Injection](./tcp-client-close-injection.md) | The abnormal-end RST\|ACK shape, its sequences, the clean-end close drain, and what a failed injection does |
| [SYN Setup Admission](./tcp-syn-setup-admission.md) | The pump-side fast paths, the capacity gate and its RST, the bounded pending-SYN index, the setup cooldown |
| [Redirect Teardown Grace](./tcp-redirect-teardown-grace.md) | The atomic retire, the TIME_WAIT tombstone, late-packet consumption, held flows under idle expiry |
| [Relay Lifecycle](./tcp-relay-lifecycle.md) | How a relay pump ends, fault observation, the stall window, dispose ordering and single-flight |

## UDP Relay — [udp-relay.md](./udp-relay.md) family

| Guide | Description |
|-------|-------------|
| [UDP Relay](./udp-relay.md) | **Hub.** One datagram's path, which anomalies are skips rather than failures, the family invariants |
| [Response Reinjection](./udp-response-reinjection.md) | Reverse routing of relayed replies and the foreign-source counter |
| [Relay Transport](./udp-relay-transport.md) | The transport seam, ASSOCIATE across address families, relay socket setup, endpoint/buffer sizing |
| [Flow Setup](./udp-flow-setup.md) | Where datagrams wait, the global memory budget, the datagram TTL, and the setup cooldown |
| [Session Lifecycle](./udp-session-lifecycle.md) | Session lifetime, teardown reasons, the fail-closed send drop, and the two retention classes |
| [Association Ownership](./udp-association-ownership.md) | One flow, one authenticated association: the dial, the watchdog, disposal order, exchange evidence |
| [UDP Over TCP](./udp-over-tcp.md) | The opt-in UoT v2 connect-mode carriage: one flow, one stream connection, one destination |

## Windows NDISAPI Interop — [windows-ndisapi.md](./windows-ndisapi.md) family

| Guide | Description |
|-------|-------------|
| [Windows NDISAPI Interop](./windows-ndisapi.md) | **Hub.** Adapter identity, enumeration vs captured handles, native-call gates, DLL resolution, the shared request ABI |
| [Capture Refresh](./ndis-capture-refresh.md) | When and why the capture view is rebuilt: bound-list change, periodic re-enumeration, fingerprints, health-triggered refresh |
| [Batched Capture](./ndis-batched-capture.md) | The batched read ABI, read-shape self-heal, pump batching, the arrival-signal idle wait, buffer ownership |
| [Batched Send](./ndis-batched-send.md) | Batched reinjection: the Pass lanes and the redirect lanes, flush points, ordering, capacity backstops, failure posture |

## Measurement Harness — [measurement-harness.md](./measurement-harness.md) family

| Guide | Description |
|-------|-------------|
| [Measurement Harness](./measurement-harness.md) | **Hub.** The harness contract's one definition point, and the topic map |
| [Record Contract](./measurement-record-contract.md) | How a key is spelled, written, published and loaded; the path alphabet; the three states; sink policies |
| [Run Lifecycle](./measurement-run-lifecycle.md) | What a run may publish when it fails or is torn down — the `ObjectDisposedException` vocabulary |
| [Lane Seam](./measurement-lane-seam.md) | Who owns each counter between the engine, the policy and the transport |
| [Judgement](./measurement-judgement.md) | Comparison classes, proving a refactor behaviour-neutral, and the `achievedRate` caliber |
| [UDP Source Census](./measurement-udp-census.md) | What `udpSummary.sources[]` and `sourceOverflow` count |
| [Tooling](./measurement-tooling.md) | Which script or gate checks what, and the analyzer's contract and outputs |

---

## How To Write In This Library

These rules are why the library is navigable. They apply to every edit here, including the
`trellis-update-spec` flow.

1. **One topic per document.** The filename says the topic; the first lines say the scope and when to
   read it. A document past ~400 lines is no longer read in one sitting — split it into a hub plus
   children named for their subject, never by source line range.
2. **A rule is one bullet, imperative, with its reason compressed to a clause.** The reason stays when
   it prevents a rediscovery (the incident behind the rule, the measurement that set a threshold, the
   hardware that verified it) and goes when it is narration.
3. **The seven-section contract block** (`Scope / Trigger`, `Signatures`, `Contracts`, `Validation &
   Error Matrix`, `Good/Base/Bad Cases`, `Tests Required`, `Wrong vs Correct`) **is for cross-layer and
   infrastructure contracts**, per `trellis-update-spec` — not a per-task ritual. Write only the
   sections that carry information; a section whose only content is its heading is deleted.
4. **Provenance is compressed, not deleted.** Keep the date or task id where it justifies a rule;
   delete a line that records only that something changed. A superseded design is mentioned only where
   its rejection is still a live constraint.
5. **Anything that can rot silently keeps an anchor to the code that owns it** — a threshold, a
   default, an allowlist, an allowed-exception list names its owning type or file.
6. **No duplication, inside or across documents.** A rule lives once; other documents link to it.
   Where two documents must both state a fact, one owns it and the other repeats it in half a line
   with a link.
7. **Code examples are real.** Every snippet, symbol, path and event name exists in the tree. No
   invented examples and no placeholder types.
8. **English**, with one exception: a non-English fragment survives only when it names a real artifact
   (a log field, a section title cited from code).
9. **A hub carries a "Where things moved" table** whenever its family replaced an older document, so
   the citations frozen in `benchmarks/results/**` stay resolvable.

---

> History: on 2026-08-29 the former 470-line `windows-ndisapi.md` monolith became
> `windows-ndisapi.md` + `tcp-local-redirect.md` + `udp-relay.md` + `traffic-policy-lifecycle.md`, and
> the never-filled `frontend/` layer and `database-guidelines.md` template were removed. On
> 2026-10-09 the five documents that had outgrown the ceiling — `hot-path.md` (1655 lines),
> `udp-relay.md` (1029), `measurement-harness.md` (607), `windows-ndisapi.md` (566) and
> `tcp-local-redirect.md` (470) — were revised against the code and split into the families above;
> `idle-expiry-sweep.md` was split out of `traffic-policy-lifecycle.md`, and `directory-structure.md`
> was translated to English.

**Language**: All documentation is written in **English**.
