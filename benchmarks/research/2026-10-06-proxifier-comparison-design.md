# Five-way Windows transparent-proxy comparison — workload design and measurement methodology

**Scope.** Design the workload and the measurement/analysis pipeline for a headless benchmark comparing
WinForward native-AOT, WinForward framework-dependent (JIT), ProxiFyre 2.6.1, Proxifier 4.14 and
ProxyBridge 4.0.0 on a Windows 11 IoT Enterprise LTSC VM (16 vCPU, ~2 GB dynamic RAM) driven over WinRM,
with every proxied flow shaped as

    client process (VM) -> [proxifier intercept] -> 127.0.0.1:1080 (sing-box, direct) -> target (Linux, 1 GbE LAN)

No code is given here; this is the design and its evidence.

**Reading conventions.** Sources are numbered `[n]` and listed in §7. Anything I could not verify is marked
**unconfirmed** inline. The most important negative result is stated up front:

> **There is no published measurement that establishes the overall TCP/UDP protocol split of a typical
> Windows desktop endpoint.** Published numbers exist for individual components (HTTP/3 request share,
> DNS query-type mix, video byte share, page weight), but they come from different populations with
> different denominators (requests vs bytes vs flows vs hosts vs websites), and no study gives the
> endpoint-level mix. Therefore the mixed arm of this benchmark must present its protocol mix as a
> **documented design choice derived from component measurements, plus sensitivity arms** — never as
> "the measured typical desktop mix". This is also what RFC 9411 demands: the mix MUST be documented,
> not assumed [12].

---

## 1. Workload design

### 1.1 What the evidence actually supports

| Component | Number | Denominator / caveat | Source |
|---|---|---|---|
| HTTP/3 | **21 %** of requests | Requests to Cloudflare's network, global, 2025. Per-*origin* negotiation in reality (a client uses H3 to some origins, not a random 21 % of requests). | [1] |
| HTTP/2 | 50 % of requests | same | [1] |
| HTTP/1.x | 29 % of requests | Not the same as "plaintext HTTP" (H1 is usually still TLS). | [1] |
| HTTP/3 site adoption | 40.8 % of websites | Website adoption, not traffic. | [2] |
| Web traffic encrypted | ~95 % | Sandvine/AppLogic 2024. | [3] |
| Video share of volume | **65.93 %** | Total Internet volume, H1 2022 (older Sandvine GIPR). | [4] |
| Top apps by volume | Netflix 13.74 %, YouTube 10.51 %, "Generic QUIC" 5.41 %, HTTP Media Stream 4.33 %, Disney+ 4.20 % | Total Internet volume, H1 2022. "Generic QUIC" is an app-classification bucket, not all QUIC. | [4] |
| Video per user | 5.6 GB/day; YouTube 1.5 GB, Netflix 1.6 GB (period totals per user) | Fixed networks, 2025 GIPR; per-user totals, not session sizes. | [5] |
| QUIC vs TCP trend | "QUIC traffic is displacing TCP traffic" in video and social | Qualitative, 2025 GIPR. | [5] |
| Median desktop page | **2.9 MB / 77 requests** | HTTP Archive 2025 (2024: 2,652 KB / 71 requests). | [6][7] |
| Connection reuse | **13 TCP connections per page; 5.6 requests per connection** | 2020 Web Almanac HTTP chapter (vs 23 / 3.2 in 2016). Historical, not Chrome telemetry. | [8] |
| DNS query types | A 54.16 %, AAAA 24.24 %, **HTTPS 19.68 %**, PTR 1.28 %, SVCB 0.45 %, SRV 0.10 % | 24 h on 2025-10-22, IIJ infrastructure; recursive-side view, not a Windows stub. | [9] |
| DNS types (older, authoritative view) | A 64 %, AAAA 22 %, PTR 6.4 %, NS 1.4 %, TXT 1.4 %, MX 1.2 %, SRV 1.1 %, CNAME 1.0 % | 1.6 trillion transactions, Jan–Apr 2019, recursive→authoritative. | [10] |
| DNS transport | **UDP 96.96 %, TCP 1.82 %** (2025; series 0.19 %→1.82 % 2021→2025) | IIJ. | [9] |
| DNS rate | **≈ 0.2 queries/s per host** | IIJ explicitly warns NAPT inflates IPv4 per-IP counts, so this is not a Windows-desktop measurement. | [9] |
| mDNS | 13 % of total bandwidth in a campus single-multicast-domain WLAN; ~69 % of mDNS packets were iTunes | Columbia campus WLAN, ~2010 era. A campus WLAN is the worst case, not a headless VM. | [11] |
| mDNS/SSDP/LLMNR chatter (extreme) | ~75 % of all IP packets on a guest Wi-Fi SSID, ~1,500 clients, ~80 % Apple devices | Vendor-field-report blog, not peer-reviewed. Use only as an upper bound. | [12] |
| NTP poll | SpecialPollInterval default **3,600 s** (domain members) / **604,800 s** (standalone) | Windows Time Service; applies when the SpecialInterval flag is used. | [13] |
| Streaming bitrates | Netflix recommends 3 Mbps @720p, 5 Mbps @1080p, 15 Mbps @4K; YouTube VP9 VOD targets 1,024/1,800/12,000 kbps @720p/1080p/2160p | Vendor *guidance*, not measured session bitrates. | [14][15] |
| Game packet rate | Dota 2 server ≈ 30 pkt/s; Counter-Strike client mean inter-departure 41.7 ms ≈ 23 pkt/s | Rates only — **no** measured packet-size distribution was found. | [16][17] |

**Not found / do not invent:** a defensible endpoint-level TCP:UDP split; a Windows-desktop DNS QPS or
cache-hit ratio; a contemporary Chrome connection-reuse distribution; measured Netflix/YouTube session
byte counts; measured CS:GO/Valorant packet-size profiles. Cisco's "video = 82 % of consumer traffic by
2022" is a 2017 *forecast* [18] and must not be quoted as an observation.

### 1.2 What a SOCKS5-based transparent proxy can and cannot carry

SOCKS5 (RFC 1928) defines exactly three commands: `CONNECT` (0x01), `BIND` (0x02), `UDP ASSOCIATE` (0x03).
The UDP request header is `RSV(2) | FRAG(1) | ATYP(1) | DST.ADDR | DST.PORT`; fragmentation is optional and a
relay that does not support `FRAG != 0` must drop the datagram. Critically: the protocol "does not provide
network-layer gateway services, such as forwarding of ICMP messages" [19].

| Class | Carryable by SOCKS5? | Notes for this benchmark |
|---|---|---|
| TCP short/long-lived, DNS/TCP, HTTPS, HTTP | Yes (`CONNECT`) | — |
| UDP unicast: DNS/UDP, QUIC, NTP, game/VoIP | Yes (`UDP ASSOCIATE`) | Requires a *unicast* destination; the relay is a UDP NAT [19]. |
| mDNS 5353, SSDP 1900, LLMNR 5355, NBNS 137 | **No** | Multicast/broadcast destinations are outside the SOCKS5 UDP model; TTL=1 link-local scope means the OS handles them, not a per-process proxifier. Exclude from the proxied mix; use as a separate noise/perturbation arm. |
| DHCP 67/68 | No (broadcast; system-owned) | Exclude; or one synthetic renew in a control arm. |
| ICMP / raw IP | **No** | Explicitly out of scope in RFC 1928 [19]. |
| QUIC (UDP/443) | Yes, if the product implements `UDP ASSOCIATE` | **This is the discriminator**: Proxifier intercepts TCP only, so QUIC is silently leaked (or forces TCP fallback). |

Product capability evidence:
* Proxifier: "Proxifier supports TCP/IP connections only so applications that uses for example UDP will not
  work properly with Proxifier" [20] (statement appears on the Mac v1 FAQ page and reflects the product
  family's TCP-only design).
* ProxiFyre: "transparently route both TCP and UDP traffic through a SOCKS5 proxy, enabling advanced use
  cases such as **QUIC over SOCKS**"; SOCKS5-over-TLS supported from v2.4.0; documented limitation:
  "Fragmented IPv6 datagrams are not redirected" [21].
* ProxyBridge: "The vast majority of proxy clients, including Proxifier, only intercept TCP connections…
  ProxyBridge natively supports UDP proxy routing without requiring a TUN device or VPN"; and a tracked
  issue confirms UDP routing only works when the upstream proxy type is SOCKS5 (an HTTP upstream makes UDP
  go direct) [22][23].
* sing-box: the `socks` inbound accepts UDP (`streamUserPacketConnection` → `RoutePacketConnectionEx`) [24].
  Note a real bug relevant to your UDP arm: the `udp_timeout` option was not applied to socks-inbound UDP
  associations (associations could hang forever), reported Feb 2026 and fixed in 1.12.23 [25]. **Pin the
  sing-box version and verify this fix is present**, or your UDP arm will measure association leakage.

### 1.3 Recommended workload arms

Two families: one **realistic mixed arm** (MIX) and a set of **clean single-purpose probe arms**. All arms
are driven by one process — the intercepted client — and all timings are recorded by that client.

#### 1.3.1 The MIX arm (one synthetic "office desktop", scaled ×8)

Derive the mix from a per-desktop *behaviour model*, then report the resulting protocol split as an output.
The model, per synthetic desktop:

| Element | Parameter | Derivation |
|---|---|---|
| Page loads | 3 pages/min, 18 s think time, 13 parallel connections/page | [6][8] |
| Page bytes | 2.9 MB/page, 77 requests, mean object ≈ 38 KB | [6] |
| Request/response | 5.6 requests per TCP connection (HTTP/1.1 keep-alive shape), plus an HTTP/2-like multiplexed variant | [8] |
| QUIC share | 21 % of *origins* served over HTTP/3 with `RequestVersionExact` / `--http3-only` (never "or lower") | [1]; fallback semantics from [26] |
| DNS | 4 lookups per page (A, AAAA, HTTPS, one CNAME chase), type mix A 54 / AAAA 24 / HTTPS 20 / other 2, 1 in 50 queries forced over TCP/53, plus 0.2 qps background | [9][10] |
| Video | 1 stream per 4 desktops, 5 Mbps 1080p, ~1 MB objects every ~2 s, 180 s sessions | [14][15] |
| Realtime UDP | 1 flow per 4 desktops, 30 pkt/s, 120-byte payloads | [16][17] |
| NTP | 1 flow, poll every 64 s (compressed from the 3,600 s domain default so a 6-minute run contains events) | [13] |
| Local noise | mDNS at a *stated synthetic* 5 pkt/s from a second process; **not** proxied | [11][12] |

Scale factor 8 (8 synthetic desktops) puts the arm at roughly 20 Mbps of offered load — about 2 % of a
1 Gbps LAN, which keeps the network out of the measurement and the VM at a controllable utilization.

Resulting split, **to be reported as computed output, not claimed as reality** (numbers are order-of-magnitude
estimates from the model above and must be recomputed from the actual run):

| Class | Flow share | Byte share |
|---|---|---|
| Short TCP request/response (TLS, H1/H2) | ~28 % | ~30 % |
| QUIC / HTTP-3 | ~7 % | ~18 % |
| DNS (UDP + TCP) | ~62 % of *flows* | < 1 % |
| Bulk TCP video | < 1 % of flows | ~50 % |
| Realtime UDP | < 1 % of flows | < 1 % |

The DNS-dominated flow count is a real and useful property: **desktop flow counts are dominated by DNS and
short TCP**, while byte counts are dominated by video. State both columns everywhere; a single "mix
percentage" is meaningless without its denominator.

**Sensitivity arms (mandatory, because the mix is a design choice):**
`MIX-h3hi` (QUIC share 40 % — top countries already exceed a third of requests over H3 [1]),
`MIX-vidhi` (video = 80 % of bytes, closer to the Sandvine consumer profile [4]),
`MIX-dnslo` (DNS background 0.02 qps).

#### 1.3.2 Probe arms

| Arm | Emulates | Protocol | Generator | Parameters | Duration | Why |
|---|---|---|---|---|---|---|
| `LAT-idle` | unloaded path latency | TCP + UDP echo | in-process harness | 20 req/s open-loop paced; latency logged from **intended** start (coordinated-omission-free); HdrHistogram | 120 s + 30 s warmup | RFC 2544/8219 prescribe a ≥120 s tagged stream [27][28] |
| `LAT-load` | loaded path latency | TCP + UDP echo | in-process harness | 1,000 req/s paced | 120 s | 120 k samples/run → p99.9 has ≥120 samples [29][30] |
| `DNS-mix` | stub resolver, cold cache | UDP/53 + TCP/53 | in-process harness (dig/kdig only on the target side to validate) | 200 q/s, type mix per §1.3.1, 2 % TCP | 120 s | isolates DNS cost + detects DNS leaking |
| `REL-tcp` | connection churn + upstream aborts | TCP | harness + **target-side fault modes** | 200 conn/s; fault mix 25 % each: clean / RST after 8 KB / partial-FIN / half-close-then-send / 2 s stall | 120 s | replaces the old "inject aborts into the proxy" idea — see §1.5 |
| `LOSS-udp` | datagram fidelity | UDP echo | harness | 200 pkt/s, 120 B payload, seq + send-ts + CRC32; wait window `W = max(200 ms, 5×p99 baseline RTT)` | 180 s | 36 k packets; exactly measures loss/dup/corrupt/reorder |
| `BURST-udp` | overload behaviour | UDP echo | harness | ramp 200 → 20,000 pkt/s over 30 s | 30 s | **calibration arm** — this is where client send-buffer overflow appears |
| `THRU-tcp` | bulk download | TCP | harness (iperf3 only as a coarse cross-check) | 1 and 4 parallel flows, 60 s, target 200 Mbps | 60 s | CPU per MB |
| `QUIC-bulk` | H3 streaming | QUIC | harness `HttpClient` H3 + `curl --http3-only` cross-check | 1 × 5 Mbps bulk + 20 req/s of 32 KB | 120 s | H3 throughput and TTFB |
| `IDLE` | steady state / leak | none | none | no client traffic, proxifier running | 300 s | memory slope and idle CPU |
| `BASE-*` | control | as above | as above | **interception disabled**: (a) direct to target, (b) via sing-box as an explicit SOCKS proxy, no proxifier | 60 s each | (a) = harness-loss floor; (b) = the true proxy-attributable delta |

### 1.4 Generator availability on Windows — the hard constraint

The client process must run **on the Windows VM**, because interception is per-process. That eliminates most
of the obvious tools. Verified status:

| Tool | Windows? | Evidence / verdict |
|---|---|---|
| **In-process .NET harness** | **Yes, best option** | Only the .NET 10 runtime is needed on the VM; publish framework-dependent `win-x64` from Linux. Covers DNS (raw UDP/TCP sockets), short/bulk TCP, UDP echo with sequence+CRC, and **HTTP/3 via `HttpClient`** — .NET's HTTP/3 uses MsQuic, requires Windows 11 build 22000+ or Server 2022, and `msquic.dll` ships with the .NET runtime for Windows; set `DefaultRequestVersion = HttpVersion.Version30` with `DefaultVersionPolicy = RequestVersionExact` [31][32]. (`RequestVersionExact` is essential — see §1.6.) |
| `curl` for Windows | **Yes** | curl.se/windows 8.22.0 build is statically linked with **ngtcp2 1.25.0 + nghttp3 1.18.0 + nghttp2 1.70.0** → `--http3` / `--http3-only` work [26][33]. Good independent cross-check. |
| `h2load` | **Unconfirmed** | `--h3` exists and is documented, but requires nghttp2 built with `-DENABLE_HTTP3=ON` against ngtcp2+nghttp3 [34][35]; no official Windows build of nghttp2/nghttp2 apps was found. Use it on the **Linux target** side, not as the Windows client. |
| `aioquic` (Python) | Yes (asyncio on Windows) | `examples/http3_client.py` exists but is a one-shot client, not a load generator with latency stats [36]. Acceptable for a QUIC *reachability* check only. |
| `iperf3` | **Not officially supported** | esnet FAQ: "iperf3 is not officially supported on Windows, but iperf2 is. We recommend you use iperf2." Community builds exist (e.g. ar51an/iperf3-win-builds) [37][38]. Cross-check only. |
| `dnsperf` | **Unconfirmed** | Autotools build (`./configure && make`), packages listed for Linux distros only [39]. No Windows package found. |
| `dig` | **Unconfirmed** | ISC published Windows zips historically (e.g. `BIND9.16.5.x64.zip`, 2020) [40], but the current ISC download page lists RHEL/CentOS/Fedora, Ubuntu, Debian and Docker packages only [41]. Treat current official `dig.exe` as unverified. |
| `kdig` / `sockperf` / `D-ITG` / `wrk` / `h2load` | No / unconfirmed | `kdig` (knot-dnsutils) and `sockperf` are POSIX-oriented with no Windows build found (**unconfirmed**); D-ITG Windows binaries **unconfirmed** [42]; `wrk` is POSIX-only. |
| `dnstop` | n/a | It is a **passive pcap analyzer**, not a generator. Belongs on the capture/target side. |

**Consequence:** put the workload *inside* the .NET harness process. That also guarantees the process-name
match for per-process interception rules, gives one JSON result file per run, and removes the
"generator-vs-proxy attribution" ambiguity for the generator's own CPU (you can still measure the target
side and sing-box separately).

### 1.5 TCP reliability metric that is fair across five products

The old harness injected adversarial aborts (`clean` / `clientRst` / `relayCancel` / `upstreamTruncate`)
into the *proxy's* code paths. That is impossible for third-party binaries and would be unfair anyway.

**Move the faults to the target server.** The Linux echo/HTTP target exposes deterministic fault modes per
connection, selected by port, by request path, or by a per-connection sequence counter:

| Target fault mode | What it tests |
|---|---|
| clean (full response + FIN) | baseline |
| RST after N bytes | does the proxy faithfully relay an upstream reset, or convert it to a clean FIN? |
| partial response then FIN | does the client see a premature EOF (not a reset)? |
| half-close: client FINs, target keeps sending | does the relay support half-closed connections? |
| stall 2 s then continue | timeout/keepalive handling |

This is product-agnostic (the fault is upstream of all five, inside the shared path), realistic (servers do
this), and it makes the metric non-trivially non-zero. Client-side outcome taxonomy, per connection attempt:

```
connectFail | reset | unexpectedEof | timeout | halfCloseViolation | clean | otherError
```

Two headline metrics:

* `tcp.unexpectedEofRate = unexpectedEof / connectAttempts` — **the denominator is attempts, not completions**,
  so connect failures cannot be hidden outside the metric. This is what stops the metric from being
  trivially zero.
* `tcp.fidelityDiscrepancyRate` — the target server also records its own per-connection verdict; a
  mismatch between the client-observed and server-observed outcome (same connection id, carried in the
  payload) is a relay artifact. This two-sided view is the strongest evidence you can produce without
  touching the products' code.

RFC 9411 gives you a published sanity threshold for the validation criteria: failed application
transactions **< 0.001 %** and "terminated TCP connections due to unexpected TCP RST sent by the DUT/SUT
**< 0.001 %**" of initiated connections, plus an equivalent QUIC criterion when HTTP/3 is used [12]. Adopt
these as pass/fail gates and report your observed rates against them.

Add the **churn** dimension explicitly (`REL-tcp` at 200 conn/s) — ephemeral-port pressure, TIME_WAIT
accumulation, and per-connection memory are where transparent proxies actually differ.

### 1.6 Two traps specific to the QUIC arm

1. **Silent fallback.** `curl --http3` (without `-only`) races a TCP connection ~100–200 ms later, and
   .NET's `RequestVersionOrLower` downgrades on failure [26][31]. A TCP-only proxifier would then look
   fine. Use `--http3-only` and `RequestVersionExact`, and record the negotiated version from the response
   (`response.Version`), asserting it is HTTP/3. Report `quic.fallbackRate` as a metric in its own right.
2. **Leak vs. intercept.** With a TCP-only proxifier, UDP still reaches the target *directly*, so an echo
   workload will report normal loss and "success". The benchmark therefore needs ground truth for
   "did the flow traverse the proxy?" — see §2.1.

---

## 2. Measurement methodology

### 2.1 Ground truth: did traffic actually traverse the proxy?

Without this, every per-product comparison is unfalsifiable. Three independent counters:

1. **sing-box as the choke point.** All proxied traffic must traverse `127.0.0.1:1080`. sing-box's
   Clash-compatible API (`experimental.clash_api.external_controller`, used in sing-box's own issue
   threads) exposes connection and traffic statistics [25]. *Verify the exact endpoint semantics against
   the pinned sing-box version — **unconfirmed** in this pass.*
2. **Target-side flow ledger.** The echo/HTTP target logs every connection and datagram source plus the
   harness connection-id carried in the payload. Flows the target sees but sing-box never saw = leaked.
3. **Client-side offer log.** What the harness believes it sent.

Define `proxiedFraction = flowsSeenBySingBox / flowsOfferedByClient` per class, and treat
`proxiedFraction = 1.0` as a precondition for including a product's row in the corresponding metric table.
For ProxiFyre/ProxyBridge/WinForward the expectation is ≈1.0 for both TCP and UDP; for Proxifier the UDP row
must be reported as **"not carried"** with `proxiedFraction ≈ 0`, never as "100 % loss".

### 2.2 Latency

* **Measure RTT, client-side only.** The echo/RTT method needs no clock synchronization, which removes the
  entire class of clock-offset error. RFC 2681 defines round-trip delay; RFC 2679 quantifies the one-way
  error budget as `Esynch(t) + Rsource + Rdest` and RFC 4656 requires an explicit error estimate plus a
  "synchronized to UTC" flag for one-way measurements [43][44][45].
* **Do not make one-way delay the headline metric.** On a sub-millisecond LAN, host timestamping error and
  scheduler jitter dominate the quantity being compared. If a one-way split is wanted, use OWAMP-style
  reporting: report the loss threshold, the clock error estimate, and the `S` (synchronized) bit [45].
* **Decompose latency rather than guess**: `connect` (SYN→SYN-ACK through the proxy), `ttfb`
  (request→first response byte), `rtt` (echo). RFC 9411 already lists TTFB/TTLB (min/avg/max) as optional
  KPIs of exactly this kind [12].
* **Percentiles**: report p50, p90, p99, p99.9 and max. RFC 8219 prescribes `TL = median(L_i)` and
  `WCL = L_99.9th percentile`, with the test repeated ≥20 times and the **1st and 99th percentiles of the
  per-repetition values** reported as the measure of variation [28].
* **Never report a mean or a standard deviation for latency.** Gil Tene: "Standard Deviation and application
  latency should never show up on the same page… If you haven't stated percentiles and a Max, you haven't
  specified your requirements." [46]
* **Record with HdrHistogram** (3 significant digits → "value quantization within the range will thus be no
  larger than 1/1,000th (or 0.1 %) of any value"; ~185 KB fixed footprint for 1 µs–1 h; recording cost
  measured at 3–6 ns) [47][48]. Absolute min/max and mean cannot be reconstructed from a 3-significant-digit
  histogram — if you need them, record them in separate scalars.
* **Coordinated omission.** A closed-loop probe that waits for each response before issuing the next one
  silently deletes the samples that would have been slow. Either (a) pace open-loop and compute latency from
  the *intended* start time, or (b) use `recordValueWithExpectedInterval()` with the pacing interval
  [47][49]. Prefer (a); document which was used.

**How many samples?** Distribution-free confidence intervals for a quantile are binomial/order-statistic
based: with the `k`-th order statistic bounding the `p`-quantile, the minimum sample size is
`n ≥ k/p − 1` [29][30]. Concretely, from the same reference implementation: bounding the 99th percentile
with the 2nd-highest observation at 90 % confidence needs **n = 389**; a 90 % two-sided CI on the *median*
using 5th-nearest order statistics needs **n ≈ 38** [29]. Practical reading:

| Target | Minimum n per run | 95 % CI on that quantile at this n |
|---|---|---|
| p50 | ≥ 40 | workable |
| p90 | ≥ 45 (k=5, 95 % CI, from Hahn & Meeker tables) | fine |
| p99 | ≥ 300–400 | ~1–2 samples' worth of resolution |
| p99.9 | ≥ 2,000–4,000 | poor without pooling; report descriptively |
| p99.9 across 5 runs | ≥ 10⁴ pooled | CI from the between-run distribution, not the pooled one |

`LAT-load` at 1,000 req/s × 120 s = 120,000 samples/run satisfies p99 and p99.9 comfortably.
`LAT-idle` at 20 req/s × 120 s = 2,400 samples supports p50/p90/p99 only — say so in the table.

### 2.3 CPU

* **Primary method: delta of cumulative CPU time, not sampling.**
  `Δ(Process.TotalProcessorTime) / Δ(wall clock)` gives an unambiguous number with no counter-normalization
  ambiguity and no sampling-interval artifacts. `Process.TotalProcessorTime` is a `TimeSpan` of all threads'
  processor time since process start [50].
* **Report the normalization explicitly, because the two ecosystems disagree.**
  PerfMon's `Process\% Processor Time` is documented as being computed over a baseline of
  `(number of logical CPUs × 100)`, i.e. it can reach 1600 % on a 16-vCPU box [51][52]; Task Manager on
  Windows 8+ actually displays `Processor Information\% Processor Utility` (frequency-weighted), not
  `% Processor Time` [53]; Task Manager's per-process CPU column is a fraction of the whole machine.
  On Linux, `pidstat`/`%CPU = 100 %` means **one** CPU. Because Microsoft's own documentation is internally
  inconsistent about the per-process baseline, **define your unit inline in every table**: "CPU is reported
  as % of one vCPU (1,600 % = all 16 vCPUs busy) and as % of the VM (100 % = all 16 busy)."
* **Normalizations to publish** (never raw seconds alone, because arms and products have different
  workloads): per 1,000 transactions, per 1,000 packets, per MB proxied, and per second of sustained load.
  Denominators come from the target-side ledger (packets/bytes) and the sing-box counters (§2.1).
* **Avoiding "measuring the harness":**
  1. Because the generator *is* the intercepted client, its CPU is inside the same process you care about —
     so report `cpu.proxy`, `cpu.generator` and `cpu.vmTotal` separately, and always publish the VM's total
     utilization so a reader can see whether the proxy was CPU-starved.
  2. Keep the offered load at a level where VM CPU stays below ~50 %; report the headroom.
  3. Run `BASE-direct` (no interception) and `BASE-singbox` (explicit SOCKS, no proxifier) so the
     proxy-attributable delta is `proxy − base`, not an absolute.
  4. For "which threads burned the CPU", ETW CPU sampling is the only method that attributes per-process
     without code changes. Windows Performance Recorder/`wpr` and WPA/`wpaexporter` ship in the Windows
     Performance Toolkit (Windows ADK) and can be automated headlessly — **verify ADK availability on the
     LTSC image; unconfirmed in this pass**.
  5. Note the documented measurement limitations even for the simple counters: `Get-Process` polling costs
     real CPU at high frequency, and `% Processor Time` under-samples because the idle determination runs at
     the system clock interval (~10 ms) [51].
* **"100 % = one core" is the right unit for comparability** with Linux tooling and with most published
  proxy/relay numbers; publish the VM-relative number alongside it.

### 2.4 Memory

Microsoft's own mapping table ties the counters together: `Private Bytes` ↔ Task Manager "Commit Size";
`Working Set` ↔ "Working Set (Memory)"; `Working Set - Private` ↔ "Private Working Set" [54]. The .NET
runtime-metrics docs define `dotnet.process.memory.working_set` as "the number of bytes of physical memory
mapped to the process context" (same as `Environment.WorkingSet`) and
`dotnet.gc.last_collection.memory.committed_size` as GC-committed virtual memory [55][56].

**Report a bundle, not one number**, because the five programs are four different runtime shapes
(native-AOT, JIT, .NET Framework/C++/CLI via `socksify`, native C++):

| Metric | Source | Role |
|---|---|---|
| **Private bytes / commit** (`Process.PrivateMemorySize64`) | primary, comparable across runtimes | cross-runtime headline |
| Working set & **peak** working set | `Process.WorkingSet64`, `PeakWorkingSet64` | physical footprint, but see ballooning below |
| Private working set (`Working Set - Private`) | shareable-page-free footprint | cross-check |
| Managed heap (`gc-heap-size`) and GC committed (`gc-committed`) | .NET only | explains the JIT vs AOT gap |
| Thread count, handle count | `Process.Threads.Count`, `HandleCount` | a relay's real scaling cost |

**Steady state.** Warm up (discard the first ≥60 s, or the first repetition per Georges et al. [57]), then
report the **median and p95 of a fixed window** (e.g. the last 5 minutes) and, separately, the **OLS slope
over the whole run in MiB/min with its CI** as the leak indicator. Report peak separately — peaks are what
actually kill a 2 GB VM.

**Dynamic memory is a genuine confound, not a detail.** Hyper-V's Minimum RAM lets the host reclaim memory
after startup by coordinating with in-guest "ballooning"; the guest's visible installed RAM changes as the
balloon inflates and deflates [58][59]. A ballooned guest is under memory pressure, so its processes trim
their working sets — meaning **working set is not comparable between a run that happened to balloon and one
that did not.**

Mitigations, in order of preference:
1. **Freeze memory for the whole matrix**: set Minimum RAM = Startup RAM = Maximum RAM (i.e. disable
   Dynamic Memory) for the duration of the benchmark, and record the setting in the report. This is the only
   way to make cross-product memory numbers directly comparable.
2. If Dynamic Memory must stay enabled, log guest-visible total physical memory alongside every memory
   sample and **reject any window in which it changed**.
3. Also note Smart Paging: it uses disk as temporary RAM for restarts and "can degrade virtual machine
   performance because disk access speeds are much slower than memory access speeds" [58]. Any Smart Paging
   event during a run invalidates the latency and memory numbers for that run.
4. On the host, `Hyper-V Dynamic Memory Balancer – Available Memory` is the documented counter for host-side
   pressure [59].

### 2.5 UDP accuracy: loss, corruption, duplication, reordering — and separating harness loss from path loss

**Packet format** (payload of every datagram): `seq(u64) | t_send_ns(u64) | connId(u32) | payload_len(u32) |
payload(...) | crc32c(payload)`. The target echoes the entire payload unchanged. This single format gives
loss (seq gaps), duplication (seq repeats), reordering (seq < NextExp), corruption
(CRC mismatch or payload bytes changed), truncation (`payload_len` mismatch), and RTT (client-side clock
only) — with **no server-side timer**.

**Metrics, with the RFC they come from:**

| Metric | Definition | RFC |
|---|---|---|
| `lossRate` | `1 − receivedUnique / sent`, where "received" = arrived within the wait window `W`. Declare `W` in every table. | RFC 2680: `Type-P-One-way-Packet-Loss`; "the threshold of 'reasonable' here is a parameter of the methodology" and the instrument must be calibrated so it rarely counts an arriving packet as lost [60] |
| `lateRate` | arrived after `W` (reported separately, never folded into loss) | RFC 2680 §2.7 |
| `corruptRate` | CRC/payload mismatch. Report both the raw rate and a **strict-loss variant** in which corrupt counts as lost, since RFC 2680 says "If the packet arrives, but is corrupted, then it is counted as lost." | [60] |
| `dupRate` | more than one non-corrupt copy; "only the first to arrive is considered for further analysis" | RFC 4737 §3.3 [61] |
| `reorderRate R` | `count(Type-P-Reordered=TRUE)/L`, with `TRUE` iff `s < NextExp` | RFC 4737 §4.1 [61] |
| `degree3Reordering` | fraction of packets that are 3-reordered; RFC 4737 notes this is the threshold at which a NewReno sender would halve its window | RFC 4737 §5.3 [61] |
| `ipdv` | `D(i) − D(i−1)`, reported as Dmin/Dmed/Dmax | RFC 5481 via RFC 8219 §7.3.2 [28] |
| `pdv` | `D99.9thPercentile − Dmin` | RFC 8219 §7.3.1 [28] |

`lossRate`, `dupRate`, `corruptRate` and `reorderRate` are **not disjoint** — do not sum them into a
"total error rate" without saying so.

**Separating client-side send-buffer loss from path loss** — the failure that produced ~⅓ phantom loss in the
earlier run. Partition the offered load into four buckets and report all four:

```
offered (app)  ──a──> handed to kernel ──b──> left the interface ──c──> seen by target ──d──> seen by app (echo)
                 clientSendLoss        kernelSendDrop        pathLoss          returnLoss
```

1. **`clientSendLoss`** — the harness counts every enqueue and every `EAGAIN`/`WSAEWOULDBLOCK`/error return,
   and never blocks a producer. This bucket must be **0 by construction**: use a bounded in-flight window
   (`min(W_max, BDP)`) and a send queue that reports overflow explicitly instead of silently dropping.
   This is the bucket that the old benchmark was actually measuring.
2. **`kernelSendDrop`** — on Linux, read `/proc/net/snmp` `Udp: OutDatagrams` and `UdpSndbufErrors`
   ("The UDP send buffer is full or no kernel memory available… your system is dropping outgoing UDP
   packets" [62]); the send buffer is bounded by `net.core.wmem_default` / `wmem_max` and the socket's
   `SO_SNDBUF` [62]. On the Windows client, the UDPv4/UDPv6 performance objects expose datagram-sent and
   error counters — **verify the exact field names on the target OS; unconfirmed in this pass** — and
   `netstat -s -p udp` gives the receive-side view. The Linux target must also report `UdpRcvbufErrors`,
   since a target that cannot drain its own receive buffer looks identical to path loss from the client
   [63][64]. **This is a mandatory pre-flight gate:** any run with a non-zero
   `kernelSendDrop + targetRcvbufErrors` is invalid and must be re-run at a lower offered rate.
3. **`pathLoss`** — sequence gaps at the target, using the client's send log as the reference.
4. **Return-path loss** — echoed datagrams that never arrive back (must be reported, since the proxy's
   return path is a separate code path).

**Calibration rule (non-negotiable):** run `BASE-direct` — the same `LOSS-udp` arm with the proxifier
stopped — and require `pathLoss < 1e-6` and `clientSendLoss = 0`. Any loss in the control is harness loss.
Then use `BURST-udp` to find the offered rate at which `clientSendLoss` first becomes non-zero: that is the
harness's ceiling, and every scored arm must run below ~50 % of it.

**Timeout choice.** Set `W = max(200 ms, 5 × p99(RTT) from the control run)`, cap at 2 s, and publish both
`W` and the resulting `lateRate`. RFC 2680 explains why: the loss threshold must be "large enough that any
value in [Th−δ, Th+δ] is an equivalent threshold", and δ must cover clock error; and it demands that the
threshold "MUST be reported" [60]. RFC 4737 adds that the timeout is effectively the loss threshold and that
IPPM declines to recommend a value [61].

**When zero events occur** (a product with no corruption at all), do not report "0 %". Report the
**rule-of-three** upper bound: with 0 events in `n` trials, the 95 % upper confidence bound is ≈ `3/n`
(e.g. 0 corruptions in 36,000 packets ⇒ < 8.3 × 10⁻⁵). *(Standard result; no primary IETF citation located
in this pass — cite it as standard statistical practice, not as an RFC.)*

### 2.6 TCP reliability

Covered in §1.5. Additional metric-level notes:
* Report the TCP outcome taxonomy as **rates with attempt-based denominators**, per arm, plus the
  target-side view, plus the discrepancy rate.
* RFC 9411's validation criteria give you published gates: failed application transactions < 0.001 %,
  unexpected RST from the DUT < 0.001 %, failed QUIC connections due to unexpected HTTP/3 error codes
  < 0.001 % [12].
* Add `tcp.connect.p50/p99` (setup latency through the proxy) and `tcp.goodput` (Mbps) so a product cannot
  buy reliability with latency.

---

## 3. Metric definition table

| Metric | Formula | Unit | Data source | Across repetitions |
|---|---|---|---|---|
| `proxiedFraction.<class>` | flows at sing-box / flows offered by client | ratio | sing-box Clash API + client log | min over reps (must be =1) |
| `lat.<arm>.<class>.pXX` | XX-th percentile of echo RTT from **intended** start | ms | client HdrHistogram | median of per-rep pXX + 95 % bootstrap CI; also 1st/99th pct of the 20 (RFC 8219 [28]) |
| `lat.<arm>.max` | absolute max | ms | client scalar | max over reps |
| `lat.connect.p99`, `lat.ttfb.p99` | same, for connect/TTFB | ms | client | as above |
| `cpu.proxy.oneCore` | `ΔTotalProcessorTime / Δwall × 100` | % of one vCPU | client process counters | median + CI; also max |
| `cpu.proxy.vm` | `cpu.proxy.oneCore / 16` | % of VM | derived | same |
| `cpu.proxy.per1kTxn` | `ΔCPU_s / (txn/1000)` | ms/1k txn | proxy CPU ÷ target-side txn count | ratio of medians, CI via bootstrap on ratios |
| `cpu.proxy.per1kPkt` | `ΔCPU_s / (pkt/1000)` | ms/1k pkt | proxy CPU ÷ target packets | same |
| `cpu.proxy.perMB` | `ΔCPU_s / MB` | ms/MB | proxy CPU ÷ sing-box bytes | same |
| `cpu.generator.*`, `cpu.vmTotal`, `cpu.headroom` | same definitions | % | client + VM | must be reported alongside |
| `mem.private.p50/p95` | median/p95 of sampled private bytes in the steady window | MiB | `Process.PrivateMemorySize64` | median of per-rep medians + CI |
| `mem.workingSet.p50/p95`, `mem.peakWorkingSet` | same / max | MiB | `WorkingSet64`, `PeakWorkingSet64` | same |
| `mem.slope` | OLS slope of memory vs time over the run | MiB/min | sampled series | median + CI; leak flag if CI excludes 0 |
| `mem.threads.p95`, `mem.handles.p95` | p95 of sampled counts | count | `Process.Threads.Count`, `HandleCount` | median |
| `mem.gcHeap` (.NET only) | gc-heap-size / gc-committed | MiB | `dotnet-counters` / runtime metrics [55] | median |
| `udp.sent` / `udp.received` | counts | packets | client / target | sum |
| `udp.lossRate` | `1 − receivedUnique(W)/sent` | ‰ (and ppm) | target sequence gaps | median + CI (declare `W`) |
| `udp.lateRate`, `udp.dupRate`, `udp.corruptRate`, `udp.reorderRate`, `udp.degree3` | per §2.5 | ‰ | target | median + CI; rule of three if 0 |
| `udp.clientSendLoss`, `udp.kernelSendDrop`, `udp.pathLoss`, `udp.returnLoss` | per §2.5 waterfall | ‰ | client + `/proc/net/snmp` + target | medians; **gates**, not scores |
| `tcp.unexpectedEofRate` | `unexpectedEof / connectAttempts` | ‰ | client | median + CI |
| `tcp.connectFailRate`, `tcp.resetRate`, `tcp.timeoutRate`, `tcp.halfCloseViolationRate` | `event / connectAttempts` | ‰ | client | median + CI |
| `tcp.fidelityDiscrepancyRate` | `1 − agreement(clientVerdict, serverVerdict)` | ‰ | client + target ledger | median + CI |
| `tcp.goodput`, `tcp.connRate` | bytes/s; conn/s | Mbps; /s | client | median |
| `dns.answeredFraction`, `dns.tcpFraction`, `dns.proxiedFraction` | counts ratios | ratio | target resolver + client | median |
| `dns.lat.p50/p99`, `dns.queryTypeMix` | per §1.3 | ms; % | client | median + CI |
| `quic.connSuccess`, `quic.reqSuccess`, `quic.fallbackToTcp`, `quic.negotiatedH3` | counts ratios | ratio | client (`response.Version`) | median; `negotiatedH3` must be 1.0 when fallback is disabled |
| `quic.ttfb.p50/p95` | per §2.2 | ms | client | median + CI |
| `env.*` | NIC offloads, dynamic-memory settings, sing-box version, product version+config hash, VM snapshot id | — | harness metadata | **constant; must be identical across all rows** |

**Aggregation rules.** Count-type metrics: sum within a run, then median across runs with a bootstrap CI.
Percentile-type metrics: compute the percentile *within* each run, then median across runs — never pool raw
samples across runs to compute a headline percentile (that hides run-to-run variance, which is exactly what
you must show). Rates/totals: always normalize by the run's own denominator.

---

## 4. Statistics plan

| Element | Recommendation | Evidence |
|---|---|---|
| Design | **Randomized complete block.** One pass = all five programs, each running the full arm set once. Randomize program order *within* each pass; randomize arm order *within* each program. | [28] (≥20 repetitions, median as summarizer); [57] (multi-invocation, CIs, discard first invocation); [65] (turbo/pinning/background-load effects on stability) |
| Repetitions | **n = 4 passes minimum, n = 5 preferred**, plus 1 discarded warmup pass. RFC 8219's ≥20 repetitions for latency is the ideal; this matrix cannot afford it — **document the deviation and the resulting CI width** rather than pretending. | [28]; budget arithmetic below |
| Warmup | One discarded pass for the whole matrix; within each run, ≥30 s / ≥60 s warmup before the measurement window; discard the first repetition per Georges et al. | [57] |
| Aggregation | Per-run metric → **median across runs** + 95 % bootstrap CI (BCa). Report IQR. Mean ± 99.9 % CI is the BenchmarkDotNet convention if you prefer it; BenchmarkDotNet's `Error` column is half of the 99.9 % CI. | [57]; [66] |
| Decision rule | A beats B iff **(i)** the 95 % CI of the ratio A/B excludes 1, **and (ii)** the effect exceeds a pre-declared practical threshold (latency 5 %, CPU 10 %, loss 0.5 pp, memory 10 %), **and (iii)** Holm–Bonferroni-adjusted p < 0.05 within the metric's comparison family. Otherwise report "no measurable difference at this sample size". | [57] (CI comparison, ANOVA + Tukey HSD for >2 alternatives); [67] (Bonferroni); [68] (BH FDR if you prefer power over FWER) |
| Distribution comparison (secondary) | Mann–Whitney U for location shift, Kolmogorov–Smirnov for shape. **Caveat:** at 10⁵ samples everything is "significant" — the effect size (rank-biserial / KS statistic) is the reportable quantity, not the p-value. | [69][70] |
| Tail comparison | Compare per-run p99/p99.9 with a **bootstrap CI on the difference**, not overlapping CIs; overlapping CIs do not imply no difference. | [57] |
| Zero-event metrics | Rule of three: 95 % upper bound ≈ 3/n. | standard practice (no primary RFC found) |
| Order/drift check | Regress each primary metric on run index within a program; report the slope. A monotone trend across a program's passes ⇒ thermal/driver drift, re-run. | [65] |
| Outliers | Do **not** silently drop. Report both `OutlierMode.RemoveUpper`-equivalent (BenchmarkDotNet's default) and raw, and state which is in the headline. | [66][71] |
| Environment pinning (one-time, documented) | Disable Dynamic Memory (or fix min=max=startup); disable Windows Update / Defender scans / Search indexing for the run window; freeze NIC advanced properties (RSC, LSO, checksum offload, interrupt moderation); record CPU affinity policy; keep the WinRM control channel quiet during measurement windows. | [58][65] |

**Time budget (the arithmetic the design has to live with).** Per program per pass:
`MIX 360 s + LAT-idle 120 + LAT-load 120 + DNS-mix 120 + LOSS-udp 180 + BURST-udp 30 + REL-tcp 120 +
THRU-tcp 60 = 1,110 s ≈ 18.5 min`, plus `IDLE 300 s` in passes 1 and 4 only, plus ~60 s for
stop/start/verify between programs.

* 4 passes → 5 × (4 × 18.5 + 2 × 5 + 4 × 1) ≈ **5.9 h**
* 5 passes → ≈ **7.2 h**

Trim knobs if that is too long: `MIX` 360→240 s (−20 min per pass), `LOSS-udp` 180→120 s (−10 min),
`DNS-mix` 120→90 s (−5 min). Cutting repetitions below 4 is the wrong knob: with n=1 you cannot report
variance at all, and a five-way ranking from single runs is indefensible [57].

---

## 5. Presentation

**Tables**

1. **Headline matrix**: rows = 5 programs, columns = the ~8 primary metrics, each cell = median [IQR] with
   the CI; a second table gives the ratio to the `BASE-singbox` baseline with its CI.
2. **Provenance block** above every table: product version + config hash, sing-box version, VM snapshot id,
   n, arm duration, `W`, CPU unit definition, NIC offload state, dynamic-memory state.
3. **Per-arm detail tables** with the full percentiles and the validation gates (`proxiedFraction`,
   `clientSendLoss`, target `RcvbufErrors`).
4. **Not-applicable cells are labelled** ("Proxifier: UDP not carried — see [20]"), never left blank and
   never filled with a number.

**Plots**

* **ECDF (or CDF-on-a-log-x-axis) of latency per program, one panel per arm** — the single most informative
  latency plot; `matplotlib.axes.Axes.ecdf` computes it exactly with no binning [72][73].
* **Percentile-distribution plot** (latency vs percentile, 0→99.99 %) per program — HdrHistogram's native
  output form [47].
* **Box plots of per-repetition metrics** (one box per program, points jittered over the boxes) — shows
  run-to-run variance, which per-sample plots hide. Use `whis=(0,100)` or explicit percentiles and show the
  mean as a point if it must appear [74].
* **Normalized ratio bars with CI whiskers vs baseline** on a log axis, one panel per metric family.
* **UDP loss waterfall**: offered → clientSendLoss → kernelSendDrop → pathLoss → late → corrupt → duplicate
  → delivered, as a stacked per-million bar (log scale if needed).
* **Time series** of CPU, memory, and per-second loss for one representative run per program — this is where
  leaks and steady state become visible, and RFC 9411 explicitly asks for "graphs showing each of these
  metrics over the duration (sustain phase) of the test" [12].
* **Run-order plot** (metric vs run index) for the drift check.

**Pitfalls, each with the reason it is a pitfall**

| Pitfall | Why it is wrong |
|---|---|
| Bar chart of mean latency | Hides the tail, which is the entire product difference; "std deviation and latency should never show up on the same page" [46]. "Numerical calculations are exact, but graphs are rough" is backwards — identical summary statistics can hide completely different distributions [75][76]. |
| Comparing raw totals across arms/products | Different arms have different durations and different denominators; use rates (`/s`, `/1000 txn`, `/MB`). |
| Mixing `% of one core` with `% of machine` | Microsoft's own per-process counter baseline is documented inconsistently (baseline `#logicalCPUs × 100` [51][52] vs Task Manager's `% Processor Utility` semantics [53]); the unit must be defined in the table, not inferred. |
| Summing UDP loss + corrupt + dup + reorder | Not disjoint: a corrupt packet is also received; a duplicate is also "reordered" by RFC 4737's definition [61]. |
| Reporting a loss rate without `W` | Loss is defined relative to a timeout parameter; RFC 2680 requires the threshold to be reported [60]. |
| Folding `late` into `loss` | Hides queueing behaviour; report both [60]. |
| Reporting "0 %" for a zero-event metric | Report the rule-of-three bound instead. |
| Pooling all samples across runs for a headline p99.9 | Erases the between-run variance you are trying to characterize [28]. |
| One run per program ("n=1") | Prevalent but non-rigorous: single-run comparisons produce misleading and outright incorrect conclusions [57]. |
| p99.9 from <10³ samples without a CI | The quantile CI is binomial; below ~10³ samples the CI is wider than the differences you are trying to resolve [29][30]. |
| Treating Proxifier's missing UDP as "100 % loss" | It is a capability boundary; report `proxiedFraction ≈ 0` / "not carried" [19][20]. |
| Hiding the environment | RFC 9411: "a summary of the DUT/SUT configuration, including a description of all enabled DUT/SUT features, MUST be published with the benchmarking results" [12]. |

---

## 6. Risks and confounds

| # | Risk | Mitigation |
|---|---|---|
| 1 | Two proxifiers' kernel drivers loaded at once (WinpkFilter/NDISAPI, WFP callouts) | Revert to a clean VM snapshot between programs; verify with `driverquery` / `fltmc` / service list; assert no foreign filter driver is bound before each run. |
| 2 | Interception scope differs between products (process name vs path vs wildcard) | Define one canonical rule per product, capture the config file verbatim in the report, and prove isolation: run a *second*, non-intercepted process generating traffic and assert it never reaches sing-box. |
| 3 | **UDP asymmetry** (Proxifier) | Report `proxiedFraction` per class; label UDP rows "not carried"; keep Proxifier in the TCP rows. Also measure the leak explicitly (does its UDP still reach the target directly?). |
| 4 | **The old harness-loss trap** (~⅓ phantom loss) | Bounded in-flight window; explicit overflow accounting; `BASE-direct` control gate (`pathLoss < 1e-6`); `BURST-udp` ceiling calibration; mandatory `kernelSendDrop`/`RcvbufErrors` gate. |
| 5 | Dynamic-memory ballooning changes the measurable working set mid-matrix | Freeze memory (min = startup = max); log guest-visible RAM per sample; reject windows where it changed [58][59]. |
| 6 | sing-box itself is in every path and is a shared, version-sensitive component | Pin the version (≥ 1.12.23, for the socks-UDP timeout fix [25]); run `BASE-singbox` so the reported delta is proxy-attributable; include the sing-box version in every table. |
| 7 | Loopback hop cost ≠ network cost | The `proxifier → 127.0.0.1:1080` leg is real syscall work and is part of what differs between products (thread-per-connection vs IOCP). Document it as in-scope; don't claim the numbers are "WAN-representative". |
| 8 | Nagle / delayed-ACK artifacts (40 ms and 200 ms steps) | Test each product with a small write-write-read pattern; report a dedicated small-message latency cell; check whether each product sets `TCP_NODELAY` and document it. |
| 9 | NIC offload state differs or drifts (RSC/LSO/checksum/interrupt moderation) | Freeze and record all advanced properties; re-verify before each pass. Offloads change how much per-packet work the proxy's driver and the stack actually do. |
| 10 | Windows background activity (Update, Defender, Search, telemetry) | Disable for the run window, document it, and keep the WinRM control channel idle during measurement windows. |
| 11 | QUIC silently downgrades to TCP | `--http3-only` / `RequestVersionExact`; assert `response.Version == 3`; report `quic.fallbackToTcp` [26][31]. |
| 12 | IPv6 fragmentation limitation (ProxiFyre documents that fragmented IPv6 datagrams are not redirected) | Run v1 IPv4-only and document the IPv4/IPv6 ratio (RFC 9411 requires the ratio to be documented); add a dual-stack sensitivity arm later [12][21]. |
| 13 | Thermal/boost drift across a 6–8 h matrix | Randomized program order per pass; run-order regression check; consider disabling Turbo/boost for stability (measured: up to +15 % stability at up to −56 % execution time [65]) — but then do it for *all* programs and report it. |
| 14 | CPU starvation when generator and proxy share 16 vCPUs | Keep VM utilization < 50 %, report `cpu.vmTotal` and headroom, publish `cpu.generator` separately. |
| 15 | Multiplicity: 5 programs × ~25 metrics | Pre-declare the primary family; Holm–Bonferroni within each metric family; publish the full secondary table as exploratory [67]. |
| 16 | Third-party products phone home / auto-update / need a GUI first run | Block egress; snapshot after configuration; assert version hash before each pass; document any GUI-only setup step as a deviation. |
| 17 | Proxifier's DNS-through-proxy setting (and equivalents) changes what the DNS arm measures | Configure and document: for the DNS-leak metric, run one sub-arm with "resolve through proxy" and one without; report `dns.proxiedFraction` for both. |
| 18 | `recordValueWithExpectedInterval` vs intended-start accounting used inconsistently across tools | One method only (intended-start), documented; assert by injecting an artificial 1 s stall and verifying the reported p99.9 moves to ≈1 s [47]. |
| 19 | Same-process generator and proxy make "proxy CPU" ambiguous | Measure the sing-box process and the target process separately; report the three-way split; use ETW sampling for per-thread attribution if the ADK is present. |
| 20 | Loss threshold `W` chosen after seeing the data (p-hacking) | Fix `W` from the `BASE-direct` control run *before* the scored runs; publish it. |

---

## 7. Sources

**Traffic mix and workload shape**

1. Cloudflare Radar 2025 Year in Review — HTTP/2 50 %, HTTP/1.x 29 %, HTTP/3 21 % of global requests, 2025 — https://blog.cloudflare.com/radar-2025-year-in-review/ (microsite: https://radar.cloudflare.com/year-in-review/2025)
2. W3Techs HTTP/3 usage statistics — "HTTP/3 is used by 40.8 % of all the websites" — https://w3techs.com/technologies/breakdown/ce-http3/ranking
3. Sandvine/AppLogic Global Internet Phenomena Report 2024 — "95 % of web traffic is encrypted" — https://japan.sandvine.com/hubfs/Sandvine_Redesign_2019/Downloads/2024/GIPR/GIPR%202024.pdf
4. Sandvine GIPR 2023 (H1 2022 data) — video 65.93 % of volume; Netflix 13.74 %, YouTube 10.51 %, Generic QUIC 5.41 %, HTTP Media Stream 4.33 %, Disney+ 4.20 % — http://www.sandvine.com/hubfs/Sandvine_Redesign_2019/Downloads/2023/reports/Sandvine%20GIPR%202023.pdf
5. AppLogic Networks 2025 Global Internet Phenomena Report — video 5.6 GB/day/user; YouTube 1.5 GB, Netflix 1.6 GB; "QUIC traffic is displacing TCP traffic" — https://www.sandvine.com/hubfs/AppLogic_Networks/Collateral/Global%20Internet%20Phenomena%20Reports/GIPR%202025.pdf
6. HTTP Archive Web Almanac 2025, Page Weight — median desktop page 2.9 MB, 77 requests — https://almanac.httparchive.org/en/2025/page-weight
7. HTTP Archive Web Almanac 2024, Page Weight — 2,652 KB / 71 requests; percentile table — https://almanac.httparchive.org/en/2024/page-weight
8. HTTP Archive Web Almanac 2020, HTTP chapter — median 13 TCP connections/page, 5.6 requests per connection — https://almanac.httparchive.org/en/2020/http
9. IIJ, "DNS Query Trends" (24 h on 2025-10-22) — A 54.16 %, AAAA 24.24 %, HTTPS 19.68 %, PTR 1.28 %, SVCB 0.45 %; ~0.2 qps/host; UDP 96.96 %, TCP 1.82 % — https://www.mynog.org/wp-content/uploads/2026/MYNOG-13-Papers/DNS%20Query%20Trends%20-%20Yoshinobu%20Matsuzaki.pdf
10. Foremski et al., IMC 2019 / DNS Observatory — 1.6 T transactions; A 64 %, AAAA 22 %, PTR 6.4 %, NS 1.4 %, TXT 1.4 %, MX 1.2 %, SRV 1.1 %, CNAME 1.0 % — https://pub.foremski.pl/2019-imc2019.pdf
11. "Measurements of Multicast Service Discovery in a Campus Wireless Network" — mDNS ≈ 13 % of total bandwidth; ~69 % of mDNS packets iTunes — https://mice.cs.columbia.edu/getTechreport.php?format=pdf&techreportID=570
12. RFC 9411, *Benchmarking Methodology for Network Security Device Performance*, 2023 — application traffic mix MUST be documented (names, percentages, object sizes); validation criteria 0.001 % failed transactions / unexpected RST; KPI reporting; graphs over the sustain phase — https://datatracker.ietf.org/doc/html/rfc9411
13. Microsoft Learn, Windows Time Service tools and settings — `SpecialPollInterval` default 3,600 s (domain) / 604,800 s (standalone) — https://learn.microsoft.com/en-us/windows-server/networking/windows-time-service/windows-time-service-tools-and-settings
14. Netflix Help, internet connection speed recommendations (3/5/15 Mbps) — https://help.netflix.com/en/node/306
15. Google, VP9 VOD encoding settings (1,024 / 1,800 / 12,000 kbps) — https://developers.google.com/media/vp9/settings/vod
16. "Assessing the Accuracy of Network Estimations in the DOTA 2 Game Client" (2016) — server sends ≈30 packets/s — https://www.tu-ilmenau.de/fileadmin/Bereiche/EI/mt-nam/publications/Assessing_the_Accuracy_of_Network_Estimations_in_the_DOTA_2_Game_Client.pdf
17. "A Comparison of the Traffic Patterns of Counter-Strike…" (CoNEXT '06) — mean client inter-departure 41.7 ms ≈ 23 pkt/s — http://wpage.unina.it/alberto/papers/40.pdf
18. Cisco VNI global device growth/traffic profiles — "Internet video traffic will be 82 % of all consumer Internet traffic by 2022" (a 2017 **forecast**) — https://www.cisco.com/c/dam/m/en_us/solutions/service-provider/vni-forecast-highlights/pdf/Global_Device_Growth_Traffic_Profiles.pdf

**Protocol capability**

19. RFC 1928, *SOCKS Protocol Version 5* — CONNECT/BIND/UDP ASSOCIATE; UDP request header `RSV|FRAG|ATYP|DST.ADDR|DST.PORT`; "does not provide network-layer gateway services, such as forwarding of ICMP messages" — https://www.rfc-editor.org/rfc/rfc1928.txt
20. Proxifier FAQ — "Proxifier supports TCP/IP connections only so applications that uses for example UDP will not work properly with Proxifier" — https://www.proxifier.com/docs/mac-v1/pgs2/faq3.html
21. ProxiFyre README — TCP+UDP over SOCKS5, "QUIC over SOCKS", SOCKS5-over-TLS in v2.4.0, fragmented IPv6 datagrams not redirected — https://github.com/wiresock/proxifyre/blob/main/README.md
22. ProxyBridge docs, "Why ProxyBridge?" — "including Proxifier, only intercept TCP connections" — https://interceptsuite.com/docs/proxybridge/why-proxybridge/
23. ProxyBridge issue #101 — UDP only supported with a SOCKS upstream — https://github.com/InterceptSuite/ProxyBridge/issues/101
24. sing-box `protocol/socks/inbound.go` — UDP packet-connection handling — https://github.com/SagerNet/sing-box/blob/9da0746d/protocol/socks/inbound.go
25. sing-box issue #3754 — socks-inbound `udp_timeout` not applied (fixed in 1.12.23) — https://github.com/SagerNet/sing-box/issues/3754

**QUIC / HTTP-3 tooling**

26. curl, "HTTP/3 with curl" — ngtcp2/quiche backends; `--http3` vs `--http3-only`; the 100 ms/200 ms eyeballing behavior — https://curl.se/docs/http3.html
27. RFC 2544 — latency stream SHOULD be ≥120 s; tag after 60 s; repeated ≥20 times; frame sizes — https://datatracker.ietf.org/doc/html/rfc2544
28. RFC 8219 — §7.2 latency (≥500 tagged frames, `TL = Median(L_i)`, `WCL = L_99.9th percentile`, ≥20 repetitions, 1st/99th percentiles as variation); §7.3 PDV/IPDV; §12 summarizing function — https://www.rfc-editor.org/rfc/rfc8219.html
29. `monaco` order-statistics implementation of Hahn & Meeker confidence bounds — `n ≥ k/p − 1`; example: 2nd-highest bounding p99 at 90 % confidence needs n = 389; 5th-nearest for a 90 % CI on the median needs n ≈ 38 — https://github.com/scottshambaugh/monaco/blob/main/src/monaco/order_statistics.py
30. Pekasiewicz, "Interval estimation of higher order quantiles" — minimum sample sizes for nonparametric p-quantile estimation (binomial/order-statistic basis)
31. Microsoft Learn, "Use HTTP/3 with HttpClient" — `HttpVersion.Version30`, `HttpVersionPolicy`, MsQuic platform requirements — https://learn.microsoft.com/en-us/dotnet/core/extensions/httpclient-http3
32. dotnet/runtime, `System.Net.Quic` readme — Windows 11 / Server 2022 / build ≥ 20145.1000 for Schannel QUIC; `msquic.dll` ships with the Windows .NET runtime — https://github.com/dotnet/runtime/blob/main/src/libraries/System.Net.Quic/readme.md
33. curl for Windows download page — 8.22.0 built with ngtcp2 1.25.0, nghttp3 1.18.0, nghttp2 1.70.0 — https://curl.se/windows/
34. h2load(1) man page — `--h3` ("Short hand for `--alpn-list`=h3, which effectively forces HTTP/3") — https://nghttp2.org/documentation/h2load.1.html
35. nghttp2 `src/h2load.cc` — HTTP/3 code guarded by `ENABLE_HTTP3` — https://github.com/nghttp2/nghttp2/blob/master/src/h2load.cc
36. aioquic `examples/http3_client.py` — https://github.com/aiortc/aioquic/blob/main/examples/http3_client.py
37. iperf3 FAQ — "iperf3 is not officially supported on Windows, but iperf2 is. We recommend you use iperf2." — https://github.com/esnet/iperf/blob/master/docs/faq.rst
38. Community iperf3 Windows builds — https://github.com/ar51an/iperf3-win-builds
39. DNS-OARC dnsperf — build from source (autotools); packages for Linux distros — https://github.com/DNS-OARC/dnsperf/ and https://dns-oarc.net/index.php/tools/dnsperf
40. ISC FTP, BIND 9.16.5 — Windows x64 zip builds existed (2020) — https://ftp.isc.org/isc/bind9/9.16.5/
41. ISC downloads — current BIND 9 packages list RHEL/CentOS/Fedora, Ubuntu, Debian, Docker only — https://www.isc.org/download/
42. D-ITG download page (**Windows binaries unconfirmed**) — https://traffic.comics.unina.it/software/ITG/download.php

**Measurement methodology**

43. RFC 2679, *A One-way Delay Metric for IPPM* — clock uncertainty terms `Esynch(t) + Rsource + Rdest` — https://datatracker.ietf.org/doc/html/rfc2679
44. RFC 2681, *A Round-trip Delay Metric for IPPM* — https://datatracker.ietf.org/doc/html/rfc2681
45. RFC 4656, *OWAMP* — error estimate with the synchronized-to-UTC `S` bit; presumed send time and loss timeout for missing packets — https://datatracker.ietf.org/doc/html/rfc4656
46. Gil Tene, "How NOT to Measure Latency" (QCon SF 2012 slides; InfoQ 2016) — coordinated omission; "Standard Deviation and application latency should never show up on the same page… If you haven't stated percentiles and a Max, you haven't specified your requirements" — https://qconsf.com/sf2012/dl/qcon-sanfran-2012/slides/GilTene_HowNotToMeasureLatency.pdf and https://www.infoq.com/presentations/latency-response-time/
47. HdrHistogram README/JavaDoc — 3 significant digits ⇒ quantization ≤ 0.1 % of any value; ~185 KB fixed footprint; 2–3 significant digits ⇒ ±~1 % / ±~0.1 % accuracy for derived statistics; `recordValueWithExpectedInterval()` for coordinated-omission correction — https://github.com/HdrHistogram/HdrHistogram and https://hdrhistogram.github.io/HdrHistogram/JavaDoc/org/HdrHistogram/package-summary.html
48. HdrHistogram (.NET) README — https://github.com/HdrHistogram/HdrHistogram.NET/blob/master/README.md
49. HdrHistogram issue #148 — load generators that "have a plan" should record `endOfOperation − intendedStart`; no correction needed then — https://github.com/HdrHistogram/HdrHistogram/issues/148
50. .NET API, `Process.TotalProcessorTime` — https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.process.totalprocessortime
51. Microsoft TechNet Wiki (archived), "Understanding Processor (% Processor Time) and Process (% Processor Time)" — Process baseline is `#logicalCPUs × 100`; `% Processor Time` under-samples at the ~10 ms system clock interval — https://learn.microsoft.com/en-us/archive/technet-wiki/12984.understanding-processor-processor-time-and-process-processor-time
52. Microsoft Q&A, "Process\% Processor Time — what is the percentage calculated from?" — same baseline discussion on a 24-logical-core box — https://learn.microsoft.com/en-us/answers/questions/979722/process-processor-time-what-is-the-percentage-calc
53. Microsoft Learn troubleshoot, "CPU usage exceeds 100 %" — Task Manager on Windows 8+ corresponds to `Processor Information\% Processor Utility`, not `% Processor Time` — https://learn.microsoft.com/en-us/troubleshoot/windows-client/performance/cpu-usage-exceeds-100
54. Microsoft Learn, "Memory Performance Information" — counter ↔ Task Manager mapping (Private Bytes ↔ Commit Size; Working Set ↔ Working Set (Memory); Working Set - Private ↔ Private Working Set) — https://learn.microsoft.com/en-us/windows/win32/memory/memory-performance-information
55. Microsoft Learn, .NET runtime metrics — `dotnet.process.memory.working_set`, `dotnet.gc.last_collection.memory.committed_size` definitions — https://learn.microsoft.com/en-us/dotnet/core/diagnostics/built-in-metrics-runtime
56. Microsoft Learn, available .NET counters — `cpu-usage` = "percent of the process's CPU usage relative to all of the system CPU resources"; `working-set`; `gc-heap-size`; `gc-committed` — https://learn.microsoft.com/en-us/dotnet/core/diagnostics/available-counters
57. Georges, Buytaert, Eeckhout, "Statistically Rigorous Java Performance Evaluation", OOPSLA 2007 — confidence intervals via z/t, discard the first VM invocation, CI within 1–2 % of the mean or stop at 30 runs, ANOVA + Tukey HSD for >2 alternatives — https://dri.es/files/oopsla07-georges.pdf (DOI: https://dl.acm.org/doi/10.1145/1297027.1297033)
58. Microsoft Learn, Hyper-V Dynamic Memory — Startup/Minimum/Maximum RAM, Memory Buffer, Smart Paging, ballooning — https://learn.microsoft.com/en-us/windows-server/virtualization/hyper-v/dynamic-memory
59. Microsoft Learn (WS2012R2 docs) — Dynamic Memory settings and `Hyper-V Dynamic Memory Balancer – Available Memory` — https://learn.microsoft.com/en-us/previous-versions/windows/it-pro/windows-server-2012-r2-and-2012/hh831766(v=ws.11)
60. RFC 2680, *A One-way Packet Loss Metric for IPPM* — loss threshold is a methodology parameter and MUST be reported; corrupted packets count as lost; duplicates count as received; instrument-resource limits are a documented error source — https://datatracker.ietf.org/doc/html/rfc2680
61. RFC 4737, *Packet Reordering Metrics* — `Type-P-Reordered` (`s < NextExp`); duplicate handling; `R = count(reordered)/L`; reordering-free runs; degree of n-reordering (n=3 matches the NewReno DUP-ACK threshold) — https://datatracker.ietf.org/doc/html/rfc4737
62. Netdata community, UDP send buffer errors — meaning of `UdpSndbufErrors`, the `net.core.wmem_default`/`wmem_max`/`SO_SNDBUF` relationship — https://community.netdata.cloud/t/1m-ipv4-udp-send-buffer-errors/2106 ; see also https://github.com/netdata/netdata/blob/master/src/health/health.d/udp_errors.conf
63. OnSphere network tuning — `netstat -suna` field meanings (packet receive errors, receive buffer errors, send buffer errors) — https://docs.swissdotnet.ch/onsphere/2.0.0-beta.6/exploitation/05_network/network-tuning.html
64. node_exporter PR #1534 — `Udp*SndbufErrors` / `Udp*RcvbufErrors` provenance in the kernel counters — https://github.com/prometheus/node_exporter/pull/1534
65. Stanojević et al., "Practical Benchmarking Configurations for Reproducible Execution-Time Measurements of CI/CD-Style Workloads", ACM TOSEM — ~30 measurement iterations give RMAD < 0.55 %; disabling Turbo gives up to +15 % stability at up to −56 % speed; pinning gives up to +16 % stability at up to −143 % slowdown for multithreaded workloads; Hyper-Threading/background-load effects — https://dl.acm.org/doi/pdf/10.1145/3838807
66. BenchmarkDotNet, "Statistics" — percentiles need `IterationCount` 10–20 and `LaunchCount` ≥3; `StatisticColumn.P0..P100` are not on by default — https://benchmarkdotnet.org/articles/features/statistics.html ; outliers: https://benchmarkdotnet.org/articles/samples/IntroOutliers.html
67. BenchmarkDotNet, "Jobs" — `LaunchCount`, `WarmupCount`, `IterationCount` (defaults: min iterations 15, max 100; warmup 6–50), `MaxRelativeError`/`MaxAbsoluteError` ("the error means half of 99.9 % confidence interval"), `OutlierMode` default `RemoveUpper` — https://benchmarkdotnet.org/articles/configs/jobs.html
68. BenchmarkDotNet, "How it works" — pilot/warmup/actual stages; overhead evaluation subtracted from the result — https://benchmarkdotnet.org/articles/guides/how-it-works.html
69. scipy.stats — `mannwhitneyu`, `ks_2samp`, `bootstrap`, `quantile_test`, `false_discovery_control` (BH/BY) — https://docs.scipy.org/doc/scipy/reference/stats.html
70. NIST/SEMATECH e-Handbook, Bonferroni method — https://www.itl.nist.gov/div898/handbook/prc/section4/prc473.htm
71. Perfolizer (BenchmarkDotNet's statistics library), `Perfolizer.Mathematics.OutlierDetection` — outlier modes used by BenchmarkDotNet — https://benchmarkdotnet.org/articles/samples/IntroOutliers.html
72. matplotlib, `Axes.ecdf` — "exact" ECDF with no binning; `complementary=True` for exceedance curves — https://matplotlib.org/stable/api/_as_gen/matplotlib.axes.Axes.ecdf.html
73. matplotlib, cumulative distributions gallery — ECDF vs cumulative histogram; non-exceedance/exceedance reading — https://matplotlib.org/stable/gallery/statistics/histogram_cumulative.html
74. matplotlib, box plot customization — `whis` percentiles, showing the mean — https://matplotlib.org/stable/gallery/statistics/boxplot.html
75. matplotlib, Anscombe's quartet — identical means/variances/regression lines, different distributions — https://matplotlib.org/stable/gallery/specialty_plots/anscombe.html
76. Wikipedia, Anscombe's quartet (incl. the Datasaurus Dozen) — https://en.wikipedia.org/wiki/Anscombe%27s_quartet

---

## 8. Unconfirmed / open items

1. Current official Windows `dig.exe` from ISC (historical zips exist; current download page lists Linux/containers only) [40][41].
2. `dnsperf` on Windows (no Windows package located) [39].
3. D-ITG Windows binaries [42]. `kdig`, `sockperf`, `wrk`, `h2load` on Windows.
4. Exact field names of the Windows `UDPv4`/`UDPv6` performance counters (verify on the target OS).
5. The Windows "Process V2" counter set and its instance-naming semantics — not verified in this pass; avoid relying on `Get-Counter` instance names, use the `Process` object from `Get-Process` instead.
6. sing-box Clash API endpoint semantics for connection/traffic counters [25].
7. Availability of the Windows ADK (for `wpr`/`wpaexporter` ETW CPU sampling) on the LTSC image.
8. Perfolizer's effect-size APIs in detail (Hodges–Lehmann, ratio distributions) [71].
9. Any published measurement of a current Windows endpoint's TCP:UDP split, DNS QPS, or cache-hit ratio — **not found**; do not assert one.
10. Cisco VNI transport-level percentages for the current year — not found (Cisco's published video share is a 2017 forecast) [18].
11. Measured (not recommended) Netflix/YouTube session bitrates and sizes [14][15].
12. Measured gaming packet-size distributions (only packet rates were found) [16][17].
