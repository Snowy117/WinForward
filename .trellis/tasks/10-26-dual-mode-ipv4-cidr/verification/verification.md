# Batch 5f verification — dual-mode client IPv4 CIDR parity

Date: 2026-10-26
Environment: WinLTSC (26100), test-signed `wfprobe` driver, .NET SDK 10.0.401 / 11.0.100-rc.1,
sing-box `127.0.0.1:1080` direct outbound, Linux TCP echo `192.168.100.4:40001/40012/40013/40014`.
Source tree on the VM: full merged layout under `C:\workspace\WinForward`, driver built from
`C:\workspace\WinForward\kernel`. CLI: the framework-dependent `win-x64` publish already used by the
Batch 5d Kernel E2E (`C:\wfbench\kernel-e2e\wf-kernel-cli`); no CLI code changed in 5f.

## Fix under test

- `kernel/src/common/v4_mapped.h`: `try_normalize_v4_mapped` recognizes
  `family == 6` plus the `::ffff:0:0/96` prefix and returns family 4 with the four IPv4 bytes.
- `kernel/src/driver/probe_facts.cpp`: raw WFP facts remain for `probe_event` and the redirect
  target; `matcher_family` / `matcher_remote_address` carry the normalized view.
- `probe_classify.cpp`: the matcher `connection_view` and the redirect context
  `original_destination` use the normalized facts. Local address, listener, redirect target, WFP
  layer, rule table and redirect table are unchanged; the context is still 36 bytes / version 1.
- Tests updated: `SelfTest` context expectation, U10 v4-mapped connected, the host rule parity
  suite's dual-mode cases, and the native C++ helper/matcher/context tests.

## Windows gate (gate.ps1)

Command:

```powershell
pwsh -File C:\workspace\WinForward\kernel\tools\windows\gate.ps1 `
  -Root C:\workspace\WinForward\kernel -Remote 192.168.100.4:40001
```

Result: `GATE-RESULT=PASS`; driver built with 0 warnings, signed `Valid`, service `RUNNING`,
PREfast 0 findings, DACL checks pass, all `wfprobe` suites pass, userland build/tests/`vm-check`
pass, `userland-e2e` pass (47 assertions), driver still running and the VM did not reboot.

Key 5f assertions:

- `rules` host parity: a dual-mode client to an IPv4 literal matches an IPv4 CIDR rule
  (`matched_permit +1`, `fallback_permit +0`) and an IPv4 family rule; the same client does not
  match an `::ffff:127.0.0.0/104` IPv6 CIDR or an IPv6-only family rule (`fallback_permit +1`).
  The raw `probe_event.family` is still `AF_INET6` (23), proving the connection was classified on
  the V6 layer rather than silently moving to the V4 layer.
- `udp-rules` U10: v4-mapped **connected** now yields context family 4 and pure `127.0.0.1`
  (previously family 6 + mapped bytes); v4-mapped unconnected and native IPv6 (connected and
  unconnected) stay family 4 / family 6 respectively.
- `selftest --dual` against `192.168.100.4:40001`: context raw shows `family=4`
  `original=192.168.100.4:40001` (the userland destination is IPv4).
- Driver SHA-256 recorded by the gate:
  `3137EE884D5C2A2F78649C9BE445FA532F7150B917D533DCB61F06825A3C3214`.

## Kernel E2E with the original rule

Config: `Provider=WinForward.Kernel`, one SOCKS5 target `main` (`127.0.0.1:1080`), fallback
`pass`, `TcpFlowCapacity` default 4096:

1. `Process: ["sing-box.exe"]` → pass
2. TCP remote port 40013 → block
3. TCP remote port 40014 → pass
4. `Process: ["pwsh.exe"]`, TCP, `RemoteCidr: ["192.168.100.4/32"]`,
   `RemotePort: ["40012"]` → proxy `main`

Startup: ABI 8, listener 58858, block sink 56740, generation 1, 4 rules, 256 bytes, fallback
`Permit`, `ContainsBlock=true`.

Client results (`client.ps1`, `TcpClient` default dual-mode):

```text
PORT=40012 RESULT=OK READ=11 DATA=hello-40012
PORT=40014 RESULT=OK READ=11 DATA=hello-40014
PORT=40013 RESULT=FAIL ... access permissions. [::ffff:192.168.100.4]:40013
```

Driver decisions:

- `seq=1` (client pid=1580): `redirected`, `family=23`, `rule=3` — the original IPv4 CIDR/process
  rule matched the V6-layer mapped connection.
- `seq=3` (sing-box pid=4028): `permitted_rule`, `family=2`, `rule=0`,
  `remote=192.168.100.4:40012` — the relay's target connection is native IPv4.
- `seq=4` (client): `permitted_rule`, `rule=2` (40014 pass).
- `seq=5` (client): `blocked_rule`, `rule=1` (40013 block).

Driver counters after the three clients: `redirected=1`, `contexts_allocated=1`,
`matched redirect=1`, `matched permit=2`, `matched block=1`, `deferred_blocks=1`,
`self_process=1`, no redirect/apply/alloc failures.

Sing-box log: inbound SOCKS from `127.0.0.1:58861` to `192.168.100.4:40012`, then a direct
outbound to `192.168.100.4:40012`. Linux target log: TCP accepts from `192.168.100.2` for
`40012` (`hello-40012`) and `40014` (`hello-40014`); no accept for `40013`.

Cleanup: `WinForward`/`sing-box` stopped by exact PID; scheduled tasks `wf-kernel-e2e` and
`wf-singbox-e2e` deleted; driver host config zeroed (`enabled=0 listener-pid=0 port=0
block-sink-port=0 self-pids=[]`); driver service left installed/RUNNING; `Test-Connection
192.168.100.4` True; Linux echo targets stopped.

## Integration fixes required by the merged layout

1. The merged root `Directory.Packages.props` enables Central Package Management. The WDK driver
   references now use central `PackageVersion` entries (`Microsoft.Windows.WDK.x64`,
   `Microsoft.Windows.SDK.cpp.x64`), so the full-layout `gate.ps1` restore no longer fails NU1008.
2. The Windows build needs a NuGet source providing the SDK-triggered `Microsoft.NET.ILLink.Tasks`
   (from the root trim/AOT analyzers); the VM's Visual Studio offline source alone fails NU1101.
   `nuget.org` was added as a machine-local source for this run. This is a machine setup
   requirement, not a repo change.

## Findings / follow-ups

- **Diagnostic display bug (pre-existing, not 5f):** `wfprobe events` formats a V6-layer event's
  remote address as `0.0.0.0` because `Program.AddressText` treats only family `6` as IPv6, while
  `probe_event.family` stores `AF_INET6 = 23`. The raw family (23) and the rule decision (`rule=3`)
  prove the driver facts are correct; only the diagnostic rendering is wrong. Recorded for a
  follow-up tool fix.
- **Test flake:** `TargetDelayInjectionTests.NoInjectionLeavesTheObservedLatencyTailWhereItWas`
  failed once during the full-solution parallel test run (p99.9 570.4 ms) and passed isolated and
  in a full `WinForward.E2E.Tests` rerun (522/522). Known host-load-sensitive latency tail test.

## Evidence files

- `kernel-clients.txt` — proxy/pass/block client output
- `wfprobe-stats.txt` — driver counters after the three clients
- `wfprobe-events.txt` — decision events seq 1/3/4/5
- `driver-load.txt` — signature/state and driver hashes
- `singbox.log` — SOCKS inbound/outbound to `192.168.100.4:40012`
- `target-echo.log` — Linux target accepts for 40012/40014
- `wf.err.log` — Kernel backend startup/config/ready log
- `windows-gate.log` — full Windows gate output
- `cleanup.txt` — cleanup and residual state

## Verdict

5f-AC1–AC4 are met: the original IPv4 CIDR/process rule now proxies the dual-mode client through
the SOCKS5 target, the userland destination is IPv4, native IPv4/IPv6 and U10 family semantics are
preserved, the ABI size/version is unchanged, the Windows/root/Kernel gates pass, and the VM is
cleaned. No WinpkFilter behavior was changed, and no WinForward userland code was modified.
