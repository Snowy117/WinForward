# Research index: host UDP reply source

Task: `.trellis/tasks/10-26-host-udp-reply-source`
Date: 2026-10-26

| File | Contents |
|---|---|
| [`wfp-reply-source.md`](wfp-reply-source.md) | Documented WFP return path for a local-redirect UDP proxy: redirect records are TCP-only, `ddproxy` is the documented clone-modify-`FwpsInjectTransportReceiveAsync0` mechanism, the ALE/firewall question, the measured connected-socket peer constraint, why source rewrite is ruled out, and what the forwarded injection code can/cannot reuse. Includes the evidence ledger (measured vs inferred). |
| [`injection-design.md`](injection-design.md) | Recommended design: transport-receive injection with `FwpsConstructIpHeaderForTransportPacket0`; exact packet construction and backfill rule; new IOCTL `0x811` + 60-byte `probe_host_udp_reply` header; ABI 9 and `probe_stats` growth; validation/security gates; sink/injector seam and composition; connected-vs-unconnected reply-mode decision; tests (host C++, Linux, `wfprobe host-reply`, A4/A8), gates, risks and fallbacks. |

## Recommended reading order

1. `wfp-reply-source.md` §0 (verdict) and §2.4 (connected peer constraint) — the two findings that
   shape the design.
2. `injection-design.md` §0 (two blocking experiments) and §4 (reply mode).
3. `wfp-reply-source.md` §1.3 and `injection-design.md` §1–2 for the implementation shape.

## Existing evidence this research builds on

- `.trellis/tasks/10-26-host-udp-baseline/verification/a4-failure.md` — A4 measurement.
- `.trellis/spec/backend/kernel/research/10-21-host-udp-route-a-probe/research/route-a-findings.md`
  — loopback invisibility, the `STATUS_NOT_ACCEPTED` injection attempt, and the connected-client
  acceptance measurement.
- `.trellis/spec/backend/kernel/wfp-architecture.md` §7.11/§7.12 — host UDP and MAC injection facts.
- `.trellis/tasks/10-26-kernel-host-udp/design.md` §9 (A4/A8 rows) — acceptance definitions, including
  "the connected arm checks payload only".

## New measurements taken for this research (VM `WinLTSC`, 2026-10-26)

- Windows connected UDP sockets filter inbound datagrams by peer (`TimedOut` for a third-party
  source, delivered from the peer); unconnected sockets accept any source.
- The Windows UDP endpoint table exposes no remote endpoint, so connectedness cannot be queried
  (`Get-NetUDPEndpoint` shows `remote=:` for connected v4 and connected dual-mode sockets).
