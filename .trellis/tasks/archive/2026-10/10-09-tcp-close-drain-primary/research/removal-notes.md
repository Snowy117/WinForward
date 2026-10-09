# Removal notes — the crafted clean-end FIN

Phase C3 of the close-drain task: delete the manual clean-end FIN path now that the drain is the
primary fix. Design §6 is the authoritative list; this note records the reference-surface audit,
what was kept, the red → green evidence, the verification results, and every deviation.

Everything below was done in the main worktree (`/home/paff/Projects/WinForward`), uncommitted.
`.trellis/spec/` and `benchmarks/` were not touched (other subagents own those).

---

## 1. Deleted symbols and the reference surface checked before each deletion

Each name was swept with `rg -n --heading <name> src tests benchmarks tools analyzers` (tracked
tree, no `-uu`); every hit outside the definition is listed.

| Symbol | Kind | Call sites before deletion |
|---|---|---|
| `ClientResetInjector.TryInjectClientCloseAsync` | public method | one: `TcpRedirectAcceptor.InjectClientVisibleCloseAsync` (clean-end branch). No test called it directly. |
| `ClientResetInjector.TryInjectClientCloseCoreAsync` | private method | two: the `reset:true` and `reset:false` delegations. Its RESET body is what remains, merged into `TryInjectClientResetAsync`. |
| `TcpResetBuilder.TryBuildFin` | public method | one: `ClientResetInjector`'s FIN branch. No test invoked it directly; its wire shape was pinned through the crafted-FIN facts in `TcpRelayEndCloseTests` (`AssertFin` asserted `frame[47] == 0x11`). |
| `TcpResetBuilder.TcpFinAck` (`0x11`) | private const | only `TryBuildFin`. |
| `TcpRedirectAcceptor.InjectClientVisibleCloseAsync` clean-end branch | private method | one: `ObserveRelayCompletionAsync`. The method is now `InjectAbnormalEndResetAsync` (RST-only) and is called only for non-clean ends. |
| `TcpRedirectLog.TcpRedirectClientClose` (event `tcp.redirect.clientClose`) | log event | one: `ClientResetInjector`'s FIN branch. |
| `TcpRedirectLog.TcpRedirectClientCloseInjectionFailed` | log event | one: `ClientResetInjector`'s FIN catch. |
| `TcpRedirectLog.TcpRedirectRelayEndCloseFailed` | log event | one: `TcpRedirectAcceptor`'s clean-end catch (deleted with the branch). |
| `TcpRelayEndCloseTests.CleanRelayEndInjectsClientFinBeforeTeardown` | fact | the crafted-FIN lock. |
| `TcpRelayEndCloseTests.AssertFin` / `AssertFinFromTrackers` | test helpers | only the deleted fact and the rewritten end-info fact. |
| `TcpRelayEndCloseTests.RelayServerStreamBytes` / `s_drainDeadline` | test consts | only the deleted fact; after the rewrite no fact in that file arms a drain, so the 250 ms deadline seam went with them. |

Post-change sweep: any hit for `TryBuildFin`, `TryInjectClientClose`,
`TcpRedirectClientClose`, `TcpRedirectRelayEndCloseFailed`, `InjectClientVisibleCloseAsync` or
`TcpFinAck` across `src tests benchmarks tools analyzers` returns nothing. The remaining
`clientClose`-shaped strings are the unrelated E2E ledger verdict `clientClosedEarly`.

## 2. Kept (no behaviour change)

- `RelayEndKind`, `EndKindName` and the `tcp.relay.ended` outcome text
  (`cleanEnded` / `stalled` / `faulted`) — unchanged.
- `ITcpRelayEndInfo.ServerStreamBytes` — now only the drain's target input
  (`TryComputeDrainTargetAck`).
- The abnormal-end crafted RST|ACK path in full: `TryInjectClientResetAsync` (session and
  association overloads), `HandleFragmentTeardownAsync`, `HandleRelaySetupFailureAsync`,
  `HandleInjectionFailureAsync`, `InjectCapacityRejectedResetAsync`, `TryBuildReset`,
  `TryBuildResetFromSyn`, `MaxResetFrameLength`, `TcpRedirectClientReset` /
  `TcpRedirectClientResetInjectionFailed` / `TcpRedirectCapacityReset*` /
  `TcpRedirectRelayEndResetFailed`.
- `DrainCleanEndAsync` and the 5 s `s_defaultDrainDeadline` (injectable for tests), every drain
  exit (arm / acknowledged / deadline / retired / no-target degradation), and the
  `tcp.redirect.drain` event with its fields.
- `TcpProxyRelay` unchanged: its client-facing socket close is the FIN the drain carries.

## 3. Test 7 — red → green

Fact: `TcpCloseDrainTests.CleanEndInjectsNoCraftedPacketAndDrainsUntilTheClientAcknowledges`
(clean end, `ServerStreamBytes = 5`, 30 s unreachable drain deadline, a `StepInjector` that
records every crafted frame).

**RED — before the removal** (the clean end injected the crafted FIN):

```
dotnet test tests/WinForward.Runtime.TcpRedirect.Tests/WinForward.Runtime.TcpRedirect.Tests.csproj -c Release \
  --filter "FullyQualifiedName~CleanEndInjectsNoCraftedPacketAndDrainsUntilTheClientAcknowledges"

Failed WinForward.Runtime.TcpRedirect.Tests.TcpCloseDrainTests.CleanEndInjectsNoCraftedPacketAndDrainsUntilTheClientAcknowledges
  Assert.Empty() Failure: Collection was not empty
  Collection: [[0, 0, 0, 0, 0, ···]]
Failed!  - Failed: 1, Passed: 0, Skipped: 0, Total: 1
```

The assertion that fails is the post-`Draining` `Assert.Empty(injector.Frames)`, so the failure is
"one crafted packet was injected", not a timing artefact. That packet is the FIN|ACK: the same
revision's `CleanRelayEndInjectsClientFinBeforeTeardown` asserted its flags byte is `0x11` and the
FIN builder is the only injection on a clean-end path. The whole affected project at that point:
`Passed 168, Failed 1, Total 169` (the only red is test 7).

**GREEN — after the removal** (project run): `Passed 168, Failed 0, Total 168`.

The degradation lock was realised by strengthening the existing
`CleanEndWithoutObservedSequencesRetiresImmediatelyWithoutADrain` with
`Assert.Empty(injector.Frames)` and `Assert.Equal(0, relay.DisposeCount)` (in addition to the
pre-existing `["teardown"]`, no-`Draining`, no-`tcp.redirect.drain` assertions) instead of
adding a second fact with the same assertions.

## 4. Test count, before → after

| Scope | Before | After | Delta |
|---|---|---|---|
| `TcpRelayEndCloseTests.cs` | 8 facts | 7 facts | −1 (the FIN fact) |
| `TcpCloseDrainTests.cs` | 11 facts | 12 facts | +1 (test 7) |
| `WinForward.Runtime.TcpRedirect.Tests` project | 168 facts (green) | 168 facts (green) | 0 (the new test 7 red run totals 169) |

## 5. Deviations from design §6 / the task text

1. **The "extra degradation fact" is a strengthening, not a new fact.** The task asked for one
   more degradation fact; the coverage ("sequence-unknown clean end injects nothing, does not
   drain, retires immediately") is now asserted in
   `CleanEndWithoutObservedSequencesRetiresImmediatelyWithoutADrain` with the frame-recording
   injector and the disposal count. Adding a near-identical fact would have duplicated every
   assertion without adding coverage.
2. **`TryInjectClientCloseCoreAsync` was merged, not just stripped.** With the FIN flavour gone
   it had only one caller, so its RESET body lives in the public `TryInjectClientResetAsync`
   (same early returns, same non-async `ValueTask` shape, same direction matrix).
3. **`TcpResetBuilder.TryBuild`'s `tcpFlags` parameter was kept** so `TryBuildReset` stays
   literally untouched as required; only `TryBuildFin` and `TcpFinAck` were removed.
4. **`RelayWithoutEndInfoIsTreatedAsCleanEnd` was rewritten**, not deleted: an end-info-less relay
   still ends as a clean end (that is what degrades the drain), so the fact now asserts "no frames,
   teardown-only" instead of the crafted FIN from the trackers.
5. **Nothing in `.trellis/spec/` or `benchmarks/` was edited**; the spec sync and the allocation
   gate are owned by other subagents working in the same worktree.

## 6. Verification

| Command | Result |
|---|---|
| `dotnet test tests/WinForward.Runtime.TcpRedirect.Tests/WinForward.Runtime.TcpRedirect.Tests.csproj -c Release` | `Passed! Failed: 0, Passed: 168, Skipped: 0, Total: 168` |
| `dotnet build WinForward.slnx -c Release` | `Build succeeded. 0 Warning(s) 0 Error(s)` |
| `dotnet format WinForward.slnx --include <the six changed files> --verify-no-changes --no-restore` | exit 0, empty output |
| `dotnet test WinForward.slnx -c Release -m:1` | exit 0 — 14 projects, `passed 1678, failed 0, skipped 0` (includes the tests the concurrent benchmarks subagent added; this removal's own project is 168) |

The full-suite run overlapped another subagent editing `benchmarks/` and
`tests/WinForward.Performance.Tests/` in the same worktree; any MSB file-lock / OOM / thread-start
failure of that run is environmental and would be retried serially before being treated as
evidence.
