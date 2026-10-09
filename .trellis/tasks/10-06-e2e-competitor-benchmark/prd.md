# End-to-end competitor benchmark: WinForward AOT/JIT vs ProxiFyre / Proxifier / ProxyBridge

## Goal

Build and run a real end-to-end benchmark on the Windows dev VM comparing the CI-built WinForward
AOT and framework-dependent artifacts against ProxiFyre 2.6.1, Proxifier 4.14 and ProxyBridge 4.0.0
on latency, CPU, memory, UDP accuracy and TCP unexpected rate, through sing-box on loopback, with
results and a report recorded under `benchmarks/results/`.

## Why

Every performance number this project owns today is **managed-only and loopback**: the
`WinForward.Benchmarks` perf mode never opens the NDISAPI driver, and the stability mode relays
through in-process sockets. Those numbers answer "is the managed hot path fast" — they cannot
answer the question a user actually asks before choosing the product: *how does WinForward behave
on a real machine, with a real driver, against the tools it competes with?*

This task produces that missing measurement. It is a measurement task, not an optimisation task:
no file under `src/` or `tests/` may change. The binary under test is the CI artifact of commit
`a247658ff4cbd6f4aaaf0f39ca219bd652579894`, downloaded rather than rebuilt, so the numbers
describe a shipped artifact rather than a working tree.

## What is compared

Five programs, each intercepting traffic from one client process on a Windows 11 IoT Enterprise
LTSC VM and relaying it to a SOCKS5 server:

| # | Program | Build | Interception |
|---|---|---|---|
| 1 | WinForward | native AOT, CI artifact `WinForward-win-x64` | WinpkFilter / NDISAPI |
| 2 | WinForward | framework-dependent, CI artifact `WinForward-win-x64-fdd` | WinpkFilter / NDISAPI |
| 3 | ProxiFyre 2.6.1 | official x64 release | WinpkFilter / NDISAPI |
| 4 | Proxifier 4.14 | official x64 installer, Standard Edition service mode | own WFP callout driver |
| 5 | ProxyBridge 4.0.0 | official x64 installer, `ProxyBridge_CLI.exe` | WinDivert |

`forwarded` mode is out of scope: the other three products have no equivalent, so there is nothing
to compare it against. Only the `host` domain — flows the machine itself originates — is measured.

## Metrics

Five families, measured per program:

1. **Latency** — round-trip percentiles (p50/p90/p99/p99.9) for TCP connect, TCP request/response
   and UDP echo. Measured client-side only, from the *intended* send instant, so no clock-offset
   correction is needed and coordinated omission cannot hide the tail.
2. **CPU** — `Δ(Process.TotalProcessorTime) / Δwall` of the product's own process, reported as a
   percentage of one vCPU with the machine percentage beside it, plus per-1000-transaction and
   per-1000-packet normalisations. The load generator's own CPU and the VM total are reported in
   the same row so a reader can see whether the product was starved.
3. **Memory** — `PrivateMemorySize64` (headline, comparable across the four runtime shapes),
   `WorkingSet64`, `PeakWorkingSet64`, thread and handle counts; steady-state median and p95 over
   a fixed window, plus the MiB/min slope as the leak indicator.
4. **UDP accuracy** — loss, corruption, duplicates and reordering from sequence-numbered,
   CRC32-checked datagrams; published loss threshold `W`; and a four-bucket attribution waterfall
   (`clientSendLoss` / `kernelSendDrop` / `pathLoss` / `returnLoss`) so harness loss can never be
   reported as path loss.
5. **TCP unexpected rate** — `unexpectedEof / connectAttempts` plus the sibling outcome rates
   (connect failure, reset, timeout, half-close violation) and a two-sided fidelity discrepancy
   rate (client verdict vs. target verdict on the same connection id).

Proxifier cannot proxy UDP at all (`Proxifier supports TCP/IP connections only`). Its UDP rows are
reported as **not carried**, never as 100 % loss, and its `proxiedFraction` is measured so the
silent leak is visible rather than assumed.

## Topology

```
 [Linux host 192.168.77.4]                          [Windows 11 IoT LTSC VM]
  target server                                      orchestrator + product under test
   TCP echo  :30010  <---------- External NIC ------->  ProxiFyre / Proxifier / ProxyBridge
   UDP echo  :30010             192.168.77.0/24        WinForward AOT / FDD
   DNS respond :30053                                      |
                                                           | SOCKS5 (+ UoT v2 for WinForward UDP)
                                                           v
                                                      sing-box 127.0.0.1:1080  --> target
```

The client's flow leaves the VM towards `192.168.77.4:30010`, is intercepted by the product,
relayed to sing-box on loopback, and re-emitted by sing-box to the same address. Management
traffic stays on the Internal NIC (`192.168.100.0/24`) and must never be captured.

WinForward runs with its two requested optimisations on: `udpOverTcp: true` (UoT v2 connect mode)
on the SOCKS5 target, and a `localTargets` entry serving DNS, so port 53 does not pay the SOCKS5
handshake.

## Requirements

- **No changes under `src/` or `tests/`.** The harness is new code under `benchmarks/`.
- The binary under test is the CI artifact of `a247658`, not a local build.
- WinForward's log level is `info` for every measured run; `debug` and `trace` change internal
  behaviour and would measure a different program.
- Only one interception product may be active at a time. WinForward and ProxiFyre both drive
  NDISAPI, and NDISAPI cannot serve two clients on one adapter.
- The `192.168.100.0/24` management path must survive the whole campaign.
- Windows Firewall is disabled for the campaign: the redirect listener accepts what the stack sees
  as an inbound connection, and the firewall silently drops it otherwise (verified on the VM).
- Every product needs an explicit direct/pass rule for the SOCKS5 server's own process
  (`sing-box.exe`); without it its egress is re-intercepted and the flow loops.
- The workload must resemble everyday traffic — short-lived and bulk TCP, DNS over UDP, and a
  general UDP class — rather than a single synthetic firehose.
- Whatever each product's own documentation recommends as its configuration for this shape is what
  it runs; no product is tuned beyond that, and each configuration is recorded verbatim.

## Acceptance Criteria

- [ ] A harness exists that drives all five programs headlessly and unattended through a full pass,
      writing one JSONL record per arm plus a per-run summary and a per-sample resource trace.
- [ ] Each program's effective configuration is recorded with the results, and a pre-run assertion
      proves the interception scope (a control process that must stay unproxied, and a
      `proxiedFraction` check that must equal 1 for the measured client).
- [ ] Latency, CPU, memory, UDP accuracy and TCP unexpected rate are reported for all five
      programs, with Proxifier's UDP cells explicitly marked "not carried".
- [ ] The UDP loss attribution waterfall is published, and the control arm (`BASE`, no proxifier)
      shows path loss below 1e-6 — otherwise the harness, not the products, is being measured.
- [ ] At least three measured passes after a discarded warm-up pass, with the between-run spread
      reported rather than a single run's number.
- [ ] Raw results, the analysis and a report: the analysis and the report live under
      `benchmarks/WinForward.E2E.Analysis/` (the C# analysis there replaced the original Python
      `analyze.py`), and the campaign's own artifact tree was removed with it.
- [ ] A written statement of what the numbers do and do not cover (loopback upstream hop,
      single-host loopback SOCKS5 server, no WAN, deviation from RFC 8219's ≥20 repetitions).

## Out of scope

- `forwarded` mode, IPv6, and a real HTTP/3 load generator (the mixed arm keeps a QUIC-shaped UDP
  class; a real `HttpClient` HTTP/3 arm is a follow-up if it is wanted).
- Tuning any product, or reporting a best-configuration search.
- Any claim about WAN behaviour. The upstream hop is loopback by request.
