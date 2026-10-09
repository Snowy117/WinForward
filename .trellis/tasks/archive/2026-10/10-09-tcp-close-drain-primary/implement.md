# Implementation plan — close drain as the primary fix

Ordered checklist. Every step names its validation and its rollback point. Nothing outside
`src/WinForward.Runtime/TcpRedirect/`, `src/WinForward.Protocols/`, the matching test project
and the two spec files is edited; the E2E harness is read and executed but not modified.

## Environment facts (inherited from the parent task, 2026-10-06)

- Target binds **192.168.100.4**, port in the firewall's admitted range (40000-41000); the host
  firewall matches by source subnet and the VM's external address is silently dropped.
- Drive the VM with `benchmarks/WinForward.E2E/scripts/wf.sh` (tmux-backed); piped stdin through
  evil-winrm-py loses output.
- **Never run arms while the E2E orchestrator is live** (`orchestrator.ps1` in the VM process
  list, or a fresh `C:\wfbench\heartbeat.txt`): it stops product processes by name and corrupts
  both its rows and the arm.
- The `wfbench-watchdog` task re-enables the firewall on a stale heartbeat; verify all three
  `Get-NetFirewallProfile` profiles show `False` before each arm.
- No cross-OS NativeAOT: verification uses a locally published **framework-dependent** win-x64
  build; the `C:\wfbench\wf-aot` artifact is the environment sanity check.
- The current tree reads **appsettings.json** (`WinForward` section, PascalCase keys), not the
  legacy config.json.
- `C:\wfbench` stays read-only; this task gets its own sandbox directory on the VM, deleted in
  Phase E after the results are copied into `research/verification/`.

## Phase A — evidence and baseline (AC0)

### A1 · Preconditions and baseline arms
- Same recipe as the parent's Phase A: Linux target on 192.168.100.4, sandbox deployed, orchestra-
  tor absent, firewall off. Baseline runs against the **unfixed master artifact** (with `7cababb`):
  `halfClose=100`, `clean=100`.
- **Validate:** the residual shape reproduces (a few dozen pure timeouts per 601).
- **Rollback:** nothing on the VM changes except the sandbox.

### A2 · AC0 classification
- Classify the timed-out attempts from the arm's own `type: "attempt"` records by
  `echoedBytes`/`trailerBytes`/`eof`: lost close vs tail gap vs mix.
- **Validate:** the split is recorded under `research/` and states which exit the drains should
  take and how long they should run (feeds AC5's expectations).

## Phase B — tests first (AC6)

### B1 · The removal test
- Rewrite `TcpRelayEndCloseTests` into the drain suite; test 7 (a clean end injects no crafted
  packet) fails while `7cababb`'s FIN path exists.
- **Validate:** watch it fail for the right reason.
- **Rollback:** additive.

### B2 · The drain tests
- Tests 1–6 of design.md §9 (acknowledged exit, deadline exit, no-sequences degradation, retire
  ordering, straggler resolution, piggybacked ACK). They fail before the drain exists.
- **Validate:** the suite is red for the right reasons.

## Phase C — implementation

### C1 · The drain (design.md §7, landing points 1–6)
- Association drain state + `RelayPhase.Draining`; `TrackClientAck`; the forward-leg
  observation; the acceptor's clean-end branch (dispose → arm → await → tear down); the injectable
  deadline; the drain log event. The A/B is realized outside the code, as two frozen snapshots of
  this revision (as-landed and a drain-only variant that removes only the clean-end injection),
  built into separate binaries for Phase D.
- **Validate:** the B2 suite goes green; `dotnet test WinForward.slnx -c Release` fully green;
  zero-warning build.
- **Rollback:** confined to the units in design.md §7.

### C2 · Quality gates
- `dotnet format WinForward.slnx --severity info --verify-no-changes --no-restore` empty;
  `jb inspectcode` zero issues.
- **Validate:** both clean before any VM run, so a later failure is the change's, not the gate's.

## Phase D — VM verification (AC1–AC5)

- D1 · AC1/AC2 drain-only arms (`halfClose=100`, `clean=100`): `timeout == 0`.
- D2 · AC3 four-mode REL arm: `timeout == 0`, existing `unexpectedEof`/`reset` shape kept.
- D3 · AC4 A/B: `halfClose=100` once from the as-landed snapshot (crafted FIN + drain), once
  from the drain-only snapshot. Decision rule: both at `timeout == 0` → C4; drain-only worse → record the
  mechanism, re-scope the removal with the user.
- D4 · AC5: drain durations (p50/p99/max), exit reasons, peak concurrent drains — stated against
  the port budget in the task's verification notes.
- **Validate:** every arm's JSONL is copied into `research/verification/` before the next arm.
- **Rollback:** the VM sandbox only; the tree is untouched by this phase.

### C3 · The removal (design.md §6, landing point 7) — after D3's decision
- Delete `TryInjectClientCloseAsync`/`TryInjectClientCloseCoreAsync`'s FIN flavor,
  `TcpResetBuilder.TryBuildFin`, and the acceptor's clean-end injection branch. The drain-only
  snapshot's patch is discarded; nothing of it was ever in the tree.
- **Validate:** test 7 goes green; the whole suite and both quality gates stay green; one VM arm
  (`halfClose=100`) re-confirms the committed shape.
- **Rollback:** this is its own commit; reverting it restores the drain-plus-FIN shape verified in
  D3's first arm.

## Phase E — spec, cleanup, wrap-up

- E1 · Spec: `tcp-client-close-injection.md` becomes the close-drain spec (the clean-end FIN
  contract out, the drain contract in); `tcp-local-redirect.md`'s close row and residual
  paragraph replaced by the measured outcome.
- E2 · Delete the VM sandbox after the results are copied back; confirm `C:\wfbench` untouched.
- E3 · Final gates: build, tests, format, inspectcode; two commits (drain, then removal) per
  design.md §10.
