# Journal - qmazon (Part 1)

> AI development session journal
> Started: 2026-08-27

---



## Session 1: Fix host UDP reinjection delivery failure (hardware-verified)

**Date**: 2026-08-27
**Task**: Fix host UDP reinjection delivery failure (hardware-verified)
**Branch**: `master`

### Summary

Root-caused and fixed host UDP response delivery: SendPacketToMstcp is adapter-bound, and pre-fix scope[0] reinjection was dropped by Windows strong-host receive validation (pktmon proof: 'Not locally destined', 0/20 queries). Host flows now reinject via FlowKey.OriginAdapterId; verified 20/20 plus 5/5 on 1s-settle restart on the two-adapter Win11 fixture with a remote sing-box SOCKS5 server. Also added wall-clock timestamp prefixes to every runtime log line. Specs updated (windows-ndisapi host-flow binding contract, logging guidelines).

### Git Commits

| Hash | Message |
|------|---------|
| `4e3c866` | (see git log) |
| `4c36ad4` | (see git log) |

### Status

[OK] **Completed**
