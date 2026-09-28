# Research: allowlist shrink (C4)

- **Query**: exact `.editorconfig` sections C4 must delete, their recorded reasons, evidence the violating sites exist, what remains after C4, and the analyzer rule-firing proof.
- **Scope**: internal, read-only for the repo (a scratch probe file was created, built, and deleted; `git status --short -- src` confirmed empty afterward).
- **Method**: full `.editorconfig:430-470` read; two scratch probes built and removed.

## Sections C4 must delete

### Entry 1 — `MultiAdapterCaptureLoop` (`WF0001`)

```editorconfig
455: # MultiAdapterCaptureLoop.cs:110 `_ = ForwardDegradationAsync(...)`：通知型回调。
456: # 删除方：C4 —— 改为所属 owner 同步调用的回调（design §4.1）。
457: [src/WinForward.Runtime/Capture/MultiAdapterCaptureLoop.cs]
458: dotnet_diagnostic.WF0001.severity = none
```

Delete lines **455-458** (comment 455-456 + section 457 + severity 458). Also drop the separating blank line 459 if it becomes doubled.

Recorded reason: `_ = ForwardDegradationAsync(...)` is a "notification callback"; C4 is to change it to "a callback invoked synchronously by the owning owner (design §4.1)".

### Entry 2 — `LayeredCaptureRunner` (`WF0003`)

```editorconfig
460: # LayeredCaptureRunner.cs:144 `Task.Factory.StartNew` 与 :149 `Task.Run`：真实并发，需迁移为
461: # scope 子任务（循环体自带 lease）。删除方：C4（design §4.1）。
462: [src/WinForward.Runtime/Capture/LayeredCaptureRunner.cs]
463: dotnet_diagnostic.WF0003.severity = none
```

Delete lines **460-463**. Drop the separating blank line 464 if doubled.

Recorded reason: `:144 Task.Factory.StartNew` and `:149 Task.Run` are "real concurrency" to be migrated to scope children ("the loop body carries its own lease").

## What remains after C4

Only the primitive's permanent exemption (do **not** delete):

```editorconfig
465: # 永久豁免（不在收缩清单内）：QuiescenceScope.cs:133 `_ = RunChildAsync(...)` 与 :170
466: # `_ = DrainCoreAsync(...)` 是原语自身的受管子任务与分离的 drain——子任务已由 TryEnter 准入、
467: # 其 lease 由 RunChildAsync 释放、故障在同一处 RecordFault 记录，drain 则自含故障（避免分离任务
468: # 浮现为未观察异常）。两处丢弃都不是无人观察的 fire-and-forget（async-lifetime.md「原语的永久豁免」）。
469: [src/WinForward.Runtime/QuiescenceScope.cs]
470: dotnet_diagnostic.WF0001.severity = none
```

The global rule block stays:

```editorconfig
449: [src/**.cs]
450: dotnet_diagnostic.WF0001.severity = error
451: dotnet_diagnostic.WF0002.severity = error
452: dotnet_diagnostic.WF0003.severity = error
453: dotnet_diagnostic.WF0004.severity = error
```

After C4 the only `severity = none` under `src/**` is `QuiescenceScope.cs` (WF0001). Note `.editorconfig:446-448` documents the non-awaitable forms that never trigger (`_ = task.Exception`, `_ = await ...`, `_ = TryWrite/TryAdd/SendTo/Interlocked.Add`, `_ = character switch`) — those remain outside the allowlist by design.

## Evidence the violating sites exist today

- `src/WinForward.Runtime/Capture/MultiAdapterCaptureLoop.cs:110`: `_ = ForwardDegradationAsync(_onAdapterDegraded, adapter, nativeError);` (awaitable `ValueTask` discarded → WF0001).
- `src/WinForward.Runtime/Capture/LayeredCaptureRunner.cs:144-146`: `var monitor = Task.Factory.StartNew(() => MonitorAsync(monitorCancellation.Token), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();` (WF0003).
- `src/WinForward.Runtime/Capture/LayeredCaptureRunner.cs:148-150`: `var periodicTick = _periodicRefreshInterval > TimeSpan.Zero ? Task.Run(() => PeriodicRefreshTickAsync(monitorCancellation.Token), monitorCancellation.Token) : null;` (WF0003).
- Exhaustive `rg` over `src/**` shows no other awaitable discards (the remaining `_ =` sites are non-awaitable or awaited; see `design-contradictions-and-hazards.md` C7 and `socks5-token-audit.md` C4).

## Rule-firing proof (scratch probe; repo left clean)

Two scratch files were created under `src/WinForward.Runtime/Capture/`, built with `dotnet build src/WinForward.Runtime/WinForward.Runtime.csproj -c Release`, then deleted; `git status --short -- src` was empty afterward in both cases.

### Probe 1 (task-required) — `_ = Task.Delay(1);`

File `ZzC4ScopeProbe.cs`:
```csharp
internal static class ZzC4ScopeProbe { public static void Probe() => _ = Task.Delay(1); }
```
`BUILD_EXIT=1`. Exact diagnostic:
```
/home/paff/Projects/WinForward/src/WinForward.Runtime/Capture/ZzC4ScopeProbe.cs(5,35): error WF0001: Await this awaitable or start it as a tracked child with QuiescenceScope.Run [/home/paff/Projects/WinForward/src/WinForward.Runtime/WinForward.Runtime.csproj]
```

### Probe 2 (extra) — `Task.Run` / `Task.Factory.StartNew`

File `ZzC4ScopeProbe.cs`:
```csharp
internal static class ZzC4ScopeProbe
{
    public static void ProbeRun() => _ = Task.Run(static () => { });
    public static void ProbeStartNew() => _ = Task.Factory.StartNew(static () => { });
}
```
`BUILD_EXIT=1`. Exact diagnostics:
```
ZzC4ScopeProbe.cs(5,42): error WF0003: Start tracked work with QuiescenceScope.Run, or use a dedicated worker its owner joins [...WinForward.Runtime.csproj]
ZzC4ScopeProbe.cs(5,38): error WF0001: Await this awaitable or start it as a tracked child with QuiescenceScope.Run [...]
ZzC4ScopeProbe.cs(6,60): error VSTHRD105: Avoid method overloads that assume TaskScheduler.Current. Use an overload that accepts a TaskScheduler and specify TaskScheduler.Default (or any other) explicitly. (...)
ZzC4ScopeProbe.cs(6,47): error WF0003: Start tracked work with QuiescenceScope.Run, or use a dedicated worker its owner joins [...]
ZzC4ScopeProbe.cs(6,43): error WF0001: Await this awaitable or start it as a tracked child with QuiescenceScope.Run [...]
```

Note: the probe file was **not** under any allowlist section, so it proves the rules fire for a non-allowlisted file at `src/WinForward.Runtime/Capture/`. It does not by itself prove removal of the two sections makes the real files fail — that follows because the real files contain exactly those constructs and have no other exemption.

## Consequences for the removal

- **Entry 2 is the hard one.** The `WF0003` diagnostic message itself offers an escape hatch: "**or use a dedicated worker its owner joins**". That aligns with keeping the monitor on a raw `Thread` (not matched by `WF0003`) with a completion bridge like `NdisCapturePump._runCompletion` (`src/WinForward.NdisApi/NdisCapture.cs:124`/`:417-423`) — required because `MonitorAsync` blocks and `scope.Run` invokes inline (`design-contradictions-and-hazards.md` H1/H2). Either way, deleting entry 2 means both `:144` and `:149` must be removed from the file.
- **Entry 1** is removable by tracking the forward through a scope (`scope.Run`) or by restructuring the callback; the design's literal "invoked synchronously" is not achievable (C1).
- The `.editorconfig` header at `:445` states every temporary entry is debt that must be repaid before the parent program is complete; C4 repays the last two.
