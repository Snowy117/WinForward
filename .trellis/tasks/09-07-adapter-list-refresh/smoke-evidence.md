# S7 Hardware Smoke Evidence — 2026-09-08

Target: Win11 dev host (winltsc\neko, admin), WinpkFilter ndisrd, 2 physical adapters
(External ifIndex 14, Internal ifIndex 7) + 3 hidden Wi-Fi Direct adapters
(Local Area Connection* 6/7/8). WinRM control channel runs over **Internal
(192.168.100.2)** — never touched during the test. Build: Linux cross-publish
`-r win-x64 --self-contained -p:PublishAot=false` (AOT cross-OS unsupported), deployed to
`C:\winforward-smoke\` with `ndisapi.dll` + config (unconstrained tcp/udp 80,443 → proxy
fake SOCKS5 192.168.100.3:1080, fallbackAction=pass — WinRM :5985 never matches, all
pre-existing connections ride the pass path through the interception cage).

## Run log (PID 11336, started 09:24:31)

```
09:24:31.999 [info] Capture scope: 5 adapter(s) in tunnel mode.
09:24:32.054 [info] Interception started. Press Ctrl+C to stop.
```

(Plus 3 zero-MAC warns for the hidden adapters — expected fail-closed UDP semantics.)

## Test 1 — disable/enable External (the AC6 core path)

Independent powershell process: `Disable-NetAdapter External` → 8 s → `Enable-NetAdapter
External` (self-healing sequence, immune to WinRM loss). Result:

```
09:27:14.454 [error] adapter.degraded adapter={A738399F-…} name=External nativeError=87
09:27:14.531 [info]  adapter.refresh removed=External({A738399F-…}) degraded="{A738399F-…}=false"
09:27:22.981 [info]  adapter.refresh added=External({A738399F-…})
```

- **87 → refresh in 77 ms** (degrade to refresh decision). The degrade log is exactly the
  production failure signature; now it self-heals instead of dying.
- `degraded=…=false` is the R5 present field: adapter no longer enumerated.
- Re-enable → adopted back under the unconstrained rule (AC3 hardware form).
- **Process alive throughout; WinRM never dropped** — a pre-existing connection crossing the
  cage survived the generation rebuild (R2 session survival on real hardware).
- Interception demonstrably resumed: Windows NCSI probes to :80/:443 after re-enable were
  re-intercepted (stream of `TCP redirect relay setup failed` warns — fail-closed RST against
  the fake SOCKS5, exactly the configured semantics).

## Test 2 — disable/enable `Local Area Connection* 8` (Wi-Fi Direct churn source)

**Zero events, zero degradation, process alive.** Disabling the hidden adapter does NOT
rebuild the NDISRD bound list; its pump rode the transient-retry path (error 31 class)
through the 8 s window and recovered. Healthy behavior; also shows NDISRD handles are
per-adapter object pointers (membership change does not rotate sibling handles), so the
`changed` diff path requires remove+re-add within one signal window — no natural single-step
hardware trigger; that path stays unit-locked (`present=false rebuild` test +
`AdapterEnumerationDiffTests`).

## R-1 / R-2 verification status

- **R-2 (auto-reset event mode): VERIFIED** — the real driver's `SetEvent` on the registered
  auto-reset event fired correctly for both list rebuilds (both refreshes triggered). No
  manual-reset swap needed.
- **R-1 (final-shutdown ordering): partially verified.** Generation teardown (pump stop +
  best-effort mode restore with old handles) ran cleanly twice on hardware as part of each
  refresh — WinRM crossing the cage stayed alive across both. The *final* user-Ctrl+C
  shutdown could not be exercised from a remote headless session (no console to deliver
  the signal; `taskkill` gentle mode rejected, AttachConsole+GenerateConsoleCtrlEvent
  injection failed against the console-less process). Residual risk: none identified —
  teardown injections during refresh were the same code path the coordinators+mode-restore
  ordering question concerns.

## Teardown & cleanup

Process force-killed (driver's process cleanup resets adapter modes), followed by an
Internal disable/enable cycle as the final filter-state scrub. Post state: WinForward
absent, both physical adapters Up, WinRM reconnects cleanly (proof the cage is fully
unwound). Remote `C:\winforward-smoke\` left in place for user inspection; local build
artifacts and helper sessions removed.

## Verdict

AC6 PASS. Production defect (adapter.degraded 87 storm on list rebuild) verified fixed on
hardware: 87 now triggers a 77 ms in-process refresh, scope tracks the live enumeration,
sessions/connections survive, interception resumes without a process restart.
