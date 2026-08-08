# Journal - Snowy117 (Part 1)

> AI development session journal
> Started: 2026-08-07

---

## 2026-08-07 — winforward-proxy check phase

- Verified build/tests on Linux and on the Win11 host (192.168.100.2 via evil-winrm-py, source copy at `C:\Users\Neko\WinForward`, SDK 10.0.102 with global.json patched on the copy only): 0 warnings/0 errors, 59/59 tests, Native AOT publish OK.
- AOT smoke on Win11 (driver 3.6.2.1 running, ndisapi.dll 3.6.1 sidecar): `validate` good/10 bad configs behave; `adapters` lists 5 adapters with stable GUID + friendly name after fixing MAC-only correlation (filter drivers clone MACs across ~6 NetworkInterface entries; GUID-primary correlation via `\DEVICE\{GUID}` == `NetworkInterface.Id`).
- trellis-check sub-agent (deepseek-v4-flash) added 19 tests and fixed adapter correlation; remaining gaps are the documented Windows-gated phases (TCP redirect, runtime wiring, adapter-change handling).

## 2026-08-07 — run milestone 1: pass/block on hardware

- trellis-implement (deepseek-v4-flash) wired `run` end-to-end (scope resolution, transactional tunnel modes, capture pumps, dispatcher with process attribution, pass/block executor); 78/78 tests.
- Hardware bring-up found a real driver contract: reinjection requests must use the enumeration handle, not the captured `m_hAdapter` (error 87 otherwise). Isolated with a scratch 3-variant harness (sendtest); fixed in `NdisCapture.cs`; spec updated.
- Win11 matrix all green: pass (100/100 ping, no DUP), block, proxy fail-closed + warn, Ctrl+C clean restore, process attribution (curl blocked / powershell passed), missing-adapter selector exit 1.
- WinRM test rig: configs + ctrlc.ps1 (GenerateConsoleCtrlEvent graceful stop) + watchdog.ps1 (90s auto-stop safety) under `C:\Users\Neko\wf-tests`; publish at `C:\Users\Neko\pub`. Test adapter = Ethernet 8 (192.168.77.2), WinRM stays on Ethernet (192.168.100.2).

---

