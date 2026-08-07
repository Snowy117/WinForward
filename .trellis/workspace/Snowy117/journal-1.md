# Journal - Snowy117 (Part 1)

> AI development session journal
> Started: 2026-08-07

---

## 2026-08-07 — winforward-proxy check phase

- Verified build/tests on Linux and on the Win11 host (192.168.100.2 via evil-winrm-py, source copy at `C:\Users\Neko\WinForward`, SDK 10.0.102 with global.json patched on the copy only): 0 warnings/0 errors, 59/59 tests, Native AOT publish OK.
- AOT smoke on Win11 (driver 3.6.2.1 running, ndisapi.dll 3.6.1 sidecar): `validate` good/10 bad configs behave; `adapters` lists 5 adapters with stable GUID + friendly name after fixing MAC-only correlation (filter drivers clone MACs across ~6 NetworkInterface entries; GUID-primary correlation via `\DEVICE\{GUID}` == `NetworkInterface.Id`).
- trellis-check sub-agent (deepseek-v4-flash) added 19 tests and fixed adapter correlation; remaining gaps are the documented Windows-gated phases (TCP redirect, runtime wiring, adapter-change handling).

---

