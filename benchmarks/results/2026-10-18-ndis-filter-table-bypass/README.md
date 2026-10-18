# NDISAPI static filter table kernel bypass — Windows evidence

Task: `10-18-ndis-filter-table-bypass`. Measured 2026-10-18 on the WinLTSC VM (Windows 11 IoT
Enterprise LTSC, build 26100, 16 logical CPUs, Hyper-V vNIC), with a framework-dependent
`WinForward` built from this tree (the feature present, `KernelPassCache` in the config), the
deployed `ndisapi.dll` 3.6.1 sidecar, and the `ndisrd` driver running.

The proxy path used the patched sing-box 1.14.2 (`singfix`) on `127.0.0.1:1080`; the target ran on
the Linux host at `192.168.100.4:40010` (TCP/UDP) and DNS `:53`. Product config: `Host` fallback
`pass`, `Process: sing-box.exe → pass`, `Process: WinForward.E2E.exe` TCP+UDP → proxy `main`. Plan:
`IDLE` 15 s then `THRU` 30 s, 4 streams, 32 KiB frames, 60 MB/s target. Each arm restarted the
product; the sing-box process and the target stayed up.

The concurrent E2E campaign was not using the VM during this run. The campaign's untracked/in-flight
files were not touched; this deployment lives under `C:\wfprof\kpcache` beside the earlier CPU
analysis, not under `C:\wfbench`.

## W1 — exports

```text
W1 export SetPacketFilterTable=True
W1 export ResetPacketFilterTable=True
W1 export GetPacketFilterTableSize=True
W1 export GetPacketFilterTable=True
```

The check used `NativeLibrary.Load`/`GetExport` on `ndisapi.dll`; all four exports are present on
the deployed 3.6.1 sidecar.

## W3/W4 — table ladder, read-back, reset

`kfprobe.exe` installed tables of 1, 64, 128, and 256 flow rules (2, 128, 256, 512 records) over
the live driver, read each back, and reset it:

```text
probe: W3 n=1   status=Success error=0
probe: W4 n=1   read=Success error=0 sum=0
probe: W4 n=1   reset=Success error=0
probe: W4 n=1   after-reset-read=TableMismatch error=0
probe: W3 n=64  status=Success error=0
probe: W4 n=64  read=Success error=0 sum=0
probe: W4 n=64  reset=Success error=0
probe: W4 n=64  after-reset-read=TableMismatch error=0
probe: W3 n=128 status=Success error=0
probe: W4 n=128 read=Success error=0 sum=0
probe: W4 n=128 reset=Success error=0
probe: W4 n=128 after-reset-read=TableMismatch error=0
probe: W3 n=256 status=Success error=0
probe: W4 n=256 read=Success error=0 sum=0
probe: W4 n=256 reset=Success error=0
probe: W4 n=256 after-reset-read=TableMismatch error=0
```

256 flow rules is 512 records, 94,216 bytes; the product's 128-flow cap is 256 records (47 KB), so
the cap is inside the measured envelope. The read-back re-encoding accepted every table, the reset
cleared the installed set (`TableMismatch` on the next read), and the per-rule counters advance in
W2 below (`bypassed > 0`), so `PacketsIn + PacketsOut` is live.

## W2/W5 — `THRU` A/B, driver filter cache off (the default)

Three off/on pairs, alternating order. CPU is the product's process CPU over the 30 s `THRU`
window: `client` is the E2E client's own process sampler; `ext` is the independent 1 Hz
`Get-Process` sampler (user+privileged). Every client run reported `failed=false`,
`connectFailures=0`, `corrupt=0`, `gates.clientSendLoss=0`.

| Arm | Mode | Goodput Mbps | Bytes | CPU client (s) | CPU ext (s) | ext user (s) | ext priv (s) | Rules | Captured | Bypassed | Reinjected | Crossings/1000 |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| off-1 | off | 403.56 | 1,513,851,200 | 44.203 | 45.688 | 9.297 | 36.391 | — | — | — | — | — |
| on-2 | on | 479.93 | 1,799,867,200 | 46.406 | 47.656 | 10.875 | 36.781 | 4 | 885,463 | 878,304 | 885,463 | 1,004 |
| on-3 | on | 479.91 | 1,799,801,600 | 46.687 | 48.281 | 11.078 | 37.203 | 4 | 908,578 | 900,139 | 908,610 | 1,004 |
| off-4 | off | 346.98 | 1,301,635,200 | 43.281 | 45.141 | 10.063 | 35.078 | — | — | — | — | — |
| off-5 | off | 469.74 | 1,761,720,800 | 40.641 | 42.281 | 7.766 | 34.516 | — | — | — | — | — |
| on-6 | on | 479.85 | 1,799,604,800 | 48.203 | 50.047 | 10.500 | 39.547 | 4 | 906,868 | 895,359 | 906,891 | 1,006 |

**W2 go/no-go.** All three on arms report `rules=4`, `active=4`, and `bypassed > 0`: the driver's
`FILTER_PACKET_PASS` entries really remove those packets from the user-mode queue while the
connection completes. The feature is not a no-op on this hardware.

**Crossings.** The on arms' derived ratio
`(captured + reinjected) / (captured + bypassed)` is **1.004–1.006**, against the uncached path's
≈2.0 (every packet read and reinjected). About half of the packets on this proxy path bypassed user
mode. The cache's summary line is the source of the counts; the off arms do not log counters.

**Throughput.** The on arms consistently reached the 60 MB/s target (479.85–479.93 Mbps). The off
arms reached it only once (off-5, 469.74); the other two offered less (403.56, 346.98) because the
client's open-loop pacer could not keep up. The feature removes the sing-box egress pass leg, so the
same offered load is sustained.

**CPU.** The feature is a transition-count and sustained-throughput win, not a CPU win on this
workload. Normalized per delivered GB, using the independent sampler:

| Arm | CPU s/GB |
|---|---:|
| off-1 | 30.18 |
| off-4 | 34.68 |
| off-5 | 24.00 |
| on-2 | 26.48 |
| on-3 | 26.83 |
| on-6 | 27.81 |

The best off arm (`off-5`, the only one that reached the target) is 24.00 s/GB; the on arms are
26.48–27.81 s/GB. The bypass removes the read+reinject of ~0.88–0.90 M packets, but the driver's
sequential static-filter evaluation now runs for **every** packet, including the ~0.89–0.91 M
proxied packets that do not match. That evaluation is the likely offset. A side experiment with the
driver's optional packet-filter cache (`EnablePacketFilterCache`, 3.6.1) did not help:

| Arm | Mode | Goodput Mbps | Bytes | CPU client (s) | CPU ext (s) | Rules | Captured | Bypassed | Crossings/1000 |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|
| off-1 | off | 272.57 | 1,023,720,800 | 40.735 | 42.688 | — | — | — | — |
| on-2 | on | 472.35 | 1,771,659,200 | 49.500 | 51.281 | 4 | 924,620 | 907,165 | 1,009 |

The driver cache raised the on arm's CPU and depressed the off arm's throughput; it was disabled
after the pair and the product does not enable it.

## W6 — correctness soak (feature on, driver cache off)

One product-on run with a reduced plan: `THRU` 30 s, `DNS` 30 s (UDP/53), `LOSS` 30 s (UDP/40010).
`run.json` reports `failed=false`, `wallSeconds=90.341`.

| Arm | Result |
|---|---|
| THRU | 479.87 Mbps, 1,799,834,400 bytes, 54,873 frames, 0 connect failures, 0 corrupt |
| DNS | 3,001 sent / 3,001 answered, 0 timeout, 0 servfail, answerRate 1, achievedRate 99.98 |
| LOSS | 9,001 sent / 9,001 arrived, 0 never, 0 corrupt, 4 reordered, 0 foreignConnection, clientSendLoss 0 |

The cache's own summaries during the THRU interval reached `5 rules, 4 active, 899,943 bypassed,
909,783 captured, crossing 1,005`; after the TCP flows ended the rules retired to 0 and the UDP/DNS
arms (not cached by design) show no bypass. The soak's gates are clean.

## Verdict and default

- **Mechanism:** proven on the real driver (W1, W2); PASS bypass, counters, read-back, reset, and
  the bounded table all behave as designed.
- **Goal:** the average user/kernel crossings per packet on the proxied path dropped from ≈2.0 to
  ≈1.0, and sustained throughput reached the configured target in every on arm.
- **Cost:** product CPU did not fall; per delivered byte the on arms are slightly worse than the
  best off arm. The static-filter evaluation is the recorded hypothesis (the driver's filter cache
  did not help in the side probe).
- **Decision:** keep `KernelPassCache` default **false**. The first release is opt-in for the
  transition/throughput benefit; a default flip would trade a measured CPU cost for that benefit,
  which is not warranted by this evidence. Revisit the default only after a CPU-positive measurement
  (smaller rule sets, a driver cache with verified invalidation, or a workload with fewer proxied
  packets per bypassed flow).

## Limits

- One VM, Hyper-V vNIC, loopback proxy; absolute CPU and Mbps are not portable. The off arms'
  throughput spread (347–470 Mbps) is VM/pacer noise; the on arms' consistent target hit is the
  firmer signal.
- The off arms do not log `captured`/`bypassed` (the feature is off); the off crossing ratio is the
  defined ≈2.0, not a measured counter.
- The driver-cache pair is a side probe, not a full study; its negative result only says it does not
  rescue this workload at this table size.
- UDP pass caching, self-traffic tuples, and non-flow static classes are out of scope; the soak's
  UDP/DNS arms exercise correctness, not those bypass classes.
- The `WinForward.E2E` client/target are the Oct 18 harness builds; the product is this tree's
  source build with only the feature under test.

## Raw data

- `raw/ab-cache-off/<arm>/`: `run.json`, `THRU.jsonl`, `IDLE.jsonl`, `cpu.csv`, `passcache.log`
  (on arms only).
- `raw/driver-cache-on/<arm>/`: the side pair.
- `raw/soak-on/`: `run.json`, `THRU.jsonl`, `DNS.jsonl`, `LOSS.jsonl`, `passcache.log`.
