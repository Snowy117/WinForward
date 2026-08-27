# Implementation Plan — udp-reinject-fix

Ordered checklist with validation commands and rollback points. Hardware steps run on the
remote Windows test machine via `evil-winrm-py -i 192.168.100.2 -u neko -p "$NEKO_PASS"`.

## Step 0 — Baseline green

- [ ] `dotnet build` OK, `dotnet test` green on Linux dev box.
- Rollback point: clean tree.

## Step 1 — Timestamped logging (R1)

- [ ] Modify the plain-text sink in `src/WinForward.Runtime/RuntimeLogging.cs` to prefix
      local time `yyyy-MM-dd HH:mm:ss.fff` on every emitted line.
- [ ] Adjust affected unit tests (exact-output asserts) to tolerate/verify the prefix.
- [ ] Update README log-format section.
- [ ] Validate: `dotnet build && dotnet test`.
- Commit candidate 1 (after check) — independent, revertable.

## Step 2 — Test-machine experiment harness (R2)

- [ ] Connect to 192.168.100.2; inventory adapters (`WinForward.exe adapters`), check
      whether q or an equivalent UDP DNS query tool exists; else use PowerShell UDP client
      script (`System.Net.Sockets.UdpClient` → send DNS A query → wait reply).
- [ ] Deploy a SOCKS5 UDP-capable server on the test box (sing-box single mixed inbound,
      minimal config, no TUN) OR reuse existing infra if present.
- [ ] Deploy current WinForward build + a config mirroring the user's rule shape
      (catch-all proxy, multi-adapter capture).
- [ ] Run loop: 20 fresh-port DNS queries; record per-attempt success/failure and the
      trace log (now timestamped).
- [ ] For a failing attempt: `pktmon` filter on port 53 across all adapters; correlate the
      reinjected frame's adapter vs the query's adapter. Document in
      `research/findings.md`.
- Decision gate: H-A / H-B / H-C discriminated? If none fits, extend instrumentation
      (e.g. log NDISAPI call return codes) and re-run before touching the fix.

## Step 3 — Fix host-flow reinjection target (R3)

- [ ] `UdpResponseReinjector.TryResolveTarget`: host flows resolve
      `_byStableId[OriginAdapterId]` first (towardMstcp on that handle, its MAC both
      slots), `_host` only as fallback with rate-limited warn.
- [ ] Unit tests: host flow with resolvable origin adapter → uses it; unresolvable →
      fallback + warn; forwarded path unchanged.
- [ ] Validate on Linux: `dotnet build && dotnet test`.
- [ ] Validate on test machine: 20/20 query success first-attempt-after-start included,
      TUN-equivalent absent (scope[0] ≠ flow adapter scenario).
- Rollback point: revert Step 3 commit only; Steps 1-2 artifacts remain.

## Step 4 — Regression sweep

- [ ] Full `dotnet test` suite.
- [ ] Re-run forwarded-flow scenario if test box has a Hyper-V vSwitch available (else
      rely on unit tests + user validation on .1).
- [ ] trellis-check sub-agent pass (spec compliance + quality).

## Step 5 — Wrap-up

- [ ] Update `.trellis/spec/backend/windows-ndisapi.md` with the SendToMstcp
      adapter-binding contract learned in Step 2.
- [ ] Commit (3.4), archive task, journal.

## Validation command cheat-sheet

- Linux: `dotnet build WinForward.slnx 2>&1` / `dotnet test WinForward.slnx`
- Remote: `evil-winrm-py -i 192.168.100.2 -u neko -p "$NEKO_PASS"`
- Remote DNS loop: PowerShell one-liner UdpClient send/recv with 2s timeout × 20
