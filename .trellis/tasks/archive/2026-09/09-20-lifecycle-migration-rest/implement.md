# C4 — Migrate the remaining lifecycle owners: implementation plan

Ordered checklist. Each step ends with the build green and the suite passing. **The `.editorconfig`
section for a file is deleted in the same step that migrates it** — a partial step must never leave a
site both unmigrated and unexempted. Every step also adds that owner's quiescence test (the shape C3
established: `DisposeAsync` does not complete while work is outstanding; it does once the work ends).

1. **`LayeredCaptureRunner`** (`design.md` §2.1) — the hardest step; do it first.
   - Replace `Task.Factory.StartNew(…, LongRunning)` (`:144-146`) with a dedicated `Thread` whose body
     admits itself into the run scope with a lease and runs the blocking loop
     (`MonitorAsync` → synchronous `MonitorLoop`); delete the `Unwrap()`.
   - Replace `Task.Run(…)` (`:148-150`) with `_scope.Run(PeriodicRefreshTickAsync, "capture.periodic-refresh")`,
     keeping the `_periodicRefreshInterval > TimeSpan.Zero` guard **outside** `Run`.
   - Create the scope in `RunAsync` (`new QuiescenceScope(cancellationToken)`) and delete the local
     `monitorCancellation`; `TeardownAsync` becomes `StopGenerationAsync` → `_scope.Cancel()` →
     `await _scope.DrainAsync()` → durable dispose (the drain is the join for both the monitor lease and
     the tick).
   - Extract the refresh workers to a new file (D-C4-3) and get the file to **≤400 effective lines**.
   - Delete `.editorconfig:460-463`.
   - Gates: `LayeredCaptureRunnerTests`, `LayeredCaptureRunnerPeriodicRefreshTests`,
     `LayeredCaptureRunnerHealthSignalTests` green; the cap check; the new quiescence test
     (disposal does not complete until an in-flight monitor/tick ends).
2. **`MultiAdapterCaptureLoop`** (`design.md` §2.2) — scope + `Run` forward + D11 drain after the
   pumps. Delete `.editorconfig:455-458`.
   - Gates: `CaptureDegradationPlumbingTests` + `NdisCaptureResilienceTests` green; new tests for
     (a) the quiescence shape and (b) a forward in flight when `DisposeAsync` runs is awaited.
3. **`CaptureLifecycle`** (`design.md` §2.3) — `_shutdown` → scope-owned CTS; drain as the last step of
   `CleanupCoreAsync`; the run task stays unregistered.
   - Gates: `CaptureLifecycleTests` (especially `ConcurrentStopWaitsForCaptureRunBeforeRestoringModes`
     and `ConcurrentStopsBeforeStartWaitForTheSameCaptureCleanup`) and `DurableCaptureBundleTests` green;
     new test that `StopAsync`'s in-flight run is joined before `Closed`.
4. **`IdleExpirySweeper` + `RuntimeHeartbeat`** (`design.md` §2.4) — scope-owned CTS + `Run` loop child
   + `_started` guard + D11; `Start()` maps a `Run` refusal to the existing throw.
   - Gates: `RuntimeHeartbeatTests` (`SecondStartIsRejected`, `DisposeStopsFurtherTicks`),
     `IdleExpirySweeperFailureTests`, `DurableCaptureBundleTests` green; new tests for the quiescence
     shape (a slow tick keeps `DisposeAsync` pending), the double-dispose join (no
     `ObjectDisposedException`), and an in-flight tick joined before disposal returns.
5. **`Socks5ControlConnection`** (`design.md` §2.5) — add the scope + D11; keep the epoch deadline CTS
   but link it to the scope token; make every `AttemptToken` reader hold a lease; order
   `DisposeAsync` as claim → `Cancel()` → stream → loop-prevention → epoch CTS → drain.
   - Gates: `Socks5ControlTimeoutTests`, `Socks5UdpAssociateTests`, `Socks5UdpConnresetTests` green;
     new test that a concurrent `RunWithinAttemptAsync` during `DisposeAsync` never surfaces
     `ObjectDisposedException` from a token read.
6. **`Socks5UdpTransport`** (`design.md` §2.6) — the `Interlocked` `_disposed` guard + D11 only; fail
   closed before `_sendGate.WaitAsync`. Do not touch the warm shape, the buffer reuse, or the gate
   order.
   - Gates: `WarmSyncSendAllocatesNoManagedBytes`, `SendSpanAsyncWarmPathRunsNoAsyncStateMachine`,
     `HotPathAllocationGateTests` (alone and in the full suite), `UdpReceiveResilienceTests`.
7. **Boundary.** `rg -n '_ = |\.ContinueWith\(|Task\.Run|Task\.Factory\.StartNew' src/` returns only the
   primitive's exemption; `.editorconfig` under `src/**` holds only that exemption; `git diff --stat`
   shows only the intended owners plus the spec files.
8. **Spec updates** (`design.md` §6) and the final full gate run.

## Validation commands

```bash
dotnet format WinForward.slnx --severity info --verify-no-changes --no-restore   # exit 0, empty output
dotnet build WinForward.slnx -c Release                                          # 0 warnings, 0 errors
dotnet test WinForward.slnx -c Release                                           # all green
jb inspectcode -f=Xml -e=HINT -o=/tmp/jb-inspectcode-c4.xml WinForward.slnx      # zero <Issue >
```

Filtered runs while iterating (never mask an exit code — no `| tail`):

```bash
dotnet test WinForward.slnx -c Release --filter "FullyQualifiedName~LayeredCaptureRunner"
dotnet test WinForward.slnx -c Release --filter "FullyQualifiedName~CaptureLifecycle"
dotnet test WinForward.slnx -c Release --filter "FullyQualifiedName~EstablishedUdpDatagramPathAllocatesNoManagedBytes"
dotnet test WinForward.slnx -c Release --filter "FullyQualifiedName~Socks5"
```

`jb inspectcode` exits 0 even with findings — parse the XML and count `<Issue ` (not
`grep -c '<Issue'`, which also matches `<Issues />` / `<IssueTypes />`).

## Risky files and rollback

- `LayeredCaptureRunner.cs` — step 1 carries the two `WF0003` sites, the file is over the cap, and the
  refresh-worker extraction is the one place a behaviour-zero move can drift. It is also the step where
  the whole design is load-bearing (the monitor cannot be a `Run` child). Do it first, alone, with the
  full runner test set as the oracle (they assert behaviour, not mechanism).
- `CaptureLifecycle.cs` — the three existing disposal layers must not be duplicated by the drain
  (H12); `_runTask` must stay unregistered or `StopAsync` deadlocks (H3).
- `Socks5UdpTransport.cs` — the sole hot-path file in this task; any added allocation on
  `SendSpanAsync` breaks three gates. Change only the guard.
- `Socks5ControlConnection.cs` — the timeout semantics are test-pinned; resist "improving" the deadline
  shape while moving it.
- Rollback: each step is a self-contained change; a step's `.editorconfig` entry is restored only
  together with the code it exempts (never restore an exemption for a site that is not there).

## Pre-start follow-up checks

- `git status --short -- src` clean at HEAD so the boundary check means something.
- `.editorconfig` still has both C4 sections at HEAD (the shrink step has something to measure).
- `research/design-contradictions-and-hazards.md` is required reading before step 1: C2/H1/H2 are the
  reason the monitor is not a `Run` child, and a step that contradicts a hazard must say so explicitly
  rather than silently diverge.
- Confirm the new spec text for `async-lifetime.md` §D7's operation-vs-lifetime CTS line (D-C4-1)
  before step 5, since the SOCKS5 shape is the first place it applies.
