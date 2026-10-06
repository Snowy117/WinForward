# Journal - qmazon (Part 2)

> Continuation from `journal-1.md` (archived at ~2000 lines)
> Started: 2026-10-07

---



## Session 59: TCP half-close fidelity: measure the mechanism, ship the close injection, verify on the VM
<!-- trellis-session: v=2 fp=1e17a3d5f084de37 -->

**Date**: 2026-10-07
**Task**: TCP half-close fidelity: measure the mechanism, ship the close injection, verify on the VM
**Branch**: `master`

### Summary

Opened task 10-06-tcp-half-close-fidelity and drove it to archive. Reproduced the symptom report's numbers exactly on the campaign AOT artifact (clean 55 / timeout 556 of 1201), then measured the mechanism instead of assuming it: the single-mode arms showed clean and halfClose failing identically (the trailers are innocent) with halfCloseViolation=0, and the relay does emit a FIN — its NetworkStream is disposed as RunPumpAsync returns — but that instant coincides with Completion completing, so the FIN races the retire that releases the reverse index and a half-closed client sees no end of stream at all. Fix: every relay end injects its client-visible close before the teardown (FIN|ACK sequenced from the server ISN plus the relay's delivered byte count, RST|ACK unchanged), plus the relay end kind in the tcp.relay.ended event. Verified on the VM through the unchanged harness: timeout 556 -> 23 of 1201 (clean 55 -> 592), halfClose 548 -> 50, clean 554 -> 30; gates green (build, tests, format, jb). Residual 2-8% is a different defect — the close is single-shot once the alias retires — captured with its Debug-level evidence and two candidate directions in the archived task and handed to child task 10-07-tcp-close-drain. Environment notes: the user's four-pass campaign was found hung (client stuck in MIX, sing-box gone, heartbeat stale) and was stopped by PID with its evidence preserved; C:\wfbench stayed read-only; the VM sandbox was deleted and the watchdog re-enabled, which restored the firewall.

### Git Commits

| Hash | Message |
|------|---------|
| `7cababb` | fix(tcp-redirect): inject the client-visible close before the session retires |
| `c828548` | docs(spec): record the client-visible close contract at relay end |
| `c58a64b` | chore(task): archive the TCP half-close fidelity task and open the close-drain child |

### Status

[OK] **Completed**

### Next Steps

- Child task 10-07-tcp-close-drain: design the bounded close drain (or the bounded repeat), then measure the residual against the port budget.
