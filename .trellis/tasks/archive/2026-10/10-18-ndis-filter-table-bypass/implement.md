# Implementation Plan: NDISAPI static filter table kernel bypass

Design: `design.md`. PRD: `prd.md`. Scope: per-flow TCP pass cache, PASS-only, opt-in.

Every phase ends with the commands in **Validation**; do not start the next phase on a red tree.
The task is not complete until the Windows go/no-go (W2) is decided and recorded.

## Phase 0 — Preconditions and safety rails

- [ ] Re-read `.trellis/spec/backend/windows-ndisapi.md`, `ndis-batched-capture.md`,
  `hot-path.md`, `allocation-gates.md`, `logging-guidelines.md`, and this task's `design.md`.
- [ ] Confirm the concurrent E2E task is not touched: no edits under `benchmarks/WinForward.E2E/**`
  or `.trellis/tasks/10-18-e2e-*`; Windows work waits for an idle slot (check the VM's processes
  and tmux session before any driver operation).
- [ ] Create the evidence directory `benchmarks/results/2026-10-18-ndis-filter-table-bypass/`
  only when the Windows phase starts (an empty dir is not evidence).

## Phase 1 — ABI mirrors, encoder, imports

Files: `src/WinForward.NdisApi/NdisStaticFilterAbi.cs` (new), `NdisApiAbi.cs` (partial imports +
layout call), `tests/WinForward.NdisApi.Tests/NdisStaticFilterAbiTests.cs` (new).

- [ ] Mirror the records from `design.md` §2.1 with `[StructLayout(LayoutKind.Explicit, Pack=1,
  Size=…)]`; use `fixed byte[]`/`fixed ulong[]` for the IPv6 address bytes and the 6-byte MACs.
- [ ] Add the constants and a `AssertManagedLayout()` that pins every size and offset in §2.1;
  call it from `NdisApiAbi.AssertManagedLayout()`.
- [ ] Add the four `[LibraryImport]` partials with `SetLastError = true` and StdCall.
- [ ] Encoder `WriteTable(Span<byte> destination, ReadOnlySpan<NdisTcpFlowRule> rules)`:
  header + two oriented entries per rule per §2.3; returns the byte count or false on a bad rule.
  Never encode zero rules; the caller uses `Reset`.
- [ ] Tests: exact sizes/offsets; table byte length `8 + 184 × 2N`; header values; IPv4 and IPv6
  address bytes (including a canonical IPv6), host-order ports, all-ones masks, PASS action,
  adapter 0, both direction flags, no data-link filter; an independent oracle (write expected
  bytes by hand) for one rule.
- [ ] Run the NdisApi tests and the format gate.

**Rollback point:** the new file is additive; revert the file and the two-line `NdisApiAbi.cs`
change if the layout cannot be pinned.

## Phase 2 — Driver seam and counters

Files: `src/WinForward.NdisApi/NdisKernelFilterTable.cs` (new), `NdisApiDriver.cs`,
`tests/WinForward.NdisApi.Tests/NdisKernelFilterTableTests.cs` (new).

- [ ] Add `NdisTcpFlowRule` (canonical validation helpers), `NdisKernelFilterStatus`,
  `NdisTrafficCounters`, `INdisKernelFilterTable` per `design.md` §3.
- [ ] Implement `Replace`, `ReadRulePacketCounts`, `Reset` on `NdisApiDriver` under `_controlGate`;
  `ArrayPool<byte>` + `fixed`; map `EntryPointNotFoundException`/`DllNotFoundException` to
  `Unavailable`; `Marshal.GetLastWin32Error()` to `Failed`; read-back verifies count, order,
  fields, and sums `PacketsIn + PacketsOut`.
- [ ] Add `_packetsRead`, `_sendCalls`, `_packetsReinjected` with `Interlocked`; wire the read path
  (successful drain count) and both send paths (batch chunk and single send). `GetTrafficCounters`
  returns the snapshot.
- [ ] Add a fake native seam for the four filter calls (same pattern as `INdisReadPacketCalls`) so
  the driver is testable without the DLL. Do not route the real calls through the existing read
  seam.
- [ ] Tests: replace encodes the expected bytes and size; zero rules is refused (and the native
  call is never made); read-back maps counters; table mismatch on count/field/action; reset;
  unavailable on a missing export; failed + native error; traffic counters; the control gate is
  entered exactly once per call (or a contention assertion if the existing tests already cover the
  gate shape).
- [ ] Run the NdisApi tests and the format gate.

**Rollback point:** the seam is additive; the only risk is the read-back verification being too
strict. If W4 shows the driver preserves order/fields, keep it; if not, the design's fallback
(field matching) is a follow-up, not a silent relaxation.

## Phase 3 — Packet pass kind and executor observer

Files: `src/WinForward.Runtime/FlowDispatcher.cs`, `FlowAttributionPipeline.cs`,
`Capture/NdisPacketActionExecutor.cs`; tests in Runtime.Flow / Runtime.Capture.

- [ ] Add `PacketPassKind` and the trailing `PassKind` field on `CapturedFlowPacket` (default
  `None`); keep every existing positional constructor source-compatible.
- [ ] Set the kinds at every call site listed in `design.md` §5.2, including the deferred
  attribution pass and the four pass origins that must not notify.
- [ ] Add `IKernelPassFlowObserver` and the executor constructor parameter; call it only for
  `PassKind.Flow`, before the buffer work. Ensure the redirect `NotRelevant` path leaves `None`.
- [ ] Tests: a recording observer sees exactly the inline and deferred policy passes, and none of
  self-traffic/non-flow/reverse/fallback; the executor's existing tests stay green.
- [ ] Add an exact 0 B gate for `PassAsync` with a `Flow` kind and a recording observer.
- [ ] Run Runtime.Flow + Runtime.Capture tests and the format gate.

**Rollback point:** the enum + field are small; if the attribution path proves ambiguous, make the
executor's hook opt-in per call site (the observer call can be moved to the dispatcher's Pass
branches) rather than inferring.

## Phase 4 — Cache

Files: `src/WinForward.Runtime/Capture/NdisKernelPassCache.cs`,
`src/WinForward.Runtime/Logging/KernelPassCacheLog.cs`, `RuntimeCounters.cs`;
`tests/WinForward.Runtime.Capture.Tests/NdisKernelPassCacheTests.cs`.

- [ ] Implement the state model, marker set, ring, worker tick, activity/lease, apply, fault
  escalation, pause/resume/start/dispose, summary, and counters from `design.md` §4.
- [ ] Use `TimeProvider` + `QuiescenceScope` like `IdleExpirySweeper`; the worker must be the only
  caller of the driver seam.
- [ ] Add the `kernel.passCache` / `.unavailable` / `.disabled` / `.fault` / `.reset` events with the
  `EventName`s; no tuple/address/port in any message.
- [ ] Tests with a fake writer + manual clock: install delay; one apply per tick under a burst;
  counter renewal; idle removal; re-arm on the next observation; eviction at the cap; ring/marker
  overflow; pause/resume; start reset and dispose reset; replace failure retry then disable; read
  failure skip then disable; counter reset; zero-denominator summary; 0 B observation gate; the
  worker is not a pump thread (fake writer records its thread id; assert it differs from the test's
  observer thread).
- [ ] Run Runtime.Capture tests + the allocation gates and the format gate.

**Rollback point:** the cache is a single new class; disabling the config leaves it unconstructed.
If the worker's ring lock shows packet-path contention in the Windows A/B, the fallback is a
per-pump observation queue into the setup executor (not a lock-free rewrite in this task).

## Phase 5 — Configuration and docs

Files: `ConfigurationModels.cs`, `README.md`, `appsettings.example.json`,
`tests/WinForward.Configuration.Tests`.

- [ ] Add `KernelPassCache` (`bool?`) to the DTO and `bool KernelPassCache = false` to
  `ValidatedConfiguration`; map it in `TryValidate` (no range check).
- [ ] README: key table row + a paragraph under the TCP settings (mechanism, opt-in default,
  exclusive driver table, TCP-only/2 s lease residual, no effect when off).
- [ ] Add `"KernelPassCache": false` to the shipped `appsettings.example.json`; its
  `AppSettingsExampleTests` run keeps the example valid under the strict reader.
- [ ] Tests: omitted → false; `true` parses; wrong case (`kernelPassCache`) fails under the strict
  reader; a `string` value fails.
- [ ] Run Configuration tests and the format gate.

## Phase 6 — Composition

Files: `src/WinForward.Cli/DurableCaptureBundle.cs`, `Program.cs`; any affected CLI tests.

- [ ] Pass the `NdisApiDriver` as the `INdisKernelFilterTable` into
  `DurableCaptureBundle.CreateAsync`; create the cache before the executor when enabled; pass it as
  `passFlowObserver`; start it after composition.
- [ ] `OnScopeInstalled`: empty scope → `Pause`, non-empty → `Resume`.
- [ ] Dispose the cache first in `DisposeCoreAsync` (driver still open) and make the final reset
  observable in the log.
- [ ] Feature-off path: no cache object, null observer, no driver table touched.
- [ ] Tests: the bundle fabricates with a fake writer; enabled creates the cache and wires the
  executor; disabled creates none; scope callbacks pause/resume; dispose resets once. (The bundle's
  internal ctor is already test-visible.)
- [ ] Run the full suite once here to catch wiring regressions early.

## Phase 7 — Full quality gates

- [ ] `dotnet build WinForward.slnx -c Release` → zero warnings.
- [ ] `dotnet test WinForward.slnx -c Release` → all green.
- [ ] `dotnet format WinForward.slnx --severity info --verify-no-changes --no-restore` → exit 0,
  empty output (it takes minutes; never pipe it in a way that hides the exit code).
- [ ] `jb inspectcode -f=Xml -e=HINT -o=/tmp/jb-inspectcode.xml WinForward.slnx` → parse the XML,
  zero `<Issue>` (the command exits 0 even with issues).
- [ ] Fix or narrowly suppress every finding with a repo-verifiable reason; no performance-hostile
  suggestions on the packet path.
- [ ] Re-read the final `prd.md` acceptance criteria and tick each one against a test or a recorded
  Windows result.

## Phase 8 — Windows go/no-go and measurement

**Gate:** the E2E campaign must be idle (no `WinForward`, `orchestrator`, `WinForward.E2E` client,
or `sing-box` from that task; tmux session idle). Do not run any step below while it is active.

- [x] Published a framework-dependent win-x64 build to `C:\wfprof\kpcache\fdd`, with `ndisapi.dll`,
  beside the pre-existing CPU-analysis tree; `C:\wfbench` was not touched (the concurrent campaign
  had redeployed it).
- [x] W1 exports → all four True on `ndisapi.dll` 3.6.1.
- [x] W2 PASS-bypass proof → passed: three on arms, 4 rules, ≈0.88–0.90 M bypassed, client
  `failed=false`; the negative branch did not apply.
- [x] W3 table-size ladder → 1/64/128/256 rules all accepted (up to 94,216-byte tables); no cap
  change needed.
- [x] W4 counter/order/reset probe → read-back accepted every table; reset cleared the set;
  `bypassed>0` proves the counters advance.
- [x] W5 THRU A/B → three on arms at 479.85–479.93 Mbps with crossing 1.004–1.006; off arms
  346.98–469.74 Mbps; CPU not lower (26.5–27.8 vs 24.0 s/GB for the best off arm). A driver
  `EnablePacketFilterCache` side probe was negative and disabled.
- [x] W6 correctness soak → THRU 479.87 Mbps/0 loss, DNS 3,001/3,001 answered, LOSS 9,001/9,001
  arrived, 0 foreignConnection; `run.failed=false`.
- [x] Wrote `benchmarks/results/2026-10-18-ndis-filter-table-bypass/README.md` with the raw data,
  commands, series table, and the verdict (default stays off; no CPU win).

**Result:** the mechanism is proven and safe; the default is **not** flipped. The original negative
branch (PASS still queues to user mode) did not apply; the CPU caveat is recorded instead.


**Rollback:** flip `KernelPassCache` to false, restart, confirm the table is cleared (the final
`kernel.passCache.reset` line, reason `shutdown`). If W2 failed, remove the runtime wiring (Phases 3, 4, 6 and the
config), and keep the ABI work only if the record says it is a tested, unused seam.

## Phase 9 — Spec and wrap-up

- [x] Added `.trellis/spec/backend/ndis-kernel-pass-cache.md` (PASS-only ownership/crash safety,
  driver-global table and exclusive claim, lease/counter lifecycle, 5-tuple reuse window,
  `PacketPassKind` eligibility table, metric definition, measured Windows cost, open items). Linked
  from `windows-ndisapi.md`'s family intro.
- [x] `RuntimeCounters` carries the eight `kernelPassCache.*` keys; the heartbeat surfaces them.
- [x] Re-ran the gates: build 0 warnings, the task's 13 assemblies green (1,387 tests), format
  empty, inspectcode zero in our files. The solution-wide test/inspectcode remain red only from the
  concurrent E2E campaign's in-flight/untracked files; the operator approved committing only this
  task's files.
- [x] Commit per Phase 3.4: the implementation/tests/docs/spec/evidence; the E2E campaign's files
  are excluded.


## Validation commands (copy)

```bash
dotnet build WinForward.slnx -c Release
dotnet test WinForward.slnx -c Release
dotnet format WinForward.slnx --severity info --verify-no-changes --no-restore
jb inspectcode -f=Xml -e=HINT -o=/tmp/jb-inspectcode.xml WinForward.slnx
```

Targeted loops while iterating:

```bash
dotnet test tests/WinForward.NdisApi.Tests/WinForward.NdisApi.Tests.csproj -c Release
dotnet test tests/WinForward.Runtime.Capture.Tests/WinForward.Runtime.Capture.Tests.csproj -c Release
dotnet test tests/WinForward.Runtime.Flow.Tests/WinForward.Runtime.Flow.Tests.csproj -c Release
dotnet test tests/WinForward.Configuration.Tests/WinForward.Configuration.Tests.csproj -c Release
dotnet test tests/WinForward.Performance.Tests/WinForward.Performance.Tests.csproj -c Release
```

## Risky files / points

| File | Risk | Mitigation |
|---|---|---|
| `NdisApiAbi.cs` / new ABI file | Layout mistake survives until a real IOCTL | Pin every size/offset; encoder oracle; W1/W4 on hardware. |
| `NdisApiDriver.cs` | A wrong gate or a zero-entry call | Control gate; zero-entry refusal test; read-back verification. |
| `FlowDispatcher.cs` / `CapturedFlowPacket` | Pass-kind mis-set could cache a proxied flow | Eligibility table tests for all five origins; never infer from `PassAsync`. |
| `NdisPacketActionExecutor.cs` | Per-packet cost/regression | Enum + null check; 0 B gate; existing executor tests. |
| `NdisKernelPassCache.cs` | Packet-path lock / lifetime bug | Ring copy outside lock; 0 B gate; fake-writer thread assertion; bounded escalation. |
| `DurableCaptureBundle.cs` | Teardown order (reset after driver close) | Dispose the cache first; test with a fake writer. |
| README/config | Strict JSON break | Parse/default tests; wrong-case rejection. |

## Review checkpoints

1. After Phase 1: show the layout/encoder tests and the table byte dump.
2. After Phase 2: show the seam tests and the failure mapping.
3. After Phase 6: full suite + build + format + inspectcode green.
4. Before Windows: the E2E task is idle and the deployment directory is separate.
5. After Windows: the A/B table, the W2 verdict, and the task-record update; then archive.
