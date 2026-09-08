# P1: behavior-zero structural refactors

Parent: `09-08-design-review-remediation` · Evidence: `../09-08-design-review-remediation/research/00-synthesis.md` (P1 table) + `04-proxy-machinery.md` §3, `05-cli-tests-benchmarks.md` §5, `01-core-configuration.md` §4

## Goal

Behavior-zero refactors: UdpProxyCoordinator split, UdpBurstScenario split + StabilityShared,
Domain.cs/PacketRuntime re-homing, UdpProxy→Socks5 edge normalization, tombstone renaming.

## Hard constraints (from quality-guidelines.md:26 + directory-structure.md)

- Every batch is behavior-zero: prefer script-assisted mechanical line-range moves over retyping;
  never modify assertion semantics; lock bodies migrate verbatim (single-lock semantics preserved).
- Before committing each batch: `dotnet test` totals equal the recorded baseline for this task
  (captured at task start, including P0/P2 deltas) and the build is zero-warning.
- Do not split for splitting's sake: readability and performance must not suffer
  (directory-structure.md:60 precedent list applies).

## Requirements

### R1 — Split `UdpProxyCoordinator.cs` (471 effective lines → ≤400 per part)

Apply the four-seam plan from research `04-proxy-machinery.md` §3, in extraction-safety order:

- A. `UdpSetupCooldownTable` (~60 eff. lines) — cooldown dict + prune/evict, leaf lock like its TCP
  counterparts; **renames `_setupTombstones` away from "tombstone"** (fixes the TCP-grace vs
  UDP-cooldown naming drift, research 04 issue 3).
- B. `UdpSetupQueueBudget` (~65) — pure Interlocked charge/credit accounting + drop logging.
- C. `UdpSessionSetup` (~150) — dial/claim/construct/flush pipeline via ctor delegates (no cycle),
  the analog of `TcpRedirectSetup`.
- D. `UdpProxyLogging` (~40) — mirror of `TcpRedirectLogging`.
- Target: coordinator ≈250 effective lines; each concern independently greppable.

### R2 — Split `benchmarks/.../Stability/UdpBurstScenario.cs` (437 → compliant) + StabilityShared

Per research `05-cli-tests-benchmarks.md` §5: extract `Stability/StabilityShared.cs`
(LatencyDistribution + percentile math + CountingRuntimeLogger + product-event helpers —
deduplicated against UdpLossScenario) and `Stability/UdpBurstInstrumentation.cs` (InFlightTracker,
BackgroundSender, BurstCountingSink, result types); main file keeps ~150 effective lines of
orchestration. Also move `BenchmarkShared` out of the `Perf/` namespace to the project root since
Stability references it.

### R3 — Re-home Core types (file = main type)

- `Domain.cs` (10 top-level types): extract `FlowTable` + `TransportTuple` (~180 lines) into
  `FlowTable.cs`; remaining vocabulary types stay per spec-sanctioned root layout.
- `PacketRuntime.cs`: `BoundedSetupQueue` moves to its own file (unrelated roommate of the lease).
- `Policy.cs` stays (3 cohesive types, spec-tolerated).

### R4 — Normalize the UdpProxy→Socks5 cross-group edge

Current edge is outside the sanctioned set (research 03 §5): UdpProxy consumes SOCKS5 datagram
codec types defined in `Socks5/Socks5UdpTransport.cs`. design.md decides between:
(a) move the datagram codec types (`Socks5UdpDatagram`, header size const, receive-result types)
    into `WinForward.Protocols` — aligns with directory-structure.md:46 "编解码仍在 Protocols";
(b) add the edge to the sanctioned set with written rationale.
Default proposal: (a). Whichever is chosen, update directory-structure.md's sanctioned-edge list.

### R5 — `NdisCapturePump` ctor options record (9 params, 4 optional callbacks)

Collapse into an options record/struct (`NdisCapture.cs:63`); pure mechanical, behavior-zero.

## Spec updates required with this task (Phase 3.3)

- directory-structure.md: sanctioned-edge set updated per R4; split precedents appended (UDP
  coordinator split mirrors the TCP 1158→5 precedent).
- tcp-local-redirect.md / udp-relay.md / quality-guidelines.md: tombstone terminology unified
  (TCP keeps "tombstone" = TIME_WAIT grace; UDP becomes "setup cooldown").

## Acceptance criteria

- [x] All touched files ≤400 effective lines; no new file violates file=main-type.
- [x] `dotnet test` totals identical before/after each batch (recorded); zero-warning build.
- [x] UdpProxyCoordinator concerns (admission/budget/dial/cooldown/logging) live in separately
      named types; no public-behavior change (public surface may shrink, never grow).
- [x] UdpBurstScenario + UdpLossScenario share StabilityShared with zero private duplicates.
- [x] Cross-group using audit (research 03 §5 method) passes against the updated sanctioned set.
- [x] Spec docs updated as listed above.

## Notes

- Complex task: `design.md` (R1 lock/gate migration map + R4 decision) and `implement.md`
  (batch-by-batch checklist with rollback points) required before `task.py start`.
- Execute after `09-08-p2-hygiene` (dead-surface deletion shrinks what moves).

## Completion evidence (2026-09-08)

- Commits b15358a/61171f9/734f326/6ea13b0/2ff9fe1 + PruneExpired leaf-lock fix (check finding) on branch p1-structure; trellis-check PASS (independent re-run: build 0 warnings, 578/578, benchmarks 0 warnings).
- B4 audit: charge 1×TryCharge / credit 5 sinks one call site each; coordinator gate sole slot-state gate (delegates only); UdpSetupCooldownTable leaf lock, no call-outs; budget Interlocked-only; public API unchanged; rg tombstone in UdpProxy/ = 0.
- Effective lines: coordinator 329; FlowTable 110 / BoundedSetupQueue 92 / StabilityShared 72 / UdpBurstInstrumentation 182 / UdpBurstScenario 196 — all ≤400.
