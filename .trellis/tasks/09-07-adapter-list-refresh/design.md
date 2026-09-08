# Design — layered capture refresh on adapter list change

## 1. Root cause recap (why refresh, not retry)

The NDISRD driver's enumeration handles (`GetTcpipBoundAdaptersInfo`) are runtime pointers into
the driver's bound-adapter list. When the list is rebuilt (plug/unplug, enable/disable,
standby/resume, Wi-Fi Direct virtual adapter churn — `本地连接* N`), every cached handle goes
stale at once; adapter-associated IOCTLs (`GetAdapterPacketQueueSize`, `ReadPackets`,
`SetAdapterMode`) then return `ERROR_INVALID_PARAMETER` (87). 87 is correctly classified
permanent by `IsTransientReadError`, so pumps degrade — the missing piece is recovery, which the
official `SetAdapterListChangeEvent` + re-enumeration guidance prescribes. Production ground
truth: user log 2026-09-07, four adapters (incl. 以太网) degraded with 87 together.

## 2. Layer split (the core decision)

Two lifetimes replace one:

- **Durable layer (created once per run, survives refresh):** driver handle, reinjector,
  self-traffic registry, `TcpRedirectTable`, `TcpProxyCoordinator`, `UdpProxyCoordinator`
  (with a refreshable target source, §5), `FlowDispatcher`/executor, `IdleExpirySweeper`,
  high-resolution timer scope, buffer pool, logger.
- **Generation layer (rebuilt per enumeration):** `WindowsAdapter` scope,
  `NdisAdapterModeController`, `MultiAdapterCaptureLoop` pump set, degraded-callback wiring,
  and the per-generation `TransactionalCaptureRuntime`.

`TransactionalCaptureRuntime`, `MultiAdapterCaptureLoop`, and `NdisCapturePump` keep their
current contracts — a refresh simply constructs a new runtime + loop around the shared durable
processor. No dynamic pump membership (justified by evidence: list rebuilds invalidate all
handles at once, so per-adapter granularity buys nothing on the common case; the rebuild window
is milliseconds).

## 3. New components and seams

### 3.1 ABI: `SetAdapterListChangeEvent`

`NdisApiAbi`: `int SetAdapterListChangeEvent(NdisApiSafeHandle, nint eventHandle)` — nint (not
SafeWaitHandle) because the release call passes NULL; the event's `EventWaitHandle` is rooted by
the watcher for the whole run so the raw handle stays valid. Export verified in
`smoke/ndisapi.dll` (binary string table, 2026-09-07) and official docs (ntkernel reference).

`NdisApiDriver`: `SetAdapterListChangeEvent(nint win32Event)` / overload with 0 to release,
through the control gate (cold path, one-time registration). Throws `Win32Exception` on FALSE —
registration failure is fatal at startup (feature unusable) but warn-and-continue would
silently reintroduce the current behavior; choose fatal.

### 3.2 `IAdapterListChangeSource` + `NdisAdapterListWatcher`

```
public interface IAdapterListChangeSource : IDisposable
{
    bool WaitOne(CancellationToken cancellationToken); // true = list changed; false = cancelled
}
```

Native impl: auto-reset `EventWaitHandle(false, AutoReset)` registered with the driver;
dedicated long-running task loops `WaitHandle.WaitAny({event, cancelEvent})`. Auto-reset chosen
so bursts coalesce — the ground truth is the re-enumeration, never the signal count. Test
impl: manual trigger queue. (Verify on hardware in smoke: driver SetEvent on auto-reset event —
expected standard semantics; if the driver requires manual-reset, swap mode and Reset before
processing; the seam hides this.)

### 3.3 `CaptureAdapterScopeResolver` — refresh-mode overload

Same matching core, different error policy (R4): startup overload keeps fatal `errors`;
refresh overload returns `(scope, warnings)` — gone selector → warn + skip rule's contribution;
ambiguous name selector → warn + skip the ambiguous addition; never fatal. Ordering by StableId
unchanged.

### 3.4 `IUdpAdapterTargetSource` (refreshable UDP reinjection targets)

`UdpResponseReinjector` today freezes `_host` (fallback handle+MAC) and `_byStableId` at
construction (`UdpAdapterTarget(nint Handle, byte[] Mac)`). New seam:

```
public interface IUdpAdapterTargetSource
{
    UdpAdapterTarget? Host { get; }              // null when scope empty
    UdpAdapterTarget? Resolve(string stableId);  // null when adapter gone
}
```

Per-response lookup (UDP response rate ≪ capture rate; a dictionary read is noise). Mutable
impl stores an immutable snapshot record swapped with `Volatile.Write` by the refresh runner.
Reinjector drops fail-closed with the existing rate-limited log when resolution returns null —
exactly today's semantics for a missing adapter.

### 3.5 Generation runner (`LayeredCaptureRunner`, Runtime/Capture)

Owns the loop (all under `[SupportedOSPlatform("windows")]`):

```
while not user-cancelled:
    enumerate → resolve scope (startup semantics for gen 0, refresh semantics afterwards)
    build generation: modeController(scope') + MultiAdapterCaptureLoop(scope', sharedProcessor, onDegraded)
    runtime = TransactionalCaptureRuntime(modeController, generationLoop)
    await runtime.StartAsync(linked(userToken, refreshToken))   // blocks until either fires
    on refreshToken: dispose runtime (best-effort mode restore w/ old handles), continue loop
    on userToken:   break
dispose durable layer (coordinators, sweeper) after the last runtime completes
```

Details:
- **No-op skip (R5/AC5):** before rebuilding, compare fresh `(StableId → handle, MAC, MTU)` map
  to the current generation's. Identical in-scope maps → log `adapter.refresh` no-op, do not
  touch pumps/modes (protects against spurious signals and avoids mode flap).
- **Storm guard:** minimum interval between executed rebuilds (default 1 s, const); signals
  during a rebuild or inside the interval coalesce to one pending rebuild consumed after the
  guard window.
- **Degrade-triggered recovery (R3):** the `onAdapterDegraded` callback already runs per
  degraded pump; it signals the runner (same pending-refresh channel) instead of only logging
  when nativeError == 87. Runner's enumeration diff then decides rebuild vs honest no-change
  log (AC4 bounded: the interval guard bounds re-check frequency).
- **Exit-reason discrimination:** linked token source per generation; after `StartAsync`
  returns, check which token fired (`refreshToken.IsCancellationRequested` first, else user).
- **Mode restore across refresh:** old-handle restore throws 87 → already swallowed
  best-effort; harmless because the driver's rebuilt context starts in default mode. Unchanged
  handles flap tunnel-off→on for one rebuild window — accepted (no-op skip avoids the common
  case).

### 3.6 Wiring changes (`Program.cs`)

`RunCaptureLoopAsync` becomes: durable-layer factory (extracted from today's
`CreateCaptureCompositionAsync`) + runner + watcher registration + final disposal. The
`CoordinatorShutdownCaptureLoop` wrapper dissolves: pumps belong to the generation runtime;
sweeper + coordinators belong to the durable layer and are disposed by the runner after the
final generation. `adapter.refresh` structured event via `logger.Event(Info, "adapter.refresh",
added/removed/changed)`; degraded-path log gains `present=true/false` after the enumeration
re-check.

## 4. Data flow across a refresh

```
driver event ─► watcher ─► runner: cancel refreshToken
  pumps stop (graceful) ─► runtime cleanup: pump buffers released, modes restored best-effort
  re-enumerate (control gate) ─► diff vs current map ─► (no-op? log+skip)
  resolve scope (refresh semantics) ─► swap UdpAdapterTargetSource snapshot
  new modeController ─► snapshot modes ─► apply tunnel
  new MultiAdapterCaptureLoop over shared processor ─► StartAsync(next generation)
TCP flows: relay sockets stay open; client retransmits resume through the redirect after the gap.
UDP flows: origin-adapter targets resolve against the fresh snapshot; gone adapters drop fail-closed.
```

## 5. Risks / open items

- **R-1 final-shutdown ordering:** today coordinators dispose BEFORE mode restore (inside the
  runtime's capture disposal). With the layer split they dispose AFTER. Neither coordinator's
  `DisposeAsync` calls the reinjector directly (verified 2026-09-07: TCP drains session store;
  UDP disposes sessions), and NDISRD injection is capture-mode-independent, so the deviation is
  believed benign — but smoke must confirm teardown injections still land (AC6). Fallback if
  broken: `FinalGenerationCaptureLoop(pumps, durableBundle)` wrapper re-introduced for the last
  generation via a runner-set final flag under its lock before user-cancel propagates.
- **R-2 event reset-mode assumption:** auto-reset assumed (§3.2); verify on hardware; seam
  isolates the swap to the native impl.
- **R-3 registration vs WOW64/native AOT:** raw handle passing only; no new structs; layout
  assertions untouched.
- **R-4 enumeration flapping mid-refresh:** a list change during rebuild re-signals; next loop
  iteration re-enumerates (loop is the reconciler); worst case one extra no-op-skipped rebuild.
- **R-5 `scope[0]` host-fallback semantics:** `CreateUdpCoordinator`'s host adapter becomes
  "current generation's first scope adapter" via the target source; keep the zero-MAC warn when
  resolution fails.

## 6. Compatibility / rollout / rollback

Additive API changes only (`NdisApiDriver` methods, new interfaces, new overload). Behavior
change: refresh replaces silent total degradation — the failure mode users currently see is
strictly worse, so no config flag is warranted. Rollback = revert the commit; no persistent
state. Logging guideline compliance: new structured events (`adapter.refresh`) follow
`RuntimeLogField` conventions in `RuntimeLogging.cs`.
