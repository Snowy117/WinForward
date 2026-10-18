# Windows A/B runbook (Phase 8)

Task: `10-18-ndis-filter-table-bypass`. Owner: main session. **Do not run while the concurrent E2E
campaign is active.** This is the operational script behind `design.md` §12; no repository file
under `benchmarks/WinForward.E2E/**` is edited.

## 0. Interference gate (mandatory, every session)

1. `tmux ls` must show the `wfbench` session; do not create a second one.
2. `benchmarks/WinForward.E2E/scripts/wf.sh idle` and then, over the VM:
   ```powershell
   Get-Process WinForward,WinForward.E2E,sing-box,orchestrator -ErrorAction SilentlyContinue |
     Select-Object Id,ProcessName,StartTime
   ```
   The list must be empty. Also check `C:\wfbench\results` has no directory newer than the last
   completed E2E pass, and `C:\wfbench\heartbeat.txt` is stale.
3. If anything is active, stop and ask the user for an exclusive window. Never kill an E2E process.
4. Our deployment is `C:\wfbench\kpcache\`; never write to `wf-aot`, `wf-fdd`, `e2e`, `singbox`,
   `results`, `orchestrator.ps1`, or `watchdog.ps1`.
5. Every experiment ends with `ResetPacketFilterTable` (the cache's dispose does it; the scratch
   probe calls it explicitly). PASS-only rules are benign, but leaving them installed still changes
   the next run.

## 1. Publish and stage

On the Linux host (direnv loaded):

```bash
dotnet publish src/WinForward.Cli/WinForward.Cli.csproj -c Release -r win-x64 \
  -p:PublishAot=false --self-contained false -o /tmp/kpcache/fdd
```

Upload `fdd/` to `C:\wfbench\kpcache` (paths without spaces; `wf.sh up` then verify the remote
size). Copy `C:\wfbench\wf-fdd\ndisapi.dll` into `C:\wfbench\kpcache\`.

Configs (copy the E2E's working config, change only the toggle):
- `config-off.json`: the E2E `wf-fdd-opt.json` content, **no** `KernelPassCache` key.
- `config-on.json`: the same content plus `"KernelPassCache": true` under `WinForward`.

Plan (write on the VM): `plan-thru.json`
```json
{ "arms": [ { "name": "THRU", "kind": "throughput", "seconds": 30,
              "streams": 4, "targetBytesPerSecond": 60000000 } ] }
```

## 2. W1 — exports

```powershell
dumpbin /exports C:\wfbench\kpcache\ndisapi.dll | Select-String 'SetPacketFilterTable|ResetPacketFilterTable|GetPacketFilterTable'
```
Expect all four (including `GetPacketFilterTableSize`). A missing export is the `Unavailable`
branch; record it.

## 3. W2 — PASS-bypass go/no-go

Run a single long THRU flow with the cache on and the E2E proxy config (sing-box running at
`127.0.0.1:1080`). Confirm from the `kernel.passCache` summary lines:
- `rules >= 1` and `bypassed > 0`;
- `captured` is materially below the feature-off arm's captured count for the same 30 s load;
- the client `run.json` shows the arm completed (no loss, bytes delivered).

If `bypassed` stays 0 while traffic flows, `FILTER_PACKET_PASS` is not bypassing the queue: record
the negative result and do **not** continue to W5.

## 4. W3/W4 — table ladder and counter/order probe

Use a scratch console (outside the repo) referencing the published `WinForward.NdisApi.dll`:

- `W3`: `Replace` tables of 1, 64, 128, 256 rules (TCP loopback tuples that never carry traffic),
  `Reset` after each; record the largest accepted and the native error for the first refusal.
- `W4`: `Replace` two rules, `GetPacketFilterTableSize`/`ReadRulePacketCounts` → verify order,
  fields, and zeros; drive a TCP flow matching one rule; read the counters (expect the matched
  rule > 0); `Replace` again → counters reset; `Reset` → size 0.

If W3 caps below 256 entries, set `NdisKernelPassCache`'s `MaxFlows` from the measured ceiling and
re-run the relevant tests.

## 5. W5 — THRU A/B

Order: off, on, on, off, off, on (alternating, 3 pairs). For each arm:
1. Start `sing-box` (same config as the E2E).
2. Start `WinForward.exe run --config C:\wfbench\kpcache\config-{off,on}.json`, redirect stdout to
   `C:\wfbench\kpcache\logs\{arm}.log`; wait for the listener.
3. Run:
   ```powershell
   C:\wfbench\e2e\WinForward.E2E.exe client --target 192.168.100.4 `
     --plan C:\wfbench\kpcache\plan-thru.json --out C:\wfbench\kpcache\results\{arm} `
     --tcp-port 40010 --udp-port 40010 --dns-port 53 `
     --sampler-process WinForward --sampler-process sing-box
   ```
   The target on `192.168.100.4:40010` must be running (`scripts/start-targets.sh` on the Linux
   host, only in the exclusive window).
4. Sample CPU at 1 Hz in parallel:
   ```powershell
   Get-Process WinForward | Select-Object UserProcessorTime,PrivilegedProcessorTime,TotalProcessorTime
   ```
   (or use the client's sampler output; record which).
5. Stop WinForward (graceful) and confirm the final `kernel.passCache.reset` line (reason `shutdown`)
   stop sing-box.

Record per arm: CPU user/privileged/total (series), client `run.json` (loss/bytes), the cache
summary deltas (`bypassed`, `captured`, `reinjected`, `crossingsPerMille`, `callsPerMille`), and the
E2E correctness gates.

## 6. W6 — correctness soak

Run the standard E2E plan (or at least THRU + MIX + UDP/DNS) with the cache on; the gates must be
green, loss 0, `foreignConnection == 0`, `directLeak == 0` where applicable.

## 7. Evidence

Download the logs, `results/`, and the scratch probe output to
`benchmarks/results/2026-10-18-ndis-filter-table-bypass/`:

```
README.md            method, gate evidence, table, verdict, limits
raw/w1/              dumpbin output
raw/w2/              on-arm log + run.json
raw/w3/              ladder output
raw/w4/              counter/order output
raw/w5/<arm>/        run.json, product log, CPU csv
raw/w6/              soak run.json + gate summary
```

The README must state the exact commands, the exclusive-window confirmation, the clock comparison
if the harness is used, and the negative-result branch if W2 failed.

## 8. Cleanup

- Every arm ends with a graceful WinForward stop (the final reset line in the log).
- Remove nothing from the E2E deployment; delete only `C:\wfbench\kpcache`.
- If the scratch probe fails midway, run `ResetPacketFilterTable` explicitly before leaving.
