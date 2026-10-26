# Windows Kernel host TCP E2E — verification

Date: 2026-10-26
Environment: WinLTSC (26100), `wfprobe` driver service running (control ABI 8), .NET 10,
sing-box `127.0.0.1:1080` direct outbound, Linux echo targets `192.168.100.4:40012/40014`.
CLI: framework-dependent `win-x64` publish of the merged tree under
`C:\wfbench\kernel-e2e\wf-kernel-cli`; Kernel backend started via scheduled task
`wf-kernel-e2e` (SYSTEM) so it survives the WinRM shell.

## Configuration

`Provider=WinForward.Kernel`, one SOCKS5 target `main`, fallback pass, four host rules:

1. `Process: ["sing-box.exe"]` → pass (prevents relay recursion)
2. TCP remote port 40013 → block
3. TCP remote port 40014 → pass
4. TCP remote port 40012 → proxy `main`

The original proxy rule also had `Process: ["pwsh.exe"]` and `RemoteCidr: 192.168.100.4/32`;
the E2E exposed that the dual-mode client's WFP connection is IPv6 v4-mapped, so the IPv4 CIDR
did not match and the flow fell back to pass. The rule was reduced to TCP+port for the verified
run. This is recorded as a parity finding below (Batch 5f).

## Kernel results

- Startup: ABI 8, listener 58581, block sink 52070, blob generation 25, 4 rules, 220 bytes,
  fallback Permit, `ContainsBlock=true`, flow capacity 4096 (capacity run later used 1).
- `40012`: `RESULT=OK READ=11 DATA=hello-40012`; driver stats
  `redirected=3 / contexts_allocated=3 / matched redirect=3` (cumulative over repeated runs);
  sing-box log shows the relay inbound `127.0.0.1:<relay-port>` → `192.168.100.4:40012` and a
  direct outbound. This proves the rule-index → `main` target mapping.
- `40014`: `RESULT=OK READ=11 DATA=hello-40014`; port rule permit; no sing-box connection.
- `40013`: `RESULT=FAIL ... An attempt was made to access a socket in a way forbidden by its
  access permissions. [::ffff:192.168.100.4]:40013`; driver `blocked=4 / deferred_blocks=4`
  (cumulative); the accepted Kernel host block form (`AccessDenied`).
- `self_process=40` confirms the self-PID gate.
- Linux target log: `listening 40012/40014` and accepts from `192.168.100.2` for the proxied and
  direct flows.

## Failure and capacity

- **Failure (sing-box stopped)**: `40012` → `RESULT=FAIL ... An existing connection was forcibly
  closed by the remote host`; the target log gained no new connection; the driver `redirected`
  incremented but the relay was not established and did not fall back to direct. Fail-closed.
- **Capacity (`TcpFlowCapacity=1`)**: startup log `flow capacity 1`; a held first connection
  returned `FIRST=OK DATA=hold-40012`; the concurrent second returned
  `SECOND=FAIL ... forcibly closed`; the target saw only the first. Fail-closed capacity refusal.

## Findings

1. **Dual-mode IPv4 CIDR parity gap (Batch 5f).** A Windows dual-mode client connecting to an IPv4
   literal is classified by WFP as IPv6 with a v4-mapped address (`::ffff:192.168.100.4`), so a
   host rule with an IPv4 `RemoteCidr` and no port-only fallback does not match under the Kernel
   provider, while the WinpkFilter packet path sees an IPv4 packet and matches. The Kernel E2E
   used port-only rules as the workaround. The fix should map IPv4 CIDRs to the native IPv4 and
   v4-mapped IPv6 network forms in the policy compiler (and/or normalize v4-mapped facts for the
   matcher), with tests and a re-run of the original CIDR rule. This is a real edition-parity
   defect, not an environment issue.
2. **WinpkFilter parity blocked (Batch 5e).** The VM has `ndisrd` running but no `ndisapi.dll`
   sidecar and no WinForward WinpkFilter product directory (searched `System32`, Program Files,
   `C:\wfbench`, zips, user dirs, `C:\workspace`). The same-config WinpkFilter run could not be
   executed. It needs the matching x64 `ndisapi.dll` deployed next to the CLI (and a clean
   foreground run to restore adapter modes). The Kernel proxy/pass/block/target decisions above
   are the Kernel-side evidence; the cross-edition comparison remains open.

## Cleanup

- Scheduled tasks `wf-kernel-e2e`/`wf-singbox-e2e` ended and deleted; no WinForward/sing-box
  process remains; `wfprobe` and `ndisrd` services remain installed/running; the old Kernel
  controller `disable` printed `disabled enabled=0 listener_port=0`; the VM can reach
  `192.168.100.4` (`Test-Connection` True); the Linux echo server was stopped.
- The Kernel CLI/config/evidence remain under `C:\wfbench\kernel-e2e` for a future parity run.

## Evidence files

- `kernel-clients.txt` — proxy/pass/block client output
- `wfprobe-stats.txt` — driver counters
- `kernel-wf.err.log` — Kernel startup/ready log
- `singbox.log`, `singbox-final.log` — SOCKS5 relay evidence
- `target-echo.log` — Linux target accepts
- `kernel-failure.txt` — stopped-proxy fail-closed run
- `kernel-capacity.txt`, `kernel-capacity-wf.err.log` — capacity run and log
- `cleanup.txt` — task/service/network cleanup

## Verdict

The Kernel provider's host TCP data path is **verified on the real VM** for proxy (with target
mapping), pass, block, proxy failure, and capacity. Edition parity is **not yet complete**: the
dual-mode IPv4 CIDR rule needs the 5f fix, and the WinpkFilter side comparison needs the 5e
environment (missing `ndisapi.dll`). No product code was changed in this verification.
