# Research: hardening the UDP allocation gate (thread-stability)

- **Query**: C3 question 7 — the exact measured window of `EstablishedUdpDatagramPathAllocatesNoManagedBytes`, the collaborator it drives, and every existing single-thread-scheduler helper
- **Scope**: internal
- **Date**: 2026-09-21

## The test and its exact measured window

`tests/WinForward.Core.Tests/HotPathAllocationGateTests.cs`,
`EstablishedUdpDatagramPathAllocatesNoManagedBytes` `:83-122`:

```
:86   var factory = new CountingTransportFactory();
:87   await using var coordinator = UdpCoordinatorFakes.CreateCoordinator(factory, new NoopResponseSink());
:88   var reinjector = new FakeReinjector();
:89   var executor = new NdisPacketActionExecutor(reinjector, udpProxy: coordinator);
...
:100  await executor.ProxyAsync(packet, s_server, CancellationToken.None);          // cold: arms setup
:101  await WaitForAsync(() => factory.Transport is not null);
:102  await WaitForAsync(() => factory.Transport!.Sends >= 1);
:103  for (var warm = 0; warm < 3; warm++) await executor.ProxyAsync(packet, s_server, CancellationToken.None);
:105  var spanSendsBeforeMeasure = factory.Transport!.SpanSends;
:106  var before = GC.GetAllocatedBytesForCurrentThread();
:107  const int count = 64;
:108  for (var index = 0; index < count; index++) await executor.ProxyAsync(packet, s_server, CancellationToken.None);
:109  var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
:111  Assert.Equal(0, allocated);
:116  Assert.Equal(count, factory.Transport!.SpanSends - spanSendsBeforeMeasure);
:117  Assert.Equal(4 + count, factory.Transport!.SpanSends);
:118  Assert.Equal(4 + count, factory.Transport!.Sends);
:120  Assert.Equal(0, reinjector.ToMstcpCount);
:121  Assert.Equal(0, reinjector.ToAdapterCount);
```

- **Measured window**: lines `105-109` (before-read `:106`, 64 dispatches `:108`, after-read `:109`).
  The PRD's "lines 106-111" is close but not exact (see `design-contradictions-and-hazards.md`).
- **Count** = 64, matching the PRD.
- The window spans **64 `await`s** (`executor.ProxyAsync` is `async ValueTask`).

### Collaborator driven (fake, not real)

- `CountingTransportFactory` `:402-411` produces a single `CountingTransport` `:423-448`.
- `CountingTransport.SendSpanAsync` `:435-439` increments `_spanSends` and returns `ValueTask.CompletedTask`
  synchronously.
- `CountingTransport.ReceiveAsync` `:441` parks on `_parkedReceive` (so the session's receive loop stays
  quiet through the measurement).
- `Sends` and `SpanSends` are the same counter (`:431,:433`).
- `NoopResponseSink` (`TestHelpers/UdpTransportFakes.cs:87-91`).
- The packet is built once at `:91-96` (reused across all 64 dispatches; `UdpFrameBuilder.TryBuildInto`).
- Setup/warm: cold dispatch `:100` arms the background setup; `:101-102` waits for the transport and first
  send; `:103` does 3 warm dispatches.

So the gate is a **fake-collaborator** allocation gate. Per `hot-path.md` §"Allocation gates must exercise
the real production collaborator" (lines 399-404), a fake cannot observe an allocation trap inside the real
`Socks5UdpTransport`; the real-collaborator counterpart is
`Socks5UdpTransportSendTests.WarmSyncSendAllocatesNoManagedBytes` (`hot-path.md:443`).

## Existing single-threaded scheduler / SynchronizationContext helpers

**There are none.** Verified by repo-wide `rg`:

- `rg "SynchronizationContext"` → **zero matches** in the entire repo (src, tests, benchmarks) — no
  occurrences at all, including `obj`/generated files.
- `rg "TaskScheduler"` matches only:
  - `src/WinForward.Runtime/TcpRedirect/TcpProxyRelay.cs:293` — `TaskScheduler.Default` in `ObservePump`.
  - `src/WinForward.Runtime/TcpRedirect/TcpRelayFaultObserver.cs:40` — `TaskScheduler.Default` in `Observe`.
  - `src/WinForward.Runtime/Capture/LayeredCaptureRunner.cs:146` — `Task.Factory.StartNew(..., LongRunning, TaskScheduler.Default).Unwrap()`.
  - `benchmarks/WinForward.Benchmarks/Stability/LoopbackSocks5UdpServer.cs:112` and `LoopbackSocks5TcpServer.cs:94` — `TaskScheduler.Default` continuations.
  - `tests/WinForward.Core.Tests/QuiescenceScopeTests.cs:247,275` — `TaskScheduler.UnobservedTaskException` subscribe/unsubscribe (the `UnobservedExceptionProbe`).
- `tests/WinForward.Core.Tests/TestHelpers/AsyncTestExtensions.cs` (35 lines) contains only
  `WaitForAsync` `:13` and `IgnoreExpectedCancellationAsync` `:24`.
- There is **no** `OnSingleThreadedContextAsync` helper anywhere, even though `hot-path.md`'s
  "Wrong vs Correct" example (lines 310-329) shows it as the *correct* pattern.

## The load-bearing finding: `ConfigureAwait(false)` defeats a `SynchronizationContext`

`hot-path.md` §"An allocation gate must evaluate on one thread" (lines 248-262) prescribes:
> "Fix the window, not the threshold — install a single-threaded `SynchronizationContext` (or scheduler) for
> the measured region so every continuation resumes on the measuring thread, or keep the window synchronous."

The measured call chain is:

```
test await executor.ProxyAsync(...)
  NdisPacketActionExecutor.ProxyAsync            async ValueTask   src/WinForward.Runtime/Capture/NdisPacketActionExecutor.cs:356
    await HandleUdpProxyAsync(...).ConfigureAwait(false)          :360
      await udpProxy.TrySendSpanAsync(...).ConfigureAwait(false)  :450
        UdpProxyCoordinator.TrySendSpanAsync   non-async         UdpProxyCoordinator.Send.cs:20
          SendOnReadySessionSpanAsync          non-async         :83
            UdpProxySession.SendSpanAsync      ValueTask         UdpProxySession.cs:139
              CountingTransport.SendSpanAsync  ValueTask.CompletedTask (synchronous)  HotPathAllocationGateTests.cs:435
            FinishSpanSendAsync / SendSpanTailAsync:
              await send.ConfigureAwait(false)  UdpProxySession.cs:171 / UdpProxyCoordinator.Send.cs:121
```

**Every `await` in the production chain uses `.ConfigureAwait(false)`** (`NdisPacketActionExecutor.cs:360,372,400,450`;
`UdpProxySession.cs:171`; `UdpProxyCoordinator.Send.cs:121`). A `SynchronizationContext` installed by the test
is only observed by awaits that *do not* pass `ConfigureAwait(false)` (i.e. default/`ConfigureAwait(true)`).
Therefore **installing a single-threaded `SynchronizationContext` cannot force these continuations back onto
the measuring thread** — the continuations will still be scheduled to the ThreadPool by the `ConfigureAwait(false)`.

Two consequences to flag to the main agent:

1. The first prescribed fix in `hot-path.md` (and repeated in the PRD: "make the measured window
   thread-stable (single-threaded `SynchronizationContext`)") **does not apply to this call chain** as
   written. Only the second option ("assert a bound plus the thread-independent `SpanSends` counter") is
   directly actionable without changing production `ConfigureAwait` usage.
2. On the *warm* path with `CountingTransport`, the entire chain completes **synchronously** (the fake's
   `SendSpanAsync` returns `ValueTask.CompletedTask`), so in principle no continuation runs and the window
   is already thread-stable — which makes the observed run-alone failure (`Expected: 0, Actual: 600`) need a
   mechanism other than warm-path thread migration. The spec attributes it to a cold pool/continuation
   migration (`hot-path.md:248-255`), but the fake's synchronous send path and the `ConfigureAwait(false)`
   chain are in tension with that explanation. **C3 should reproduce the 4/4 failure and identify the actual
   allocating continuation before choosing a fix.** (Research role: report the tension; do not pick the fix.)

### Thread-independent backstop already present

The gate already pairs the byte assertion with a call counter: `SpanSends` delta `:116` and absolute
`:117-118`. Per `hot-path.md:260-262`, this catches a regression regardless of thread migration. The
remaining gap is only the byte assertion's validity/window.

## Related gate facts

- `dotnet test WinForward.slnx -c Release --filter "FullyQualifiedName~EstablishedUdpDatagramPathAllocatesNoManagedBytes"`
  is the isolation command the PRD/AC requires to pass (`prd.md:38-40`; `hot-path.md:286-289`).
- The class has 10 `[Fact]`s, matching the PRD's "the whole `HotPathAllocationGateTests` class alone leaves
  1 of 10 failing".
- `QuiescenceScopeAllocationGateTests.WarmEnterExitPairAllocatesNoManagedBytes` (`:9-29`) is the C1
  analogue: a fully **synchronous** window (no `await`), with warm-up `:13-17`, measured loop `:21-25`, 0-B
  assert `:28`. It is the shape `hot-path.md` calls "keep the window synchronous".

## Caveats / Not Found

- No `SynchronizationContext` implementation exists anywhere to reuse; C3 either adds one and verifies it is
  honoured (it will not be, given `ConfigureAwait(false)`), or adopts the counter+bound option.
- `NdisPacketActionExecutor` was read only at `:340-479` for this question; other awaits in the class were
  not exhaustively enumerated, but the four in the measured chain are confirmed.
