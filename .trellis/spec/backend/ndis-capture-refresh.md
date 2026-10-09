# NDIS Capture Refresh and Self-Healing

> When and why the capture view is rebuilt: bound-list change, periodic re-enumeration, address
> fingerprints, and health-triggered forced refresh. Read it before touching
> `LayeredCaptureRunner`, `NdisCaptureGenerationFactory`, the adapter enumeration or its watcher, or
> `InterceptionHealthMonitor`. Hub (handle rules, gate topology, adapter identity):
> [windows-ndisapi.md](./windows-ndisapi.md). This file owns the generation lifecycle that
> [ndis-batched-capture.md](./ndis-batched-capture.md) and
> [ndis-batched-send.md](./ndis-batched-send.md) consume; the network path after a lane is built is
> `hot-path.md`.

## Why the view needs rebuilding

> Windows rebuilds the NDISRD TCP/IP-bound adapter list on plug/unplug, enable/disable,
> standby/resume, and Wi-Fi Direct virtual-adapter churn (a `Local Area Connection* N`). Every
> enumeration handle is a runtime pointer into that list and goes stale at once; adapter-associated
> IOCTLs then fail with `ERROR_INVALID_PARAMETER` (87) — production ground truth 2026-09-07: four
> adapters (including the physical NIC) degraded together. 87 stays in the permanent-error class
> (`IsTransientReadError` is NOT extended), so the fix is recovery, not retry: the official
> `SetAdapterListChangeEvent` + re-enumeration guidance. Hardware smoke 2026-09-08 (task S7):
> 87 degrade → refresh in 77 ms, session survived, interception resumed; R-2 auto-reset event mode
> verified against the real driver (
> `.trellis/tasks/archive/2026-09/09-07-adapter-list-refresh/smoke-evidence.md`).

- **Two lifetimes**: durable components survive a refresh (sessions recover via retransmission;
  relays stay open); only the generation layer (scope, mode controller, pump set, per-generation
  runtime) is rebuilt. No dynamic pump membership — a list rebuild invalidates all handles at once,
  so whole-generation replacement is the correct granularity.
- **Refresh semantics are deliberately non-fatal** (PRD R4, the documented bounded exemption from
  startup-fatal scope resolution): gone selector → warn + rule contributes nothing; ambiguous
  selector → warn + skip the addition; id/name disagreement → warn + rule skipped. Widening to all
  MSTCP-bound adapters requires an unconstrained rule or a zero-warning empty selector set — a
  fully-constrained policy whose adapters ALL disappeared must NOT widen. gen0 scope resolution stays
  the fatal `CaptureAdapterScopeResolver.TryResolve` surface; `ResolveForRefresh(adapters, policy, out warnings)`
  is its non-fatal twin.
- **No-op skip before stopping the generation**: a pending refresh demand re-enumerates and diffs
  `(StableId → handle, MAC, MTU, AddressFingerprint)` against the running generation; identical
  in-scope maps log `adapter.refresh` no-op and never touch pumps/modes (no mode flap, AC4/AC5).
  Handle-value reuse makes an empty diff genuinely possible after a rebuild, which is why a *forced*
  demand bypasses this skip.
- **Storm guard**: at least 1 s between executed rebuilds (`TimeProvider`-injectable); signals during
  a rebuild or inside the window coalesce into one pending rebuild. The periodic timer signals the
  SAME gate — no new state machine.
- **87 as a defense-in-depth trigger**: a pump degrading with 87 enters the pending-refresh channel
  (`SignalDegraded`); the enumeration diff then decides rebuild vs honest no-change log. 87 on a
  present adapter remains a genuine defect signal — never add it to the transient table.
- **Degradation is fail-soft per adapter**: a permanent read error (or exhausted transient budget)
  makes the pump return normally — no throw — so its loop-exit flush and batch-buffer release still
  run, `onDegraded(nativeError)` fires once, sibling pumps keep running, and the wiring restores only
  that adapter's mode (`TransactionalCaptureRuntime.MarkAdapterDegradedAsync`, logging
  `adapter.degraded` with the full native error). Old-handle `MarkAdapterDegradedAsync` and the
  `adapter.degraded` log live INSIDE `NdisCaptureGenerationFactory`'s degrade callback; outer wiring
  only forwards to `SignalDegraded` — never restore twice.
- **UDP reinjection targets resolve against the current snapshot per response**: gone adapters drop
  fail-closed with the existing rate-limited warn; host flows fall back to the host target, and a
  missing host target is itself fail-closed — the seam legitimately has no target to invent. The
  source is slot-keyed (`IUdpAdapterTargetSource.Resolve(ushort slot)`, plus `Host` and
  `AdapterIds`), swapped wholesale via `Volatile.Write` so a response never observes a half-updated
  map.

## Signatures

- `NdisApiDriver.SetAdapterListChangeEvent(nint win32Event)` — control-gated registration of a
  caller-owned Win32 event; `nint.Zero` releases (official NULL semantics). Native FALSE throws
  `Win32Exception` — registration failure is fatal at startup by design.
- `IAdapterListChangeSource { bool WaitOne(CancellationToken); }` — true = bound list rebuilt (all
  enumeration handles stale), false = cancelled/disposed. The native implementation
  `NdisAdapterListWatcher` registers an **auto-reset** `EventWaitHandle` (bursts coalesce; the ground
  truth is the re-enumeration, never the signal count) and blocks via `WaitHandle.WaitAny` (no spin).
- `LayeredCaptureRunner(enumerationProvider, generationFactory, changeSource, policy, logger,
  disposeDurableAsync, onScopeInstalled, minimumRefreshInterval, timeProvider, periodicRefreshInterval,
  interceptionHealthMonitor)`, plus `SignalDegraded(adapter, nativeError)`, `HealthSignal`,
  `MaxConsecutiveStartupRecoveries = 3`, and the two injected clocks: `minimumRefreshInterval`
  (storm guard, default 1 s) and `periodicRefreshInterval` (default 30 s; `TimeSpan.Zero` disables;
  negative throws). `ICaptureGeneration`/`ICaptureGenerationFactory` are the test seam;
  `NdisCaptureGenerationFactory` (windows-gated) composes `NdisAdapterModeController` +
  `MultiAdapterCaptureLoop` + `TransactionalCaptureRuntime` per generation.
- `ICaptureGeneration.ReachedPumpRun` / `TransactionalCaptureRuntime.ReachedPumpRun` (task 09-11) —
  phase latch set under the runtime gate immediately before the capture loop run starts; read after
  the generation task completes it is race-free by await ordering. This is the classification input
  that keeps NDIS error-code knowledge out of the runtime itself.
- `DurableCaptureBundle` (`src/WinForward.Cli/`) — built once per run: redirect table, TCP/UDP
  coordinators, attribution dispatcher, executor chain, idle sweeper, refreshable UDP target source.
  `DisposeAsync` is single-flight in the order sweeper → attribution pipeline → UDP coordinator
  (→ UDP pools) → TCP coordinator (→ syn-copy/attribution/relay pools → wake owners).

## Contracts

- **Startup stale-handle recovery (task 09-11)**: a generation fault is classified recoverable iff
  it is `Win32Exception` with `NativeErrorCode == 87` AND the generation's `ReachedPumpRun` latch is
  still false (during startup the only adapter-associated native calls are the mode snapshot/apply,
  so that signature means the list rebuilt between the runner's enumeration and the snapshot).
  Classification lives in the runner (`IsRecoverableStartupFault`), next to
  `AdapterListRebuiltNativeError`. Absorption happens at exactly the two points where a generation
  task is awaited: exit observation (fault won — release the dead generation exactly like a refresh
  stop, arm a forced demand) and `StopGenerationAsync`'s fault-capture branch (demand won — absorb
  with no extra signal; the in-flight demand installs the replacement). Every other escape — non-87
  startup faults, 87 after the pumps started, over-cap streaks — keeps the fail-closed rethrow.
  `MaxConsecutiveStartupRecoveries = 3` bounds the streak; it resets wherever a generation with
  `ReachedPumpRun == true` completes. Telemetry per absorption: structured warn
  `generation.startup-fault` with `nativeError` + `attempt=k/{limit}`; a final error-level variant
  precedes the fail-closed exit when the cap is exceeded.
- **Forced rebuild honesty (task 09-11)**: a recovery-armed demand consumes `_forceRebuild` at
  `ProcessRefreshDemandAsync` entry and bypasses the empty-diff no-op skip — the current generation is
  dead, so an empty diff must still install a replacement. `adapter.refresh` records `forced=true`
  (never `noop=true`) on forced installs.
- **Forced-flag double-producer semantics**: startup-fault recovery and the health monitor both write
  `_forceRebuild`, and there are exactly two clearing sites — `ProcessRefreshDemandAsync` entry
  (reads-and-clears) and `InstallGenerationAsync` (clears on install). The monitor writes the flag
  immediately before `_demandGate.Signal()` so the gate's lock/TCS provides happens-before
  visibility. Benign race: an in-flight install may clear a just-armed flag — the next demand then
  degrades to a non-forced no-op recheck; harmless, the refresh already happened.
- **Shutdown order (R-1 deviation)**: generation cleanup (pump stop + best-effort mode restore with
  old handles) runs BEFORE durable disposal. Old-handle restore failures (87) are swallowed
  best-effort — the driver's rebuilt context starts in default mode; unchanged handles accept a
  one-window tunnel-off→on flap, which the no-op skip avoids in the common case. Neither
  coordinator's `DisposeAsync` invokes the reinjector and NDISRD injection is capture-mode
  independent, so teardown injections still land (smoke-verified 2026-09-08).
- **Lane lifecycle is generation-scoped** — see
  [ndis-batched-send.md](./ndis-batched-send.md) for `OnScopeInstalled`'s composition and the
  scope-sized lane-table rebuild.

## Self-healing: address fingerprints, periodic re-enumeration, health-triggered refresh

> Production ground truth 2026-09-17: host link state changed (suspected IPv6 temporary-address
> rotation) with NO NDISRD bound-list rebuild — the last `adapter.refresh` was 78 minutes before a
> total proxied-traffic outage that only a process restart cured. The NDISRD change event alone
> cannot see address changes, so the view self-heals through two independent channels.

- **AddressFingerprint joins the diff tuple**: `AdapterEnumerationItem` carries `AddressFingerprint`
  (normalized: the adapter's unicast addresses sorted, `';'`-joined, invariant lower-case; IPv6
  link-local `fe80::/10` EXCLUDED so ND churn never flaps). `AdapterEnumerationDiff.LinkStateEquals`
  compares handle, MAC, MTU, and the fingerprint — any change rebuilds through the existing refresh
  pipeline. Default `""` keeps legacy no-op semantics for untouched fakes. Address-query failure is
  non-fatal (empty fingerprint + one-shot debug `adapter.addressQuery.failed`): a broken query must
  degrade to the pre-task view, never break capture.
- **iphlpapi table row alignment is layout-determined (E1-caught, 2026-09-17)**: owner-pid tables
  (`MIB_TCPTABLE_OWNER_PID` etc.) have 4-byte-aligned rows, so `Table[0]` sits at offset 4;
  `MIB_UNICASTIPADDRESS_ROW` contains `NET_LUID`/`LARGE_INTEGER` members, so `Table[0]` sits at offset
  **8** (`IPHelperAbi.UnicastTableFirstRowOffset`, MS-documented padding). `IPHelperTables.ReadRow<T>(buffer, index, firstRowOffset)`
  (`src/WinForward.Windows/ProcessAttribution.cs`) is the single row-read entry — never hardcode `+4`
  for a new table; derive the offset from the row's largest alignment class and pin it with a
  poisoned-padding test (`UnicastAddressInventoryTests` writes `0xDeadBeef` into the padding bytes).
  The pre-E1 bug read unicast rows at +4, parsed garbage, and the tolerance path silently emptied
  every fingerprint — the root-cause fix would have been a no-op on real hardware while all tests
  stayed green.
- **Table bounds cross-check before any row dereference (fixed 2026-09-08)**: `IPHelperTables.ValidateRowCount`
  throws fail-closed when `4 + (long)rowCount * rowSize > bytesWritten` or `rowCount < 0` — the same
  driver-reported-count discipline the NDISAPI seam enforces on batch sizes. Attribution callers
  tolerate the throw as "no attribution", so an inconsistent table never becomes a
  partially-dereferenced table or a wrong PID.
- **Periodic re-enumeration (R1-A)**: each `periodicRefreshInterval` tick signals the same
  `RefreshDemandGate` as the NDISRD watcher — non-forced: an unchanged enumeration no-ops (no mode
  flap), any diff input change rebuilds. The 1 s storm guard absorbs tick/event races.
- **Failure-rate forced refresh (R1-B)**: `IInterceptionHealthSignal.ReportFailure(counter)` feeds
  `InterceptionHealthMonitor` (runner-owned, exposed as `HealthSignal`). Per-counter 30 s sliding
  window; default thresholds `relaySetupFailed ≥ 3`, `passReinjectFailed ≥ 3`,
  `udpOriginUnresolved ≥ 8`, `udpFailClosedDrop ≥ 8`. A trigger arms the SAME `_forceRebuild` flag the
  09-11 startup-fault recovery uses and signals a demand; forced demands bypass the empty-diff no-op
  (a forced refresh is idempotent and harmless when the real cause is an upstream outage). Anti-storm:
  storm guard → shared 60 s cooldown → 3 consecutive forced triggers degrade to one per 5 min plus a
  single error `runner.forcedRefresh.degraded`. `NoteRefreshCompleted()` (called on BOTH successful
  demand paths — no-op and install; NOT on the empty-scope pause) resets windows/consecutive/degraded
  but keeps earned cooldown.
- **Trigger-time snapshot (fixed 2026-09-30, task 09-30-exact-gate-residual-lumps)**: the handler
  attached through `AttachTrigger` receives a `ForcedRefreshTrigger` — counter, consecutive streak,
  degraded flag, earned cooldown, and every window count — captured under the monitor's gate in the
  same locked section that arms the trigger. The handler must log **that** snapshot and never read
  `ConsecutiveForcedTriggers` / `WindowSnapshot()` / `IsDegraded` / `CooldownRemaining` back from the
  monitor: the demand-processing success hook (`NoteRefreshCompleted`) runs concurrently with the
  handler and resets the streak and clears the windows, so a read-back logged `consecutive=0` for a
  trigger that fired at streak 1 (`LayeredCaptureRunnerHealthSignalTests`). The report path stays
  lean — the snapshot is built once per threshold crossing, at most one per cooldown.
- **Signal sources** (null-safe injection, `Noop` default; counting stays with `RuntimeCounters` —
  `ReportFailure` never double-counts): `UdpResponseReinjector` (host fallback → `udpOriginUnresolved`;
  missingOriginAdapter/missingHostTarget drops → `udpFailClosedDrop`; missingClientMac is a different
  failure class, unreported), `ClientResetInjector.HandleRelaySetupFailureAsync` (`relaySetupFailed`),
  `NdisPacketActionExecutor` pass-native failures (`passReinjectFailed`).

## Validation & Error Matrix

| Condition | Required result |
|---|---|
| Driver signals list change | watcher `WaitOne` returns true; runner re-enumerates and diffs |
| Enumeration identical to current generation | `adapter.refresh` no-op log; pumps/modes untouched |
| Scope adapter disappeared | warn + dropped from scope; run continues on remaining |
| Adapter cannot be interned (slot space exhausted, F4) | refused at generation build: excluded from the capture scope + one rate-limited `adapter.slot-exhausted` error; never keyed as `NoSlot`; factory's exclusion/log loop has no test (Windows-only, real driver required) |
| New adapter + unconstrained rule | adopted into scope and intercepted after refresh |
| New adapter + fully constrained policy | NOT adopted (a fully-constrained policy whose adapters all vanished must not widen) |
| Ambiguous name selector at refresh | warn + addition skipped, non-fatal |
| Pump degrades with 87, enumeration unchanged | honest no-change log; no rebuild loop |
| Per-adapter permanent read error, or transient budget exhausted (21/170/1237/995/1167/31) | pump returns normally; one `onDegraded`, mode restored for that adapter only. Transient path first: 5 attempts, 100 ms base doubling, 1.6 s per-attempt cap (worst incident ≈3.1 s), rate-limited warn `adapter.retry`, counters `TransientReadRetryCount`/`TransientReadIncidentCount`; a healthy read closes the incident |
| Generation startup faults with 87 before its pump run (task 09-11) | absorbed: warn `generation.startup-fault attempt=k/3`, dead generation released, forced storm-guarded refresh installs a replacement even on an empty diff (`forced=true`) |
| > 3 consecutive recoverable startup faults | final error-level `generation.startup-fault`, original fault rethrown fail-closed |
| Startup fault with native error ≠ 87, or 87 after `ReachedPumpRun` | fail-closed rethrow; no warn event, no recovery |
| Refresh demand races a startup-87 fault (demand wins the await) | demand processing absorbs the classified fault; ends with a live generation |
| Generation reaches its pump run, later isolated startup-87 | streak was reset; recovery starts from attempt 1/3 |
| Signals faster than the storm window | coalesce into one rebuild |
| gen0 scope resolution failure | fatal startup error (unchanged `TryResolve` surface) |
| Empty scope at refresh | pause (no pumps), await next signal; startup empty stays fatal |
| Watcher registration native FALSE | `Win32Exception` → startup fatal (feature unusable) |
| Fingerprint change alone (handle/MAC/MTU equal) | diff changed → refresh rebuild |
| Periodic tick, enumeration unchanged | no-op, pumps/modes untouched |
| Health threshold crossed in the 30 s window | `_forceRebuild` armed + demand + warn `runner.forcedRefresh`; forced install logs `forced=true` |
| Cooldown active | failures still counted; no trigger until expiry |
| 3 consecutive forced triggers without a completed refresh | error `runner.forcedRefresh.degraded`; 5-min cadence |
| Any successful demand processing (no-op or install) | `NoteRefreshCompleted` resets windows/streak; earned cooldown survives |
| Trigger handler reads the monitor back after being invoked | forbidden — the success hook resets the streak concurrently; log the handed `ForcedRefreshTrigger` snapshot |
| Empty scope (all adapters gone) | pause; no `NoteRefreshCompleted` (no live generation) |
| Address query fails | empty fingerprint, one-shot debug; enumeration unaffected |

## Tests Required

- `LayeredCaptureRunnerTests` / `LayeredCaptureRunnerRefreshTests` (AC1–AC5: rebuild on fresh handles
  without disposing durable; drop-with-warn; adopt via unconstrained rule; 87 no-op re-check; storm
  coalescing + no-op skip; generation-before-durable disposal ordering on natural end, fault, and
  user cancel). Since task 09-11 the refresh suite also locks the startup recovery ACs: recovery on
  fresh handles; forced install on unchanged enumeration (`forced=true`, no `noop`); cap exceeded →
  original `Win32Exception` survives to `RunAsync` with the warn×3+error event sequence and
  `attempt=1/3..4/3`; non-87 startup fault and post-pump 87 propagate with zero
  `generation.startup-fault` events; demand-races-fault ends with a live generation; streak reset
  proven by two recoveries both logging `attempt=1/3`.
- `CaptureLifecycleTests` (task 09-11): `TransactionalCaptureRuntime.ReachedPumpRun` stays false when
  `SnapshotAsync`/`ApplyCaptureModeAsync` throws during start, latches true once the capture loop run
  started (fakes carry `failOnSnapshot`/`failOnApply` knobs).
- `AdapterScopeRefreshTests` (R4 semantics matrix + ordering + zero-warning equivalence with
  `TryResolve`); `AdapterEnumerationDiffTests` (handle/MAC/MTU diff, plus the fingerprint diff cases).
- `AdapterListWatcherTests` (signal/cancel/dispose semantics via the OS-agnostic wait core);
  `UdpAdapterTargetSourceTests` (snapshot swap without reconstruction, fail-closed miss, host-null
  drop, frozen-snapshot immunity).
- `LayeredCaptureRunnerPeriodicRefreshTests` (fingerprint-change rebuild; no-op on unchanged ticks;
  tick+signal coalesce; zero/negative interval).
- `LayeredCaptureRunnerHealthSignalTests` (threshold → `forced=true` rebuild + warn fields + streak
  reset, with the post-state reset **awaited** — the install returns before `NoteRefreshCompleted`
  runs, so asserting immediately read the pre-reset streak; below-threshold silence).
- `InterceptionHealthMonitorTests` (windows/cooldown/degrade/reset/no-op, and
  `TriggerSnapshotSurvivesAResetInsideTheHandler` — the reset-inside-the-handler interleaving that
  pins the snapshot contract).
- `UnicastAddressInventoryTests` (parse/group/link-local exclusion + poisoned-padding offset lock).
- Windows hardware smoke (AC6, passed 2026-09-08): disable/enable a NIC under a live run →
  `adapter.refresh` logs the transition and interception resumes without a process restart. Evidence:
  task `09-07-adapter-list-refresh/smoke-evidence.md` (87→refresh 77 ms; a hidden Wi-Fi Direct
  adapter's membership change does NOT rebuild the bound list — zero events, pump rides transient
  retries).
