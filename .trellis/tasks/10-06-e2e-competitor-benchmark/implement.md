# Implementation plan — end-to-end competitor benchmark

Ordered checklist. Each step names its validation command and its rollback point. Nothing under
`src/` or `tests/` is edited at any step.

## Phase A — harness

### A1 · Scaffold the project
- Create `benchmarks/WinForward.E2E/WinForward.E2E.csproj` (`net10.0`, cross-platform, no
  dependencies beyond the BCL; `System.IO.Hashing` only if the CRC32C implementation costs more
  than a table-free loop).
- Copy the repo's `.editorconfig`/analyzer expectations; add the project to `WinForward.slnx`.
- **Validate:** `dotnet build WinForward.slnx -c Release` is zero-warning.
- **Rollback:** remove the project from the solution; the directory is inert on its own.

### A2 · Wire format and target server
- `Protocol/Frame.cs`: encode/decode the `WFE1` frame, CRC32C, deterministic payload pattern.
- `Target/TargetServer.cs`: TCP echo with the five fault modes, UDP echo, DNS responder, and a
  per-flow ledger (`connId`, source, bytes, verdict) written as JSONL.
- **Validate:** publish `linux-x64`, run on the Linux host, exercise every fault mode with a small
  ad-hoc client; the ledger shows one entry per connection with the expected verdict.
- **Rollback:** none needed; nothing else depends on it yet.

### A3 · Client
- `Client/Arms/*.cs`: one class per arm, driven by `Plan/plan.json`.
- `Client/LatencyHistogram.cs`: fixed-bucket histogram (3 significant digits) plus exact scalars.
- `Client/ResourceSampler.cs`: 1 Hz `TotalProcessorTime`, private bytes, working set, peak working
  set, threads, handles for the named product process, and for the client process itself.
- `Client/ResultWriter.cs`: one JSONL record per arm, environment block, per-flow classification
  counters, and the send-side overflow accounting.
- **Validate:** publish `win-x64` framework-dependent, run `BASE-direct` on the VM against the
  Linux target; loss must be zero and the `pathLoss` bucket must read < 1e-6.
- **Rollback:** arms are independent; a failing arm can be dropped from the plan without touching
  the rest.

### A4 · Preflight and scope assertions
- Enumerate adapters, firewall profiles, checksum/RSC/LSO advanced properties, loaded filter
  drivers and running services; refuse to continue when a foreign interceptor is present.
- Control-process check: a helper process that is not in any rule must reach the target while the
  product runs.
- `proxiedFraction` check on a warm-up probe.
- **Validate:** run against WinForward and confirm all three assertions behave (including a
  deliberately wrong config, which must be refused).
- **Rollback:** assertions are read-only checks.

## Phase B — product adapters

### B1 · WinForward AOT and FDD
Config with `udpOverTcp: true`, a `localTargets` DNS entry, a pass rule for `sing-box.exe`, a proxy
rule for the client, `logLevel: info`. Both artifacts from the CI download.
- **Validate:** one `LAT-idle` arm each, `proxiedFraction == 1`.

### B2 · ProxiFyre
`app-config.json` with one client rule, UDP enabled, and `sing-box`/`ProxiFyre`/`ProxiFyreUI`
excluded. Service start/stop. `ndisapi.dll` must be resolvable by `socksify.dll`.
- **Validate:** one `LAT-idle` arm, `proxiedFraction == 1`; `Stop-Service` leaves no interception.

### B3 · ProxyBridge
`.pbprofile` with a `PROXY` rule for the client and a `DIRECT` rule for `sing-box.exe`; console
process lifecycle; WinDivert driver unload on stop.
- **Validate:** one `LAT-idle` arm, `proxiedFraction == 1`.

### B4 · Proxifier — time-boxed spike, first
Proxifier's Standard Edition needs its WFP driver service and a profile. The documented
configuration path is the GUI `ServiceManager.exe`.
- Try, in order: the installer's service registration; `sc.exe create` for `ProxifierDrv` honouring
  `ProxifierDrv.inf`; `Proxifier.exe <profile>.ppx silent-load` in session 0 with
  `ProcessServices`/`ProcessOtherUsers` enabled; the portable edition plus the Standard Edition
  profile.
- **Decision point:** if interception cannot be established headlessly, stop and report it. The
  cell becomes "not measurable in this environment" and the user decides between an interactive
  setup session and dropping Proxifier from the comparison. Do not fabricate a row.
- **Rollback:** stop the service, remove the driver service, uninstall if needed; the VM can be
  reset to its initial state.

## Phase C — campaign

### C1 · Smoke pass
Full arm list at one third duration, all five programs, one pass.
- **Validate:** every arm writes a record, `MIX` shows non-zero flows in every class, `REL`
  produces non-zero `unexpectedEof` (the metric must not be trivially zero), and the control gates
  pass.
- **Rollback:** discard and fix; no campaign time spent.

### C2 · Full campaign
Four passes with randomized program order; pass 1 discarded as warm-up.
- Estimated wall clock: 4 × ≈65 min ≈ 4.5 h, unattended.
- **Validate:** every pass produces five program blocks; `proxiedFraction == 1` everywhere;
  `BASE-direct` gates pass in every pass.
- **Rollback:** a pass can be re-run independently; the VM can be reset between programs.

## Phase D — analysis and report

### D1 · Analysis pipeline
`analysis/analyze.py`: JSONL → `tables.md`, `plots/`, `verdict.json`, with per-run percentile
computation, cross-run median and bootstrap intervals, the pre-declared practical thresholds and
Holm–Bonferroni correction.
- **Validate:** re-running on the same inputs reproduces byte-identical tables.

### D2 · Report
`benchmarks/results/2026-10-06-e2e-competitors/README.md`: the headline matrix, the ratio-to-control
matrix, per-arm detail with gate values, the plots, the verbatim product configurations, and an
explicit statement of what the numbers do and do not cover (loopback upstream hop, single-host
SOCKS5 server, no WAN, four passes instead of RFC 8219's ≥20 repetitions).

### D3 · Quality gates and commit
- `dotnet format WinForward.slnx --severity info --verify-no-changes --no-restore` (empty output)
- `dotnet build WinForward.slnx -c Release` (zero warnings)
- `dotnet test WinForward.slnx -c Release` (green)
- `jb inspectcode -f=Xml -e=HINT -o=/tmp/jb-inspectcode.xml WinForward.slnx` (zero `<Issue>`)
- **Rollback:** the harness is additive; `git revert` of the single commit removes it cleanly.

## Open questions for the user

1. **DNS port.** An authentic port-53 arm needs the target host to bind 53 and the firewall to
   allow it. One-off commands on the Linux host would do it:
   `sudo sysctl -w net.ipv4.ip_unprivileged_port_start=53` and
   `sudo iptables -I nixos-fw 1 -p udp --dport 53 -j ACCEPT` (plus the TCP twin).
   Without them the DNS arm runs on port 30053 with identical semantics and the same
   `localTargets` mechanism, which is a documented deviation rather than a different measurement.
2. **Pass count.** Four passes ≈ 4.5 h unattended, giving three measured passes. More passes are
   better statistics and more wall clock; fewer would leave a single-run ordering behind the
   headline, which is not defensible.
3. **Proxifier headless.** If B4 cannot establish interception without the GUI, is an interactive
   setup session on the VM acceptable, or should Proxifier be dropped from the comparison?
