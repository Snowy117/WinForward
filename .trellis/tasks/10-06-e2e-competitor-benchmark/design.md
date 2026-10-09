# Design — end-to-end competitor benchmark

## 1. Shape of the system

Three cooperating pieces. Only the middle one is new code; the outer two are the machine under
test and the analysis host.

```
 Linux host (192.168.77.4)                  Windows 11 IoT LTSC VM (192.168.77.2)
 ┌────────────────────────┐                ┌──────────────────────────────────────────┐
 │ WinForward.E2E target  │◄── External ──►│ orchestrator.ps1  (product lifecycle)    │
 │  TCP echo   :30010     │   192.168.77/24│   ├─ start/stop product + config         │
 │  UDP echo   :30010     │                │   └─ WinForward.E2E client (all arms)    │
 │  DNS answer :30053     │                │        ├─ load generation                │
 └────────────────────────┘                │        ├─ resource sampler               │
 ┌────────────────────────┐                │        └─ JSONL writer                   │
 │ analysis/analyze.py    │◄── results ────│                                          │
 └────────────────────────┘                │   sing-box 127.0.0.1:1080 (SOCKS5)       │
                                           │   product under test                     │
                                           └──────────────────────────────────────────┘
```

The management path (`192.168.100.0/24`, Internal NIC) carries WinRM for the whole campaign and is
never in any product's interception scope.

### Why the client targets `192.168.77.4`

NDIS filter drivers see frames between the TCP/IP stack and the miniport. Two consequences were
verified on the VM, and they dictate the topology:

- **Traffic to a local address never reaches NDIS.** Windows short-circuits it in the IP layer. A
  probe that connected to `127.0.0.1`, `192.168.77.2` and `192.168.100.2` with an interception
  rule on the port produced three successful connections and zero captured flows.
- **Traffic to a remote address does.** The same probe against `192.168.77.4` was captured,
  classified and proxied.

So the target must live on the second machine. It does not need to be the proxy server: sing-box
still runs on the VM's loopback as requested, and its own egress to `192.168.77.4:30010` is a
second, short hop that every product relays.

## 2. Harness project

New project `benchmarks/WinForward.E2E/`, `net10.0`, cross-platform, two roles in one binary:

```
WinForward.E2E <role> [options]
  target   --bind 192.168.77.4 --tcp-port 30010 --udp-port 30010 --dns-port 30053
  client   --plan plan.json --out <dir> --target 192.168.77.4 --proxy-process <name>
```

Published twice from Linux: `linux-x64` self-contained for the target, `win-x64`
framework-dependent for the client (the VM has the .NET 10 runtime but no SDK).

### 2.1 Wire format

One frame layout for TCP echo, UDP echo and DNS-shaped UDP, so every class shares a decoder and a
single set of counters:

| offset | size | field |
|---|---|---|
| 0 | 4 | magic `WFE1` |
| 4 | 4 | `connId` (u32) |
| 8 | 8 | `seq` (u64, per connection) |
| 16 | 8 | `clientSendTicks` (u64, `Stopwatch.GetTimestamp()` on the client) |
| 24 | 4 | `payloadLen` (u32) |
| 28 | N | payload, deterministic pattern keyed by `connId` and `seq` |
| 28+N | 4 | CRC32C over bytes `[0, 28+N)` |

The target echoes the frame verbatim. RTT is therefore computed entirely from the client's own
clock — no clock offset, no NTP, no one-way delay estimator. A corrupted or rewritten payload is
detected by the CRC and by re-deriving the pattern, so "reached but wrong" is never counted as
"reached".

UDP datagrams for the DNS class carry a valid DNS query as the payload instead of the pattern, and
the target answers with a real DNS response; the accuracy counters come from the same header.

### 2.2 Arms

| Arm | Emulates | Protocol | Duration | Notes |
|---|---|---|---|---|
| `BASE-direct` | control: no proxy at all | TCP+UDP | 60 s | harness loss floor |
| `BASE-singbox` | control: explicit SOCKS5, no product | TCP+UDP | 60 s | attributable proxy increment |
| `IDLE` | idle machine | — | 60 s | idle CPU, memory slope |
| `MIX` | 4 synthetic desktops: browsing + video + DNS + realtime UDP | TCP+UDP | 120 s | the "everyday traffic" arm |
| `LAT-idle` | latency on an unloaded path | TCP+UDP | 60 s | 20 req/s open loop |
| `LAT-load` | latency under load | TCP+UDP | 60 s | 500 req/s open loop |
| `DNS` | stub-resolver traffic | UDP + a little TCP/53 | 90 s | 200 q/s, A/AAAA/HTTPS mix |
| `LOSS` | datagram fidelity | UDP | 120 s | 500 pkt/s, 200 B |
| `REL` | connection churn + upstream aborts | TCP | 120 s | target-side fault modes |
| `THRU` | bulk transfer | TCP | 60 s | 1 and 4 parallel streams |

Per program: about 12 minutes of measurement. Five programs plus a control block: about 65 minutes
per pass.

`MIX` composition (a documented design choice, not a measurement — no published study gives a
contemporary Windows endpoint's TCP:UDP split): page objects of ≈38 KB with 5.6 requests per
connection and 13 connections per page load, a 5 Mbps video-like bulk stream, a 30 pkt/s × 120 B
realtime UDP stream, 4 DNS queries per page with an A/AAAA/HTTPS type mix, and a small
TCP/53 share. The report states the mix explicitly, as RFC 9411 §7.1 requires.

### 2.3 Target-side fault modes for `REL`

The previous harness injected aborts into the proxy's own code path. That is impossible for a
third-party binary and unfair besides. The faults move to the target, where all five products meet
the same upstream:

| mode | target behaviour | what it exposes |
|---|---|---|
| `clean` | full response, then FIN | baseline |
| `resetAfterN` | N bytes, then RST | does the relay forward an upstream reset as a reset? |
| `partialFin` | half the expected bytes, then FIN | does the client see a premature EOF? |
| `halfClose` | keeps sending after the client's FIN | half-close support |
| `stall` | 2 s pause mid-response | timeout and keepalive handling |

The mode is derived deterministically from the connection's index, so the client knows the expected
mode without a negotiation round trip, and client and target each record their own verdict for the
same `connId`. Their disagreement is `fidelityDiscrepancyRate`.

## 3. Product adapters

Each adapter is a PowerShell function pair (`Start-<product>` / `Stop-<product>`) plus a generated
configuration file. Every configuration is copied verbatim into the results directory.

| Product | Start | Measure | Configuration |
|---|---|---|---|
| WinForward AOT | `WinForward.exe run --config wf-aot.json` | `WinForward` | `socks5Servers[main]` with `udpOverTcp: true`; `localTargets[dns]`; pass rule for `sing-box.exe`; proxy rule for the client process; `logLevel: info` |
| WinForward FDD | `WinForward.exe run --config wf-fdd.json` | `WinForward` | identical config, artifact from `WinForward-win-x64-fdd` |
| ProxiFyre | `Start-Service ProxiFyreService` | `ProxiFyre` | `app-config.json`: one rule for the client, `supportedProtocols: ["TCP","UDP"]`, `excludes: ["sing-box","ProxiFyre","ProxiFyreUI"]` |
| Proxifier | service mode (`ProxifierService`) | `Proxifier` | `.ppx` profile: SOCKS5 `127.0.0.1:1080`, one rule for the client, explicit `Direct` rule for `sing-box.exe`, `ProcessServices`/`ProcessOtherUsers` enabled |
| ProxyBridge | `ProxyBridge_CLI.exe --profile pb.pbprofile` | `ProxyBridge_CLI` | profile with a `PROXY` rule for the client and a `DIRECT` rule for `sing-box.exe` |

Three rules are common to all five, and each was learned the hard way:

1. **The SOCKS5 server's process must be excluded.** sing-box's egress to `192.168.77.4:30010`
   otherwise re-enters interception and loops: observed as six nested `UDP ASSOCIATE` handshakes
   and a client timeout.
2. **Everything the client does not own stays unproxied** (`fallbackAction: pass`, or an explicit
   `Direct` rule). The management path depends on it.
3. **Only one product is loaded at a time.** WinForward and ProxiFyre both drive NDISAPI, and
   NDISAPI queues one copy of each packet. The orchestrator asserts that no foreign filter driver
   or service is active before each run.

## 4. Run lifecycle

```
preflight   firewall off · checksum/RSC/LSO state recorded · no foreign driver ·
            sing-box up and answering · target reachable on TCP+UDP ·
            control process confirmed unproxied
per pass    randomized program order
  per program
    generate config -> start product -> wait for readiness
    assert proxiedFraction == 1 on a warm-up probe
    run client: BASE-singbox, IDLE, MIX, LAT-idle, LAT-load, DNS, LOSS, REL, THRU
    stop product -> wait for driver release -> verify no interception
  control   BASE-direct with no product loaded
teardown    download JSONL, restore firewall and offload state
```

`proxiedFraction` is the ground truth that the product actually did the work: the client reports
the flows and datagrams it supplied, the target's ledger reports what arrived with which source,
and the ratio must be 1 for a measured row. Without it, a product that silently leaks shows up as
a suspiciously fast run instead of a failure.

## 5. Metrics and data

One JSONL record per arm per program per pass, plus a 1 Hz resource trace, plus a per-run summary.
Schema is `camelCase`, versioned, and every record carries the environment block (product and
version, configuration hash, sing-box version, log level, NIC offload state, firewall state, pass
index, program order).

| Metric | Definition |
|---|---|
| `lat.<arm>.<class>.pXX` | percentile of echo RTT measured from the intended send instant |
| `cpu.proxy.oneCore` | `ΔTotalProcessorTime / Δwall × 100` (percent of one vCPU) |
| `cpu.proxy.vm` | the same divided by the logical processor count |
| `cpu.proxy.per1kTx` | proxy CPU seconds per 1000 target-side transactions |
| `mem.private.{p50,p95}` | private bytes over the steady-state window |
| `mem.slope` | OLS slope of memory against time, MiB/min |
| `udp.lossRate` | `1 − uniqueArrived(W) / sent`, with `W` published beside it |
| `udp.{corruptRate,dupRate,reorderRate,lateRate}` | from sequence and CRC observables |
| `udp.{clientSendLoss,kernelSendDrop,pathLoss,returnLoss}` | the attribution waterfall |
| `tcp.unexpectedEofRate` | `unexpectedEof / connectAttempts` |
| `tcp.{connectFailRate,resetRate,timeoutRate,halfCloseViolationRate}` | sibling outcomes |
| `tcp.fidelityDiscrepancyRate` | client verdict vs. target verdict disagreement |

Zero-event rates are reported as a bound (`< 3/n`), never as "0 %". Percentiles are computed per
run and then aggregated across runs by median with a bootstrap interval; raw samples are never
pooled across runs for a headline percentile.

## 6. Environment prerequisites

Verified on the VM; each one is asserted by the orchestrator rather than assumed.

1. **Windows Firewall disabled.** The redirect listener's connection looks inbound to the stack,
   and the firewall drops it silently. With the firewall on, every TCP flow fails to establish and
   the listener never accepts; with it off, the same flow echoes correctly.
2. **WinpkFilter driver running** (`ndisrd` service) and an x64 `ndisapi.dll` sidecar next to each
   WinpkFilter-based executable.
3. **VC++ 2015-2022 runtime ≥ 14.44.35211** for ProxiFyre. Its standalone MSI refuses to install
   below that and its UCRT launch condition fails on this image; the official `setup.exe` installs
   and registers the service correctly.
4. **Admin session** for the driver, the firewall change and the services.
5. **sing-box on `127.0.0.1:1080`**, `socks` inbound, `direct` outbound, version pinned at 1.14.2,
   which contains the socks-inbound UDP association timeout fix.
6. **Linux-side firewall allowance** for the target ports on `eth1`.
7. **WinForward at `logLevel: info`.** Debug and trace change internal behaviour.

## 7. Analysis

`benchmarks/WinForward.E2E.Analysis/` (C#, .NET console) reads the JSONL and emits:

- `tables.md` — the headline matrix, the ratio-to-control matrix, and per-arm detail with the gate
  values;
- `plots/` — per-arm latency ECDFs, percentile curves, per-pass box plots, the UDP loss waterfall,
  resource time series, and a run-order drift check;
- `verdict.json` — the decision per metric, with the pre-declared practical thresholds (latency
  5 %, CPU 10 %, memory 10 %, loss 0.5 percentage points) and Holm–Bonferroni correction.

## 8. Risks

| Risk | Mitigation |
|---|---|
| Proxifier's service install and profile import are documented as GUI-only for the Standard Edition | Time-boxed spike first; fall back to `Proxifier.exe <profile> silent-load` in session 0, and if neither works, report the cell as "not measurable in this environment" rather than guessing |
| Two WinpkFilter clients on one adapter steal each other's packets | One product at a time, asserted before every run; the VM can be reset to its initial state between programs |
| Dynamic-memory ballooning makes working sets incomparable | Private bytes as the headline, per-sample guest-visible memory recorded, and any window whose visible memory moves is rejected |
| Loopback upstream hop inflates absolute latency | Stated in the report; every product pays it equally, and `BASE-singbox` makes the product-attributable increment explicit |
| 2 GB RAM with five products and a load generator | One product at a time, a bounded in-flight window, and an early abort if the VM starts paging |
| Harness loss masquerading as path loss | Bounded window with explicit overflow accounting, `BASE-direct` gate at path loss < 1e-6, and a burst calibration arm to find the harness ceiling |

## 9. Deliverables

- `benchmarks/WinForward.E2E/` — harness source (target, client, plan, orchestrator, adapters).
- `benchmarks/WinForward.E2E.Analysis/` — the analysis that replaced the retired Python reader, plus
  the campaign report (`README.md`) with the tables and the interpretation limits. The campaign's own
  artifact tree (`benchmarks/results/2026-10-06-e2e-competitors/`) was removed once the analysis was
  ported; only the report and the frozen oracle inputs survive in the repository.
