# Windows VM benchmark + soak program (backlog #9)

Parent: `08-30-proxy-perf-stability`. Measurement task — no product code changes; findings
feed the parent backlog (e.g. decide whether #6/#7 are worth it, and whether #1–#5 landed
their projected wins on Windows).

## Environment (user-decided 2026-08-30)

- Target: Win11 IOT LTSC **VM** at `192.168.100.2` (winrm/5985), user `neko`.
  - No AV, all physical cores assigned. Hosts both this VM and the Linux dev box, so
    Linux-vs-Windows deltas are attributable to the OS/driver stack, not virtualization.
- Remote channel: `evil-winrm-py -i 192.168.100.2 -u neko -p "$NEKO_PASS"`
  (`upload`, `runexe`, `runps`, `download`).
- Baselines to compare against (on-disk):
  - `benchmarks/results/stability-windows-full.jsonl` (2026-08-29 Windows run)
  - `benchmarks/results/2026-08-29-windows-real-machine/README.md` (open issues list)
  - `benchmarks/results/stability-linux-full.jsonl` + latest Linux BDN baselines.
- Comparability discipline per `benchmarks/README.md`: same command, same publish mode,
  before/after deltas only; ns/pps deltas < ~2× are noise, allocation bytes are exact gates.

## Requirements

### R1 — Windows stability matrix at current HEAD (vs 2026-08-29 baseline)

Publish self-contained, run the full stability suite on the VM:

- `--stability --scenario all --duration 60 --pps 25000` (comparable to prior run), plus
  one higher-pps probe (e.g. `--pps 50000`) to re-probe the known Windows send-path
  ceiling (prior: 6914 vs 22391 pps Linux, 2.47% loss, tick overflows).
- Re-check prior open issues with numbers: UDP send throughput ratio, loss ratio,
  `WSAEADDRINUSE` under TCP churn (`--tcp-concurrency 64`), footprint working-set delta.

### R2 — Key perf families on Windows (BDN subset)

Run at minimum: `CapturePumpBenchmarks`, `DispatcherBenchmarks`, `TcpRelayBenchmarks`,
`UdpSessionBenchmarks`, `NdisBufferBenchmarks` (the families touched by children #1–#5).
Full matrix only if time permits; note in README which families ran.

### R3 — Hours-scale soak

- At least one 1-hour soak on the VM (scenario with highest churn, likely `tcp` with
  concurrency + `udp` at sustained pps; exact scenario justified in the README).
- Record: loss ratios, overflow counts, working set / handle counts over time, GC counts.
- Gate: no monotonic working-set or handle growth over the hour.

### R4 — Results land on disk + interpretation

- New `benchmarks/results/2026-08-30-windows-vm/` directory: raw JSONL/BDN output +
  README documenting environment (VM, cores, OS build, publish mode, runtime version),
  exact commands, and a comparison table vs Linux and vs the 2026-08-29 Windows run.
- Verdict per prior open issue: improved / unchanged / regressed, with numbers.
- New findings → appended to the parent backlog as candidates (not fixed here).

## Out of Scope

- NDIS/WinpkFilter real-path perf (the stability suite is managed-only loopback); if a
  proxy functional smoke on the VM is cheap, record it as evidence, not a requirement.
- Fixing any regression found — file it into the parent backlog.
- Ctrl+C graceful-shutdown verification, AOT publish verification (prior leftovers):
  include only if they fall out of the run for free.
- IPv6 / fragmentation / DNS-shaped scenarios (deferred in research §5).

## Acceptance Criteria

1. `benchmarks/results/2026-08-30-windows-vm/` exists with R1–R3 raw outputs + README.
2. README contains: environment record, exact repro commands, Linux-vs-Windows-vs-08-29
   comparison tables, and a verdict line for every prior open issue.
3. Soak (R3) has a pass/fail statement against the R3 gate with numbers.
4. Parent backlog updated (or explicitly confirmed unchanged) based on findings.
5. No product source changes in the merge; only `benchmarks/results/**` additions.
