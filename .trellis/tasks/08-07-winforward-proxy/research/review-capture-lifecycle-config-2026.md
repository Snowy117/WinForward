# Functional review: capture lifecycle, idle sweeper, capture loop, mode controller, configuration DTO

Review date: 2026-08-07 (research only; no code modified)
Scope: CaptureLifecycle.cs, IdleExpirySweeper.cs, MultiAdapterCaptureLoop.cs, NdisAdapterModeController.cs, ConfigurationModels.cs
Focus: transactional adapter-mode rollback, thread-safety, sweep intervals, capture-loop disposal, config DTO normalization/validation edge cases, redaction.

Wiring context read: `src/WinForward.Cli/Program.cs` (RunCaptureLoopAsync), `src/WinForward.NdisApi/NdisCapture.cs`, `NdisApiDriver.cs`.

## Finding 1 — HIGH: capture pumps are never awaited on disposal; adapter modes are restored and the driver handle is released while pump/handler work may still be in flight

- `src/WinForward.NdisApi/NdisCapture.cs:58-63` — `NdisCapturePump.DisposeAsync` only sets `_stopped = 1`. It does not cancel the pump's `RunAsync` and does not wait for an in-flight `Task.Delay` or, more importantly, an in-flight `_handler(...)` (packet processing can include async SOCKS5 setup/relay).
- `src/WinForward.Runtime/MultiAdapterCaptureLoop.cs:41-45` — `DisposeAsync` calls each pump's `DisposeAsync` but never awaits the pump tasks. `MultiAdapterCaptureLoop` itself does not retain the `RunAsync` tasks to await.
- `src/WinForward.Runtime/CaptureLifecycle.cs:115-120` — `RestoreBestEffortAsync` runs immediately after `DisposeCaptureAsync`; there is no barrier ensuring all pump `_handler` invocations have completed before adapter modes are restored, and none before the caller (`Program.RunInterceptionAsync` `using var driver`) disposes the shared `NdisApiSafeHandle`.

Impact: on Ctrl+C during active traffic, a pump may be mid-`_handler` (e.g. a proxy relay setup or a packet injection via `NdisPacketReinjector` → `NdisApiDriver.SendPacketToMstcp/Adapter`) while `RestoreAsync` rewrites adapter flags and the driver SafeHandle is disposed. That is a use-after-dispose of the driver handle and a mode-restore-while-processing race. The `_stopped` flag only breaks the loop on its next iteration; the loop can also be parked in `Task.Delay(_pollDelay)` for up to 1 ms, which is harmless, but the handler path is not.

Suggested fix direction (not applied): dispose the capture loop by cancelling the run token and awaiting the pump tasks (e.g. keep the `RunAsync` task in `MultiAdapterCaptureLoop` and await in `DisposeAsync`), then restore modes.

## Finding 2 — MEDIUM: `_state` transitions and `_applied` list are not synchronized; concurrent `StopAsync`/`StartAsync` could corrupt state and dispose `_shutdown` under a running `StartAsync` (not reachable from current CLI wiring, but the abstraction allows it)

- `src/WinForward.Runtime/CaptureLifecycle.cs:31, 44-90` — `_state` is a plain field read/written without a lock; `StartAsync` writes `_state = CaptureRuntimeState.Running;` after applying modes, and `StopAsync` concurrently writes `Stopping`/`Closed`.
- `CaptureLifecycle.cs:66-72` — `StartAsync` builds `CreateLinkedTokenSource(cancellationToken, _shutdown.Token)` and calls `_capture.RunAsync(...)`; `StopAsync` (non-Created path) calls `_shutdown.CancelAsync()` and `RestoreBestEffortAsync()` which calls `_shutdown.Dispose()` (line 118). If `StopAsync` runs while `StartAsync` is between `SnapshotAsync` and `RunAsync`, `_shutdown` can be disposed while `StartAsync` still reads `_shutdown.Token` (ObjectDisposedException) or while `StartAsync` continues applying modes after `_applied` was cleared — leaving applied modes unrestored.
- `CaptureLifecycle.cs:105-113` — `StopAsync`'s non-Created/Closed branch cancels the shutdown token even when the runtime is only `Prepared`/`ModesApplied`; combined with the unsynchronized state writes this is a TOCTOU window.

Impact: theoretically a lost rollback or an ObjectDisposedException. In the current CLI the only cancellation source is `shutdown.Cancel()` from Ctrl+C passed into `StartAsync`'s token, and `StopAsync`/`DisposeAsync` (`await using`) run only after `StartAsync` returns, so the race is not reachable today. Because the class is a public `IAsyncDisposable` runtime abstraction, the hazard is worth a guard (e.g. a gate on the state machine, or cancel-then-await semantics in `StopAsync`).

## Finding 3 — LOW/MEDIUM: `IdleExpirySweeper` first sweep occurs after one full interval; 1-minute sweep vs 2-minute UDP relay timeout is acceptable but the per-tick `now` is captured once and coordinators are swept sequentially under one awaited chain

- `src/WinForward.Runtime/IdleExpirySweeper.cs:47-60` — `PeriodicTimer` yields the first tick only after `_interval` (default 1 min); idle timeouts are 2–5 min, so a relay idle for exactly 2 min can be removed at ~3 min worst case. Not a functional bug, but the effective timeout has up to one interval of slack.
- `IdleExpirySweeper.cs:52-56` — `_dispatcher.RemoveExpiredFlows`, then `await _tcp.RemoveExpiredAsync`, then `await _udp.RemoveExpiredAsync` run sequentially in one tick; a slow `RemoveExpiredAsync` (e.g. teardown of many sessions) delays later sweeps but failures are caught and retried next tick. If `_tcp`/`_udp` teardown blocks on socket operations, the dispatcher sweep stalls behind it. Acceptable bounded behavior; no unbounded queue.
- `IdleExpirySweeper.cs:63-64` — the `RCS1075`-suppressed catch swallows ALL exceptions including programming errors (e.g. NullReferenceException from a disposed coordinator), hiding them permanently. The comment says "transient teardown error", but there is no distinction; a persistent bug would be silently retried forever. Low severity.

## Finding 4 — LOW: `MultiAdapterCaptureLoop.RunAsync` rethrows the first pump failure after cancelling siblings, but the siblings' `OperationCanceledException` is swallowed only when `linked.IsCancellationRequested`; if the outer token is cancelled by an external caller while a non-OCE pump failure is propagating, `Task.WhenAll` may surface an OCE instead of the original failure — acceptable, but worth noting the original exception is preserved only via the `catch { CancelAsync(); throw; }` path

- `MultiAdapterCaptureLoop.cs:29-39` — on pump failure, `linked.CancelAsync()` then `throw`. Sibling pumps exit via OCE caught as graceful. If the outer token was ALSO cancelled at the same time, `WhenAll` still rethrows the original non-OCE exception (the OCE is swallowed). Correct.
- `MultiAdapterCaptureLoop.cs:56-64` — `RunPumpAsync` swallows OCE only `when (linked.IsCancellationRequested)`. If a pump throws OCE for a reason other than the linked token (e.g. the Outer token cancelled but the linked source wasn't yet observed cancelled — microsecond window), the OCE is rethrown to `WhenAll` and treated as a failure, cancelling siblings. In practice any cancellation of the outer token also cancels `linked` (linked source), so the guard is met. Low/no bug.

## Finding 5 — LOW: configuration unknown-property handling downgrades to a generic "Invalid JSON" diagnostic and loses redaction granularity

- `ConfigurationModels.cs:44-47` — `[JsonSourceGenerationOptions(..., UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]` applies to the whole context. A typo such as `"fallbackaction"` or `"socks5Server"` throws `JsonException` during `JsonSerializer.Deserialize`; `TryParse` (lines 55-66) reports `Invalid JSON: <exception message>` at `$` without naming the offending field. The PRD requires "Configuration errors shall identify the relevant server/rule and field." The raw `exception.Message` may embed the JSON value that failed conversion (e.g. a string where a number was expected), which could theoretically include credential-like content for a malformed config — though never for a valid credential value. Recommend: map `JsonException.Path`/`.Message` to a field-pathed diagnostic and keep messages template-based (no value interpolation).

## Finding 6 — LOW: `ValidateFailureAction`/`ParseAction` accept values with surrounding whitespace after trim, but `ParseAction` reports "Action is required." for a whitespace-only `fallbackAction` while `ValidateFailureAction` reports "Only block is supported" for whitespace-only failure actions — consistent. No bug.

## Finding 7 — LOW: `NdisAdapterModeController` — `SnapshotAsync`, `ApplyCaptureModeAsync`, and `RestoreAsync` are synchronous and ignore the cancellation token; a cancellation arriving mid-apply cannot abort the loop, so rollback still runs. Correct fail-closed behavior. `_byStableId` dictionary lookup `_byStableId[adapter.AdapterId]` throws `KeyNotFoundException` if the snapshot list contains an adapter no longer in the dictionary (e.g., adapter list changed between snapshot and restore). In the current wiring the scope is fixed at startup and the adapter-list-change seam is deferred, so this is not reachable today; when the seam is implemented, restore must tolerate a missing adapter (fail-closed diagnostic) rather than crash the rollback loop — note `RestoreBestEffortAsync` catches per-adapter exceptions, so a missing adapter would be skipped silently, which is fail-closed but not logged. Worth a log.

## Config normalization notes (verified correct)

- Omitted vs empty arrays: `ValidateNonEmpty` returns without error for `null` (omitted = no condition) and errors for `[]` (empty = invalid). Matches PRD.
- `ParsePorts` rejects `start == 0`, `end == 0`, `end < start`, and >2 dash segments; `ushort` bounds cap at 65535. Correct.
- Duplicate server names detected case-insensitively; `servers.Add` guarded by `!ContainsKey`. Correct.
- Username/password pairing and 255-byte UTF-8 length checks correct; no credential value is ever included in diagnostics.
- `RemotePort` items are trimmed via `Split('-', TrimEntries)`; bare `"53"` yields `(53,53)`.
- `IsValidHost` accepts IP literals and DNS names; single-label names accepted via `UriHostNameType.Basic` — acceptable per PRD ("DNS hostname").
- `ParseRule` returns null when any field error exists for the rule, so partial parse results never reach the policy snapshot. Correct.
- `ProxyServer` on non-proxy rules is an error; missing/unknown server on proxy rules is an error. Correct.

## Summary (prioritized)

1. HIGH — Capture pumps not awaited on dispose; mode restore + driver disposal race in-flight handler work (`NdisCapture.cs:58`, `MultiAdapterCaptureLoop.cs:41`, `CaptureLifecycle.cs:115`).
2. MEDIUM — Unsynchronized `_state`/`_applied` + `_shutdown` disposal under concurrent `StopAsync`/`StartAsync`; not reachable from current CLI but latent in the abstraction (`CaptureLifecycle.cs:31,44-90,105-118`).
3. LOW — Sweeper swallows all exceptions forever (incl. programming errors) and first tick has one-interval slack (`IdleExpirySweeper.cs:63-64,47-60`).
4. LOW — Unknown config fields produce generic "Invalid JSON" without field path; exception message may embed values (`ConfigurationModels.cs:44-66`).
5. LOW — `NdisAdapterModeController` restore would throw `KeyNotFoundException` on adapter-list change between snapshot and restore; currently unreachable (deferred seam), restore skip is silent (`NdisAdapterModeController.cs:47-50`).