# Implementation plan — TCP half-close fidelity

Ordered checklist. Every step names its validation and its rollback point. Nothing outside
`src/WinForward.Runtime/TcpRedirect/`, `src/WinForward.Protocols/` and the matching test project is
edited; the E2E harness is read and executed but not modified.

## Environment facts (2026-10-06, learned the hard way)

- Target binds **`192.168.100.4`**, port in the firewall's admitted range (`40000-41000`); the host
  firewall matches by source subnet and the VM's external address is silently dropped. Sandbox port
  chosen: **40020**.
- Drive the VM with `benchmarks/WinForward.E2E/scripts/wf.sh` (tmux-backed). Piped stdin through
  `evil-winrm-py` loses output — that is what made the first attempts look like silent failures.
- **Do not run arms while the E2E orchestrator is live** (`orchestrator.ps1` in the VM process list,
  or a `C:\wfbench\heartbeat.txt` modified in the last few minutes). It stops product processes by
  name, so a sandbox `sing-box.exe`/`WinForward.exe` both corrupts its rows and gets killed by it.
- The watchdog task `wfbench-watchdog` is enabled: a stale heartbeat makes it kill product processes
  and re-enable the firewall. Verify `Get-NetFirewallProfile` shows all three profiles `False` before
  each arm (the local-redirect listener port is silently dropped otherwise).
- No cross-OS NativeAOT: the win-x64 AOT artifact cannot be built on this host
  (`Cross-OS native compilation is not supported`). Verification uses a locally published
  **framework-dependent** win-x64 build, with the original AOT artifact (`C:\wfbench\wf-aot`) as the
  environment sanity check.
- `C:\wf-hcfix` is this task's sandbox: `e2e\`, `wf\` (config + artifact), `wf-fdd\`, `singbox\`,
  `plans\`, `logs\`, `out\`. Deleted in Phase E. `C:\wfbench` stays read-only.

## Phase A — reproduce and discriminate

### A1 · Linux target
- `WinForward.E2E target --bind 192.168.100.4 --tcp-port 40020 --udp-port 40020 --dns-port 40053
  --label hcfix --ledger /tmp/hcfix/ledger.jsonl` (published linux-x64 binary; ledger in this task's
  scratch, never `/tmp/wf-bench/rel-ledger.jsonl`).
- **Validate:** `ss -ltn` shows the tuples; the VM can reach them (`wf.sh run` a
  `System.Net.Sockets.TcpClient` probe — `Test-NetConnection` is too slow for the tmux REPL).
- **Rollback:** stop by pid.

### A2 · Preconditions
- Orchestrator absent, heartbeat stale, firewall off, `C:\wf-hcfix` populated.
- **Validate:** the check is a single `wf.sh run` whose output is recorded in the task notes.

### A3 · Baseline arms (unfixed artifact)
- Order: `clean=100` → `halfClose=100` → four-mode REL. Client runs in the foreground of the arm
  script; results are read from the JSONL afterwards.
- **Validate:** the REL arm reproduces the defect shape; the two single-mode arms record whether the
  client-first failure is pure `timeout` (nothing delivered) or `halfCloseViolation` (a reset
  arrived) — that is the discriminator for `design.md` §2.
- **Rollback:** nothing on the VM changes except the sandbox; results are copied into
  `research/verification/baseline-<arm>.json`.

## Phase B — instrument and localise (conditional)

Entered only if A3 contradicts the design's model (e.g. the client receives a reset, or the trailer
bytes go missing).

- B1 · Temporary debug-level logging in the relay: per-direction byte counts at pump end, and the
  fact that the client-facing socket was closed at completion.
- B2 · Deploy into `C:\wf-hcfix\wf-fdd`, re-run `halfClose=100`.
- B3 · Record the decision in `design.md` and drop the temporary instrumentation.
- **Rollback:** the instrumentation is uncommitted.

## Phase C — fix and regression test

### C1 · Discriminating test first (watch it fail)
- New `TcpRelayEndFinTests` mirroring `TcpRelayEndResetTests`: a clean relay end must inject a
  client-visible FIN|ACK before the session teardown, built from the tracked `ServerNextSeq` (not
  ISN+1), with the direction chosen like the RST path; unknown sequences degrade to no injection.
- **Validate:** the test fails today because a clean end injects nothing, and
  `TcpProxyRelayTests.HalfClosePropagatesFinAndAllowsReverseResponse` stays green (it is not the
  discriminator — `RunPumpAsync`'s own `await using` closes the socket in both variants).
- **Rollback:** additive.

### C2 · The fix
- `TcpResetBuilder`: parameterize the TCP flags so one builder produces RST|ACK and FIN|ACK from the
  same SYN template plus seq/ack (no second packet-construction path).
- `ClientResetInjector`: add `TryInjectClientCloseAsync(association)` for the FIN flavor, sharing the
  sequence fallbacks, buffer pool, injection lanes, cancellation containment and logging shape of the
  RST path.
- `TcpRedirectAcceptor.ObserveRelayCompletionAsync`: `CleanEnded` → inject the FIN before
  `_tearDownSession`; non-clean ends keep the existing RST injection.
- Relay end kind into the existing `tcp.relay.ended` event instead of the literal `"completed"` (R4).
- **Validate:** C1 passes; full local suite green; the RST tests unchanged.
- **Rollback:** three files, independently revertible.

### C3 · Local gates
- `dotnet build WinForward.slnx -c Release` (zero warnings), `dotnet test WinForward.slnx -c Release`.

## Phase D — E2E verification on the VM

- D1 · Publish the fixed framework-dependent win-x64 artifact, deploy into `C:\wf-hcfix\wf-fdd`.
- D2 · `clean=100`, `halfClose=100`: `observed.clean == connectAttempts`, `observed.timeout == 0`.
- D3 · Four-mode REL: `clean ~ 601`, `timeout ~ 0`, `partialFin`/`resetAfterN` shape unchanged.
- D4 · Product log shows the relay end kind; target ledger shows `halfClose` rows.
- **Validate:** the three arms' JSONL records copied into `research/verification/`; `C:\wfbench` still
  unmodified (no file newer than the run's start).
- **Rollback:** redeploy the previous artifact; `C:\wfbench` was never touched.

## Phase E — cleanup and finish

- E1 · Copy results off the VM into `research/verification/`.
- E2 · Delete `C:\wf-hcfix`; confirm `C:\wfbench` untouched.
- E3 · Stop the Linux target by pid; clean `/tmp/hcfix` of anything not worth keeping.
- E4 · Quality gates per `AGENTS.md`: `dotnet format … --verify-no-changes --no-restore` (empty
  output) and `jb inspectcode -f=Xml -e=HINT -o=/tmp/jb-inspectcode.xml WinForward.slnx` (zero issues).
- E5 · Spec update: record the invariant "every client-visible close event — RST for an abnormal end,
  FIN for a clean end — is injected before the alias is retired" in
  `.trellis/spec/backend/tcp-local-redirect.md`, with the new test as the lock.
- E6 · Commit; resolve the branch question (the task currently records `master`, which blocks
  `task.py archive`).

## Risky files and rollback points

| File | Risk | Rollback |
|---|---|---|
| `src/WinForward.Protocols/TcpResetBuilder.cs` | flags parameterization must not change the RST bytes | the RST tests (`TcpResetBuilderTests`) pin the existing output |
| `src/WinForward.Runtime/TcpRedirect/ClientResetInjector.cs` | sequence/direction handling for a new packet kind | revert the added method |
| `src/WinForward.Runtime/TcpRedirect/TcpRedirectAcceptor.cs` | clean-end branch gains an injection | revert the branch + the log argument |
| VM `C:\wf-hcfix` | sandbox only; `C:\wfbench` untouched | delete the directory |
