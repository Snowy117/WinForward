# WinForward.E2E

An end-to-end harness for Windows transparent SOCKS5 proxies. A client process (`WinForward.E2E
client`) sends synthetic traffic to a Linux target (`WinForward.E2E target`) through the product
under test, and the harness reports latency, CPU, memory, UDP accuracy and the TCP unexpected rate.

The products compared are WinForward in two builds, ProxiFyre, Proxifier and ProxyBridge.

The analysis of a campaign is a sibling directory and a separate document:
[analysis/README.md](../results/2026-10-06-e2e-competitors/analysis/README.md). This file covers the
harness: how to build it, how to run it, what it writes and how to read it.

## What this is, and what it is not

One cross-platform binary is both the client and the target. The whole harness therefore runs end to
end on a single Linux host with nothing between the two ends — that is the self-test.

**A green self-test means the harness works. It says nothing about any product.** No product is
installed, configured, loaded or measured in that loop; there is no proxy between the client and the
target. A statement about a product exists only in a campaign run in which the product is loaded on
the machine under test and the campaign's own `proxy-truth.json` shows flows reaching the proxy.

The harness never speaks SOCKS5 and never impersonates a proxy. The client dials the target address
directly, and a transparent proxy rewrites the path beneath it.

## Layout

| Path | Holds |
|---|---|
| `Program.cs` | the two verbs (`target`, `client`), usage and process exit codes |
| `Cli/ExitCodes.cs` | `0` success, `1` runtime error, `2` usage error |
| `Cli/CommandLine.cs` | the argument walk both verbs share: the `--name=value` split and the two walk-level refusals (`unknown argument`, `missing value`) |
| `Cli/ClientOptions.cs` | the client's arguments, parsed and validated before a plan is read |
| `Cli/TargetOptions.cs` | the target's arguments, parsed and validated before anything is bound |
| `Client/ClientRunner.cs` | the client run: the arm loop, the arm's failure boundary, and the output-path checks |
| `Client/ArmRecordWriter.cs` | the `result`, `armSummary` and `error` records of one arm |
| `Client/RunFileWriter.cs` | `run.json`: the run's environment and one summary per arm |
| `Client/PlanFile.cs` | the plan schema and its loader |
| `Client/ArmSpec.cs` | one arm's declared parameters |
| `Client/ArmContext.cs` | the shared clock, the pacer, and the per-run latency histograms |
| `Client/Arms/` | one file per arm kind, plus `ArmDispatch.cs`; the largest kinds also have their plan, metrics writer and phase loops beside them (`Mix*`, `Reliability*`, `Persistent*`) |
| `Client/LogHistogram.cs` | the client's latency histogram: logarithmic buckets and the percentile snapshot |
| `Client/UdpReliability.cs` | the UDP sequence bookkeeping and the arrived/late/never classification |
| `Client/ResourceSampler.cs` | the 1 Hz process sampler: the tick loop, its target and its barrier |
| `Client/ProcessCounterSource.cs` | the OS process-counter reads, and the sums one process name yields |
| `Client/ProcessSample.cs` | one sampled process's identity, and the summed totals per process name |
| `Client/ResourceSampleWriter.cs` | the `sample` and `samplerError` record shapes |
| `Client/FrameBuffer.cs` | the frame buffer an arm builds its requests in, and the socket helpers the lanes share |
| `../WinForward.E2E.Contracts/` | what both verbs share: the record key constants, the typed metrics and parameters value objects, the JSONL sink, its failure policy, and the number formats the records publish |
| `Target/` | the target: TCP echo/command server, UDP echo server, DNS responder, the ledger summaries, the target log and the runner |
| `Target/TcpConnectionProtocol.cs` | one TCP connection's command/echo state machine and its verdicts |
| `Target/TcpAcceptLoop.cs` | the accept loop both TCP listeners share, its connection table and the drain |
| `Target/SocketIo.cs` | the shared stream send/receive helpers |
| `Target/Sockets.cs` | where the listeners are bound, and the reuse-port policy that makes a leftover instance loud |
| `Target/SourceCensus.cs` | the UDP echo server's per-receiver source-endpoint census |
| `Wire/` | the protocol both verbs share: frame codec, stream reader, CRC32C, command payload, half-close trailer, DNS wire subset, payload filler |
| `scripts/plans/` | the committed plans: `full-plan`, `udp-plan`, `dns-plan`, `dual-plan`, `base-plan`, `selftest-plan` |
| `scripts/plans-short/` | the same arm shapes at short durations and lower rates, for validating a change |
| `scripts/configs/` | the product configurations each row is measured with |
| `scripts/orchestrator.ps1` | the campaign driver, run on the machine under test |
| `scripts/publish.sh` | build and publish the artifacts |
| `scripts/selftest.sh` | run the harness against itself on one host |

Machine-specific files, listed in `.gitignore` and **absent from a fresh checkout**:

| Path | Why it is not in the repository |
|---|---|
| `AGENTS.local.md` | the local runbook: host addresses, credentials, ports and installed-product state for one particular pair of machines |
| `scripts/wf.sh` | drives the remote Windows session on one particular host |
| `scripts/deploy-campaign.sh` | copies the artifacts and configurations to one particular machine |
| `scripts/start-targets.sh` | starts the targets with one particular set of addresses and ports |
| `scripts/publish-campaign.sh` | collects a campaign from one particular machine and runs the analysis on it |

Nothing in this document depends on those files. `bin/` and `obj/` are build output and are
gitignored as well.

## Build and run

### Publish

```bash
scripts/publish.sh
```

The script builds Release (`dotnet build -c Release`), publishes three framework-dependent trees
under `$WF_PUB` (default `${TMPDIR:-/tmp}/wf-bench/pub`), prints their contents and finishes by
printing both Windows client `.exe` paths:

| Tree | Command | Artifact |
|---|---|---|
| `linux/` | `dotnet publish -c Release -r linux-x64 --self-contained false` | `WinForward.E2E` |
| `win/` | `dotnet publish -c Release -r win-x64 --self-contained false` | `WinForward.E2E.exe` |
| `win-direct/` | the same as `win/` with `-p:AssemblyName=WinForward.E2E.Direct` | `WinForward.E2E.Direct.exe` |

Four artifacts come out of that one source tree. The Linux binary is two of them, because it is
published once and runs as both ends of the self-test.

| Artifact | Runs as |
|---|---|
| Linux `WinForward.E2E` | the target |
| the same Linux file | the client of the self-test |
| Windows `WinForward.E2E.exe` | the proxied-lane client |
| Windows `WinForward.E2E.Direct.exe` | the direct-lane client |

The second Windows image is not a duplicate by accident. The products tell applications apart by
image name, so sending one application around the proxy and another one through it requires two
different images: a rule that routes the proxied client has to be able to exempt the direct one.
`-p:AssemblyName=` renames the assembly, the apphost, the `.dll`, the `.deps.json` and the
`.runtimeconfig.json`, and the tree is published into its own directory so the two lanes never share
an image identity. The direct lane's measurement depends on that: `proxy-truth.json`'s `directLeak`
counts flows to the direct-lane target, and a leak is only visible because the direct client is a
process image the product was configured to leave alone.

### Run the self-test

```bash
scripts/publish.sh
scripts/selftest.sh scripts/plans/selftest-plan.json
```

`selftest.sh` publishes the Linux build if `$WF_PUB/linux/WinForward.E2E` is missing, starts a target
on `127.0.0.1` (`--tcp-port 31010 --udp-port 31010 --dns-port 5301 --dns-alt-port 5302`, ledger
`/tmp/wf-bench/selftest/ledger.jsonl`), waits one second, and runs the client against the same
address and ports with the plan given as the first argument and `--out /tmp/wf-bench/selftest/out`.
The client's complete output is kept in `/tmp/wf-bench/selftest/client.out`, with its last 20 lines
repeated on the console. On exit the script prints, arm by arm, every metric, every latency histogram
that recorded a sample and every note the arm wrote. The ports are deliberately unusual so the
self-test can run while a campaign target is up.

The plan path is the only argument. With no argument the script prints usage and exits `2` before it
starts anything, so a missing argument can neither be read as a green run nor leave a target behind;
pass a path under `scripts/plans/` or `scripts/plans-short/`. The work directory is fixed at
`/tmp/wf-bench/selftest`.

The client also runs against any plan directly, which is how the campaign drives it:

```bash
WinForward.E2E client --target <ip> --plan <plan.json> --out <dir> [--label <name>]
                      [--tcp-port 40010] [--udp-port 40010] [--dns-port 53]
                      [--sampler-process <name>]...
                      [--inject-corrupt-every <n>] [--inject-rewrite-every <n>]
```

| Client flag | Meaning |
|---|---|
| `--target <ip>` | target host, required, IP literal only |
| `--plan <path>` | plan to run; omitted, the client runs a built-in eight-arm default plan; an empty value (`--plan=` or `--plan ""`) is a usage error rather than a silent fallback to the built-in plan |
| `--out <dir>` | output directory, required; `<arm>.jsonl`, `run.json`. Its path is checked before the run: no component over 255 characters and no whole path over 250 |
| `--label <name>` | free-form label copied into every result and into `run.json` |
| `--tcp-port`, `--udp-port`, `--dns-port` | the target ports; defaults 30010, 30010 and 53 |
| `--sampler-process <name>` | process name to sample at 1 Hz, repeatable, no `.exe` suffix; an empty name is a usage error |
| `--inject-corrupt-every <n>` | flip one payload byte of every n-th UDP datagram without recomputing its CRC: the target drops the frame as undecodable, so the client can only report it as path loss |
| `--inject-rewrite-every <n>` | flip one payload byte and recompute the CRC, so the target echoes it and the client books it corrupt |

Both injection flags are read by the loss arm, so they apply to `LOSS` and to `BASE`'s loss phase.

The value of a string option (`--target`, `--plan`, `--out`, `--label`, `--sampler-process`) must not
start with `-`: an option consumes the next argument as its value, so `--label --out x` would have
taken `--out` as the label and left the run without an output directory. It is a usage error
naming the option and the value. Values of the numeric options cannot start with `-` either, since
they fail their own parsers.

Exit codes: `0` when every requested arm completed, `1` when an arm failed, `2` on a usage error. The
target exits `0` on a clean shutdown (Ctrl+C or SIGTERM), `1` on a socket error, `2` on a usage error.
A plan the loader rejects is a usage error too, and the message names the arm and the value.

### Run the target

A campaign needs a target reachable from the machine under test. The target is the same binary:

```bash
WinForward.E2E target [--bind <ip>] [--tcp-port <n>] [--udp-port <n>] [--dns-port <n>]
                      [--dns-alt-port <n>] [--label <name>] [--ledger <path>]
```

| Target flag | Default | Meaning |
|---|---|---|
| `--bind <ip>` | `0.0.0.0` | address to bind |
| `--tcp-port <n>` | 30010 | TCP echo and command listener |
| `--udp-port <n>` | 30010 | UDP echo listener |
| `--dns-port <n>` | 30053 | DNS responder, UDP and TCP |
| `--dns-alt-port <n>` | none | a second DNS responder, UDP and TCP, on a port no product special-cases; it must differ from the other three ports |
| `--label <name>` | empty | run identity copied into every ledger record |
| `--ledger <path>` | `target-ledger.jsonl` | JSONL ledger output |

It runs until Ctrl+C or SIGTERM and writes its summary records at shutdown. The campaign runs two
target instances — one per lane — against two different ports so that a flow to the direct lane is
attributable; the launcher for them is machine-specific (`scripts/start-targets.sh`, local), and the
ports it uses are the orchestrator's defaults.

### Plan schema

A plan is `{"arms": [...]}`. Every arm needs a non-empty `name` and a `kind`, names must be unique,
the array must not be empty, and `seconds` must be greater than zero. An arm may declare only the
keys its kind reads: an unknown key is a hard load error naming the arm and the key, as are an
unknown `kind`, an unknown `protocol`, a bad `seconds`, a value outside a key's domain and a value of
the wrong JSON type (`"protocol": 5` is refused rather than quietly read as `tcp`). Nothing in a plan
is silently ignored or silently clamped.

Arm names also have to survive becoming file names. Two names whose output files would be identical
(`A/B` and `A_B`) are refused rather than letting the second arm truncate the first arm's records,
and a name whose sanitized form is longer than 128 characters is refused before any file is created.

Kinds: `latency`, `loss`, `reliability`, `throughput`, `dns`, `mix`, `idle`, `persistent`, `base`.

| Key | Read by | Defaults, in the order listed | Accepted values |
|---|---|---|---|
| `seconds` | every arm | 60 | greater than 0 |
| `ratePerSecond` | latency, loss, dns, and both BASE phases | 20 / 500 / 200 / 20 and 500 | 0 or more |
| `payloadBytes` | latency, loss, persistent, both BASE phases | 120 / 200 / 120 / 120 and 200 | 0 or more |
| `protocol` | latency, and BASE's latency phase (BASE's loss phase always runs udp) | `"tcp"` | `tcp`, `udp` or `tcp+udp` |
| `window` | in-flight requests per lane: latency, loss, both BASE phases | 64 / 4096 / 64 and 4096 | 0 or more |
| `lanes` | latency, both BASE phases | 1 | 0 or more |
| `lossWindowMs` | loss, mix, BASE's loss phase — the UDP loss threshold W in milliseconds | 200 | 0 or more |
| `modeMix` | reliability, as `<mode>=<weight>` pairs | `clean=25,resetAfterN=25,partialFin=25,halfClose=25` | at least one pair with a positive total weight |
| `connectionsPerSecond` | reliability | 20 | 0 or more |
| `expectedBytes` | reliability; persistent, which announces it to the target | 8192 / 0 | 0 or more |
| `streams` | throughput | 4 | 0 or more |
| `targetBytesPerSecond` | throughput | 25 000 000 | 0 or more |
| `tcpPercent`, `cnameEvery`, `dnsPort` | dns: the TCP share of queries, the CNAME substitution interval, and an override of the run's DNS port | 0 / 0 / the run's `--dns-port` | 0..100 / 0 or more / 0..65535 |
| `desktops` | mix | 4 | 0 or more |
| `intervalMs`, `idleSeconds` | persistent: the pacing interval and the idle window | 1000 / 20 | 0 or more |

Every numeric key is an integer, and `0` means "not declared": the arm then uses the default in the
table rather than the zero. A fractional value on an integer key (`"window": 100.5`) is a load error
instead of a silent fall back to the default, and so is any value outside the accepted range. The
one exception to "0 = not declared" is `dnsPort`, where 0 is the same "use the run's `--dns-port`"
the table documents, not port 0.

`window` is a count, `lossWindowMs` is a duration; the two are different keys because the latency
arm's window is a number of requests and the loss arm's is a millisecond threshold.

The plan's SHA-256 (first 16 hex digits) is recorded as `planHash` in `run.json`, which also records
where the plan came from: `planPath` is the absolute path and `planSource` is `"file"`, or `planPath`
is `null` and `planSource` is `"builtin"` when the run used the built-in default plan.

A `loss`, `mix` or `base` arm is refused when the schedule it declares cannot fit the tracker's
bounded sequence space: `ceil(ratePerSecond × seconds)` above 2¹⁸ − 1 (262143), with `base` using the
rate its entry declares and falling back to its loss phase's own 500/s when it declares none, and
`mix` checked as `ceil(30 × seconds)` because its UDP rate is a per-desktop constant rather than a
key. The other kinds index no array by sequence, so they have no such limit.

### The orchestrator

`scripts/orchestrator.ps1` runs the campaign on the machine under test. Invoke it with PowerShell 7:

```powershell
pwsh -NoProfile -ExecutionPolicy Bypass -File scripts/orchestrator.ps1 `
     -Passes 4 -OutRoot C:\wfbench\results
```

| Parameter | Default | Meaning |
|---|---|---|
| `-Passes` | 4 | passes to run |
| `-OutRoot` | `C:\wfbench\results` | campaign output root |
| `-PlanRoot` | `C:\wfbench\e2e` | directory holding the deployed plan files |
| `-TargetAddress` | `192.168.100.4` | the Linux host running the targets |
| `-TcpPort`, `-UdpPort`, `-DnsPort` | 40010, 40010, 53 | proxied-lane target ports |
| `-DnsPortAlt` | 40053 | the alternate DNS responder port |
| `-TcpPortDirect`, `-DnsPortDirect` | 40011, 40054 | direct-lane target ports |
| `-ClientExe`, `-ClientExeDirect` | `C:\wfbench\e2e\WinForward.E2E.exe`, `C:\wfbench\e2e-direct\WinForward.E2E.Direct.exe` | the two client images |
| `-SingBoxExe`, `-SingBoxConfig`, `-SingBoxLog` | `C:\wfbench\singbox\*` | the proxy the traffic is relayed through, and the log its flow census is read from |
| `-HeartbeatPath` | `C:\wfbench\heartbeat.txt` | touched around each arm |
| `-Seed` | 20261006 | seeds the per-pass product order |

One product is loaded at a time, measured with that row's own plan, then stopped. The campaign root
holds `environment.json`; each `pass<N>/` holds `order.txt`, a `control-pre/` and a `control-post/`
block, and one directory per product row. The orchestrator writes each row's `proxy-truth.json`,
copies the product's effective configuration into the row and writes the `environment.json` block;
deploying the artifacts, the plans and the product configurations to that machine is machine-specific
work (`scripts/deploy-campaign.sh`, local).

## The record

One client run writes one directory. Every JSONL file is one JSON object per line, UTF-8, no BOM.

| File | Record `type`s | Carries |
|---|---|---|
| `<arm>.jsonl` | `result` | the arm's declared `parameters`, its `metrics`, its `latency` histograms, its `gates`, its `notes`, and the arm's tick window |
| | `armSummary` | the same `parameters` and `gates`, the `resultFile` name and the arm's tick window |
| | `sample` | one 1 Hz resource sample: `process`, `self`, `matched`, the summed counters and the per-process block |
| | `samplerError` | one sampling failure: `process`, `error` (the exception type), `message` |
| | `error` | an arm that threw: `arm`, `kind`, `label`, `error` (the exception type), `message`, `detail` (the innermost exception type, `GetBaseException()`), and the tick window |
| | `attempt` | the reliability arm's per-attempt evidence: `connectionId`, `mode`, `status`, `observed`, `expected`, `truncated`, `echoedBytes`, `trailerBytes`, `eof`, `reset`, `protocolError`, `otherError`, `connectTicks`, `transferTicks` |
| `run.json` | one object | the run's identity and environment, its plan hash, path and source, its per-arm tick windows, and whether any arm failed |
| target ledger | `tcp` | one finished TCP connection: `connectionId`, `mode`, `expectedBytes`, `bytesEchoed`, `verdict`, `peer`, tick window |
| | `udpSummary` | once a second while the target runs and once at shutdown: running totals (`received`, `undecodable`, `bytes`) and the source endpoints seen in that interval |
| | `dnsSummary` | once per listener at shutdown: UDP and TCP query, answer, empty-answer, malformed, send-error and connection totals |
| | `tcpSummary` | once at shutdown: connections, bytes echoed, protocol errors, verdicts |
| | `targetSummary` | once at shutdown: the tick window, the ledger write-error count, and the `tcp` / `udp` / `dns` / `dnsAlt` totals |
| `proxy-truth.json` | one object | `tcp`, `udp`, `utcp` and `total` — the proxied flows observed in the proxy's log — plus `directLeak` in the dual phase's file |
| `dual/proxied/`, `dual/direct/` | as a row | the two lanes of the dual phase: one run around the proxy and one configured to go direct, the same workload shape against two targets. Each lane is a full run directory, and `dual/proxy-truth.json` carries the row's flow census plus `directLeak` |
| `order.txt` | one line | the row ids in the order this pass ran them |
| `environment.json` | one object | the orchestrator's environment block: start time, OS and build, logical CPUs, visible RAM, pass count, seed, firewall profiles, proxy version and target ports |

Every ledger record also carries `utc` (absolute wall clock) and `label`; every client record carries
`type`, and every record written into an arm file except `attempt` carries `arm`.

An arm the operator interrupts is booked as a failure like any other: it gets the same `error` record,
`run.json` is written with `failed: true` and the client exits `1`. Its `error` is
`OperationCanceledException` and its `message` is the literal `cancelled`, so an interrupted arm is
distinguishable from a broken one without reading the text.

The envelope of a `result` is what the analysis reads:

| Field | Meaning |
|---|---|
| `type`, `arm`, `kind`, `label` | record kind, arm name and kind as declared in the plan, and the client's `--label` |
| `parameters` | the effective run shape after defaults — the plan's numbers, not an echo of its text |
| `metrics` | the measurements; the spelling of this map differs by arm (see below) |
| `latency` | one histogram per latency class that recorded a sample: `count`, `minUs`, `maxUs`, `meanUs`, `p50Us`, `p90Us`, `p99Us`, `p999Us` |
| `gates` | the validity counters — numbers that describe the measurement rather than the product |
| `notes` | prose written by the arm: what it measured, which caveat applies, what the numbers mean |
| `startedTicks`, `endedTicks` | `Stopwatch.GetTimestamp()` on the writing machine — a monotonic tick count, not a wall clock, and not comparable between hosts |

`run.json`'s `startedUtc` and `startedTicks` are written next to each other: that pair is the only
bridge between the client's stopwatch and absolute time, and it is what lets the analysis place an
arm's window beside the target ledger's timestamps.

### The `null` convention

`null` means "there is no value to report here". It is never a zero.

| Case | What is written |
|---|---|
| a rate or ratio whose denominator was zero | `null` (`JsonRate.Rate`), for example `meanConnectMs` with no completed connect, `meanTransferMs` with no completed send, `classes.page.bytesPerPage` with no page |
| a rate whose elapsed time was zero | `null` (`JsonPerSecond.PerSecond`), the same missing measurement: an unmeasured rate is never reported as `0` |
| a per-mode extreme over an empty set | `null` |
| a process whose counters could not be read | `cpuSeconds` and `privateBytes` are `null`, alongside `countersRead: false`; the whole sample also carries `readError: true` |
| the handle count on a platform that has none | `handles` is `null` (on Linux, for example) |

A `null` rate is not a perfect score. The analysis separates a missing key from a JSON `null`,
prints the number of null passes beside the pass count, and renders a cell whose every pass is null
as empty rather than as `0`.

## The arms

Ten arms; `DNSALT` in the plans is a second instance of the `dns` kind with `dnsPort` set to a port
no product special-cases, and the analysis reads it as its own arm.

| Arm (kind) | What it measures | Parameters that shape it | Latency class it feeds |
|---|---|---|---|
| `IDLE` (idle) | nothing is sent; the 1 Hz resource samples attached to the arm are the measurement | `seconds` | — |
| `LAT` (latency) | open-loop request/response latency at a probe rate, plus a 1 Hz TCP connect probe | `seconds`, `ratePerSecond` (20), `payloadBytes` (120), `protocol` (tcp), `window` (in-flight requests per lane, 64), `lanes` (1) | `tcp-connect` (probe), `tcp-rtt`, `udp-rtt` |
| `LATLOAD` (latency) | the same arm at a load rate — the same code, `ratePerSecond` 500 in the committed plans | as `LAT` | as `LAT` |
| `DNS` (dns) | DNS query/answer behaviour over UDP and TCP, on the run's DNS port or a plan-declared one | `seconds`, `ratePerSecond` (200), `tcpPercent`, `cnameEvery`, `dnsPort` | `dns-rtt` |
| `LOSS` (loss) | UDP path loss against a declared millisecond threshold, with the full arrived/late/never classification | `seconds`, `ratePerSecond` (500), `payloadBytes` (200), `window` (in-flight datagrams, 4096), `lossWindowMs` (200) | — |
| `REL` (reliability) | TCP fidelity under a weighted fault mix: one connection per attempt, a command, a fixed transfer, and how the client saw it end | `seconds`, `connectionsPerSecond` (20), `expectedBytes` (8192), `modeMix` | `tcp-connect` (every attempt that connected) |
| `THRU` (throughput) | bulk goodput over long-lived connections, paced to an aggregate byte rate and bounded by a byte budget | `seconds`, `streams` (4), `targetBytesPerSecond` (25 000 000); frame payload fixed at 32 KiB | — |
| `MIX` (mix) | four flow classes per desktop, concurrently: page loads (13 connections, 73 requests), bulk transfer, DNS, and a low-rate UDP flow | `seconds`, `desktops` (4), `lossWindowMs` (200) | `tcp-connect`, `tcp-rtt`, `udp-rtt`, `dns-rtt` |
| `PERSIST` (persistent) | one long-lived TCP connection across an idle window: whether it survived, how often it had to reconnect, and the RTT of the rounds it carried | `seconds`, `intervalMs` (1000), `idleSeconds` (20), `payloadBytes` (120), `expectedBytes` (announced to the target) | `tcp-rtt` |
| `BASE` (base) | the floor: the latency arm and then the loss arm back to back in one record, with no product loaded | `seconds` per phase, so `parameters.seconds` is twice the declared value; every load key the plan declares overrides both phases, and the phase defaults differ (latency 20 req/s × 120 B, loss 500 datagrams/s × 200 B) | whatever the two phases produce: `tcp-connect`, `tcp-rtt`, `udp-rtt` |

## Which keys are contract

The analysis reads a subset of the record verbatim and recomputes nothing: percentiles come straight
from the harness's histograms, published rates are read as they were written, and the only arithmetic
the analysis does on them is the ratios it states (for example `REL metrics.unexpectedEof /
metrics.connectAttempts`). Those keys are the contract. **Everything else in a record — the extra
counters, the `notes`, the `error` records, the self samples and the absent-process fields — is a
diagnostic kept so that a bad run can be debugged.**

A rename inside the contract does not fail loudly: the cell becomes `n/a`, or the value silently
drops out of an aggregate, and the campaign report still renders. The analysis resolves most keys by
arm kind, so the contract is (arm kind → key paths), not one global list.

| Read from | Paths (inside the arm's `result` record) |
|---|---|
| every arm, by `kind` | `kind`; `type` to select the record; `parameters/…` for the dual-phase shape comparison |
| `latency` (LAT, LATLOAD) | `parameters/protocol`; `metrics/tcp.connectAttempts`, `metrics/tcp.sentOk`, `metrics/tcp.laneSupplied[]`, `metrics/udp.sentOk`, `metrics/udp.laneStarted`, `metrics/udp.lossRate`, `metrics/udp.foreignConnection`; `gates/clientSendLoss` (with `metrics/clientSendLoss` as its fallback), `gates/windowOverflow`, `gates/backlogDrops`, `gates/scheduleTruncated`, `gates/laneShortfall`, `gates/inFlightCeilingMs`, `gates/lanesPlanned`, `gates/lanesStarted` |
| `loss` (LOSS) | `metrics/sent`, `metrics/supplied`, `metrics/arrived`, `metrics/late`, `metrics/never`, `metrics/corrupt`, `metrics/corruptDatagrams`, `metrics/duplicate`, `metrics/reordered`, `metrics/foreignConnection`, `metrics/abandonedAtTeardown`, `metrics/clientSendLoss`, `metrics/lossRate`, `metrics/strictLossRate`, `metrics/corruptRate`, `metrics/reorderRate`, `metrics/window`; `gates/clientSendLoss`, `gates/windowMs` |
| `reliability` (REL) | `metrics/connectAttempts`, `metrics/scheduledAttempts`, `metrics/outcomes/<name>` and `metrics/expected/<name>` for `clean`, `reset`, `unexpectedEof`, `timeout`, `connectFail`, `halfCloseViolation`, `otherError`; `metrics/unexpectedEof`, `metrics/fidelityMismatch`, `metrics/meanConnectMs`, `metrics/meanTransferMs` |
| `throughput` (THRU) | `metrics/goodputMbps`, `metrics/frames`; `parameters/streams` |
| `dns` (DNS, DNSALT) | `metrics/sent`, `metrics/answered`, `metrics/servfail`, `metrics/timeout`, `metrics/other`, `metrics/answerRate`, `metrics/udpSent`, `metrics/tcpSent`; `parameters/dnsPort` |
| `mix` (MIX) | `metrics/classes/page/connections`, `metrics/classes/page/messages`, `metrics/classes/bulk/frames`, `metrics/classes/dns/sent`, `metrics/classes/udp/lossRate`, `metrics/classes/udp/sent`, `metrics/classes/udp/window`, `metrics/classes/udp/foreignConnection`; `metrics/udpSent`, `metrics/udpLossRate`, `metrics/pageConnections`; `metrics/desktops[]` with `desktop`, `udpSent`, `udpForeignConnection`, `pageConnections`, `bulkFrames`, `dnsSent`; `parameters/desktops`, `parameters/udpPacketsPerSecondPerDesktop`; `gates/idleLanes` |
| `persistent` (PERSIST) | `metrics/requests`, `metrics/responses`, `metrics/responseRate`, `metrics/reconnects`, `metrics/survivedIdle`, `metrics/connectAttempts`, `metrics/idleSecondsScheduled`, `metrics/idleSecondsObserved` |
| `base` (BASE) | `metrics/latency/udp.sentOk`, `metrics/latency/udp.lossRate`, `metrics/latency/tcp.connectAttempts`, `metrics/loss/lossRate`, `metrics/loss/sent`, `metrics/loss/window`, `metrics/loss/foreignConnection` |
| every arm with a histogram | `latency/tcp-connect`, `latency/tcp-rtt`, `latency/udp-rtt`, `latency/dns-rtt`, each with `count` and `minUs`, `meanUs`, `p50Us`, `p90Us`, `p99Us`, `p999Us`, `maxUs` |
| the UDP accuracy table | reads the fifteen fields it tabulates — `sent`, `arrived`, `late`, `never`, `corrupt`, `corruptDatagrams`, `duplicate`, `reordered`, `foreignConnection`, `abandonedAtTeardown`, `lossRate`, `strictLossRate`, `corruptRate`, `reorderRate`, `clientSendLoss` — from `metrics/…` for LOSS and from `metrics/classes/udp/…` for MIX, plus `sent` and `supplied` as the denominators behind its rule-of-three bounds. MIX does not publish `strictLossRate`, `corruptRate`, `reorderRate` or `supplied`, and the analysis prints an explicit `n/a (MIX does not publish …)` for those cells rather than a zero |
| `run.json` | `label`, `planHash`, `clientVersion`, `osDescription`, `frameworkDescription`, `logicalProcessors`, `samplerProcesses`, `startedUtc`, `startedTicks`, `endedTicks`, `wallSeconds`, `arms[].{name,file,kind,startedTicks,endedTicks,failed}`, `target.{address,tcpPort,udpPort,dnsPort}`, `failed` |
| samples | `self`, `process`, `matched`, `absent`, `ticks`, `readError`, `readErrors`, `cpuSeconds`, `privateBytes`, `workingSetBytes`, `peakWorkingSetBytes`, `processes[].{pid,startUtc,countersRead,cpuSeconds,privateBytes}` |
| the ledger | `type`, `utc`, `label`; `tcp` records' `verdict`; `dnsSummary`'s `port`, `tcpQueries`, `udpQueries`; `udpSummary`'s `received`, `sources`, `sourceOverflow`; `targetSummary`'s `ledgerWriteErrors` |

Two paths in that read-set are satisfied by no record, and both are read-only misses rather than
invented numbers: `parameters/window` (the latency arm publishes the plan's `window` as
`parameters/inFlightWindow`, because a bare `window` would be ambiguous between a request count and
the loss arm's millisecond threshold) and `parameters/loss.lossWindowMs` (BASE nests the loss phase's
parameter map, so the readable path is `parameters/loss/lossWindowMs`).

The `metrics` map uses four different conventions, and all four are load-bearing:

| Convention | Examples | Published by |
|---|---|---|
| flat per-arm names | `metrics.sent`, `metrics.lossRate`, `metrics.connectAttempts`, `metrics.goodputMbps`, `metrics.answerRate`, `metrics.reconnects` | loss, reliability, throughput, dns, persistent |
| flat names that contain a dot | `metrics.tcp.sentOk`, `metrics.udp.sentOk`, `metrics.udp.lossRate`, `metrics.udp.foreignConnection`, `metrics.tcp.laneSupplied` | latency (`LAT`, `LATLOAD`) |
| one level of nesting under `classes.*` | `metrics.classes.udp.lossRate`, `metrics.classes.udp.window`, `metrics.classes.page.connections`, `metrics.classes.bulk.frames`, `metrics.classes.dns.sent` | mix |
| a whole sub-arm nested under its phase name | `metrics.latency.udp.sentOk`, `metrics.latency.udp.lossRate`, `metrics.loss.sent`, `metrics.loss.lossRate` | base |

The dot inside the latency arm's keys is the reason the analysis walks paths on `/` rather than on
`.`: `metrics/tcp.sentOk` is one map key, not a nested `tcp` object. The same statistic therefore has
up to four spellings — `metrics.udp.sentOk` (latency), `metrics.udpSent` (dns and the mix arm's
roll-up), `metrics.classes.udp.sent` (mix), `metrics.latency.udp.sentOk` (base) — and each spelling
is what some table reads. Renaming one is a coordinated change with the analysis, never a local
clean-up.

## Gates and validity

Gates are counters about the measurement, not results. The analysis reads them and fails or warns on
them; the harness writes them next to the metrics so a bad record can be recognised without the
analysis.

| Gate | Meaning |
|---|---|
| `gates/clientSendLoss` | samples the client destroyed instead of offering them: for the latency arms, supplied minus sent, which is the deferred queue's residue plus the backlog drops plus the refused sends; for `LOSS` and `MIX`, the datagrams still in flight when observation stopped plus refused sends and window overflows; for `PERSIST`, the connect and send failures; `BASE` sums the two phase gates. The four arms that cannot destroy a sample write a literal `0`: `IDLE` offers no traffic, `THRU` refuses a frame against its byte budget before offering it, `REL` back-pressures its pacer on a full attempt window instead of dropping an attempt, and `DNS` publishes a pacing slot skipped by a full in-flight window as `metrics.unsent`, which never enters `sent` |
| `gates/windowMs` | the declared UDP loss threshold W in milliseconds. It is `0` for every arm that has no millisecond loss window — that is every arm except `LOSS`, `MIX` and `BASE`, which publish the declared W they classified against |
| `gates/windowOverflow` | the in-flight window was full. In the latency arms the request is deferred, not dropped: it goes out when a slot frees, still stamped with its original intended instant, so a deferral there is a disclosure rather than a loss. In `LOSS` the datagram is dropped and the overflow is folded into `clientSendLoss`; in `MIX` the UDP class has no in-flight window and the counter is structurally zero |
| `gates/backlogDrops` | requests discarded because the deferred queue was full (bounded per lane by `max(4 × window, 10 s of the offered rate)`, capped at 2²⁰). These are censored samples and are folded into `clientSendLoss` |
| `gates/sendFailures` | individual sends that threw; the loop continues, so a transient error costs that sample, which is also folded into `clientSendLoss` |
| `gates/lanesPlanned`, `gates/lanesStarted`, `gates/laneShortfall` | lane bookkeeping for the latency arm: how many lanes the effective plan asked for, how many ran and supplied traffic, and how many were idle |
| `gates/scheduleTruncated` | a lane never connected, or an offer loop ended before its deadline, so part of the offered schedule was never offered at all |
| `gates/inFlightCeilingMs` | the tightest direct-measurable-latency ceiling, `window × lanes / achieved rate`; slower requests are measured through the deferred queue, so a reached ceiling is a disclosure (`windowOverflow > 0`), not a failure. `metrics/tcp.windowCeilingMs` and `metrics/udp.windowCeilingMs` are the per-protocol ceilings |
| `gates/idleLanes` | mix only: the number of lane witnesses that stayed at zero |

The analysis asserts three accounting identities per record:

| Arm | Identity |
|---|---|
| `LOSS` (`metrics/*`), `MIX` (`metrics.classes.udp.*`), `BASE` (`metrics.loss.*`) | `arrived + late + never + abandonedAtTeardown + corruptDatagrams == sent` |
| `DNS`, `DNSALT` | `answered + servfail + timeout + other == sent` |
| `REL` | `connectAttempts == scheduledAttempts`, and the seven `metrics.outcomes` counts sum to `connectAttempts` |

The first is exact by construction: every sent datagram is booked into exactly one bucket. The
`corrupt` counter is deliberately outside the sum — it counts corrupt arrivals, including a replay of
a frame already booked corrupt, whereas `corruptDatagrams` counts the sent datagrams that were
booked corrupt and is the term inside the identity.

**Lane witnesses.** An arm that runs several lanes or desktops publishes one witness per lane, and a
zero witness means that lane never ran rather than that it sent nothing.

| Arm | Witnesses |
|---|---|
| `LAT`, `LATLOAD` | every `metrics.tcp.laneSupplied[i] > 0`, `metrics.udp.laneStarted > 0` when the protocol includes udp, and `gates.lanesStarted == gates.lanesPlanned` |
| `MIX` | every desktop entry's `udpSent`, `pageConnections`, `bulkFrames` and `dnsSent` > 0, and `gates.idleLanes == 0` |

The witness is set by the lane body itself, not inferred from a total, because a lane that never
runs removes its whole share of the offered schedule while the record still reads as a complete run
of a smaller shape — an aggregate that looks entirely plausible. The lane states exist before any
lane starts, so an absent lane still occupies its slot in the per-lane arrays instead of shrinking
them, and each lane is started on a thread of its own so that a lane whose first await completes
synchronously cannot run to completion inside the loop that starts the others.

`foreignConnection` belongs to the same class of check: it counts replies carrying a connection id
the socket never used, and a non-zero value means the product delivered a datagram into the wrong
flow — a correctness failure, not a slow path. The connection id is checked before the payload
filler, because a datagram from another flow is internally consistent: its checksum and its filler
both validate against its own id, so a filler-first rule would book the product mixing two flows as
an ordinary corrupt datagram.

## The target's protocol

Everything on the wire is defined in `Wire/` and read by both verbs.

| Offset | Size | Field | Encoding |
|---|---|---|---|
| 0 | 4 | magic `0x57464531` | big-endian `u32` |
| 4 | 4 | connection id | big-endian `u32` |
| 8 | 8 | sequence | big-endian `u64` |
| 16 | 8 | client send ticks | big-endian `u64` |
| 24 | 4 | payload length | big-endian `u32`, at most 4 MiB |
| 28 | payload length | payload | filler bytes derived from the connection id and sequence |
| 28 + payload length | 4 | CRC32C of everything before it | big-endian `u32` |

Header 28 bytes, trailer 4 bytes, frame overhead 32. CRC32C is the Castagnoli polynomial in its
reflected form (`0x82F63B78`), initial value `0xFFFFFFFF`, final XOR `0xFFFFFFFF`, hardware-accelerated
where SSE4.2 is available. A frame is rejected on a bad magic, a payload length above the ceiling or a
bad checksum; the UDP target never echoes a frame it could not decode and counts it as `undecodable`
in its ledger.

Sequence `0` is reserved for the command. Its 5-byte payload is a mode byte followed by a big-endian
`u32` byte count:

| Mode | Payload | What the target does | Verdict |
|---|---|---|---|
| `clean` (0) | mode + expected bytes | echoes every frame; on the client's FIN it shuts down its send side | `clean` |
| `resetAfterN` (1) | mode + N | echoes until N payload bytes have been echoed, then sets a zero linger and aborts: the peer sees a reset | `reset` |
| `partialFin` (2) | mode + N | echoes until N payload bytes have been echoed, then shuts down its send side without waiting for the client's FIN | `partialFin` |
| `halfClose` (3) | mode + expected bytes | on the client's FIN it writes the half-close trailer and then shuts down its send side | `halfClose` |
| `stall` (4) | mode + expected bytes | delays the first data frame after the command by 2 s, then echoes | `stall` |

A connection that closes before a valid command, or before the byte count in a fault mode, is
recorded as `clientClosedEarly`; a first frame that is not a parsable command frame is recorded as
`protocolError`. Each connection produces one `tcp` ledger record with its id, mode, byte counts and
verdict.

**Half-close trailer.** When the client's FIN arrives in `halfClose` mode, the target writes three
frames of 256 payload bytes each — 768 payload bytes in total, sequences from
`0xFFFFFFFFFFFFFF00` upward — and then shuts down its send side. The client calls a `halfClose`
connection clean only when it has read the full echo and at least `TrailerProtocol.TotalBytes` of
trailer, and it books every payload byte read after the expected echo as `trailerBytes`. Both ends
read the shape from `Wire/TrailerProtocol.cs`: frame count, payload size, the derived total and the
sequence base are one shared constant, because a trailer the target writes and the client does not
expect is indistinguishable from a product that failed half-close.

**Stall delay.** `stall` mode delays the first data frame by exactly 2 seconds, and that delay is not
cancellable: a teardown must not cut the injected fault short and record an error where the mode
asked for a stall. A target stopped during a stall therefore takes up to two seconds to exit.

**DNS wire subset.** The responder speaks the subset the harness generates: a 12-byte header with a
single question, labels length-prefixed ASCII, `QDCOUNT` 1, the recursion-desired flag set, and a
question type among `A` (1), `AAAA` (28), `HTTPS` (65), `TXT` (16) and `CNAME` (5), class `IN`. Over
TCP each message carries a 2-byte big-endian length prefix. A response echoes the transaction id, the
question and the opcode, sets the response and recursion-available flags, and answers `A` with
`10.0.0.1` and `AAAA` with fifteen zero bytes and a one — one answer, name compressed to the question,
TTL 60. Every other question type gets an empty `NOERROR` answer.

**The responder never emits a non-zero RCODE.** Its flags are built from the response bit, the
recursion-available bit, the query's own opcode and the query's recursion-desired bit; no RCODE bits
are ever set. Every well-formed query is answered `NOERROR`, so a `servfail` count on the client side
can only come from something on the path rewriting the response.

## Running the analysis

From the analysis directory, with the campaign tree copied to `raw/`:

```bash
cd benchmarks/results/2026-10-06-e2e-competitors/analysis
python3 analyze.py --raw ../raw --out ..
```

A flat tree — a single run directory holding `run.json` and the arm files, which is what the
self-test produces — is read with `--flat`:

```bash
python3 analyze.py --raw /tmp/wf-bench/selftest/out --flat --out /tmp/selftest-analysis
```

The flags, the inputs, the aggregation policy and every table are described in
[analysis/README.md](../results/2026-10-06-e2e-competitors/analysis/README.md); the script is Python 3
standard library only, and matplotlib is imported lazily and only for the plots.

## Non-obvious properties

- **The histogram's ceiling.** The latency histograms are logarithmic: 34 buckets of 2048
  sub-buckets, tracking values clamped into `[1 ns, 2³⁴ − 1 ns]` — that is 17.18 seconds. A sample
  slower than the ceiling is recorded **at** the ceiling, and there is no overflow counter, so a
  `maxUs` of 17179869.183 means "17.18 s or more", not "17.18 s". A percentile reports the exclusive
  upper bound of the bucket it lands in, so it is never an underestimate.
- **`latency.tcp-connect` and `metrics.meanConnectMs` are different statistics over different
  populations.** For `LAT` and `LATLOAD`, `latency.tcp-connect` holds only the 1 Hz connect probe and
  measures each probe from its *intended* instant, so pacing lateness is inside the sample; its count
  is the number of probes. `metrics.meanConnectMs` is the mean over *every* successful connect the arm
  made — the lane connects and the probe together, the population that `metrics.tcp.connectAttempts`
  counts — each measured from the start of its own connect. `REL` and `PERSIST` publish their own
  `meanConnectMs` over their own attempts. The two must not be cited side by side.
- **`THRU` paces to a byte budget and can end budget-bound.** `budgetBytes = targetBytesPerSecond ×
  seconds`, and each frame is reserved whole, so a frame that would overshoot the budget is refused
  and the streams stop. `metrics.budgetReached: true` means the arm ended because the budget was
  spent, not because its time ran out, and `budgetRemainingBytes` is the unused remainder — always
  less than one frame. A time-bound run has `budgetReached` false and a remainder that is whatever it
  did not have time to send.
- **The two UDP drains are not merged.** In `LOSS` the offer loop drains the socket without blocking
  between sends, and a second drain runs after the offer loop until W has elapsed since the last
  *real* send. They are separate because the in-loop drain must not stall the open-loop pacer, and
  only the post-offer drain knows the horizon. `MIX` keeps a dedicated receive task per desktop and a
  separate waiter that ends at the same horizon, and both arms cap the observation instant at it.
  A datagram still inside its window when observation stops is booked as `abandonedAtTeardown`, never
  as `never`.
- **`abandonedAtTeardown` is the interrupt bucket.** It counts sent datagrams whose W had not elapsed
  when observation stopped: neither an arrival nor a loss can be claimed for them. They are excluded
  from `lossRate` and are counted as client loss in `gates.clientSendLoss`, so a drain cut short
  moves datagrams out of `never` and into a bucket the gates already fail on. The latency arms use the
  same name for supplied-but-never-sent requests, which is the deferred queue's residue.
- **The reliability arm's per-attempt records are bounded by the arm, not by its runtime.** At most
  4096 `attempt` records are written per arm. The budget is spent on the disconfirming attempts
  first — every attempt whose observed outcome differs from the expected one, or whose echo was
  truncated, qualifies — with the ordinary attempts sampled at one in 16.
  `metrics.attemptRecords` and `metrics.attemptRecordsOmitted` say how much of the arm the file
  covers, so a multi-hour campaign cannot grow the file and a reader can tell what was left out.
- **Latency is measured from each request's intended instant, never from the instant it was actually
  sent.** A request deferred by a full in-flight window or a stalled product is published as an
  inflated sample, not a missing one, and the percentile includes the pacing lateness.
- **The UDP sequence space is bounded at 2¹⁸ − 1.** A plan whose offered schedule would run past it
  is refused where it is loaded, so reaching the bound means the schedule outran the space it
  declared. The tracker then refuses each such sequence and counts it in `metrics.outOfRangeSequences`
  (published by `LOSS` and by the MIX UDP class): the datagram was offered and handed to the socket,
  but it is not booked as sent, so it lands in no classification bucket, it is not part of the
  published `clientSendLoss`, and `sent` is smaller than `supplied`. A non-zero value therefore means
  the classification covers fewer datagrams than the schedule offered and the record is not a
  complete loss measurement.

## Verification

The harness is healthy when the self-test runs green, and a record is healthy when the following
hold. All of them are visible in the record itself.

| Check | What to look for |
|---|---|
| the run completed | one `result` and one `armSummary` per arm; `run.json`'s `failed` false and no arm's `failed` true; no `error` record |
| the self-test client exited 0 | the client exits 1 if any arm failed, 2 on a usage error |
| the targets answered | the ledger holds `tcp` records with `clean`, `reset`, `partialFin` and `halfClose` verdicts, `udpSummary` totals, and the `targetSummary` written at shutdown. A `clientClosedEarly` record with `mode: unknown` is the normal record of a connect probe: the probe connects and closes without sending a command |
| the UDP identity holds | `arrived + late + never + abandonedAtTeardown + corruptDatagrams == sent` in `LOSS`, in `MIX`'s UDP class and in `BASE`'s loss phase |
| the DNS partition holds | `answered + servfail + timeout + other == sent` in `DNS` and `DNSALT` |
| the reliability identities hold | `connectAttempts == scheduledAttempts` and the outcome counts sum to `connectAttempts` |
| no lane was dead | every witness above is non-zero: `gates.laneShortfall == 0`, `gates.scheduleTruncated == 0`, `gates.idleLanes == 0`, every `tcp.laneSupplied[i] > 0` |
| nothing was dropped by the client | `gates.clientSendLoss == 0` and `gates.backlogDrops == 0` (the arms that compute them); for the latency arms a non-zero `gates.windowOverflow` is a disclosure, not a failure |
| flows were not mixed | `foreignConnection == 0` in `LOSS`, `LAT`/`LATLOAD`'s udp block, `MIX`'s UDP class and per-desktop witnesses, and `BASE`'s loss phase |
| the product behaved as configured | `REL`'s `fidelityMismatch == 0`; `PERSIST`'s `survivedIdle` true; a non-zero `unexpectedEof` excludes the early EOFs that `partialFin` deliberately provokes, which are counted separately as `expectedEarlyEof` |
| the sampler worked | no `samplerError` record and no sample carrying `readError` |
| a campaign is trustworthy | the analysis's own gates: the three identities, the lane witnesses, `clientSendLoss`, the two control blocks against each other, and `directLeak == 0` in the dual phase — see [analysis/README.md](../results/2026-10-06-e2e-competitors/analysis/README.md) |

A healthy self-test run prints the analysis's summary line — `1 pass(es), 1 row(s), N metric(s)` —
and reports zero `correctness-failure` and zero `harness-error` findings. Two findings remain, and
both are properties of a self-test rather than of the harness: an informational note that the DNS arm
ran on port 5301 instead of 53, so the port-53 carriage label is declared rather than measured, and a
measurement caveat that a flat tree has neither control block.
