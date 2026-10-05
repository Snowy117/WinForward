# Remove UDP association sharing

Child of `10-05-udp-association-sharing-correctness` (R4 + R5). Sibling R3
(`10-05-local-dns-transport`) is archived: local targets now serve proxy-decided UDP/53 flows with one
socket per flow and no association.

## Goal

Delete association sharing entirely. A proxy-decided SOCKS5 UDP flow is always served by its **own**
authenticated association — its own control connection, its own `UDP ASSOCIATE`, its own relay
socket — and that association lives and dies with the flow. Record why sharing was removed and what
would reopen the question.

## Background — the evidence that decided it

- **Sharing misdelivers at concurrency.** R1's corrected harness
  (`benchmarks/results/2026-10-05-udp-reuse-ownership/`): 4–7 of 48 churn flows per wave receive their
  own reply (41–44 are answered by a sibling); the burst scenario answers 3 of 48 with 251 of 256
  background flows misdelivered; at 500 flows/s, 156 of 30,000.
- **The misdelivery is structurally undetectable in the case that matters.** R2's counter compares a
  reply's declared source with the receiving flow's destination, so two flows sharing a destination
  are indistinguishable: a zero count does not prove safety, and no detector can demote a server on
  this evidence.
- **The population that motivated sharing no longer needs it.** Sharing was introduced to amortize the
  control connect plus `UDP ASSOCIATE` (~7 ms) and the per-flow ports over the DNS-shaped majority.
  R3 moved that population to local targets — measured at 48/48 flows answered per wave with zero
  control connections and ≈7.5 KB/session (`benchmarks/results/2026-10-05-local-target/`) — and the
  same directory's `--reuse off` column already prices the post-deletion shape: 48/48 with 96/144/192
  handshakes and ≈91 KB/session.
- **Every remaining sharing shape needs extra machinery to be safe.** Concurrent sharing is unsafe by
  construction. An exclusive-lease warm pool (one live flow per association, the association kept warm
  between flows) removes the concurrency but keeps a stale-reply window: a late reply for a finished
  flow is written to whichever flow has since attached, and closing that window needs the reply-drop
  decision the parent PRD deferred. Deleting the mechanism is the only shape that is correct without
  new machinery.
- **Decision (user, 2026-10-05).** Performance does not justify a large correctness reduction. The
  mechanism goes; a future need for "one control connection carrying many flows" is met by an L2
  transport with per-flow framing (R7), not by association reuse.

## Requirements

- **R1 — Remove the configuration surface.** `udpAssociationReuse`, `udpAssociationMaxPerServer`, and
  `udpAssociationFlowsPerAssociation` leave the schema outright, and the removal carries **no**
  migration property or removal diagnostic: the product has never been released and carries no
  backward-compatibility contract (decided 2026-10-05). A configuration that still sets one fails
  closed at parse through the loader's existing unknown-property rule, whose diagnostic names the
  JSON path of the offending key. R3's `proxyServer` legacy machinery — the migration-only DTO
  property, its rename diagnostic, and its test — is removed in the same change for the same reason:
  the rename is complete and the old spelling is now simply an unknown property.
- **R2 — Remove the machinery.** `UdpAssociationPool`, `UdpControlAssociation`,
  `UdpAssociationLease`, `UdpAssociationEvidence`, `UdpAssociationCapability` (and its sampler),
  `UdpAssociationReuseMode`, and the per-server set/capability bookkeeping are deleted. The flow's
  transport dials, ASSOCIATEs, owns, and disposes its own control connection and relay socket.
- **R3 — Keep what serves the per-flow path.** The alias table and its reverse-leg classification, the
  expiry sweep, `SelfTrafficRegistry` registration, the relay-socket contract
  (`udpRelayReceiveBufferKb`, `SIO_UDP_CONNRESET`, source validation), the reply-ownership counter and
  its rate-limited warn (`udpResponseSourceMismatch` / `udp.response.foreign_source` — a server may
  still answer from a different endpoint, and with sharing gone that is legitimate server behaviour
  rather than evidence of misdelivery), and the one-shot retention class.
- **R4 — Remove the observability that lost its subject.** `udpAssociationRecovered`,
  `udpAssociationFallbacks`, and the `udp.association.recovered` / `udp.association.fallback` events
  go: in-place re-association and the capability sampler no longer exist. Three things stay, because
  they did not lose their subject: `udpAssociationLost` / `udp.association.lost` (a flow's own
  association dying is exactly the failure mode R5 makes explicit), the `udpAssociation=<generation>`
  field (the flow's association correlation id), and the reply-ownership counter with its warn (a
  server answering from a different endpoint is legitimate behaviour once sharing is gone, and the
  observation still belongs in the record).
- **R5 — Make association death explicit.** With no pool there is no in-place re-association: a
  control connection that ends fails its flow closed, the flow re-establishes on its next datagram,
  and the failure semantics are counted and documented (the path `UdpAssociationLostException`
  already feeds).
- **R6 — Harness and history.** The `--reuse` knob and the pooling verdicts lose their subject; churn
  and burst keep measuring what survives (per-flow and local columns), and every historical result
  directory gains a note that its reuse columns describe a removed feature. No historical number is
  edited.
- **R7 — Documentation and decision record (parent R5).** README states the shipped guarantee for a
  connection-oriented server — one flow per association, so the shared reply path's ambiguity cannot
  occur — drops the sharing/fallback documentation, and presents the local target as the recommended
  DNS placement. The decision record states the evidence, the rejection of the warm pool and of L1's
  payload-inspection clause, and the reopen condition: a future need to carry many flows over one
  control connection is satisfied by an **L2 transport** (UoT v2 connect, or VLESS + XUDP with
  per-flow session framing) plugged into the seam R3 built (the `UdpTransportFactory` composite and
  the transport-neutral `PeerEndpoint`/`UdpTransportDatagram` contract).

- **R8 — L2 readiness note (deliverable, decided 2026-10-05).** `research/l2-readiness.md` prepares the
  UoT v2 / VLESS task without constraining its design: the seam it plugs into
  (`UdpTransportFactory` composite, transport-neutral contract, `ProxyTarget` as the union the new
  kind joins), what an L2 transport must implement — per-flow framing above all, plus
  `IUdpExchangeCounters`, self-traffic registration, and the skip-class fault rules — the two
  candidate protocols with their costs (UoT v2's per-destination `CONNECT` over TCP versus VLESS +
  XUDP's session ids over one muxed connection), the configuration-shape options for a third target
  kind, the server-side support surface, and the questions the L2 task must answer itself.

## Acceptance criteria

- [ ] No configuration key, symbol, event, or counter of the sharing feature is reachable, and no
      migration-only property or removal diagnostic exists for the removed keys or for the
      `proxyServer` rename; a config that still sets any of them fails closed at parse with the
      loader's unknown-property diagnostic naming the key's JSON path.
- [ ] A proxied UDP flow dials, ASSOCIATEs, serves, and tears down its own association; a test locks
      the 1:1 shape and asserts that both the control connection and the relay socket are released
      with the flow.
- [ ] A control connection that dies mid-flow fails the flow closed (no datagram passed as a
      fallback) and the flow re-establishes on its next datagram; the case is tested.
- [ ] Churn and burst run on the surviving columns with the per-flow accounting identity intact, and
      each historical result directory carries the "removed feature" note.
- [ ] README states the guarantee and the DNS recommendation; the decision record and the L2 readiness
      note are committed.
- [ ] Gates: Release build zero warnings, tests green, `dotnet format --verify-no-changes` empty,
      `jb inspectcode` zero `<Issue>`.

## Out of scope

- Implementing L2 (UoT v2 / VLESS + XUDP): this task removes sharing, records the decision, and writes
  the readiness note; the transport itself is its own task.
- Re-litigating the reply-drop posture: with sharing gone, a mismatched reply source is legitimate
  server behaviour, so counting and injecting stays. Changing that is its own decision.
- The local target's own semantics, which R3 shipped and this task does not revisit.
- Historical numbers under `benchmarks/results/**`: annotated, never edited.
