# Windows NDISAPI Interop Contracts

> How WinForward talks to the WinpkFilter NDISAPI driver: adapter identity correlation, request-handle rules, native-call gating, DLL resolution, and the batched capture ABI. Split 2026-08-29 from the former monolithic NDISAPI file; sibling contracts: [tcp-local-redirect.md](./tcp-local-redirect.md), [udp-relay.md](./udp-relay.md), [traffic-policy-lifecycle.md](./traffic-policy-lifecycle.md).

---

## Adapter Identity Contract

> How WinForward correlates NDISAPI runtime adapters with stable Windows adapter identities. Verified on a real Windows 11 host with WinpkFilter 3.6.2.1 during the 08-07-winforward-proxy check phase.

### 1. Scope / Trigger

- Trigger: any code that enumerates adapters (`adapters` CLI command), resolves `adapterId`/`adapterName` config selectors, or maps an `INTERMEDIATE_BUFFER` adapter handle back to a configured adapter.
- This is an infra integration contract: NDISAPI (kernel driver handles) and Windows IP Helper (`NetworkInterface`) are two separate enumeration planes that must be joined correctly.

### 2. Signatures

- `WindowsAdapterInventory(Func<IReadOnlyList<(string InternalName, nint Handle, byte[] Mac, ushort Mtu)>> ndisAdapters, Func<IReadOnlyList<IPAdapterInfo>>? ipAdapters = null)` — the second provider is injectable so correlation is unit-testable without Windows hardware.
- `IReadOnlyList<WindowsAdapter> GetCurrentAdapters()` — `WindowsAdapter(StableId, FriendlyName, InternalName, RuntimeHandle, Generation)`.
- `IPAdapterInfo(string Id, string Name, byte[] Mac)` — projection of `NetworkInterface.Id` / `.Name` / `.GetPhysicalAddress()`.

### 3. Contracts

- NDISAPI `GetTcpipBoundAdaptersInfo` internal name is typically `\DEVICE\{GUID}`; stripping the `\DEVICE\` prefix yields the adapter's NetCfgInstanceId, which equals `NetworkInterface.Id` (`{GUID}`, same casing on observed systems — compare case-insensitively and brace-insensitively via `Guid.TryParse` normalization).
- **Correlation is GUID-primary.** MAC is only a sanity-check fallback when the internal name is not a GUID.
- `RuntimeHandle` is process-lifetime state: never persist it, never print it as identity, rebuild it on adapter-list change.
- Ambiguity rule: a correlation key must match **exactly one** IP Helper adapter; zero or multiple matches fall back (see matrix).
- IP Helper owner-PID IPv6 `ScopeId` fields are host-order DWORDs and must be preserved when constructing `IPAddress`; only owner-row port fields use network byte order and require conversion.
- IP Helper table reads cross-check driver-reported bounds before any row dereference (fixed 2026-09-08): `IPHelperTables.ValidateRowCount` throws fail-closed when `4 + (long)rowCount * rowSize > bytesWritten` or `rowCount < 0` — mirroring the NDISAPI seam's bounded discipline; attribution callers already tolerate failure, so an inconsistent table becomes "no attribution", never a partially-dereferenced table or a wrong PID.

### 4. Validation & Error Matrix

| Condition | Result |
|---|---|
| GUID extracted from internal name, exactly one `NetworkInterface.Id` match | correlated (`StableId` = `Id`, `FriendlyName` = `Name`) |
| GUID match count 0 or >1 | try MAC fallback |
| MAC fallback matches exactly one | correlated |
| MAC fallback matches 0 or >1 | uncorrelated: `StableId`/`FriendlyName` fall back to the internal name (never guess) |
| Internal name not a GUID and MAC is all-zero (hidden/virtual adapters report `000000000000`) | uncorrelated fallback |

### 5. Good/Base/Bad Cases

- Good: `\DEVICE\{DD8CD9A1-...}` ↔ `Id = {DD8CD9A1-...}` → friendly name `Ethernet`. Verified for physical and hidden `Local Area Connection* N` adapters alike (the hidden ones have no MAC at all and only resolve via GUID).
- Base: adapter whose internal name is not a GUID → MAC fallback resolves it when the MAC is unique.
- Bad: MAC-only correlation. NDIS filter drivers (WFP Native/802.3 MAC Layer LWF, WinpkFilter's own NDIS LWF, Npcap, QoS Packet Scheduler) each clone the physical MAC, so one MAC appears on ~6 `NetworkInterface` entries and the ambiguity check always fails. **MAC-only correlation never works on a real Windows host.**

### 6. Tests Required

- Unit (hardware-independent, via injected `IPAdapterInfo` provider): GUID correlation success; brace/case-insensitive GUID compare; MAC fallback when internal name is not a GUID; bare-GUID internal name; duplicate-MAC interfaces must NOT corrupt GUID correlation; zero-MAC and ambiguous adapters fall back to the internal name. IP Helper projection tests preserve a nonzero IPv6 scope ID while converting network-order ports.
- Windows smoke: `WinForward.exe adapters` prints stable GUID + friendly name + internal name for every MSTCP-bound adapter (exit 0); without `ndisapi.dll` it exits 1 with an actionable diagnostic.

### 7. Wrong vs Correct

#### Wrong

```csharp
// MAC-only correlation: always ambiguous once any NDIS filter driver
// (including WinpkFilter itself) clones the MAC onto filter interfaces.
var matches = ipAdapters.Where(ip => ip.Mac.SequenceEqual(adapter.Mac)).Take(2).ToArray();
var matched = matches.Length == 1 ? matches[0] : null; // always null on real hosts
```

#### Correct

```csharp
// GUID primary (internal name GUID == NetworkInterface.Id), MAC as sanity fallback.
if (TryExtractGuid(adapter.InternalName, out var guid))
{
    var guidMatches = ipAdapters.Where(ip => TryExtractGuid(ip.Id, out var id) && EqualsOrdinalIgnoreCase(id, guid)).ToArray();
    if (guidMatches.Length == 1) return guidMatches[0];
}
// ... then MAC fallback, then internal-name fallback. See AdapterIdentity.cs.
```

**Related**: `.trellis/tasks/archive/2026-08/08-07-winforward-proxy/research/winpkfilter-ndisapi-design-constraints.md` (direction semantics, handle lifetime, IP Helper correlation evidence); `src/WinForward.Windows/AdapterIdentity.cs` (reference implementation).

---

## Adapter Handles: Enumeration vs Captured (hardware-verified 2026-08-07)

> **Warning**: `GetTcpipBoundAdaptersInfo` returns per-adapter handles that are the ONLY valid values for request-level `hAdapterHandle` fields. The `INTERMEDIATE_BUFFER.m_hAdapter` seen in captured packets is a DIFFERENT kernel pointer (observed: list handle `0xFFFFAD8A2B30B010` vs captured `0xFFFFAD8A2B30B2D0` on the same adapter). Passing the captured `m_hAdapter` as the request handle makes `SendPacketToAdapter`/`SendPacketToMstcp` fail with `ERROR_INVALID_PARAMETER` (87) on every packet.

- `NdisCapturePump` must stamp captured packets with the pump's enumeration handle (`NdisCapture.cs`), never with `NdisPacketBuffer.CapturedAdapterHandle`.
- A captured packet carries two distinct flag values: `DeviceFlags` selects MSTCP-relative direction, while `INTERMEDIATE_BUFFER.m_Flags` is NDIS packet metadata. Preserve both through the managed capture record and ordinary pass reinjection; a fresh synthetic frame intentionally starts with metadata flags zero.
- **Native-call gate topology (superseded 2026-08-28, task 08-28-udp-loss-design-flaws D3)**: `NdisApiDriver` splits serialization into a **control gate** (open/close/enumeration/mode snapshot+set+restore — cold path) and a **per-adapter-handle gate map** (`NdisAdapterGateMap` in `NdisNativeCallGate.cs`; since 2026-08-30, task 08-30-batched-ioctls D4, a `ConcurrentDictionary.GetOrAdd` — no per-call lock; the map only grows, bounded by the adapter count, and a racing `GetOrAdd` may construct a discarded gate at most once per handle, which is harmless for these lazily-registered passive objects) used by `TryReadPackets`, `SendPacketToMstcp`, `SendPacketToAdapter`, keyed by the enumeration handle the request carries. Within one adapter handle every native call stays serialized (preserves per-adapter read/reinject ordering and the historical OVERLAPPED concern — each request struct is method-local); **across adapters calls proceed in parallel**, so a slow IOCTL on adapter A can no longer stall adapter B's pump reads. The queue query + batch read pair keeps sharing ONE lease of the adapter's gate. Lock order is fixed: the map is never held across `gate.Enter()` (leaf-free by construction in the concurrent map), and the control gate is never nested inside an adapter gate or vice versa. This replaces the earlier "serialize every operation on one driver instance" contract: that single-Monitor design coupled all adapters through one lock and was a confirmed driver-queue-overflow (silent loss) amplifier under multi-adapter/high-pps load. Gate contention telemetry (`MaxConcurrentCalls`) is preserved per gate. Rollback shape if driver-level coupling ever shows up on hardware: a single send-gate + per-adapter read-gates.
- This matches the official samples: `ETH_M_REQUEST.hAdapterHandle` is set once from the adapter list and reused for read/write requests.
- All NDISAPI `[LibraryImport]` declarations use `SetLastError = true`; send-path exceptions must include `Marshal.GetLastWin32Error()` — driver-side rejections are otherwise undiagnosable.
- Diagnostics context worth logging on send failure: native error, frame length, device flags, adapter handle.

**Symptom / Cause / Fix** (recorded as a verified bug class):

- Symptom: capture works, tunnel mode applies, but every pass reinjection fails with native error 87 and the runtime shuts down fail-closed.
- Cause: request built with the captured buffer's `m_hAdapter` instead of the enumeration handle.
- Fix: `NdisCapturedPacket(buffer, _adapterHandle, buffer.DeviceFlags)` in the pump.
- Verification: scratch harness `sendtest` (read -> reinject captured buffer with enumeration handle: 97/97 OK, both directions). Product re-verified: 100/100 ICMP pass-through with 0% loss and no duplicates.

**Performance note**: the pump now reads in batches (`ReadPackets`, batch capacity 32, one gate lease per batch; wired 2026-08-27 — see "Batched capture reads and buffer pooling" below). The 1 ms poll delay remains only on empty batches; since 2026-08-28 the capture run is wrapped in `WinForward.Windows.HighResolutionTimerScope` (winmm `timeBeginPeriod(1)`, fail-open with a one-shot warn), so the 1 ms delay resolves to ~1–2 ms instead of the ~15.6 ms default timer tick; `SetPacketEvent` event-driven reads are the optional next upgrade if empty-to-first-packet latency still matters.

---

## Adapter list change refresh — layered capture generations (wired 2026-09-07, task 09-07-adapter-list-refresh; startup stale-handle recovery added 2026-09-11, task 09-11)

> Windows rebuilds the NDISRD TCP/IP-bound adapter list on plug/unplug, enable/disable,
> standby/resume, and Wi-Fi Direct virtual-adapter churn (`本地连接* N`). Every enumeration
> handle is a runtime pointer into that list and goes stale at once; adapter-associated IOCTLs
> then fail with `ERROR_INVALID_PARAMETER` (87) — production ground truth 2026-09-07: four
> adapters (incl. the physical NIC) degraded together. 87 stays in the permanent-error class
> (`IsTransientReadError` is NOT extended); the fix is recovery, not retry: the official
> `SetAdapterListChangeEvent` + re-enumeration guidance. Since 2026-09-11 the fourth 87 surface
> — a rebuild racing the generation's mode snapshot/apply phase, before the pumps ever start —
> is also recovered in the runner instead of exiting the process. Unit-locked end to end; hardware
> smoke passed 2026-09-08 (task S7, evidence in the task's `smoke-evidence.md`): 87 degrade →
> refresh in 77 ms, session survival across the cage, interception resumed; **R-2 auto-reset
> event mode verified against the real driver**. R-1 final-shutdown ordering verified per
> refresh (generation teardown ran cleanly on hardware twice); the terminal Ctrl+C exit could
> not be exercised from a headless remote session — recorded as an environment limitation,
> no residual risk identified (teardown injections use the same code path).

### 1. Scope / Trigger

- Trigger: any change to capture startup/teardown wiring (`Program.cs`,
  `DurableCaptureBundle`), the refresh loop (`LayeredCaptureRunner`), per-generation
  composition (`NdisCaptureGenerationFactory`), adapter enumeration plumbing
  (`AdapterEnumeration.cs`), or the list-change watcher (`AdapterListWatcher.cs`).

### 2. Signatures

- `NdisApiDriver.SetAdapterListChangeEvent(nint win32Event)` — control-gated registration of a
  caller-owned Win32 event; `nint.Zero` releases (official NULL semantics). Native FALSE throws
  `Win32Exception` — registration failure is fatal at startup by design.
- `IAdapterListChangeSource { bool WaitOne(CancellationToken); }` — true = bound list rebuilt
  (all enumeration handles stale), false = cancelled/disposed. Native impl
  `NdisAdapterListWatcher` registers an **auto-reset** `EventWaitHandle` (bursts coalesce; the
  ground truth is the re-enumeration, never the signal count) and blocks via
  `WaitHandle.WaitAny` (no spin).
- `CaptureAdapterScopeResolver.ResolveForRefresh(adapters, policy, out warnings)` — refresh
  semantics twin of `TryResolve`; the startup overload stays byte-identical and fatal.
- `IUdpAdapterTargetSource { UdpAdapterTarget? Host; UdpAdapterTarget? Resolve(stableId); }`
  with the mutable `UdpAdapterTargetSource` (immutable snapshot record swapped via
  `Volatile.Write`; zero-allocation reads). `UdpResponseReinjector` consults it per response.
- `LayeredCaptureRunner(enumerationProvider, generationFactory, changeSource, policy, logger,
  disposeDurableAsync, onScopeInstalled)` + `SignalDegraded(adapter, nativeError)`;
  `ICaptureGeneration`/`ICaptureGenerationFactory` are the test seam, `NdisCaptureGenerationFactory`
  (windows-gated) composes `NdisAdapterModeController` + `MultiAdapterCaptureLoop` +
  `TransactionalCaptureRuntime` per generation.
- `ICaptureGeneration.ReachedPumpRun` / `TransactionalCaptureRuntime.ReachedPumpRun` (task
  09-11) — phase latch set under the runtime gate immediately before the capture loop run
  starts; read after the generation task completes it is race-free by await ordering. This is
  the classification input that keeps NDIS error-code knowledge out of the runtime itself.
- `LayeredCaptureRunner.MaxConsecutiveStartupRecoveries = 3` (internal const, task 09-11) —
  consecutive recoverable startup faults beyond this count rethrow the original fault
  fail-closed (genuine-defect guard; a settling adapter-list churn recovers within two or three
  rebuilds because every retry re-enumerates).
- `DurableCaptureBundle` (Cli) — built once per run: redirect table, TCP/UDP coordinators,
  dispatcher/executor chain, idle sweeper, refreshable UDP target source; `DisposeAsync`
  single-flight, order sweeper → udp → tcp.

### 3. Contracts

- **Two lifetimes**: durable components survive a refresh (sessions recover via retransmission;
  relays stay open); only the generation layer (scope, mode controller, pump set,
  per-generation runtime) is rebuilt. No dynamic pump membership — list rebuilds invalidate all
  handles at once, so whole-generation replacement is the correct granularity.
- **Refresh semantics are deliberately non-fatal** (PRD R4, the documented bounded exemption
  from startup-fatal scope resolution): gone selector → warn + rule contributes nothing;
  ambiguous selector → warn + skip the addition; id/name disagreement → warn + rule skipped.
  Widening to all MSTCP-bound adapters requires an unconstrained rule or a zero-warning empty
  selector set — a fully-constrained policy whose adapters ALL disappeared must NOT widen.
- **No-op skip before stopping the generation**: a pending refresh demand re-enumerates and
  diffs `(StableId → handle, MAC, MTU)` against the running generation; identical in-scope maps
  log `adapter.refresh` no-op and never touch pumps/modes (no mode flap, AC4/AC5).
- **Storm guard**: minimum 1 s between executed rebuilds (TimeProvider-injectable); signals
  during a rebuild or inside the window coalesce into one pending rebuild.
- **87 as defense-in-depth trigger**: a pump degrading with 87 enters the pending-refresh
  channel (`SignalDegraded`); the enumeration diff then decides rebuild vs honest no-change
  log. 87 on a present adapter remains a genuine defect signal — never add it to the transient
  table.
- **Startup stale-handle recovery (task 09-11)**: a generation fault is classified recoverable
  iff it is `Win32Exception` with `NativeErrorCode == 87` AND the generation's `ReachedPumpRun`
  latch is still false (during startup the only adapter-associated native calls are the mode
  snapshot/apply, so that signature means the list rebuilt between the runner's enumeration and
  the snapshot). Classification lives in the runner (`IsRecoverableStartupFault`), next to
  `AdapterListRebuiltNativeError`. Absorption happens at exactly the two points where a
  generation task is awaited: exit observation (fault won — release the dead generation exactly
  like a refresh stop, arm a forced demand) and `StopGenerationAsync`'s fault-capture branch
  (demand won — absorb with no extra signal; the in-flight demand installs the replacement).
  Every other escape — non-87 startup faults, 87 after the pumps started, over-cap streaks —
  keeps the fail-closed rethrow. The streak resets wherever a generation with
  `ReachedPumpRun == true` completes.
- **Forced rebuild honesty (task 09-11)**: a recovery-armed demand consumes `_forceRebuild` at
  `ProcessRefreshDemandAsync` entry and bypasses the empty-diff no-op skip — the current
  generation is dead, so an empty diff must still install a replacement (pointer-reuse makes
  empty diffs genuinely possible after a rebuild). `adapter.refresh` records `forced=true`
  (never `noop=true`) on forced installs; the force flag clears on the next install, so a live
  generation never loses its no-op protection. Telemetry per absorption: structured warn
  `generation.startup-fault` with `nativeError` + `attempt=k/{limit}`; a final error-level
  variant precedes the fail-closed exit when the cap is exceeded.
- **Shutdown order (R-1 deviation, documented)**: generation cleanup (pump stop + best-effort
  mode restore with old handles) runs BEFORE durable disposal, which is the reverse of the
  pre-2026-09-07 capture-loop wrapper in one respect: coordinators now dispose after mode
  restore. Neither coordinator's `DisposeAsync` invokes the reinjector and NDISRD injection is
  capture-mode-independent, so the deviation is believed benign — smoke must confirm teardown
  injections still land.
- **Mode restore across refresh**: old-handle restore failures (87) are swallowed best-effort —
  the driver's rebuilt context starts in default mode; unchanged handles accept a one-window
  tunnel-off→on flap (no-op skip avoids the common case).
- Old-handle `MarkAdapterDegradedAsync` restore and `adapter.degraded` logging live INSIDE
  `NdisCaptureGenerationFactory`'s degrade callback; outer wiring only forwards to
  `SignalDegraded` — never restore twice.
- UDP reinjection targets resolve against the current snapshot per response: gone adapters drop
  fail-closed with the existing rate-limited warn (host flows fall back to the host target;
  missing host target is itself fail-closed — the seam legitimately has no target to invent).

### 4. Validation & Error Matrix

| Condition | Required result |
|---|---|
| Driver signals list change | watcher `WaitOne` returns true; runner re-enumerates and diffs |
| Enumeration identical to current generation | `adapter.refresh` no-op log; pumps/modes untouched |
| Scope adapter disappeared | warn + dropped from scope; run continues on remaining |
| New adapter + unconstrained rule | adopted into scope and intercepted after refresh |
| New adapter + fully constrained policy | NOT adopted |
| Ambiguous name selector at refresh | warn + addition skipped, non-fatal |
| Pump degrades with 87, enumeration unchanged | honest no-change log; no rebuild loop |
| Generation startup faults with 87 before its pump run (task 09-11) | absorbed: warn `generation.startup-fault attempt=k/3`, dead generation released, forced storm-guarded refresh installs a replacement even on an empty diff (`forced=true`) |
| > 3 consecutive recoverable startup faults | final error-level `generation.startup-fault`, original fault rethrown fail-closed |
| Startup fault with native error ≠ 87, or 87 after `ReachedPumpRun` | fail-closed rethrow; no warn event, no recovery |
| Refresh demand races a startup-87 fault (demand wins the await) | demand processing absorbs the classified fault; ends with a live generation |
| Generation reaches its pump run, later isolated startup-87 | streak was reset; recovery starts from attempt 1/3 |
| Signals faster than the storm window | coalesce into one rebuild |
| gen0 scope resolution failure | fatal startup error (unchanged `TryResolve` surface) |
| Empty scope at refresh | pause (no pumps), await next signal; startup empty stays fatal |
| Watcher registration native FALSE | `Win32Exception` → startup fatal (feature unusable) |

### 5. Tests Required

- `LayeredCaptureRunnerTests` / `LayeredCaptureRunnerRefreshTests` (AC1–AC5: rebuild on fresh
  handles without disposing durable; drop-with-warn; adopt via unconstrained rule; 87 no-op
  re-check; storm coalescing + no-op skip; generation-before-durable disposal ordering on
  natural end, fault, and user cancel). Since task 09-11 the refresh suite also locks the
  startup recovery ACs: recovery on fresh handles; forced install on unchanged enumeration
  (`forced=true`, no `noop`); cap exceeded → original `Win32Exception` survives to `RunAsync`
  with the warn×3+error event sequence and `attempt=1/3..4/3`; non-87 startup fault and
  post-pump 87 propagate with zero `generation.startup-fault` events; demand-races-fault ends
  with a live generation; streak reset proven by two recoveries both logging `attempt=1/3`.
- `CaptureLifecycleTests` (task 09-11): `TransactionalCaptureRuntime.ReachedPumpRun` stays
  false when `SnapshotAsync`/`ApplyCaptureModeAsync` throws during start, latches true once
  the capture loop run started (fakes carry `failOnSnapshot`/`failOnApply` knobs).
- `AdapterScopeRefreshTests` (R4 semantics matrix + ordering + zero-warning equivalence with
  `TryResolve`); `AdapterEnumerationDiffTests` (handle/MAC/MTU diff).
- `AdapterListWatcherTests` (signal/cancel/dispose semantics via the OS-agnostic wait core);
  `UdpAdapterTargetSourceTests` (snapshot swap without reconstruction, fail-closed miss,
  host-null drop, frozen-snapshot immunity).
- Windows hardware smoke (AC6, passed 2026-09-08): disable/enable a NIC under a live run →
  `adapter.refresh` logs the transition and interception resumes without a process restart.
  Evidence: task `09-07-adapter-list-refresh/smoke-evidence.md` (87→refresh 77 ms; hidden
  Wi-Fi Direct adapter membership change does NOT rebuild the bound list — zero events,
  pump rides transient retries).

---

## Batched reinjection sends (wired 2026-08-30, task 08-30-batched-ioctls)

### 1. Scope / Trigger

- Trigger: any change to the executor Pass path, the pump batch-end callback, or the
  driver's batched send surface.
- Infra contract: which reinjections may be deferred into a batch, the ordering
  guarantees while deferring, and the observed all-or-nothing failure semantics.

### 2. Signatures

- `NdisApiDriver.SendPacketsToMstcp/SendPacketsToAdapter(nint adapterHandle, NdisPacketBuffer[] buffers, int count)` — batched native send; one adapter-gate lease across all chunks.
- `NdisPacketActionExecutor.FlushPendingPasses(nint adapterHandle)` — flush the executor's pending Pass lanes for that adapter.
- `NdisCapturePump` optional `onBatchCompleted` callback — invoked after every batch's slot loop (empty batches included) and from the run loop's `finally` before `ReleaseBatchBuffers()`.
- Telemetry: `NdisApiDriver.BatchedSendFlushCount` / `BatchedSendPacketCount` (average batch size = packet/flush — the syscall-amortization evidence).

### 3. Contracts

- **PacketsSuccess is unobservable on the send path (export-verified 2026-08-30)**: the send IOCTLs pass `lpOutBuffer=NULL, nOutBufferSize=0` and both IOCTLs are `METHOD_BUFFERED` (verified against the pinned ndisapi source `417b8734` and the local DLL's disassembly — evidence in the task's `research/abi-packets-success.md`). Batched send is therefore **all-or-nothing**: failure throws the same `Win32Exception` (native error, direction, chunk range, adapter) as the single-packet path. Never build resend logic on `PacketsSuccess` for sends.
- **Batching scope**: only executor Pass dispositions are deferred. Per-(adapterHandle, direction) lanes (fixed capacity, linear scan) accumulate in-place capture buffers and rented pool buffers; `PassAsync` appends instead of sending. TCP redirect SYN/RST injection and UDP response reinjection stay immediate single sends (they share only the adapter gates).
- **Flush points**: once per pump iteration via `onBatchCompleted` (before the next `TryReadPackets`) and on run-loop exit (before batch-buffer release) — teardown never drops pending frames. `FlushPendingPasses` is adapter-scoped because one executor serves every pump; pump A must never flush pump B's mid-iteration frames.
- **Serialization rests on the pump's strictly-ordered await chain, not thread identity** — `ConfigureAwait(false)` may resume on any thread-pool thread, but exactly one pump chain calls into a given adapter's lane at a time. A `PassAsync` caller outside a pump chain would break this; the audit in the task's `research/passasync-call-site-audit.md` pins the current callers.
- **Ordering**: same (adapter, direction) Pass frames are reinjected in capture order (append order == slot order). Cross-lane reordering is not a contract: flows never mix dispositions, so per-flow order is untouched. Pass sends may shift relative to interleaved cold injections of other flows within one iteration.
- **Lane lifecycle is generation-scoped (fixed 2026-09-08, task 09-08-p0-correctness R1)**: lanes are retired between generations — `DurableCaptureBundle.OnScopeInstalled` (the runner's scope-installed callback) composes `UpdateUdpTargets` + `Executor.RetireLanesExcept(<scope handles>)` strictly after the old generation's run task is awaited and before the next install; empty scope retires all lanes. Retirement drains defensively (rented buffers returned exactly once; a non-empty lane at retire time is a contract breach and warns rate-limited). Precondition: no pump for a retired handle may still be running; driver handle-value reuse is safe (same key ⇒ same accumulation semantics).
- **Lane overflow** (>4 adapters × 2 directions) degrades those Passes to immediate single sends — defensive; production shapes are single-adapter — and is **observable since 2026-09-08**: `ImmediateSendLaneOverflowCount` (Interlocked) increments and a rate-limited (5 s) warn fires; capacity degradation is never silent.
- **Exactly-once pool return**: rented buffers return in the flush's `finally` (including the throwing path); in-place capture buffers are never pool-returned.

### 4. Validation & Error Matrix

| Condition | Required result |
|---|---|
| Pass disposition inside pump iteration | appended to its lane; no native call until flush |
| Flush with N pending in one lane | one batched send for the lane; rented buffers returned exactly once |
| Batched send returns FALSE | `Win32Exception` (all-or-nothing); rented buffers still returned |
| Count > 126 | chunked inside one gate lease |
| Run loop exits (cancel/stop/fault) | pending lanes flushed from `finally` before batch-buffer release |
| Lane capacity exceeded | overflow Pass sends immediately as single send |
| Non-batched reinjector callers | unchanged single-packet path |

### 5. Tests Required

- `NdisPacketActionExecutorBatchingTests`: same-lane append order; two keys flush independently; mixed materialized/in-place frames; exactly-once pool return on success and on batch failure; empty flush no-op; lane-overflow fallback.
- `NdisApiBatchedSendAbiTests`: request-slot layout/chunk-budget derivation.
- `BatchedPassReinjectionE2eTests`: N frames → one batched call per direction per iteration (measured 96 frames → 6 calls = 16× reduction vs single sends); strictly increasing per-direction markers (order); flush-on-exit with a faulting handler slot.

### 6. Wrong vs Correct

```csharp
// Wrong: trusting PacketsSuccess on the send path to resend a "suffix" —
// the field never comes back (lpOutBuffer=NULL, METHOD_BUFFERED).
if (request->PacketsSuccess < count) ResendSuffix(...); // dead code at best

// Correct: fail the batch wholesale, same diagnostics as the single path.
if (result == 0) throw new Win32Exception(error, $"...packets {offset}..{end} of {count}...");
```

---

## Native DLL resolution (fixed 2026-08-12)

- `NdisApiNative` (in `NdisApiAbi.cs`) registers a `NativeLibrary.SetDllImportResolver` in its static constructor that loads `ndisapi.dll` only from `AppContext.BaseDirectory` — matching the README claim; there is no PATH/current-directory search. Other library names return zero and use default resolution. AOT-safe (no reflection).

---

## Batched capture reads and buffer pooling (wired 2026-08-27)

### 1. Scope / Trigger

- Trigger: any change to the capture pump read loop, the frame copy path into `PacketLease`, TCP in-place rewrite, or `NdisPacketBuffer` allocation on the injection path.
- Infra contract: the NDISAPI batched ABI shape, the gate lease granularity, and the pooled-frame lifetime boundary across `WinForward.NdisApi` → `WinForward.Core` → `WinForward.Runtime`.

### 2. Signatures

- `NdisApiDriver.TryReadPackets(nint adapterHandle, NdisPacketBuffer[] buffers) -> int` — queue query + batched read merged into a single `NdisNativeCallGate` lease; returns the driver-filled success count (0 = empty queue).
- Batched send since 2026-08-30 (task 08-30-batched-ioctls): `NdisApiDriver.SendPacketsToMstcp/SendPacketsToAdapter(nint, NdisPacketBuffer[], int)` submit up to `MaxPacketsPerSendRequest` (126) packets per `EthernetMultiRequest`, chunking larger counts inside one adapter-gate lease. See "Batched reinjection sends" below for the executor-side batching contract.
- `PacketLease(ReadOnlyMemory<byte> frame, Action<ReadOnlyMemory<byte>>? onCompleted)` — completion-callback constructor; the plain constructor keeps null semantics.
- `NdisPacketBufferPool` (process-wide `Shared`, capacity 256) with `Rent()`/`Return()`; `NdisPacketBuffer` carries an owner-pool state machine (private ctor → Dispose frees; pool-rented → Dispose returns, double-Dispose no-op).

### 3. Contracts

- **ETH_M_REQUEST ABI** (x64, Pack=1): `hAdapterHandle(8) + dwPacketsNumber(4,in) + dwPacketsSuccess(4,out) + NDISRD_ETH_Packet[N]` (one `INTERMEDIATE_BUFFER*` each, 8 bytes) = 16 + 8N total. Caller fills handle/count/buffer pointers; the driver fills `dwPacketsSuccess` and each buffer's contents on success. Managed shape is `EthernetMultiRequest` (`NdisApiAbi.cs`, 24-byte fixed head, `FirstBuffer` aliases `EthPacket[0]`) with `AssertManagedX64Layout` size/offset assertions (24; 0/8/12/16). Exports verified against the pinned commit 417b8734 (`ndisapi.vs2012/ndisapi.def`, `include/ndisapi.h:300-302`): `ReadPackets`, `SendPacketsToMstcp`, `SendPacketsToAdapter` (all `BOOL __stdcall (HANDLE, PETH_M_REQUEST)`).
- **Frame lifetime boundary (the load-bearing rule)**: `FlowDispatcher.CompleteAsync` calls `lease.TryComplete(disposition)` BEFORE `execute()`, so every frame consumer (pass injection copy, UDP payload parse, clientMac slice, TCP in-place rewrite, RST template) runs AFTER lease completion but must stay INSIDE `CapturePacketProcessor.ProcessAsync`'s await window. Therefore the `ArrayPool<byte>.Shared.Return` lives in `ProcessAsync`'s outer `finally` — never on the lease completion callback. Any new code that lets a `Frame`/`Memory` slice escape the `ProcessAsync` window (returning it, capturing it in a background task, storing it without copying) reintroduces a use-after-return.
- **Copy-out points are mandatory**: data that must outlive the window is copied synchronously — UDP `clientMac` (`ToArray()` at session creation), SOCKS5 payload (`Encode` copies before any await), the bounded SYN template (recorded before rewrite).
- **In-place rewrite ordering**: `RecordClientSyn`/`RecordServerSynAck` (reads of the original frame) MUST run BEFORE `TryRewriteTcpEndpoints` (write) on the same frame, or the RST template is polluted by rewritten endpoints/MACs. `TryRewriteIpv4Tcp/Ipv6Tcp` keeps the invariant "every parse/validation precedes the first field write; no failure branch after writing begins", so an in-place rewrite either leaves the frame untouched or fully rewrites it. A lease whose `Frame` is not array-backed fails closed (`reason=rewrite`).
- **Pump batching**: batch buffers are pump-private for the pump's lifetime, released exactly once (run-loop exit, or dispose — `DisposeAsync` sets the stop flag, then awaits the run-completion source that the run's `finally` completes after releasing, so a mid-run dispose can never free buffers a handler is still processing; fixed 2026-09-08, task 09-08-p0-correctness R4. The wait is bounded by the loop's poll delay because the loop observes the stop flag every iteration; a dispose before any run started releases directly on the synchronous fast path); packets within a batch are awaited strictly in index order, so reinjection order matches arrival order. Empty batch keeps the poll-delay pacing. **Scope of the ordering contract (R8, 2026-08-30)**: strict index order governs per-flow correctness — a flow's rewritten packets reinject in arrival order. A genuinely new TCP SYN is the one sanctioned relaxation: the pump-side handler retains a bounded copy and returns `SetupPending`, and the rewrite+injection of that one frame completes in a background task (see `tcp-local-redirect.md`). Per-flow ordering is preserved by construction (the client sends data only after the handshake completes, which requires our injected SYN), and same-flow retransmissions inside the pending window are absorbed by the pending index rather than reordered; cross-flow injection order within one adapter iteration is not a contract.
- **Gate granularity**: one gate lease per batch on the read path (query + read inside the same lease); since 2026-08-30 one gate lease per flush on the batched send path (all chunks inside one lease), with the single-packet sends kept for non-batched callers (TCP injector, UDP response reinjector).

### 4. Validation & Error Matrix

| Condition | Result |
|---|---|
| Queue query native call fails | throw `Win32Exception` (existing `HasQueuedPackets` semantics, now in `NdisNativeCallStatus.cs`) |
| `TryReadPackets` fails with a transient native error (21/170/1237/995/1167/31, `NdisNativeCallStatus.IsTransientReadError`; R7, 2026-08-30) | the pump retries with bounded exponential backoff (5 attempts, 100 ms base doubling, 1.6 s per-attempt cap → worst incident ≈3.1 s), rate-limited warn `adapter.retry`, counters `TransientReadRetryCount`/`TransientReadIncidentCount`; a healthy read closes the incident |
| Transient retries exhausted, or a permanent read error (everything else, fail-closed conservative) | **degraded exit** (R7): the pump returns normally (no throw) — flush + batch-buffer release still run in the `finally` — and `onDegraded(nativeError)` fires once; the process and sibling adapter pumps keep running, the wiring restores just that adapter's mode (`TransactionalCaptureRuntime.MarkAdapterDegradedAsync`), and the degradation log records the full native error so production ground truth can refine the table |
| Queue empty (`queuedPacketCount == 0`) | `TryReadPackets` returns 0; pump takes poll delay |
| `ReadPackets` returns FALSE with a non-empty queue | throw `Win32Exception` |
| Driver fills `dwPacketsSuccess` > requested count | clamped to the request count (defensive; `InterpretBatchReadResult` in `NdisNativeCallStatus.cs`) |
| `buffers` empty or contains null entries | 0 / `ArgumentNullException` (parameter validation) |
| Lease frame not array-backed at an in-place rewrite point | fail-closed `TcpRedirectOutcome.Blocked`, `reason=rewrite` |
| Pool return above capacity (256) or after drain | buffer freed immediately, never double-returned |

### 5. Good/Base/Bad Cases

- Good: a full batch of 32 frames flows through classify → dispatch → in-place rewrite → pool-rented inject with zero per-packet managed allocations beyond the single `ArrayPool` copy and zero native allocs.
- Base: zero-length frame (`ArrayPool.Rent(0)` returns an empty array; safe), partial batch (n < capacity) processed in order.
- Bad: returning the pooled array from a lease `TryComplete` callback — the executor then reads a reused array (torn frame); storing `lease.Frame` in a session without copying and reading it after `ProcessAsync` returns.

### 6. Tests Required

- `NdisApiAbiTests`: batch read error matrix (partial-batch clamp, non-empty failure throws).
- `NdisCapturePumpTests`: in-batch ordering with enumeration-handle stamping, partial batch, empty-batch poll, exactly-once batch-buffer release, capacity/null argument checks.
- `FlowDispatcherExecutorTests`: completion callback fires exactly once across double `TryComplete` + `Dispose`; `Dispose` is a completion path; plain constructor has no callback; pooled copy is byte-identical at actual length.
- `TcpProxyCoordinatorRewriteTests`: `SynRewriteParseFailureLeavesFrameByteIdentical` (parse failure leaves the frame byte-identical, returns Blocked, releases resources) — the in-place-rewrite no-intermediate-state lock.
- `NdisPacketBufferPoolTests`: rent/return round-trip reuse, over-capacity free, 64-thread rent uniqueness, double-Dispose no-op, drain-then-rent, foreign-buffer rejection, private-buffer (pump) dispose semantics regression.

### 7. Wrong vs Correct

#### Wrong

```csharp
// Returning the pooled frame when the lease completes: CompleteAsync runs
// TryComplete BEFORE the executor reads Frame, so another pump's Rent() can
// reuse this array before the pass injection copies it.
var lease = new PacketLease(frame, _ => ArrayPool<byte>.Shared.Return(array));
```

#### Correct

```csharp
// The return point is ProcessAsync's outer finally — the proven boundary of
// the whole dispatch chain. Every Frame consumer closes inside the window.
var pooledFrame = ArrayPool<byte>.Shared.Rent(frameSpan.Length);
try { ... await dispatcher.DispatchAsync(...); }
finally { ArrayPool<byte>.Shared.Return(pooledFrame); }
```

```csharp
// Wrong: record the SYN template after rewriting it in place — the RST
// builder then inherits rewritten endpoints/MACs and the client rejects it.
TryRewriteForwardLeg(frame, ...);
RecordClientSyn(frame.Span, association);

// Correct: read-then-write. The template keeps the original client bytes.
RecordClientSyn(frame.Span, association);
TryRewriteForwardLeg(frame, ...);
```

**Related**: task `08-27-fix-datapath-throughput` (prd/design/implement artifacts hold the full audit tables); the parent `08-27-fix-eof-reset-design-flaws` maps the throughput bottleneck to the RST/EOF frequency symptom.
