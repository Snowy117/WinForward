# SOCKS5 Full-Path Optimized Results (Phase D)

Date: 2026-08-29 · Same host/job as baseline (Linux 9955HX, .NET 10.0.10, BDN ShortRun).
Raw artifacts: `*-report-github.md` / `*-report.csv`, `stability-quick.jsonl` (includes a
follow-up bare-mode tcp.throughput row), `windows/` (real-machine smoke evidence).

## Baseline → Optimized (acceptance-relevant numbers)

| Metric | Baseline | Optimized | Acceptance |
|---|---|---|---|
| FrameRewriter ForwardedShape @1400 | 2,887.9 ns | **~607–626 ns** (4.8×, equals host shape) | datapath parity ✓ |
| FrameRewriter SwapEthernetMacs | 7.0 ns / 0 B (JIT-elided) | **4.1–4.3 ns / 0 B** (constructive) | 0 B ✓ |
| Dispatcher WarmProxy | 596.4 ns / 352 B | **~258 ns / 160 B** | alloc equal to WarmPass ✓ |
| UdpSession Noop probe (bookkeeping) @100 | 5.73 KB | **3.84 KB** (enumeration share ~0.4 KB) | ≤1 KB product share ✓ |
| UdpSession real @1000 (time scaling) | 310.4 ms (10.8× @10×) | **305.8 ms (11.6× @10×)** | linear ✓ |
| tcp.throughput socks5 vs bare | (no bare mode) | **130.9 vs 152.1 MB/s = 86.1%** (C4 run: 93.9%) | ≥70% ✓ |
| udp.lossRate (Linux quick) | 0 | **0** (150,100/150,100) | no regression ✓ |
| udp.sessionFootprint @1000 | 7.66 MB | **5.34 MB** | bonus improvement |
| Build / tests | 0 warnings / 386 | **0 warnings / 386** | ✓ |

ShortRun ns noise note: optimized matrix ran on a busier desktop (±noise per hot-path.md
contract 9); the allocation columns are exact and are the gates.

## Windows real-machine smoke (WinLtsc 192.168.100.2, ndisrd running)

| Check | Result |
|---|---|
| `udp.lossRate --quick` (two runs) | **lossRate 0** (73,271/73,271 and 73,983/73,983; 0 out-of-order, 0 duplicates, relaySendFaults 0) |
| Product TCP proxy (cloudflare trace + google.com via curl) | `200 84144B`, TLSv1.3; **7 concurrent ESTABLISHED connections observed on Linux sing-box :30890 from 192.168.100.2** during traffic (0 while product stopped) |
| Product UDP proxy (nslookup → 8.8.8.8) | resolved via dns.google |
| Product debug log (61 events) | **0 warn / 0 error / 0 fail**; 3× complete `tcp.redirect.created → tcp.relay.started → tcp.relay.ended → tcp.redirect.closed`; 6× `udp.session.created` → `udp.session.expired` |

### Measurement-methodology finding (recorded for future smokes)

The "egress IPv6 = proxied" proof used by earlier smokes is **not valid on this network**:
the upstream fake-IP router transparently proxies LAN traffic, so `curl https://.../cdn-cgi/trace`
reports a 2406:da18:… IPv6 egress **even with WinForward stopped** (Windows itself has only a
ULA fd00:… address and no working direct IPv6 route — `curl -6` returns nothing). The reliable
proxies-are-ours evidence on this network is the SOCKS5-server-side connection table
(`ss -tn 'sport = :30890'` on the Linux box) plus the product's own redirect/relay debug
events. Both were used for the numbers above.

## Verdict

All re-scoped acceptance criteria met (see task prd.md): zero-allocation datapath
(FrameRewriter/Dispatcher-WarmProxy), UDP cold-path bookkeeping ≤1 KB, linear session
scaling, socks5/bare ≥70%, zero-warning build, 386/386 tests, Windows smoke green.
