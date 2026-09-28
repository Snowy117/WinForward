# C3 — Migrate the TCP/UDP lifecycle cluster: implementation plan

Ordered checklist. Each step ends with the build green and the suite passing; the allowlist section for a
file is removed **in the same step** that migrates it (a partial step must never leave a site both
unmigrated and unexempted).

1. **Harden the allocation gate** (`design.md` §3). Reproduce the isolation failure first
   (`dotnet test WinForward.slnx -c Release --filter "FullyQualifiedName~EstablishedUdpDatagramPathAllocatesNoManagedBytes"`),
   then determine whether the driven path can complete without yielding. Make the measured window
   thread-stable by construction if it can (assert the managed thread id is stable + keep
   `Assert.Equal(0, allocated)`), otherwise keep the thread-independent `SpanSends` counter and replace
   the exact zero with a documented bound. Record the outcome in `hot-path.md`.
   Gate: the filtered run passes **alone** and in the full suite; no other allocation gate regresses.
2. **Add `QuiescenceScope.IsSealed`** (`design.md` §2) plus its tests (sealed before drain completes,
   false when never sealed) and the `async-lifetime.md` surface entry.
   Gate: full build green; the C1 allocation gate untouched and green.
3. **Migrate `TcpRedirectSession` + `TcpRedirectSessionStore`** (`design.md` §4.1, §4.2), including the
   call-site remap in `TcpProxyCoordinator` (`:259-283`) and the accept-loop lease in
   `TcpRedirectAcceptor`.
   Delete from `.editorconfig`: the `TcpRedirectSessionStore.cs` section.
   Gates: per-owner quiescence test; `DisposeIsSingleFlightAndLateTeardownNeverReEnters` green;
   single-flight idempotency of `DisposeLifetime`.
4. **Migrate `TcpProxyRelay`** (`design.md` §4.3), delete `TcpRelayFaultObserver.cs` and
   `TcpRedirectAcceptor.cs:120` (§4.4), make `ShutdownSend` synchronous at `:284`.
   Delete from `.editorconfig`: the `TcpProxyRelay.cs` and `TcpRelayFaultObserver.cs` sections.
   Gates: fault injection — no unobserved task exception, `tcp.relay.faulted` still recorded; bounded
   drain under the stall fast-exit; no spurious client reset when disposing mid-transfer;
   `TcpProxyRelayTests` / `TcpRelayEndResetTests` / `TcpRelayObservationTests` green.
5. **Migrate `UdpProxySession`** (`design.md` §4.4): scope-owned CTS, lease-based send admission,
   `scope.Fault` as the single failure cause read under `_activityGate`, `Action<UdpProxySession>` signal.
   Delete from `.editorconfig`: the `UdpProxySession.cs` section.
   Gates: `IdleExpiryEndsTheReceiveLoopWithoutRecordingAFailure` and
   `GenuineReceiveFaultRecordsFaultedAndFiresTheFailureHandler` green; the thread-independent send
   counter unchanged.
6. **Migrate `UdpProxyCoordinator`** (`design.md` §4.5): scope-owned CTS, `_scope.Run` for the
   receive-failure teardown with the warning inside the body, `_inFlightTeardowns` +
   `DrainInFlightTeardownsAsync` deleted, the scope sealed after `await slot.Completion` and before the
   teardown drain, `IUdpSessionSlotHost`/`UdpSessionSetup` seam signature updated.
   Gates: `ConcurrentDisposalIsSingleFlightAndRejectsNewSends` green; an in-flight teardown still
   completes (D-C3-8); the setup-failure slot removal still works (D-C3-7).
7. **Confirm the boundary.** `rg -n '_ = |\.ContinueWith\(|Task\.Run|Task\.Factory\.StartNew'
   src/WinForward.Runtime/TcpRedirect src/WinForward.Runtime/UdpProxy` returns nothing; the four
   `.editorconfig` cluster sections are gone; `git diff` shows only the cluster files plus the intended
   call-site edits.
8. **Spec updates** (`design.md` §8) and the final full gate run.

## Validation commands

```bash
dotnet format WinForward.slnx --severity info --verify-no-changes --no-restore   # exit 0, empty output
dotnet build WinForward.slnx -c Release                                          # 0 warnings, 0 errors
dotnet test WinForward.slnx -c Release                                           # all green
jb inspectcode -f=Xml -e=HINT -o=/tmp/jb-inspectcode.xml WinForward.slnx         # zero <Issue >
```

Filtered runs while iterating:

```bash
dotnet test WinForward.slnx -c Release --filter "FullyQualifiedName~EstablishedUdpDatagramPathAllocatesNoManagedBytes"
dotnet test WinForward.slnx -c Release --filter "FullyQualifiedName~TcpProxyRelayTests"
dotnet test WinForward.slnx -c Release --filter "FullyQualifiedName~UdpProxyCoordinator"
```

Never mask an exit code (no `| tail`). `jb inspectcode` exits 0 even with findings — parse the XML, and
count `<Issue ` elements rather than `grep -c '<Issue'` (which also matches `<Issues />`).

## Risky files and rollback

- `src/WinForward.Runtime/QuiescenceScope.cs` is C1's shipped, independently verified artifact. C3 adds
  one cold-path property; do not otherwise touch `TryEnter`/`Exit`/`DrainAsync`, and re-run the C1
  allocation gate after the edit.
- `src/WinForward.Runtime/TcpRedirect/TcpProxyRelay.cs` carries the trickiest semantics in the cluster
  (three exit paths, `Completion`'s relay-level contract, the abandoned pump). Migrate it in one step and
  keep the existing tests as the behaviour oracle.
- `src/WinForward.Runtime/UdpProxy/UdpProxySession.cs` and `UdpProxyCoordinator.cs` interact through the
  `IUdpSessionSlotHost` seam; the handler-signature change touches `UdpSessionSetup.cs:100` and the
  interface. Change producer and consumers in the same commit or the build breaks mid-step.
- Rollback: each step is a self-contained commit-shaped change. The allowlist entries are restored by
  `git checkout -- .editorconfig` only if the corresponding code change is also reverted; never restore
  an exemption without restoring the site it exempts.

## Pre-start follow-up checks

- `git status --short -- src` must be clean at HEAD so the boundary check means something.
- The four cluster allowlist sections must exist in `.editorconfig` at HEAD (C2's verified state) —
  a missing section means the baseline moved and the shrink step has nothing to measure.
- `research/design-contradictions-and-hazards.md` is required reading before step 3: H5–H12 are the
  traps this plan steers around, and a step that contradicts one of them must say so explicitly rather
  than silently diverge.
