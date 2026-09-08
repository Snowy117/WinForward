# Refresh adapter enumeration on list-change to survive handle invalidation

## Goal

WinForward must survive Windows network reconfiguration events (adapter plug/unplug,
enable/disable, standby/resume, Wi-Fi Direct virtual adapter churn) that rebuild the NDISRD
driver's TCP/IP-bound adapter list. Today those events invalidate every cached enumeration
handle: reads fail with `ERROR_INVALID_PARAMETER` (87), all capture pumps degrade, and the
process keeps running with zero interception until a manual restart. After this task the same
events trigger an in-process refresh that re-enumerates, re-applies tunnel modes, and restarts
the pumps with fresh handles while the process — and to the extent possible the proxy sessions —
keep running.

Production evidence (2026-09-07, user log): `adapter.degraded adapter={2B8613AD-…} name=以太网
nativeError=87` plus `本地连接* 8/9/10` degrading together — a whole-list rebuild invalidating
all handles simultaneously, including the physical NIC's.

## Requirements

- R1 (event subscription): register the driver's adapter-list-change notification
  (`SetAdapterListChangeEvent`, export verified in `smoke/ndisapi.dll`) and react by refreshing
  the capture generation. Registration is released on shutdown (pass NULL, per official docs).
- R2 (layered refresh): a refresh stops the current generation (pumps + adapter modes), then
  re-enumerates, re-resolves scope, re-snapshots/re-applies tunnel modes, and restarts pumps on
  fresh handles. Durable components — TCP/UDP coordinators, redirect table, relays,
  self-traffic registry, dispatcher, idle sweeper, driver handle, buffer pool — are NOT
  disposed across a refresh; sessions survive and recover via retransmission.
- R3 (defense-in-depth trigger): a pump degrading with native error 87 must trigger an
  enumeration re-check and a refresh if the bound list changed, with a refresh-storm guard
  (minimum refresh interval; no rebuild when the enumeration is unchanged) so a genuine bug
  cannot cause a refresh loop.
- R4 (refresh-time scope semantics — deliberately non-fatal, unlike startup): an adapter that
  disappeared drops out of scope with a warn; a newly appeared adapter joins scope only when the
  policy widens to all MSTCP-bound adapters (existing resolver semantics); an ambiguous
  name-based selector skips the ambiguous addition with a warn. No refresh-time scope condition
  is fatal. Startup resolution behavior is unchanged (missing/ambiguous selectors stay fatal).
- R5 (honest telemetry): a structured `adapter.refresh` info event records the generation
  transition — adapters added / removed / handle-changed (name + stableId each) and whether a
  rebuild was skipped as no-op. Degradation diagnostics distinguish "adapter no longer
  enumerated" from "handle stale while adapter present".
- R6 (shutdown order): final shutdown keeps today's disposal semantics as closely as the layer
  split allows (pumps → coordinators → mode restore); any deviation is verified in smoke and
  covered by the documented fallback (see design R-1).
- R7 (no regression): existing startup, capture, redirect, and UDP behavior unchanged outside
  the refresh path; existing test suite stays green.

## Acceptance Criteria

- [ ] AC1: Unit tests drive a fake list-change source and fake NDIS reader through a refresh:
      pumps are rebuilt with the new handles, mode snapshot/apply runs against the new
      enumeration, and the durable coordinators are not disposed.
- [ ] AC2: Adapter absent from the fresh enumeration → dropped from scope with a warn; the run
      continues on remaining adapters.
- [ ] AC3: New adapter appears while policy contains an unconstrained rule → adopted into scope
      and intercepted after the refresh.
- [ ] AC4: Degradation with native error 87 on an unchanged enumeration → honest log, no
      rebuild loop (bounded re-check).
- [ ] AC5: Refresh-storm guard: signals arriving faster than the minimum interval coalesce into
      one rebuild; an enumeration identical to the current generation is a logged no-op.
- [ ] AC6: Manual smoke on the Windows target: while WinForward runs, disable/enable a NIC (and
      where available trigger Wi-Fi Direct churn or standby/resume) → `adapter.refresh` logs
      the transition and interception resumes without a process restart.
- [ ] AC7: `dotnet build` + full `dotnet test` + repo lint/format checks pass; no public API
      breaks outside `WinForward.NdisApi`/`WinForward.Runtime` additions.

## Constraints

- Windows-only feature; all new platform code carries `[SupportedOSPlatform("windows")]`.
- No ndisrd driver changes; everything through the documented ndisapi DLL exports.
- `SetAdapterListChangeEvent` handle semantics: user-created Win32 event, NULL releases
  (official reference; see research note in
  `.trellis/tasks/archive/2026-08/08-07-winforward-proxy/research/winpkfilter-ndisapi-design-constraints.md`).
- The transient-error table (`NdisNativeCallStatus.IsTransientReadError`) is NOT extended with
  87: with R3 in place, 87 on a present adapter remains a genuine defect signal.
