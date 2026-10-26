# Batch 5d design: Windows E2E and edition parity

## Environment

- WinLTSC, `wfprobe` driver service running (ABI 8), .NET 10, sing-box at `127.0.0.1:1080`
  (direct outbound), Linux echo targets at `192.168.100.4:40012/40014`.
- CLI: framework-dependent `win-x64` publish under `C:\wfbench\kernel-e2e\wf-kernel-cli`;
  `appsettings.json` with `Provider=WinForward.Kernel`, 4 host rules, fallback pass, one target.
- The Kernel backend runs under scheduled task `wf-kernel-e2e` (SYSTEM) so it survives the WinRM
  shell. The listener/block-sink ports are ephemeral; the startup log records ABI/generation/
  rules/bytes.

## Kernel checks

1. **Proxy/pass/block**: `client.ps1 -Port 40012/40014/40013`. Expected: proxy echo, direct echo,
   block `AccessDenied` (Kernel host form). Evidence: client output, `wf.err.log` startup, sing-box
   log (40012 relay → target), `wfprobe stats` (`redirected/contexts/matched redirect`, `blocked`/
   `deferred_blocks`, `self_process`).
2. **Target mapping**: the 40012 rule is index 3; the sing-box inbound `to 192.168.100.4:40012`
   from the relay's local port proves the `rule_index` → `main` mapping and original destination.
3. **Failure**: stop sing-box, connect 40012; the relay setup fails, the accepted connection is
   closed abortively (reset/empty), no direct fallback. Update `client.ps1` to treat a zero read
   as failure. Restart sing-box afterwards.
4. **Capacity**: set `TcpFlowCapacity=1`, restart the Kernel task, open one live 40012 connection
   and then a second; the second must be closed fail-closed; record the counters. Restore the
   capacity/config afterwards.

## WinpkFilter parity

- Copy the matching `ndisapi.dll` sidecar (from the VM's WinForward product install, e.g.
  `C:\wfbench\wf-aot`/`wf-fdd`) next to the CLI.
- Run the CLI with a layered override (`--config`) that keeps the same `Socks5Servers`/`Host` rules
  but sets `Provider=WinpkFilter`, `WinpkFilter.KernelPassCache=false`, and
  `WinForwardKernel=null` (the base object would otherwise be an inapplicable-key error).
- Run the same three clients. Expected: 40012 proxy via the same sing-box/target, 40014 direct,
  40013 block as a **drop/timeout** (the accepted edition difference vs Kernel `AccessDenied`).
- Use a **foreground console in a second `windows-vm` session** and stop it with Ctrl+C so the
  WinpkFilter adapter modes are restored; do not hard-kill this run. If a second session is not
  available, use the VM's existing adapter-mode recovery/watchdog and verify connectivity after.

## Evidence and cleanup

- Save the client outputs, Kernel `wf.err.log`, sing-box log, `wfprobe stats`, and the WinpkFilter
  run output under `verification/`; write `verification.md` with commands and observed results.
- Stop the scheduled task, sing-box, Linux echo servers; disable the Kernel host config
  (old controller `disable` or the next clean start); remove the scheduled task; restore the VM's
  firewall/watchdog state; record any residual (the Kernel driver service remains installed but
  disabled).
- No product code changes unless a defect is found; a defect goes back to the owning batch.

## Risks

- The WinpkFilter foreground run can leave the NDISAPI adapter modes in filter if hard-killed;
  Ctrl+C/clean exit is required.
- The Kernel scheduled task is hard-stopped; its driver config stays enabled until the next
  start/disable. Use the old controller disable or a clean start.
- The sing-box process is shared with other checks; stop/restart it explicitly and record the PID.
- The client's dual-mode socket is v4-mapped; port-only rules are used for the proxy/block/pass
  checks (the CIDR/family lesson from the first run).
