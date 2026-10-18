# NDISAPI static filter table kernel bypass

## Goal

Cut the average number of kernel↔user crossings per packet on the capture path by keeping
policy-pass TCP flows in the kernel: NDISAPI's `SetPacketFilterTable` installs
`FILTER_PACKET_PASS` rules for flows the dispatcher has already decided `Pass`, so their later
packets take the driver's normal network path instead of being read into the WinForward pump and
reinjected.

## Background

- **Current per-packet shape.** Every in-scope adapter is put in tunnel mode
  (`NdisAdapterModeController.ApplyCaptureModeAsync`, `src/WinForward.Runtime/Capture/NdisAdapterModeController.cs`),
  every packet is pulled by `NdisApiDriver.TryReadPackets` (`src/WinForward.NdisApi/NdisApiDriver.cs`),
  classified by `CapturePacketProcessor.ProcessAsync` (`src/WinForward.Runtime/Capture/CapturePacketProcessor.cs`),
  decided by `FlowDispatcher` (`src/WinForward.Runtime/FlowDispatcher.cs`), and a passed frame is
  reinjected by `NdisPacketActionExecutor.PassAsync` → `FlushPendingPasses`
  (`src/WinForward.Runtime/Capture/NdisPacketActionExecutor.cs`). A pass packet therefore crosses
  the boundary twice: one read and one reinject.
- **The measured hotspot.** `benchmarks/results/2026-10-18-cpu-usage-analysis/README.md` records a
  480 Mbps proxy arm at **154.0 % process CPU, 83 % of it privileged**, about 3.0 M packets in 20 s
  (~150 k pps), ~4.9 k read IOCTLs/s and ~9.2 k send IOCTLs/s. Its P1 backlog row is "driver-level
  self-traffic/proxy-traffic bypass (if the NDISAPI filter table supports it)": self traffic is
  still captured and reinjected and only then decided pass (§4 P1, §2.4).
- **The pass decision is process-aware.** The E2E proxy config puts `Process: sing-box.exe → pass`
  ahead of the proxy rule (`benchmarks/WinForward.E2E/scripts/configs/wf-fdd-opt.json`), and the
  capture scope widens to every MSTCP-bound adapter because the proxy rules carry no adapter
  selector (`.trellis/spec/backend/traffic-policy-lifecycle.md`). A static rule cannot express
  process identity; the decision for such a flow is only knowable after its first packet has been
  captured and attributed. Covering it requires a per-flow 5-tuple rule installed after the
  decision.
- **The driver API** (verified against the pinned `ndisapi.h` / `Common.h` and the NT KERNEL docs):
  `SetPacketFilterTable(HANDLE, PSTATIC_FILTER_TABLE)` replaces the driver's **whole** static filter
  table with one IOCTL; `ResetPacketFilterTable(HANDLE)` clears it; `GetPacketFilterTableSize` and
  `GetPacketFilterTable` read the table back **including per-rule packet/byte counters**. `ndisrd`
  evaluates rules sequentially (index 0..N, first match wins). Actions: `FILTER_PACKET_PASS` (0x1) =
  normal network path (no user mode), `DROP` (0x2), `REDIRECT` (0x3), `PASS_RDR` (0x4),
  `DROP_RDR` (0x5). `STATIC_FILTER` is a 1-byte-packed 184-byte record; the table header is 8 bytes
  (`m_TableSize`, `Padding`), so N rules span `8 + 184 × N` bytes. `m_Adapter = 0` means all adapters.
  Direction flags are `PACKET_FLAG_ON_SEND = 0x1` / `PACKET_FLAG_ON_RECEIVE = 0x2`. IPv4 addresses
  are the raw four header bytes (the legacy sample assigns `in_addr`'s bytes directly), ports are in
  host byte order, and the IPv4 subnet mask for an exact match is `0xFFFFFFFF`.
- **The table is driver-global, not per client handle.** The docs describe one active rule set in
  `ndisrd.sys`; `m_Adapter` scopes a rule to an adapter, not to a process. Enabling this feature
  claims exclusive ownership of the driver's static filter table.
- **Nothing in the tree consumes the API today.** `NdisApiAbi.cs` declares no static-filter
  structures and `NdisApiDriver` has no filter-table call; the ABI layer only declares the 14
  existing imports.
- **The deployed sidecar is `ndisapi.dll` 3.6.1** (`benchmarks/WinForward.E2E/AGENTS.local.md`). The
  four C exports exist in that version. The driver's maximum accepted table byte length is not
  documented and is an open Windows item.

## Scope (decided with the operator on 2026-10-18)

Option **B: a per-flow kernel pass cache**, TCP-only MVP.

- **Covered:** flows whose stored decision is `FlowAction.Pass` — host and forwarded — including
  process-based pass rules such as the E2E's `sing-box.exe` rule. Once such a flow has been decided
  and observed, its 5-tuple is mirrored into a kernel `FILTER_PACKET_PASS` rule (two oriented rules,
  one per header direction), so its subsequent packets never enter user mode.
- **Not covered in this MVP:** UDP flows, `SelfTrafficRegistry` tuples (the dispatcher claims no
  flow for them), non-flow frames (ARP/ICMP/other L2/L3), `Proxy`/`Block` decisions, the reverse leg
  of a TCP redirect, and the `TcpRedirectOutcome.NotRelevant` fallback pass of a proxy-decided
  flow. Those keep today's behavior.
- **Why not the static-classes-only option (A):** it cannot express the process-pass rule that the
  measured hotspot consists of, so its measured gain would be near zero.

## Requirements

1. **ABI and driver seam.** Declare the four static-filter imports with `SetLastError = true` and
   the Win32 calling convention; mirror the static-filter records with the exact 1-byte-packed
   layout and pin the sizes and offsets in `AssertManagedLayout`; build the variable-length table in
   one contiguous buffer; issue every table operation under the driver's control gate. The managed
   surface the runtime consumes is a small rule/counter seam, not a public mirror of the native
   structs.
2. **Crash-safe rule set.** The table uses **PASS only** — no DROP, REDIRECT, PASS_RDR, or DROP_RDR
   rule may ever be installed. A crashed or killed process must leave at worst a benign
   pass-through rule; when the feature is enabled, the next start must clear a previous run's
   table before installing (with the feature off, WinForward deliberately does not touch the driver's
   table — another client may own it).
3. **Eligibility.** Only a TCP flow whose stored decision is `Pass` is mirrored. The evidence must
   come from the dispatcher's own decision path, never from a bare `PassAsync` call: a proxied UDP
   flow's reverse response is also passed through the executor, and a kernel rule for it would
   bypass the proxy in the client→server direction. A `Pass` observed for a flow that is not a
   claimed `FlowAction.Pass` decision (reverse pass, redirect fallback, non-flow, self traffic) is
   not eligible.
4. **Rule shape.** Each cached flow becomes two oriented rules: `(source = A, destination = B,
   source port = A, destination port = B)` and the mirror image, both `FILTER_PACKET_PASS` with
   `m_Adapter = 0`, protocol TCP, exact addresses/ports, and both direction flags set (the header
   tuple, not the direction flag, selects the direction). IPv6 link-local flows (a non-zero scope
   id, or an address in `fe80::/10`) are refused because the native filter cannot express the scope
   id.
5. **Lifecycle.** The cache is a bounded, leased mirror, not a second policy:
   - install only after a short observation delay (so one-shot flows do not churn the table);
   - renew while the driver's per-rule packet counters advance;
   - remove a rule after a short idle lease, and re-install it when the flow is observed again;
   - cap the number of cached flows and evict the least-recently-active one when full;
   - coalesce table changes to at most one `SetPacketFilterTable` per tick;
   - clear the whole table when interception is paused (empty scope), on graceful shutdown, and
     after a persistent native failure (fail-safe: fall back to today's capture path).
   A rule outlives its last matched packet by at most the idle lease; once it is
   removed, the next packet of that flow re-enters user mode and re-arms the cache.
6. **No packet-path allocation or blocking.** The observation hook runs on the pump thread and must
   add no managed allocation; it only tests a marker and appends to a preallocated bounded ring.
   All driver IOCTLs run on the cache worker, never on a pump. Existing warm-dispatch and executor
   allocation gates stay green.
7. **Observability.** Count packets read, packets reinjected, packets bypassed (from the driver's
   per-rule counters), rules installed/evicted/rejected, table applies, and native failures. Emit a
   periodic `kernel.passCache` line carrying the derived per-packet crossing ratio
   `(read + reinjected) / (read + bypassed)` so the Windows A/B has an observable result. Counters
   and logs are observational only and never change a disposition.
8. **Configuration.** The feature is behind a single boolean, **default off** for the first
   release. The strict configuration reader, validation, README key table, and the shipped
   `appsettings.example.json` learn the key. The operator confirmed on 2026-10-18: land default off,
   complete W2-W6, then decide the default from the evidence. The measurement shows the
   crossing/throughput win but no CPU win, so the default stays **off**; a flip requires a
   CPU-positive measurement. The driver-table exclusivity and the 5-tuple reuse window remain the
   reasons the first release is opt-in.

9. **Verification and non-interference.** Unit-level ABI, builder, eligibility, lifecycle and
   failure tests run on the Linux host. A scripted Windows A/B records CPU, captured packets,
   bypassed packets and the crossing ratio on the existing THRU proxy shape, with the existing E2E
   correctness gates green. No change lands under `benchmarks/WinForward.E2E/**` or in the active
   E2E task tree, and Windows experiments run only when the concurrent E2E campaign is not using
   `ndisrd`/`WinForward`.

## Acceptance Criteria

- [x] `StaticFilter` (184 B), the table header (8 B) and every nested filter record are pinned by
      size/offset tests, and the table builder's bytes are checked against an independent oracle
      (`8 + 184 × N`, IPv4 and IPv6 addresses, host-order ports, exact subnet masks, action PASS,
      adapter 0, both direction flags).
- [x] A seam test proves the complete table operation cycle — replace → read counters → replace
      with a changed set → reset — including the native error and missing-export paths, and that the
      driver never issues `SetPacketFilterTable` with a zero-entry table.
- [x] An executor/dispatcher test proves the observation fires for a policy `Pass` decision
      (inline and deferred attribution) and does **not** fire for self-traffic, non-flow, reverse
      pass, or the redirect fallback pass.
- [x] Cache tests prove install delay, counter-driven renewal, idle removal, re-install on
      re-observation, cap eviction, one apply per tick, pause/resume, start/dispose reset, and the
      fail-safe disable on persistent native or read-back failure.
- [x] The observation path allocates 0 B and the existing warm-dispatch/executor gates stay green;
      no cache IOCTL runs on a pump thread (proved by a fake writer that records its caller or by a
      pump-thread gate test).
- [x] The new configuration key parses, defaults to `false`, is rejected with wrong casing/type
      under the strict reader, and the README/example document it.
- [x] The Windows A/B records, per arm: process CPU (user/privileged), packets read, packets
      reinjected, packets bypassed, the crossing ratio, and the E2E correctness gates. The
      feature-on arm shows a reduced captured-packet count and a crossing ratio moving toward 1 with
      no loss/correctness regression; the exact series and the negative-result path are recorded in
      `benchmarks/results/2026-10-18-ndis-filter-table-bypass/README.md`. The measured CPU did not
      improve, so the default stays off (see "Measured outcome").
- [ ] Release build zero-warning, full suite green, `dotnet format WinForward.slnx --severity info
      --verify-no-changes --no-restore` empty output, and `jb inspectcode` zero `<Issue>`.
      **Partially:** the Release build is zero-warning, the 13 assemblies this task touches are green
      (1,387 tests), `dotnet format` is empty, and `jb inspectcode` has zero findings in this task's
      files. The solution-wide test suite and inspectcode remain red only because of the concurrent
      E2E campaign's in-flight/untracked files (2 flaky/failing E2E tests, 46 findings in
      `benchmarks/WinForward.E2E.Analysis/Release/**` and `tests/WinForward.E2E.Tests/**`). The
      operator approved committing only this task's files while that campaign settles.

## Measured outcome (2026-10-18, Windows)

Full evidence: `benchmarks/results/2026-10-18-ndis-filter-table-bypass/README.md`.

| Item | Result |
|---|---|
| W1 exports | all four present on `ndisapi.dll` 3.6.1 |
| W3/W4 tables | 1/64/128/256 rules accepted; read-back and reset verified |
| W2 PASS bypass | three on arms: 4 rules, ~0.88–0.90 M packets bypassed, client `failed=false` |
| Crossing ratio | 1.004–1.006 on (≈2.0 uncached) |
| Throughput | on arms 479.85–479.93 Mbps; off arms 346.98–469.74 Mbps |
| CPU | on arms 26.5–27.8 s/GB; best off arm 24.0 s/GB (the other two 30.2/34.7) |
| Driver filter cache side probe | did not help; raised on CPU and depressed the off arm |
| W6 soak | THRU 479.87 Mbps/0 loss, DNS 3,001/3,001, LOSS 9,001/9,001, 0 foreignConnection |

**Decision:** keep `KernelPassCache` default **false**. The mechanism meets the transition-count and
sustained-throughput goals and is safe (PASS-only, all soak gates green), but it does not reduce CPU
at this table size and workload; the sequential static-filter evaluation is the recorded hypothesis.
A default flip requires a CPU-positive measurement.



## Key Decisions

| Decision | Chosen | Why |
|---|---|---|
| Scope | B, per-flow pass cache | The measured traffic is a process-pass rule; static classes cannot cover it. |
| Protocol | TCP only in the MVP | TCP 5-tuple reuse is TIME_WAIT-protected; UDP port reuse is immediate, so UDP stays in user mode until it has its own safety story. |
| Action | PASS only | A stale DROP rule after a crash would blackhole traffic; a stale PASS rule is benign. |
| Rule adapter | `m_Adapter = 0` | The same flow can be observed on the host and forwarded paths; the tuple is the identity. |
| Config | Default off retained after the 2026-10-18 Windows measurement (operator-confirmed); opt-in for the transition/throughput benefit | The driver table is process-global and the 5-tuple reuse window is bounded; the measured CPU did not improve, so the default was not flipped. |


## Out of Scope

- Kernel DROP / REDIRECT / PASS_RDR / DROP_RDR rules, and any kernel block enforcement.
- UDP pass caching, `SelfTrafficRegistry` mirroring, non-flow static classes (separate follow-ups).
- Changing capture scope, adapter modes/tunnel flags, or introducing a WFP/WinDivert path.
- Per-packet filter updates from a pump thread.
- Any change to `benchmarks/WinForward.E2E/**` or the active E2E task tree.

## Open Technical Items — resolved 2026-10-18

The Windows program closed every item below; the raw data is in
`benchmarks/results/2026-10-18-ndis-filter-table-bypass/`.

1. **Exports:** all four present on the deployed `ndisapi.dll` 3.6.1 (`W1 export ...=True`).
2. **PASS bypass:** confirmed. Three on arms reported 4 rules and ≈0.88–0.90 M bypassed packets
   while the client completed with `failed=false`; the packet was not read into user mode.
3. **Table size:** 1/64/128/256 flow rules (2/128/256/512 records, up to 94,216 bytes) were all
   accepted; the 128-flow cap (256 records, 47 KB) is inside the measured envelope. No cap change
   was needed.
4. **Counters:** read-back works; the `bypassed` summary (>0) proves `PacketsIn + PacketsOut` advances,
   and `ResetPacketFilterTable` clears the installed set (`TableMismatch` on the next read).
5. **Order/fields:** the read-back re-encoding accepted every table in the ladder and in the A/B; no
   `TableMismatch` occurred in a product run.
6. **A/B series:** recorded in the evidence README. The crossing ratio dropped to ≈1.0 and sustained
   throughput reached the target, but the measured CPU did not improve (26.5–27.8 s/GB on vs 24.0
   s/GB for the best off arm). **The default stays off.**
7. **Driver `EnablePacketFilterCache` side probe:** negative; it raised the on arm's CPU and
   depressed the off arm's throughput. The product does not enable it. Revisit only with a
   cache-invalidation test and a CPU-positive measurement.

