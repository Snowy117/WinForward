# Migrate remaining lifecycle owners

## Goal

Migrate the remaining lifecycle owners to `QuiescenceScope`: `CaptureLifecycle`,
`LayeredCaptureRunner`, `NdisCapturePump`, `IdleExpirySweeper`, `RuntimeHeartbeat`, and the `Socks5*`
owners (`Socks5ControlConnection`, `Socks5UdpTransport`). Removing the last C2 allowlist entries is
what makes the program complete.

## Requirements

- Uses the C1 primitive; introduces no new lifetime mechanism (`design.md` §2).
- `LayeredCaptureRunner.cs:144-146,149` spawns become `Run` children; the loop bodies hoist their
  delegates to fields so the spawn path stays allocation-neutral (`design.md` §2.3).
- `NdisCapturePump` / `CaptureLifecycle` / `IdleExpirySweeper` / `RuntimeHeartbeat` replace their
  ad-hoc `_runCompletion` / `_runTask` / `_cleanupTask` / `_loop` handles with scope children plus
  drains.
- `Socks5ControlConnection` / `Socks5UdpTransport` stop reading a token after its source is disposed
  (currently unguarded at `Socks5UdpTransport.cs:255,282,294` and `Socks5ControlConnection.cs:295`)
  by moving the CTS into the scope, and gain consistent disposal guards.
- Removes the remaining allowlist entries; the program is complete only when the allowlist is empty.

## Acceptance Criteria

- [ ] Every migrated owner has a test proving `DisposeAsync` returns only after registered work
      completes.
- [ ] No `_ =`, `.ContinueWith`, `Task.Run`/`StartNew`, or allowlist entry remains anywhere in `src/`.
- [ ] `HotPathAllocationGateTests` green; no Capture-path allocation delta beyond the documented
      cold-path spawn cost.
- [ ] Specs updated (`hot-path.md`, `error-handling.md`, plus `async-lifetime.md` per-owner notes).
- [ ] Full gates green (format / Release zero-warning / tests / `jb inspectcode`).

## Notes

- Depends on C1; follows C3. Completing this child empties the C2 allowlist, which is the parent's
  completion condition.
- This child is a complex task: write its own `design.md` and `implement.md` before `task.py start`,
  referencing the parent `design.md` §4.1 and §4.3.
