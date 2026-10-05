# UDP association sharing: response ownership, routing granularity, and the cost model

All children archived; nothing in flight. The task map below records the four children and their
evidence.

## Goal

Decide, on measured evidence, how WinForward may share authenticated SOCKS5 UDP associations with a
connection-oriented server, and land the policy that follows from that evidence.

The current `auto` default assumes a NAT-like server: any flow may join any association, and the server
routes and answers per datagram. Neither assumption holds for sing-box.

- **Routing granularity.** One association carries exactly one route decision, taken from the first
  datagram it ever carries, sniffing included; later datagrams are neither re-sniffed nor re-routed
  (`sing` `protocol/socks/handshake.go` UDP ASSOCIATE branch, sing-box `route/route.go`
  `routePacketConnection`). A DNS flow sharing an association with a non-DNS flow inherits that flow's
  outbound and can be forwarded in cleartext to its hardcoded resolver — the reported field symptom,
  including the `udp.association.recovered … flows=N` churn a hijack-owned association produces when a
  sibling sends a non-DNS datagram.
- **Response ownership.** The same server-side socket keeps one peer address, refreshed on every read,
  and writes every reply to it (`sing` `common/bufio/bind.go:129-176`;
  `protocol/socks/packet.go:92-100`). Measured against sing-box 1.14.1: two live flows per association →
  45–50 % of replies reach the flow that asked; eight flows → 9 %; zero packet loss throughout.

WinForward checks neither. `UdpProxySession.TryGetReceiveSource` (`:395-414`) decodes a reply's source
and `InjectResponseAsync` (`:417-421`) uses it as the injected frame's source without ever comparing it
to `Flow.Remote`, so a reply that the server delivered to the wrong flow is injected toward that wrong
flow's client as a legitimate frame.

The measured justification for sharing is real but differently sourced than assumed: first-response p50
12.513 → 3.246 ms and churn bytes/session −42 % (`benchmarks/results/2026-09-28-udp-reuse/`) come from
amortizing the control connect (2.46 ms) and ASSOCIATE (4.49 ms total) against a 13.1 µs relay socket —
not from multiplexing flows onto one association. The harness that produced those numbers cannot see
either defect (`UdpChurnScenario.cs:392-405` ignores the arriving flow;
`LoopbackSocks5UdpServer.cs:306-342` reproduces the last-sender write path faithfully).

## Requirements

- R1 — Trustworthy measurement before any policy change. Everything below is priced against it.
  Child: `10-05-harness-response-ownership`.
- R2 — Reply-ownership observability in the product: a reply whose decoded source does not match the
  flow's own destination is counted rather than silently injected as legitimate. Whether such a reply is
  also dropped is a separate decision, because some protocols legitimately answer from a different
  endpoint; the counter is not optional.
- R3 — Take DNS off the SOCKS5 path (`L0`): port-53 flows are served by a local transport bound for the
  local `dns-in` endpoint, with the flow's original destination spoofed as the reply source. The
  rationale is stronger than when first proposed — hijacked DNS is the one flow class that is
  latency-critical, semantics-critical, and exposed to misdelivery whenever it is not hijacked — and it
  is a net performance win, since it replaces a 7 ms connect + ASSOCIATE per query with a loopback send.
- R4 — A sharing policy that survives the evidence. Candidates to be chosen on R1's numbers:
  destination-keyed placement (`L1`), an exclusive-lease warm pool, a per-server `routeScope`
  declaration, or a more conservative default. Note that `L1` fixes routing inheritance but not reply
  ownership, and that `L1`'s proposed QUIC clause is **rejected as specified**: it requires payload
  inspection in a transport-layer component, cannot be made reliable across QUIC versions, and encodes a
  server-side routing configuration into the client. That knowledge belongs in configuration
  (`routeScope`) or is made unnecessary by structure (exclusive lease, `L2`).
- R5 — Record the decision and the condition that would reopen it, including when `L2` (UoT v2 connect,
  or VLESS + Mux.Cool + XUDP) becomes the right answer: it is the only shape that keeps both routing and
  reply ownership correct while still sharing a control connection.

## Task map

| Task | Delivers | Status |
| --- | --- | --- |
| `10-05-harness-response-ownership` | R1: corrected ownership measurement, reusable columns, re-run baseline | archived |
| `10-05-reply-ownership-observability` | R2 | archived |
| `10-05-local-dns-transport` | R3 | archived |
| `10-05-remove-udp-association-sharing` | R4, R5 | archived |

Parent/child here is not a dependency system: each child is independently verifiable, and where one must
wait for another the ordering is written in the child's own PRD.

## R4 evidence pointer (recorded 2026-10-05, from child `10-05-local-dns-transport` R7)

`benchmarks/results/2026-10-05-local-target/` prices a local placement beside the sharing columns
under R1's corrected accounting, on the same DNS-shaped churn (48 flows per wave, three waves) and
burst shapes:

| Column | own / misdelivered per wave | SOCKS5 control connections + ASSOCIATE replies | first-response p50 | bytes/session |
| --- | --- | --- | --- | --- |
| `--reuse off` | 48 / 0 | 96, 144, 192 | 156–165 ms | ≈91 KB |
| `--reuse auto` | 4–7 / 41–44 | 3 (pooled during warmup) | 4.2–5.1 ms | ≈13.4 KB |
| `--target local` | 48 / 0 | 0 / 0 | 3.1–4.1 ms | ≈7.5 KB |

Burst: the local column answers 48/48 with zero handshakes, against `auto`'s 3/48 with 251 of 256
background flows misdelivered and `off`'s 48/48 at 304 handshakes.

Two facts for R4's decision, neither of which chooses a policy here:

- For the DNS-shaped population the local placement is strictly better than sharing on correctness,
  latency, and footprint at the same time: the win sharing was introduced for (the handshake
  amortization) is obtained without the reply-ownership risk.
- Sharing's remaining sweet spot is short non-DNS request/response flows. R3 takes the DNS
  population off the SOCKS5 path wherever it is configured, so R4 must price that remainder
  explicitly instead of inheriting the assumption that concurrent flows per association are free.

## R4/R5 resolution (recorded 2026-10-05, child `10-05-remove-udp-association-sharing`)

**Decision: remove UDP association sharing entirely.** A proxy-decided SOCKS5 UDP flow now owns its own
authenticated association — its control connection, its `UDP ASSOCIATE`, its relay socket — and that
association lives and dies with the flow. The three configuration keys, the pool / lease /
control-association / capability machinery (1,161 lines), and the harness's `--reuse` knob are gone.
The candidate list under R4 is therefore resolved by rejection rather than by selection:

- **Destination-keyed placement (`L1`)** — rejected: it fixes routing inheritance but leaves reply
  ownership ambiguous for two flows to one destination, which is precisely the case the R2 counter
  cannot see.
- **Exclusive-lease warm pool** — rejected: it removes concurrency but keeps a stale-reply window (a
  late reply for a finished flow is written to whichever flow has since attached), and closing that
  window needs the reply-drop decision this PRD defers.
- **Per-server `routeScope` declaration** — rejected: with the DNS-shaped population served by local
  targets (R3), the remaining population has no use for concurrent sharing, so the declaration would
  buy a knob nothing needs.
- **A more conservative default** — taken to its conclusion: the unsafe path is not reachable at all,
  rather than reachable-behind-a-configuration-key.
- **`L1`'s QUIC payload-inspection clause** — permanently rejected (payload inspection in a
  transport-layer component; not reliable across QUIC versions; encodes server-side routing in the
  client); it must not return as a "small addition".

**Shipped guarantee** (README): one flow per association, so the reply-ownership ambiguity a shared
association has cannot occur.

**Reopen condition (R5):** if carrying many flows over one control connection becomes a real
requirement — a resource need, not a latency one — it is met by an **L2 transport** (UoT v2 `CONNECT`,
or VLESS + XUDP session ids over one muxed connection) plugged into the seam R3 built
(`UdpTransportFactory` composite, transport-neutral `PeerEndpoint` / `UdpTransportDatagram`,
`ProxyTarget` as the union a new kind joins). Association reuse does not return without its own PRD
and new evidence.

**Evidence:** `benchmarks/results/2026-10-05-no-association-sharing/` (the shipped series: 48/48 per
churn wave with exactly one control connection and one ASSOCIATE reply per flow; the local column
48/48 with zero handshakes), the annotated historical series, and the child's
`research/decision-record.md` plus `research/l2-readiness.md` for the L2 work.

## Constraints

- WinForward targets Windows for capture, but the benchmark/stability harness is managed-only and must
  keep running on Linux.
- Every commit passes the repository quality gate: `dotnet format WinForward.slnx --severity info
  --verify-no-changes --no-restore` (empty output), `dotnet build -c Release` (zero warnings),
  `dotnet test -c Release` (green), and `jb inspectcode` with zero `<Issue>` entries.
- Never commit or publish the user's real configuration: `__RUNTIME_*__` placeholder secrets, personal
  domains, and LAN addresses must be redacted from any artifact.
- The rejected `L1` QUIC clause must not be reintroduced as a "small addition" later; if payload
  inspection is ever proposed again, it needs its own PRD and an explicit decision.

## Acceptance criteria

- [x] Every policy choice in R4 is justified in this task's artifacts by a number produced under R1, not
      by an assumed server model.
- [x] Each child task's acceptance criteria are verifiable on their own, and the child is archived only
      with its evidence attached.
- [x] The sharing policy that ships states, in `README.md`, what a shared association does and does not
      guarantee for a connection-oriented server. (Resolved in substance by the R4/R5 resolution: no
      sharing ships, and the README states the guarantee of the shipped one-flow-per-association
      architecture.)
- [x] Superseded numbers in `benchmarks/results/` are marked as superseded rather than silently replaced.
- [x] Final integration review: the shipped default, the documented guarantee, and the measured evidence
      agree with each other.

## Out of scope

- Changes to sing-box or `sing`; this is a WinForward placement and transport decision triggered by an
  interaction with a connection-oriented server.
- Payload inspection for protocol classification (see R4).
- Re-deriving the routing-granularity mechanism: it is established by the handoff and re-checked in the
  parent Background above.

## Notes

- Evidence package for planning: `/tmp/findings-socks5-udp-response-ownership.md` plus the reproduction
  scripts `/tmp/sbtest/exp2.py`…`exp7.py`. These live outside the repository; if they are needed for
  implementation, they must be re-created under the child task's `research/` directory with the user's
  real configuration absent.
- The experiments ran on sing-box 1.14.1 (sing v0.9.4) while the user's box runs the testing channel
  (sing v0.9.7-0.20260929150544). The SOCKS5 reply path is line-for-line unchanged between the two, but
  the result has not been reproduced on the exact build in use — the child task should confirm it.
- Related planning: `09-06-local-mux-transport` R4 asks how a pooled/mux transport slots into
  `IUdpProxyTransportFactory` composition. R3's selector seam is the same decision point; whichever lands
  first should own the seam so it is not designed twice.
