# 2026-08-30 Windows VM benchmark + soak (proxy-perf-stability backlog #9)

First Windows-side measurement pass after backlog items #1–#5 landed (fast-hardening,
hot-path-revival, atomic-retire, udp-alloc-jumbo). Companion Linux runs were taken at the
same HEAD on the same day so every table below is apples-to-apples.

## Environment

| | Windows target | Linux companion |
|---|---|---|
| System | Win11 IoT LTSC, build 10.0.26100 | Ubuntu 24.04 |
| vCPU | 32 logical | 16 logical (Ryzen 9 9955HX host) |
| Memory | 4 GB | (dev VM) |
| Power plan | High Performance (switched from Balanced before running) | — |
| Runtime | .NET 10.0.10, self-contained single-file publish | .NET 10.0.10 |
| Admin session | yes | — |

Both guests run on the same physical host; cross-OS deltas are attributable to the OS
stack (kernel/socket/timer), not virtualization. The Windows guest has twice the logical
cores of the Linux guest, which if anything favors Windows on the CPU micro-benchmarks.

Repro commands use `<vm-host>` as a placeholder for whatever Windows host you drive; this
run drove it over WinRM (channel details are not part of the measurement).

## Method notes

- Stability mode: same runner, same parameters as the 2026-08-29 run
  (`--stability --scenario all --duration 60 --pps 25000`), so numbers line up with
  `../2026-08-29-windows-real-machine/`.
- BDN perf: both sides ran ShortRun jobs with the **InProcessEmitToolchain**
  (`--inProcess`). The Windows guest has no .NET SDK, so the default process-isolated
  toolchain cannot spawn children there; running both sides in-process keeps the
  comparison valid. Treat <2× ns/ms deltas as noise per `../../README.md`; allocation
  bytes are exact gates.

## R1 — Stability matrix (60 s, 25k pps requested)

| metric | win 08-30 | win 08-29 | lin 08-30 | lin 08-29 |
|---|---:|---:|---:|---:|
| udp achievedPps | 4,607 | 6,914 | 24,960 | 22,391 |
| udp lossRate | **0** | 2.475 % | 0.281 % | 0.194 % |
| udp sendLoopOverflows | 1,105 | 1,952 | 31 | 18 |
| udp outOfOrder / duplicates | 0 / 0 | 0 / 0 | 18 / 0 | 0 / 0 |
| rawBaseline achievedPps | 4,743 | — | 25,000 | — |
| rawBaseline outOfOrder | 184 | — | 19 | — |
| tcp eof meanTransferMs | 152.4 | 150.3 | 9.0 | 13.5 |
| tcp eof otherErrors (rate) | 3,098 (13.9 %) | 1,849 (8.2 %) | 0 | 0 |
| tcp eof resets | 3,367 | 4,749 | 44,263 | 32,624 |
| tcp throughput failed | 14,400 / 14,899 (96.6 %) | n/a | 0 / 138,986 | n/a |
| footprint allocatedBytes | 4,735,568 | 10,385,104 | 4,804,096 | 9,785,920 |

50k pps UDP probe (`--pps 50000`, 60 s): achievedPps 4,717, loss 0, overflows 566,
duplicates/outOfOrder 0 — the loopback send ceiling (~4.7k pps) is independent of the
requested rate.

## R2 — BDN perf subset (ShortRun, in-process, both sides)

CPU micro-benchmarks are platform-equivalent (well inside the noise band):
CapturePump 70.8–75.2 ms (win) vs 74.4–79.7 ms (lin); Dispatcher 227–576 ns vs 239–559 ns;
NdisBuffer reuse 5.9–25.3 ns vs 6.9–30.8 ns.

Allocation gates are byte-identical where they matter: CapturePump 1.86 MB both sides,
Dispatcher 160/352 B both sides, NdisBuffer 40 B / 0 B both sides.

Socket-I/O-shaped cases show a large OS-stack gap:

| benchmark | win Mean | lin Mean | ratio |
|---|---:|---:|---:|
| TcpRelay[ChunkBytes=1] | 2,374.0 ms | 191.8 ms | 12.4× |
| TcpRelay[ChunkBytes=1024] | 213.8 ms | 19.3 ms | 11.1× |
| TcpRelay[ChunkBytes=8192] | 34.4 ms | 11.7 ms | 2.9× |
| TcpRelay[ChunkBytes=65536] | 71.8 ms | 12.7 ms | 5.7× |
| UdpSession populate[100] | 95.0 ms | 24.6 ms | 3.9× |
| UdpSession populateNoop[100] | 16.3 ms | 2.2 ms | 7.5× |

Known caveats: TcpRelay absolute allocations include one-time socket scaffolding (see
`../../README.md`) — small-chunk Windows runs allocate ~2× more scaffolding (414 vs 212 KB
at chunk=1); this is not a production-path gate. `UdpSession.PopulateSessionsAsync[1000]`
(1,000 loopback-transport sessions) is **NA on Windows** (NoopTransport[1000] succeeds,
33.9 ms); cause not diagnosed this pass.

## R3 — 1-hour TCP soak

`--stability --scenario tcp --duration 3600` (TcpEof, 64 concurrency, mixed aborts) with
external 60 s sampling of the benchmark process working set / handle count
(`soak-sample.csv`, 60 samples covering the full window).

| metric | value |
|---|---:|
| transfers | 928,024 (~15.5k connection cycles/min for 1 h) |
| completed | 208,803 (22.5 %) |
| unexpectedEof | 417,528 (45.0 % — matches the injected abort-mix watermark; the 60 s run shows 47.3 %) |
| resets / otherErrors | 129,204 / 172,489 (18.6 %) |
| bytesCompleted | 218.9 GB |
| meanTransferMs | 212.0 (vs 152.4 in the 60 s run — sustained port pressure slows the tail) |
| working set | 80–93 MB, first-third → last-third mean drift **+0.9 %** |
| handles | 1,442–1,749, mean drift **−4.0 %** |

**Gate: PASS** — no monotonic working-set or handle growth over one hour at ~257
connection cycles/s. The elevated otherErrors rate (18.6 % vs 13.9 % at 60 s) tracks the
port-pool finding below, not a leak.

## R4 — Port-pool attribution experiment

The throughput failures were hypothesized to be OS dynamic-port exhaustion, not a
product defect. With only the port pool widened (`netsh int ipv4 set dynamicport tcp
start=10000 num=55535`, 16,384 → 55,535 ports; TIME_WAIT untouched) and the same 60 s
matrix rerun (`stability-windows-wideports.jsonl`):

| metric | default pool | widened pool |
|---|---:|---:|
| tcp.throughput failed | 14,400 / 14,899 (96.65 %) | **52 / 13,036 (0.40 %)** |
| tcp.throughput completed | 499 | 12,984 |
| tcp.eof otherErrors (rate) | 3,098 (13.9 %) | 2,531 (9.5 %) |
| udp lossRate / achievedPps | 0 / 4,607 | 0 / 4,486 (noise) |

**Confirmed: the failures are dynamic-port-pool capacity.** Steady-state capacity at
default settings is ≈ 16,384 ports / 60 s TIME_WAIT ≈ 273 conn/s; the scenario demands
~500 ports/s (client + upstream per transfer). Linux shows zero failures on the same
scenario because its default local range (~28k ports) allows loopback TIME_WAIT reuse
(`tcp_tw_reuse=2`). The VM was restored to the default pool after the run.

## Verdicts on the 2026-08-29 open issues

1. **Windows UDP send throughput & loss (was: 31 % of Linux, 2.47 % loss)** — **loss
   eliminated** (0 % at 25k and 50k requested), overflows halved (1,952 → 1,105). But the
   loopback ceiling is unchanged in nature: ~4.7k pps vs Linux ~25k pps (18.5 %), and
   achieved pps dropped 6,914 → 4,607 (−33 %) — the 08-29 number was a lossy overshoot.
   The bottleneck is the OS loopback socket + timer granularity, not the managed path
   (R2 shows managed CPU cost is platform-equivalent).
2. **WSAEADDRINUSE under TCP churn (was: 8.2 %)** — **worsened, then attributed**: 13.9 %
   of transfers in the eof scenario, 96.65 % in the throughput scenario. The R4
   experiment (widened port pool only) drops throughput failures to 0.40 % — pure
   dynamic-port-pool capacity, no product defect. Remediation belongs to deployment
   guidance + harness accounting, plus a loopback-aware close policy (see backlog).
3. **Hours-scale soak** — this pass (R3).
4. Ctrl+C graceful shutdown / AOT publish verification — still out of scope.

## Landing checks for backlog #1–#5 (managed-only visibility)

- #5 udp-alloc-jumbo: session footprint −54 % on Windows (10.39 → 4.74 MB), −51 % on
  Linux. Directly confirmed.
- #2 fast-hardening (client RST): eof-scenario resets 4,749 → 3,367 (−29 %), consistent
  with the intended fault-visible behavior.
- #1 / #3 / #4 (hot dispatch revival, atomic retire, bounded queues) act on the NDIS
  capture path, which the managed-only loopback suite cannot see — no Windows-side number
  here claims to validate them; that remains NDIS-path territory.

## New backlog candidates (feed `08-30-proxy-perf-stability`)

- **Port-pool capacity for high-churn Windows deployments** (R1+R4): attributed to OS
  dynamic-port capacity, not the product. Candidate child combining: deployment
  guidance (widen dynamic range / tune TcpTimedWaitDelay), loopback-aware upstream
  close policy (error-path RST when the SOCKS5 target IsLoopback — the #2 client-side
  RST already covers the other side), and harness-side separation of harness/product
  port consumption. Real desktop load (~20 conn/s) is far below the ~273 conn/s
  steady-state ceiling; the risk is adversarial/P2P churn and long-run peaks.
- **Mux-shaped local transport research**: replacing per-flow loopback TCP to the local
  SOCKS5 server (fixed connection pool + stream multiplexing, e.g. sing-box VLESS
  inbound + multiplex) would eliminate port churn, per-flow SOCKS5 handshake cost, and
  loopback connect syscalls at once. sing-box has no UDS inbound (issue #733 closed
  unimplemented), so TCP mux is the viable shape. Research first: verify server-side
  mux behavior and loopback mux throughput.
- **Windows loopback send ceiling** (~4.7k pps, rate-independent): managed path is not
  the cause; needs a real-NIC / ETW-level measurement to go further. Extends the
  windows-reality idea rather than the managed code.
- **UdpSession real-transport populate at 1000 sessions fails on Windows** (NA): small
  diagnostic item.

## Implications for the parent backlog (direction, pre-soak)

Data-backed reordering of the remaining backlog items:

1. **New: port-pool exhaustion under churn** — strongest user-visible finding (96.6 %
   throughput failures). Not covered by any existing backlog item.
2. **#7 batched-ioctls up** — Windows per-IO cost is the structural gap (TcpRelay 12.4×
   at chunk=1 shrinking to 2.9× at chunk=8192 shows batching amortizes exactly this).
3. **#8 hardening-bundle as planned** — ServerGC matters on many-core/small-RAM shapes
   like this 32 vCPU / 4 GB guest; GC pauses remain an open measurement gap.
4. **#6 zero-copy-datapath down** — the managed path is platform-equivalent on time and
   byte-identical on allocations; further managed-side ns/copy reduction does not
   address the Windows bottleneck.
5. **Do not optimize the send loop against the ~4.7k pps loopback ceiling** — it is a
   measurement property of loopback sockets + timer granularity (rate-independent,
   loss-free now). Only a real-NIC/NDIS-side measurement can see past it.

## Files

- `stability-windows-full.jsonl` — R1 matrix
- `stability-windows-udp50k.jsonl` — 50k pps probe
- `stability-windows-wideports.jsonl` — R4 attribution rerun (widened pool)
- `soak-tcp-3600.jsonl`, `soak-sample.csv` — R3 soak + memory/handle samples
- `bdn-win/` — Windows BDN csv (in-process ShortRun)
- `linux-companion/` — Linux stability run at same HEAD
- `bdn-linux-inproc/` — Linux BDN csv, same toolchain as `bdn-win/`

## Repro

```text
# Linux (repo root)
dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- \
  --stability --scenario all --duration 60 --pps 25000 --output stability-linux-full.jsonl
dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- \
  -f '*CapturePump*' '*Dispatcher*' '*TcpRelay*' '*UdpSession*' '*NdisBuffer*' --job short --inProcess

# Windows (publish first, elevated session, High Performance power plan)
dotnet publish benchmarks/WinForward.Benchmarks -c Release -r win-x64 \
  --self-contained -p:PublishSingleFile=true
.\WinForward.Benchmarks.exe --stability --scenario all --duration 60 --pps 25000 --output stability-windows-full.jsonl
.\WinForward.Benchmarks.exe -f '*CapturePump*' '*Dispatcher*' '*TcpRelay*' '*UdpSession*' '*NdisBuffer*' --job short --inProcess
.\WinForward.Benchmarks.exe --stability --scenario tcp --duration 3600 --output soak-tcp-3600.jsonl
```
