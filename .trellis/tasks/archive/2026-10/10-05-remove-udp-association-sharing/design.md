# Design — Remove UDP association sharing

Companion to `prd.md`. Anchors below were read during planning, not assumed.

## 1. Shape of the change

Three movements, in this order of risk:

1. **Configuration surface removal** — small, mechanical, and it is what an existing deployment
   notices first: the keys leave the schema and the loader's unknown-property rule does the rest
   (§3).
2. **Ownership move**: the association (control connection + `UDP ASSOCIATE` + relay socket) becomes
   the flow transport's own property instead of a pooled, leased resource. This is the substance.
3. **Machinery deletion** — the pool, control-association, lease, evidence, capability sampler, reuse
   mode, and their bookkeeping (`UdpAssociationPool.cs` 584, `UdpControlAssociation.cs` 396,
   `UdpAssociationCapability.cs` 108, `UdpAssociationLease.cs` 73 lines).

The target architecture already exists inside the pool as its `ReuseMode.Off` branch: `Acquire` falls
through to `Create(server, set, isPrivate: true)` (`UdpAssociationPool.cs:333-353`), and `SharesFor`
returns false for `Off` (`:372-377`). The deletion makes that branch the only branch and moves its
ownership down to the transport.

## 2. Target architecture

**Per-flow association, owned by the transport.**

- `Socks5UdpTransportFactory.CreateAsync(ProxyTarget, ct)` resolves the SOCKS5 target, dials an
  authenticated `Socks5ControlConnection`, sends `UDP ASSOCIATE`, and binds the relay socket in the
  returned relay family — the sequence `UdpControlAssociation` performs today, minus the pool, the
  lease refcount, the evidence record, and the capability verdict.
- A small per-flow association type (proposal: `Socks5UdpAssociation`) owns the control connection,
  the negotiated relay endpoint, the **watchdog on the control stream**, and the
  `UdpAssociationLostException` contract. `Socks5UdpTransport` owns that association plus its relay
  socket and disposes both with the flow. The pool's in-place re-association and recovery disappear;
  what remains is "the control stream ended → this flow is lost".
- `UdpAssociationTable` stays as the alias registry (original key ↔ `(local socket endpoint, relay
  endpoint)`), because it is what makes a relay reply classifiable as the reverse of a stored flow and
  what the expiry sweep walks. Its `UdpAssociation` rows keep `OriginalKey`, `RelayAlias`,
  `Generation`, `LastActivityUtc`; with no re-association the generation becomes a per-flow
  correlation id rather than a re-association epoch — cheap to keep, and it is what the trace events
  already print.
- `ValidatedConfiguration` loses `UdpAssociationReuse`, `UdpAssociationMaxPerServer`,
  `UdpAssociationFlowsPerAssociation`; `UdpProxyComposer.CreateAssociationPool` and
  `DurableCaptureBundle`'s pool construction/disposal go; the coordinator keeps only the transport
  factory it already takes.
- `UdpAssociationReuseMode.cs` is deleted, and with it the `auto`/`always`/`off` semantics, the
  capability verdict, and the sampler.
- `UdpProxyCoordinator.ReceiveWindowRetireFloor` (64) is the named floor the receive-window pool's
  retire allowance is derived from — `max(ReceiveWindowRetireFloor, sessionCapacity / 16)` — and it
  replaces the removed `4 × DefaultUdpAssociationFlowsPerAssociation` at the identical value (that
  constant was 16, so the allowance is unchanged and the two are interchangeable numerically).

## 3. No migration surface

The loader rejects unknown properties (`JsonUnmappedMemberHandling.Disallow`), so removing a key makes
an existing `"udpAssociationReuse": "auto"` fail closed at parse with the loader's diagnostic naming
the JSON path of the offending property. That is the whole compatibility story, by decision
(2026-10-05): **the product has never been released**, so no migration-only DTO property, no removal
message, and no dual spelling is carried. The same reasoning retires R3's `proxyServer` legacy
machinery — its migration-only property, its rename diagnostic, its two-row theory, and the README
clause that documents the rejection all go, leaving the old spelling to the unknown-property path.

## 4. Failure semantics

With no pool there is no in-place recovery, so the per-flow association's death is the flow's death:

- control stream ends mid-flow → the transport refuses further datagrams with
  `UdpAssociationLostException` → the coordinator removes the slot as `AssociationLost` **without
  arming the setup cooldown** (the existing path, `UdpSessionSetup.HandleSetupFailureAsync`) → the
  flow re-establishes on its next datagram.
- The refused send rethrows the **same stored** `UdpAssociationLostException` instance the watchdog
  recorded (`Socks5UdpAssociation.Fault` → the transport's fail-closed check), so a consumer sees the
  original death — its inner exception and stack — rather than a fresh exception per datagram; the
  fault is published once, and the dead control connection is closed before its reference is given up
  so a throwing close cannot strand the socket.
- A send or receive that fails for any other reason keeps today's fail-closed behaviour (counted,
  rate-limited warn, no pass downgrade).

Observability disposition (revised during planning — the counters were not all orphaned):

| Symbol | Disposition | Why |
| --- | --- | --- |
| `udpAssociationLost` / `udp.association.lost` | **keep** | It now means "this flow's own association died", which is exactly the failure mode R5 asks to be explicit; the counting site does not change. |
| `udpAssociationRecovered` / `udp.association.recovered` | remove | In-place re-association no longer exists. |
| `udpAssociationFallbacks` / `udp.association.fallback` | remove | The capability sampler no longer exists. |
| `udpAssociation=<generation>` log field | keep | It is the flow's association correlation id. |
| `udpResponseSourceMismatch` / `udp.response.foreign_source` | keep | A server may still answer from a different endpoint; with sharing gone that is legitimate behaviour, and the counter keeps the observation without implying misdelivery. |

## 5. Harness

- `--reuse` and its enum/row parameter are deleted; no dedicated refusal message replaces them, since
  the harness already fails unknown arguments closed and the historical commands live in annotated
  READMEs rather than in anything re-runnable verbatim.
- `udp.churn` and `udp.burstEstablishment` keep their remaining columns (per-flow and `--target
  local`); their rows keep `target`, the per-flow accounting, and the two observed counter groups.
- `udp.sessionBudget` loses its pooling half (`require-pooling`, `associationsPerSession`, the pooled
  verdict fields) and keeps the budget/descriptor/retention half, which still measures something real
  under per-flow associations — the shape the deletion ships.
- Historical directories (`2026-09-28-udp-reuse`, `2026-10-05-udp-reuse-ownership`,
  `2026-10-05-local-target`) gain a note that their `off`/`auto`/`always` columns describe a removed
  feature. Numbers are annotated, never edited.

## 6. Documentation and decision record

- README: the shared-association section and the `udp.association.fallback` event row go; the UDP
  resource-shape prose is rewritten for one association per flow (two local ports per live flow —
  the control connection and the relay socket — with the kernel-buffer estimate unchanged); the
  guarantee is stated plainly: **one flow per association, so the reply-ownership ambiguity a shared
  association has cannot occur**; the local target is presented as the recommended DNS placement.
- Decision record (`research/decision-record.md` in the task dir, feeding parent R5): the evidence
  (4–7 of 48, undetectable same-destination misdelivery), the rejected alternatives (concurrent
  sharing, exclusive-lease warm pool and its stale-reply window, L1's payload-inspection clause), the
  shipped guarantee, and the reopen condition — an L2 transport (UoT v2 connect, or VLESS + XUDP with
  per-flow session framing) plugged into the seam R3 built.

## 7. L2 readiness note (deliverable)

`research/l2-readiness.md`, written for the task that implements UoT v2 / VLESS rather than for this
one. It records, with anchors:

- **The seam**: `IUdpProxyTransport` / `IUdpProxyTransportFactory` (`ProxyTarget` → transport) and the
  composite `UdpTransportFactory`, all transported-neutral since R3
  (`src/WinForward.Runtime/UdpProxy/UdpTransportContracts.cs`); a new kind is a new union member, a
  new factory, and one dispatch branch.
- **What an L2 transport must implement**: the transport contract, `IUdpExchangeCounters` (or the
  one-shot retirement class is lost), self-traffic registration before the first datagram, the
  skip-class fault rules (`ConnectionReset`), and — the part SOCKS5 UDP cannot do — **per-flow
  framing**: a session/stream identifier per flow so replies are demultiplexed by identity rather
  than by socket.
- **The two candidate protocols and what each costs**: UoT v2 (a `CONNECT` per destination with
  length-prefixed datagrams over TCP — per-flow correctness, TCP head-of-line coupling, one
  connection per destination) versus VLESS + XUDP (per-flow session ids over one muxed connection —
  correctness and sharing, at the cost of a mux implementation and a protocol-specific framing).
- **Configuration shape options** for a third target kind (a `type` discriminator on a target list
  versus a sibling list), with the R3 precedent: the kind lives in the declaration, and the rule field
  keeps naming a target.
- **Server-side support**: sing-box's `udp_over_tcp` (v2) and VLESS `packet_encoding: xudp` +
  `multiplex`; the 09-06 finding that sing-box has no UDS/named-pipe inbound.
- **Open questions the L2 task must answer**: head-of-line cost on the local leg, framing overhead per
  datagram, whether one muxed connection or one connection per destination, and how the 09-06 mux
  research (still in planning, premised on a local mux to the SOCKS5 server) should be repurposed.

## 8. Risks and rollback

| Risk | Mitigation |
| --- | --- |
| Deleting 1.1k lines from the shipped UDP path regresses association setup or teardown | the target shape is today's `off` branch, already exercised by the recorded `--reuse off` columns; keep the setup/teardown tests green and add the 1:1 ownership + control-death tests |
| Per-flow associations cost two local ports and ~7 ms per flow | priced by `benchmarks/results/2026-10-05-local-target/`'s `off` column (48/48, 96/144/192 handshakes, ≈91 KB/session); the DNS population is served by local targets, which is documented as the recommendation |
| A deployment silently keeps the old (unsafe) posture | the removed keys fail closed at parse through the unknown-property rule |
| Historical comparability is lost | the surviving harness runs the same scenarios and the historical directories say what changed |

Rollback: revert the change set; the feature was configuration-driven, and no persisted state changes.

## 9. Rejected alternatives

- **Keep sharing behind an opt-in.** A path that is unsafe for a connection-oriented server and
  undetectable in the same-destination case should not be reachable by a configuration key.
- **Default to per-flow but keep the machinery.** Dead machinery with a config surface is a liability:
  it must keep passing the gates, and the next reader cannot tell whether it is supported.
- **Exclusive-lease warm pool.** Safe against concurrency but leaves the stale-reply window described
  in `prd.md`'s background, which needs the deferred reply-drop decision to close.
- **L1 destination-keyed placement.** Fixes routing inheritance only; reply ownership stays ambiguous
  for two flows to one destination.
