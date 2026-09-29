# UDP association reuse and session resource budget

Parent: `08-30-proxy-perf-stability`.

## Goal

Remove the structural cause of the time-dependent UDP degradation: every proxied UDP flow owns one
authenticated SOCKS5 control connection and one relay socket with a 512 KiB kernel receive buffer,
retained 2–3 minutes after its last datagram, so sustained flow churn accumulates TCP ephemeral
ports, sockets, handles and kernel memory until new flows fail and established flows slow down.

Deliver: **one authenticated SOCKS5 association serving many UDP flows (default on)**, with passive
per-server capability detection and an automatic sticky fallback to per-flow associations, plus a
bounded per-session socket/port/kernel-buffer budget and the observability to see all of it.

## Confirmed facts (evidence)

### Measured (this session; probe over `UdpProxyCoordinator` + real `Socks5UdpTransportFactory` + loopback SOCKS5 server)

- 20 new UDP flows/s for 60 s ⇒ 1,201 live sessions, 3,674 open fds (≈3.06/session), one native
  receive-window lease held per session (all allocated outside the 256-slot pool).
- Sessions are retained until idle 120 s **and** the next 60 s sweep tick: the last flow created at
  t=60 was released at t≈180. All resources are released afterwards (fds 3,674 → 73, outstanding
  window leases → 0) — resource amplification, not a leak.
- Capacity rejection is invisible at `info`/`warn`: with capacity 200, 1,300 datagrams were silently
  rejected (0 info, 0 warn, trace only).

### Code facts

- Per session: one control TCP connection + one relay UDP socket + one `SelfTrafficRegistry` tuple
  (`src/WinForward.Runtime/Socks5/Socks5UdpTransport.cs:177-203`); teardown disposes both
  (`:381-410`).
- Relay socket receive buffer is a fixed 512 KiB constant, explicitly not a config knob
  (`src/WinForward.Runtime/Socks5/Socks5UdpTransport.cs:104-109`, applied `:190`).
- UDP session capacity is hard-coded 16,384 with no configuration surface
  (`src/WinForward.Runtime/UdpProxy/UdpProxyOptions.cs:19`, composition
  `src/WinForward.Cli/UdpProxyComposer.cs:56-66`).
- Retention: UDP relay idle timeout 2 min, sweeper interval 1 min
  (`src/WinForward.Runtime/IdleExpirySweeper.cs:46-49`).
- TCP has an explicit ephemeral-port budget (default 4,096, max 8,192, 2 ports/flow, warning above
  the default: `src/WinForward.Configuration/ConfigurationModels.cs:100-115`, `:237-251`); UDP has no
  port budget, a 4× larger capacity, and the same 2 ports/flow.
- The proxy path already has an injectable control-connection factory seam (`createControl`,
  `src/WinForward.Runtime/Socks5/Socks5UdpTransport.cs:161-170`), used by tests only.
- `RelayAlias` uniqueness (local relay endpoint + relay endpoint) is claimed per flow
  (`src/WinForward.Runtime/UdpProxy/UdpSessionSetup.cs:99-112`).

### Prior decisions revisited here

- **2026-08-07** (`archive/2026-08/08-07-winforward-proxy/design.md:253`): one association per flow,
  "aliases are never shared because reverse routing would become nondeterministic". That rejection
  covers **sharing one relay socket/alias**; sharing the control connection while keeping one local
  relay socket per flow preserves deterministic reverse routing (the local port still identifies the
  flow). This task therefore does not contradict it.
- **2026-09-21** (`archive/2026-09/09-21-session-creation-cost/research/reconciliation-and-decision.md:90,153`):
  control-connection reuse is the #1 measured lever, but a product/protocol decision: shared
  connections change upstream attribution (all flows = one authenticated client), change failure
  domains, and need server-capability detection + fallback. D1 below resolves that decision.

### Protocol facts (RFC 1928 §7)

- A UDP association is bound to its control TCP connection and terminates with it.
- The relay validates the client's **source IP** only; the source **port** is unconstrained, so
  several local sockets may share one association. Per-implementation port pinning is handled by R2.

## Requirements

- **R1 Reuse (default on)**: one authenticated SOCKS5 association serves many UDP flows; each flow
  keeps its own local relay socket, so `RelayAlias` semantics and reverse routing are unchanged.
  Association reuse is the default; it can be disabled per run (`off`) or pinned (`always`).
- **R2 Capability detection + sticky fallback**: passively detect a server that pins one client
  source port per association, flip that server to per-flow associations, keep the verdict for the
  run, and report it once (warn + counter). No datagram may be lost because of the flip.
- **R3 Failure domain**: association death is detected by a watchdog, affects a bounded number of
  flows (fan-out cap), recovers by re-associating and re-pointing leased sockets when the relay
  address family is unchanged, and fails closed with a counted reason otherwise. Association loss
  must not arm the 1 s setup-failure cooldown (recovery must not be delayed).
- **R4 Resource budget**: per-session relay receive buffer becomes a validated configuration value
  with a documented global arithmetic warning; UDP session retention is shortened so the steady-state
  footprint follows the active flow set rather than a 2–3 minute tail; the UDP session capacity
  becomes configurable (default unchanged) with the same port-budget warning style as
  `tcpFlowCapacity`.
- **R5 Observability**: every UDP rejection path (capacity, budget, setup, association loss) is
  counted and rate-limited-warned; the heartbeat reports associations, leased flows, relay sockets,
  estimated relay receive-buffer bytes, fallback state and the new counters.
- **R6 Hot path**: the established-datagram path stays allocation-free; no pool interaction per
  datagram (`HotPathAllocationGateTests` stays at 0 B).
- **R7 Deployment safety**: `udpAssociationReuse: off` reproduces today's behaviour exactly (per-flow
  associations) and is a supported, tested mode — it is the rollback lever.

## Acceptance criteria

- [x] Default configuration, churn soak at ≥100 new UDP flows/s for ≥1 h, stated in the **two terms it
      actually has**: (i) **bounded over time** — the live population tracks `rate × retention` instead
      of accumulating, so relay sockets, fds, ephemeral ports and the estimated kernel receive-buffer
      bytes stay flat as the run proceeds (the `udpSessionBudget` soak asserts the steady-state ceiling
      and the drain-to-zero); and (ii) **sub-linear in the concurrent flow count** — at the default caps
      one authenticated association serves 16 flows, so the control-connection half of the descriptor
      cost is amortized (`associations ≈ sessions / udpAssociationFlowsPerAssociation`). One relay
      socket per live flow is by design (R1/I2), so the descriptor floor is ≈1 per live flow and the
      kernel-buffer estimate stays `rate × retention × relay buffer`; the soak reports both. The 1 h run
      is recorded with `--require-pooling`, so its verdict states which term held. Zero datagram loss,
      and no first-response latency regression against the recorded baseline.
- [x] Permissive fake server: N flows share K associations (K ≪ N), responses never cross wires, and
      per-flow client MAC / origin adapter are preserved for forwarded flows.
- [x] Source-port-pinning fake server: detection fires within its window, the server flips to
      per-flow associations, no datagrams are lost around the flip, and the verdict is sticky for the
      run (asserted, not just logged).
- [x] Association drop: watchdog detects it, the blast radius is bounded by the fan-out cap,
      re-association succeeds, the teardown reason is counted, no socket or lease is orphaned, and no
      setup cooldown is armed.
- [x] `udpAssociationReuse: off` passes the existing UDP suites unchanged (rollback mode).
- [x] Pool drain: no lease outlives its owner; the bundle's dispose releases the drained UDP
      coordinator before the association pool and the native pools (the relative order of the latter
      two is immaterial), and after disposal the coordinator reports zero sessions and the pool zero
      associations/leases; a faulted association never surfaces as an unobserved task exception.
- [x] Allocation gates unchanged; `udp.burstEstablishment` and `udp.lossRate` matrices re-run green
      after the retention/buffer changes.

### Completion verification (2026-09-29, session 37)

Independently re-verified before archiving, because the task's boxes were never ticked although the work
had landed: all four acceptance test suites exist (`UdpAssociationPoolTests`,
`UdpAssociationRecoveryTests`, `Socks5UdpTransportLeaseTests`, `UdpAssociationCapabilityTests`), all six
UDP config keys are documented in `README.md` (lines 112–143), the pooling/retention/failure-taxonomy
specs were updated (`udp-relay.md` pooling contract + `hot-path.md`, `error-handling.md`,
`traffic-policy-lifecycle.md`, `async-lifetime.md`), the burst/udp/churn matrices are archived under
`benchmarks/results/2026-09-28-udp-reuse/`, and the 1-hour acceptance run is recorded there
(`step4-session-budget-1h.jsonl`, measured 2026-09-29): verdict row
`{"passed":true,"retentionBounded":true,"poolingCovered":true,"requirePooling":true,"failures":[]}` —
360,000 flows, 0 lost / 0 rejected datagrams, steady population 4,500 vs the 6,200 ceiling
(`retentionDiscriminating: true`), 282 shared associations vs the 296 ceiling, 1.070 descriptors per
live session, first-response p50 0.878 ms, drain to zero sessions/associations/leases, no capacity-block
or setup-failure events. The task's D3 follow-up (relay-socket sharing) remains intentionally out of
scope and is recorded in the report.

Ordering note: `udpAssociationReuse: auto` is off-equivalent until Step 3 lands passive detection, so
the default-configuration soak above becomes a *sharing* acceptance only with Step 3. The Step 2
acceptance runs exercise sharing explicitly (the harness scenarios hard-code `always`); Step 3 runs
them on the production default (`auto`, share + detect).

## Out of scope

- Sharing one relay socket across flows with the same remote endpoint (the 2026-08-07 determinism
  objection; needs per-destination claiming and is a later step).
- TCP relay reuse and the local mux transport (`09-06-local-mux-transport`).
- SOCKS5 wire changes or non-standard protocol extensions.

## Key decisions

- **D1 (2026-09-28, user)**: reuse is **default on with automatic sticky fallback**, not per-server
  opt-in — the benefit must be immediate; the compatibility/attribution risk is carried by the
  product and mitigated by R2/R3/R7.
- **D2 (proposed, pending approval)**: this task ships R4 (budget/retention/observability) together
  with R1–R3, because pooling alone does not fix the reported symptom for same-remote churn (DNS
  towards one resolver keeps one socket per concurrent flow).
- **D3 (proposed, pending approval)**: relay-socket sharing (Axis B) stays a separate follow-up task.
- **D4 (2026-09-28, user — Phase D)**: the soak measured the shared head at 16 × 16 = 256 flows per
  server against the acceptance load (≈4,500 live sessions at 100 flows/s with the 45 s retention), so
  94 % of flows fell back to private per-flow associations and the descriptor shape was ≈1.95/session —
  the per-flow shape, with the per-flow TCP controls the diagnosis blamed for ephemeral-port pressure.
  The user chose **A&C**: raise the ceiling and expose both pool caps as validated configuration keys
  (`udpAssociationMaxPerServer` default 1024, `udpAssociationFlowsPerAssociation` default 16), keeping
  `FlowsPerAssociation` at 16 because it is the blast-radius knob. The shared head becomes 16,384 flows
  per server = the default `udpSessionCapacity`, so every admitted flow can be shared. Not fixed by
  this: the descriptor floor stays ≈1 per live flow (one relay socket per flow is R1/I2 by design).
