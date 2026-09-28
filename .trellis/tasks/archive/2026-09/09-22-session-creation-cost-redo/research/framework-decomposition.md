# Framework decomposition (corrected) — real-dial per-session cost with the harness server out of process

Task `09-22-session-creation-cost-redo`, correcting
`archive/2026-09/09-21-session-creation-cost/research/framework-decomposition.md`.
Evidence base: the framework ladder batch `research/raw/framework-{in,ext}-run{1,2,3}.log`
and the probe batch `research/raw/udpsession-{ext,in}-run{1,2,3}.log`
(2026-09-22, HEAD `eaac0c5` + the harness change of this task).

## 1. What was wrong with the recorded numbers

The recorded "framework path 83,442 B/session (control connect + greeting 77,448 B = 92.8 %)"
was measured with the loopback SOCKS5 server in the **same process** as the measured client.
That server allocates per accepted control connection:

- a 64 KiB relay-loop buffer — `new byte[65_536]` at
  `benchmarks/WinForward.Benchmarks/Stability/LoopbackSocks5UdpServer.cs:240`, inside
  `RelayLoopAsync()`, which every `RelayConnection` starts unconditionally before its control
  handshake (`:150`);
- a per-connection UDP relay socket with a 4 MiB kernel receive buffer (`:139-144`);
- control-side arrays (`:169-210`) and a `ConcurrentDictionary` entry + `ContinueWith`
  (`:106-112`).

`GC.GetTotalAllocatedBytes` is process-wide, so all of it landed next to the client's
allocations. The recorded docs noticed the 64 KiB buffer but attributed it only to a ~3 KB
"harness-shaped residual" (reading it as a concurrency effect: "the component variants
exercise one connection at a time"); it is in fact a **per-connection** cost and therefore
inflates every server-touching instrument by ~75 KB/session.

## 2. Method

- Child server mode: `--stability --serve-socks5-udp --flows N [--dial-delay-ms D]` hosts the
  same `EchoReceiver` + `LoopbackSocks5UdpServer` pair in a separate process and prints one
  JSON handshake line; the parent-side `ExternalLoopbackSocks5UdpServer` spawns it, reads the
  handshake, and kills it on dispose (stdin-EOF + process-tree kill).
- External runs: `WINFORWARD_BENCH_EXTERNAL_SERVER=1` on the benchmark host switches
  `FrameworkSetupBenchmarks` / `UdpSessionBenchmarks` to the child server.
- Command lines (from the repo root, one process at a time):

```text
# external (clean) ladder, 3 runs -> research/raw/framework-ext-run{1,2,3}.log
WINFORWARD_BENCH_EXTERNAL_SERVER=1 dotnet run -c Release --project benchmarks/WinForward.Benchmarks \
  -- --filter '*FrameworkSetup*' --job short
# in-process (recorded shape) ladder, 3 runs -> research/raw/framework-in-run{1,2,3}.log
dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- --filter '*FrameworkSetup*' --job short
```

- Conventions (matching the archived docs, verified against them by
  `research/tools/parse-bdn-alloc.py`): per-session value of a ladder row =
  `Allocated(Sessions=1000) / 1000`, mean of the three runs, spread = max−min of the runs.
  BDN's "KB" is 1,024 B; every number here is in bytes.
- Environment: this Linux dev box, `--job short` (3 warmups + 3 iterations + the
  allocation-only pass), sequential runs; per-run `/proc/loadavg` in the sibling `.load`
  files. ns/µs are ordinals only on this box and are not used.

## 3. A/B verification: the recorded shape reproduces, the artifact is the difference

| Variant (Sessions=1000 ÷ 1000, mean of 3) | in-process, this batch | recorded (2026-09-21/22) | out-of-process, this batch | harness share |
|---|---:|---:|---:|---:|
| `RealTransport_CreateDisposeAsync` | 83,441.6 (spread 0.8) | 83,441.9 | **7,952.0** (spread 0.6) | 75,489.6 |
| `Control_ConnectDisposeAsync` (connect + greeting) | 77,447.3 (spread 6.1) | 77,447.8 | **3,792.2** (spread 0.0) | 73,655.1 |
| `Control_ConnectAssociateDisposeAsync` | 80,319.0 (spread 15.9) | 80,319.8 | **4,751.2** (spread 1.1) | 75,567.8 |
| `RelaySocket_CreateBindClose` | 576.0 | 576.0 | 576.0 | 0 |
| `SelfTraffic_RegisterRelease` | 160.0 | 160.0 | 160.0 | 0 |

Two facts close the loop:

1. **The in-process path is byte-identical to the record** (every ladder row within ~1 B of
   the archived values), so the correction is a measurement change, not a behavior change.
2. **The delta is the harness**: 75,489.6 B/session on the full path, 73,655.1 on the
   connect-only prefix. The 1,834.5 B/session difference between those two harness shares is
   the server-side work the ASSOCIATE request adds (the recorded ASSOCIATE delta of 2,872 B
   was itself ~1.9 KB harness: the clean delta is 959.0 B).
   An independent standalone probe run during diagnosis (real product client via
   `Socks5UdpTransportFactory`, out-of-process SOCKS5 server, 1→1000 marginal) measured
   8,346 B/session for the same create+dispose path — 5.0 % above the BDN figure here
   (different harness shaping: Python server, explicit frame-size/DNS settings). It is
   consistent, not authoritative; the BDN numbers above are.
3. **Provenance confirmation on the reviewed code.** After the `trellis-check` pass applied two
   behavior-preserving gate fixes to the harness, one further probe run per shape reproduced the
   campaign values: external real 17,031.3 / Noop 5,721.1, in-process real 92,296.2 / Noop
   5,729.9 (+0.06 % / +0.04 % on the real rows; `research/raw/udpsession-{ext,in}-confirm.log`).

## 4. Corrected framework table (out-of-process, 3 runs)

| Component | B/session | Notes |
|---|---:|---|
| control connect + greeting + disposal | 3,792.2 | the per-flow TCP dial: socket graph, epoll registration, async/await state machines, `NetworkStream`, the attempt deadline CTS + its timer, the `QuiescenceScope`, the no-auth greeting exchange, per-session DNS (the address cache is null in the benchmark) |
| UDP ASSOCIATE | 959.0 | delta of the +ASSOCIATE row over the connect-only prefix: request write, reply parse, relay-endpoint materialization, one linked CTS per call |
| relay socket (create / 512 KiB recv buffer / bind / non-blocking / close) | 576.0 | kernel buffer not counted (managed allocation only) |
| self-traffic register/release | 160.0 | one dictionary entry + one token |
| residual: transport construction + wiring | 2,464.8 | derived: 7,952.0 − the four rows above; dominated by the 1,536 B send buffer (`6 + 16 + cap`) plus `SocketAddress`, receive-sender `IPEndPoint`, `SemaphoreSlim(1,1)`, the transport instance |
| **framework path total (create+dispose)** | **7,952.0** | named components sum exactly (residual is the balance) |
| real-transport probe marginal (both layers, echo-fed shape) | 17,021.2 | spread 15.4; = framework 7,952.0 + Noop bookkeeping 5,727.0 + 3,342.2 residue (see `churn-measurements.md` §Composition) |
| churn whole cycle (`udp.churn`, wave / sustained) | 13,248.9–14,069.9 / 13,720.2–13,868.6 | spreads ≤251 B; see `churn-measurements.md` |

## 5. Cross-checks against the archived anchors

| Quantity | Recorded | Corrected | Note |
|---|---:|---:|---|
| framework sub-anchor (isolated create+dispose) | 83,442 | **7,952** | the recorded value is ~10.5× the clean one; the difference is the in-process harness |
| "control connect + greeting = 92.8 % of the framework path" | 77,448 (92.8 %) | 3,792 (47.7 % of 7,952) | the share was computed inside the contaminated total; the correction also changes the *composition* reading |
| ASSOCIATE | 2,872 | 959 | the in-process delta carried ~1.9 KB of server-side work |
| probe-derived difference | 86,520.2 recorded (92,254.8 − 5,734.6); reproduced 86,526.6 (92,260.6 − 5,734.0) | 11,294.2 clean (17,021.2 − 5,727.0) | the harness share of the recorded real-probe marginal is 75,233.6 (92,254.8 − 17,021.2) = 81.6 % of it; the clean difference = this layer (7,952.0) + a 3,342.2 B/session residue (29.6 % of the clean difference) |
| relay socket / self-traffic | 576 / 160 | 576 / 160 | unchanged; never touched the server |

## 6. Interpretation (allocation first)

- The client-side framework path of one UDP session is **~8.0 KB/session**, of which the
  actual per-flow TCP dial is **~3.8 KB** — roughly the irreducible .NET TCP-socket lifecycle
  for a connect + tiny exchange (a bare standalone floor probe measured 2,589 B/session for
  the same primitive shape: socket + bind + connect + `NoDelay` + 3 B write + 3 B read), plus
  the product's own attempt/deadline/scope machinery.
- The recorded 77 KB "control dial" does not exist in the product. A control-connection reuse
  design would remove the ~3.8 KB/session dial (and the ~1 KB ASSOCIATE round trip per flow
  stays, since each flow still associates), not 77 KB.
- Nothing in this layer is removable by a WinForward-side allocation micro-fix: the dial's
  allocations are the BCL socket/stream/CTS graph, and the relay socket and send buffer are
  per-session by design. The structural lever (control-connection reuse) is a product/protocol
  decision whose *allocation* case is now ~3.8 KB/session rather than 77 KB — its latency,
  port-exhaustion and TTL motivations are unaffected by this correction.

## 7. Gaps carried forward (unchanged unless noted)

- A fake control connection is not expressible (`Socks5ControlConnection` is sealed), so
  connect+greeting and ASSOCIATE are protocol-indivisible without a src seam.
- DNS resolution is inside the measured connect+greeting (address cache null in the
  benchmark); production may cache per server — a cold-edge option, not a per-session cost.
- The out-of-process probe is echo-fed (see `design.md` §4.1 of this task): its sessions
  receive one response each, so its marginal covers response work the recorded discard-fed
  probe never exercised. Reported with the shape named; used to attribute the recorded docs'
  "1.6–3.3 KB/session response/retire residue".
