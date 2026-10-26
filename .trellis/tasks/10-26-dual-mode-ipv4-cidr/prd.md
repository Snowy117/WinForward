# Batch 5f: dual-mode client IPv4 CIDR parity

Parent: `.trellis/tasks/10-25-kernel-backend-integration`

## Goal

Make a Windows dual-mode client connecting to an IPv4 literal match the same host rule under the
Kernel provider as under the WinpkFilter packet path. WFP reports that connection at the V6 layer
with a v4-mapped address (`::ffff:a.b.c.d`), so a host rule with an IPv4 `RemoteCidr` (or an IPv4
`AddressFamily` selector) currently misses it; the packet path sees an IPv4 packet and matches.

## Requirements

- **R5f-1 Normalize at classify.** In the host ALE classify, detect a V6-layer remote address in
  `::ffff:0:0/96` and present it to the rule matcher as IPv4 (family 4, the four IPv4 bytes). The
  same normalized family/address goes into the redirect context's original destination so the
  userland target is IPv4 (matching the WinFore packet path).
- **R5f-2 Preserve native semantics.** Native IPv4 and native IPv6 connections keep their family;
  v4-mapped UDP/TCP associations are normalized consistently for the future host UDP path. Native
  IPv6 rules and broad IPv6 CIDRs must not falsely match a v4-mapped connection (it is IPv4
  semantically). The redirect/local-redirect target selection (D6) and the listener behavior are
  unchanged.
- **R5f-3 Tests.** Host-side C++ matcher/redirect-context tests for the normalized view; the
  `wfprobe` host rule suite with a dual-mode client to an IPv4 target; update
  `selftest --dual` and U10's v4-mapped connected context expectations to family 4 + pure IPv4;
  the existing native IPv4/IPv6 and native-U10 family cases stay green; no rule-index/target-table
  change. Diagnostic event facts stay raw.
- **R5f-4 Windows E2E.** Re-run the Kernel host TCP E2E with the **original** proxy rule
  (`Process: ["pwsh.exe"]`, `Protocol: ["tcp"]`, `RemoteCidr: ["192.168.100.4/32"]`,
  `RemotePort: ["40012"]`, `Action: proxy`, `Target: main`) and the `pwsh` dual-mode client;
  the proxy must reach the sing-box/target, and the pass/block checks remain.
- **R5f-5 Boundaries.** No WinFore behavior change; no ABI size/version change (the redirect
  context family field becomes 4 for the mapped case; version 1/size 36); no host UDP/forwarded/
  ARM64/productization; no unrelated files.

## Acceptance Criteria

- [ ] 5f-AC1: the dual-mode client matches the original IPv4 CIDR/process rule; the Kernel proxy
      reaches the target via the SOCKS5 target, with the userland destination as IPv4.
- [ ] 5f-AC2: native IPv4/IPv6 and U10 family cases stay green; IPv6 rules do not match the
      v4-mapped flow; the redirect context/target behavior is correct.
- [ ] 5f-AC3: the Windows E2E evidence (client, sing-box, stats, logs) is recorded; the VM is
      cleaned; the Windows/root/Kernel gates pass.
- [ ] 5f-AC4: no WinFore behavior/ABI size change and no unrelated dirty files.

## Out of Scope

- The WinpkFilter same-config comparison (Batch 5e) until the VM has `ndisapi.dll`; host UDP,
  forwarded, ARM64, packaging.
