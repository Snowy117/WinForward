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


## Session 2: Datapath throughput: batched reads, pooling, hardware smoke

**Date**: 2026-08-27
**Task**: Datapath throughput: batched reads, pooling, hardware smoke
**Branch**: `master`

### Summary

Analyzed EOF/reset root causes into a parent task with four children; completed the datapath-throughput child: ETH_M_REQUEST batched ReadPackets ABI (export-verified against the real DLL), pump batching (cap 32, in-order), ArrayPool-backed frame lease with the return point moved to ProcessAsync finally (design §4.4 audit falsified the complete-after-read assumption), in-place TCP rewrite (RecordClientSyn moved before write), NdisPacketBufferPool for injection. Linux benchmark 2.44M pps steady state (~669B/pkt clones remain for phase 2); WinLtsc smoke with real ndisapi.dll: 1497/1497 captured/completed paired, zero failed/warn, 15 relays. Spec contracts landed in windows-ndisapi.md.

### Git Commits

| Hash | Message |
|------|---------|
| `fec967a` | (see git log) |
| `5ad8e60` | (see git log) |
| `2519b0d` | (see git log) |
| `3f30cf8` | (see git log) |

### Status

[OK] **Completed**

