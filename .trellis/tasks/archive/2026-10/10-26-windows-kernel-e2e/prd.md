# Batch 5d: Windows Kernel host TCP E2E and edition parity

Parent: `.trellis/tasks/10-25-kernel-backend-integration`

## Goal

Validate the Kernel provider's host TCP path on the real Windows VM and compare its user-visible
decisions with the WinpkFilter provider for the same policy. This is a verification task: no product
code change unless the E2E exposes a defect.

## Confirmed environment

- WinLTSC VM, `wfprobe` driver service running (control ABI 8), .NET 10 runtime, sing-box SOCKS5
  `127.0.0.1:1080` + direct outbound, Linux targets at `192.168.100.4:40012/40014`.
- Kernel CLI: framework-dependent `win-x64` publish of the merged tree
  (`WinForward.exe` + `appsettings.json`) under `C:\wfbench\kernel-e2e\wf-kernel-cli`.
- Kernel config: `Provider=WinForward.Kernel`, four host rules (sing-box pass, block 40013, pass
  40014, proxy 40012 → `main`), fallback pass, one SOCKS5 target.
- The Kernel backend started via a scheduled task (`wf-kernel-e2e`, SYSTEM) so it survives the
  WinRM shell; listener/block-sink ports are ephemeral; the startup log confirms ABI 8, blob
  generation 25, 4 rules, 220 bytes, block reachable.

## Confirmed Kernel results (first run)

- `40012`: proxy echo `hello-40012`; driver stats `redirected=1`, `contexts_allocated=1`,
  `matched redirect=1`; sing-box log shows the relay's inbound connection to
  `192.168.100.4:40012` and a direct outbound. This proves the rule-index target mapping.
- `40014`: direct pass echo `hello-40014`; `matched permit` includes the port rule; no sing-box
  connection.
- `40013`: `AccessDenied` (`[::ffff:192.168.100.4]:40013`); driver `blocked/deferred_blocks`
  increments; the accepted M1 block form.
- The first attempt with an IPv4 CIDR in the proxy rule did not match the dual-mode v4-mapped
  connection (fallback pass); the rule was corrected to TCP+port, which is the configuration used
  for the results above.

## Remaining validation

- **Failure path.** Stop sing-box and connect to 40012: the relay setup must fail closed (client
  reset/refused; no direct fallback). Restart sing-box afterwards.
- **Capacity.** With `TcpFlowCapacity=1`, one live 40012 session succeeds and a concurrent second
  connection is rejected fail-closed; the counter/log evidence is recorded.
- **Edition parity.** Run the same policy through `Provider=WinpkFilter` (same rules/targets, the
  WinpkFilter driver and `ndisapi.dll` sidecar) and compare proxy/pass decisions and the target;
  host TCP block is expected to be drop/timeout under WinpkFilter vs AccessDenied under Kernel
  (the accepted edition difference).
- **Cleanup.** Stop the scheduled task/processes, dispose the sing-box, stop the Linux targets,
  disable the Kernel host config (old controller/disable or next clean start), remove the
  scheduled task, and record the VM state.

## Acceptance Criteria

- [x] R5d-AC1: Kernel proxy/pass/block results and the rule-index target mapping are recorded with
      command output, logs, driver counters and the sing-box log.
- [x] R5d-AC2: failure and capacity paths fail closed with recorded evidence.
- [ ] R5d-AC3 (deferred to Batch 5e): the same policy through WinpkFilter was not run because the
      VM has no `ndisapi.dll` sidecar or WinForward WinpkFilter product directory (only `ndisrd`).
      The Kernel-side evidence and the dual-mode IPv4 CIDR finding are recorded; the cross-edition
      comparison remains a separate follow-up.
- [x] R5d-AC4: evidence is committed under this task; the VM is cleaned (tasks/processes/echo
      stopped, Kernel host config disabled, network reachable); no product code changed.

## Out of Scope

- Host UDP, forwarded, ARM64, release signing/installer, full performance/stability; a clean
  Ctrl+C shutdown of the scheduled-task process (the task is stopped by the scheduler; the
  next-start disable path is noted).
