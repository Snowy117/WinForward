# Batch 5d implementation plan

1. **Record the confirmed Kernel run.** Save the current client outputs, `wf.err.log`, sing-box
   log and `wfprobe stats` under `verification/`; the 40012 proxy relay, 40014 pass and 40013
   `AccessDenied` are already observed (listener 58581, blob generation 25, 4 rules, 220 bytes,
   `redirected=1`, `contexts=1`, `matched redirect=1`, `blocked=2`, `deferred_blocks=2`).
2. **Failure.** Update `client.ps1` so a zero-length/closed echo is a failure. Stop the sing-box
   process by exact PID; connect 40012; record the fail-closed result (reset/empty, no direct
   fallback); restart sing-box and verify the proxy works again.
3. **Capacity.** Stop the Kernel scheduled task; set `TcpFlowCapacity=1`; restart it; open one
   live 40012 connection, then a second; the second must be refused fail-closed. Record the client
   result and the `SessionRefused`/capacity evidence. Restore the normal config/task.
4. **WinpkFilter parity.** Copy the matching `ndisapi.dll` sidecar next to the CLI; create a
   `--config` override (`Provider=WinpkFilter`, `WinpkFilter.KernelPassCache=false`,
   `WinForwardKernel=null`, same rules/targets); start the CLI in a foreground console in a second
   VM session; run 40012/40014/40013; stop it with Ctrl+C so the adapter modes are restored.
   Expected: same proxy/pass/target; block is drop/timeout (accepted edition difference).
5. **Evidence.** Write `verification.md` with commands, observed outputs, logs, stats and the
   accepted differences. Save the raw logs under `verification/`.
6. **Cleanup.** Stop the scheduled task/process, sing-box, Linux echoes; disable the Kernel host
   config (old controller `disable` or a clean start); remove the task; verify the VM network and
   adapter modes; record residuals.
7. **Do not change product code** unless the E2E exposes a defect; a defect returns to its owning
   batch. Do not touch unrelated dirty files. No commit until the evidence is complete.

## Verification commands (examples)

```powershell
& C:\wfbench\kernel-e2e\wf-kernel-cli\client.ps1 -Port 40012
& C:\wfbench\kernel-e2e\wf-kernel-cli\client.ps1 -Port 40014
& C:\wfbench\kernel-e2e\wf-kernel-cli\client.ps1 -Port 40013
& C:\workspace\WinForward.Kernel\tools\wfprobe\bin\Release\net11.0\wfprobe.exe stats
Get-Content C:\wfbench\singbox\singbox.log -Tail 12
```

## Risks

- Do not hard-kill the WinpkFilter foreground run; the NDISAPI adapter modes must be restored by
  the clean shutdown.
- The Kernel scheduled task is hard-stopped; disable its driver config before finishing.
- The sing-box is shared; stop/restart with exact PIDs and record them.
