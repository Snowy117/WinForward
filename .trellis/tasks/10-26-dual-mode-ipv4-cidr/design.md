# Batch 5f design: normalize v4-mapped host connections

## Root cause

A Windows dual-mode client connecting to an IPv4 literal is delivered to
`ALE_CONNECT_REDIRECT_V6` with a v4-mapped remote address (`::ffff:a.b.c.d`, family 6). The host
classifier feeds that raw family/address into the Kernel matcher, so an IPv4 `RemoteCidr` or an
IPv4 `AddressFamily` selector misses; the WinpkFilter packet path sees an IPv4 packet and matches.
The redirect context also carries the mapped family/address, so the userland target is IPv6-mapped
instead of IPv4.

## Fix

Add a portable helper in `src/common/` (no WFP types, no STL/exceptions):

```cpp
bool try_normalize_v4_mapped(std::uint8_t family, const std::uint8_t address[16],
                             std::uint8_t& out_family, std::uint8_t out_address[16]) noexcept;
```

Recognize `family == 6` and the `::ffff:0:0/96` prefix (bytes 0..9 zero, 10..11 `0xFF`), copy the
last four bytes, return family 4. Otherwise leave the values and return false.

In the host classify (`src/driver/probe_classify.cpp`):

- Build the matcher `connection_view` from the normalized family/address; the port/protocol/
  process path are unchanged. The matcher then sees IPv4 for a v4-mapped flow; an IPv6 CIDR or
  `AddressFamily=IPv6` rule does not match it (the packet path also sees IPv4).
- Build the `redirect_context`'s `original_destination` from the same normalized family/address,
  so the userland `RedirectDestination.EndPoint` is IPv4 and the relay sends an IPv4 SOCKS5
  CONNECT (`ATYP=1`), matching the WinFore packet path.
- Do not change the local-redirect target selection (D6), the listener, the WFP layer, the
  redirect table, the ABI version/size (the context family field changes value; version 1/size 36).
- Apply the same normalization in the host UDP classify path for the future host UDP backend; it
  is the same fact conversion, not a new rule semantic.

## Tests

- Portable helper test in `kernel/tests/host`: mapped TCP/UDP facts normalize; native IPv4/IPv6 do
  not; malformed/zero cases safe; the helper leaves the output untouched on refusal.
- Matcher test: a normalized mapped view matches an IPv4 CIDR and not an IPv6 CIDR; native IPv6
  views keep matching IPv6.
- Redirect-context test: the normalized destination yields family 4 + the four IPv4 bytes; the
  context size/version unchanged.
- `wfprobe selftest --dual`: the dual-mode client connects to an IPv4 literal, so
  `SelfTest.TryCheckContext` must expect family 4 + the four IPv4 bytes after the change; a native
  IPv6 remote still expects 6. Update the verdict text to report the semantic family, not `dual`.
- `wfprobe udp-rules` U10: v4-mapped **connected** now yields family 4 + pure IPv4 bytes (the
  unconnected case already did); native IPv6 cases stay family 6. The cmsg level remains whatever
  WFP reports; only the context facts normalize.
- `wfprobe rules` host parity suite: a dual-mode client to an IPv4 target matches an IPv4 CIDR
  rule and does not match an IPv6 CIDR rule; the existing native IPv4/IPv6 family cases stay
  green.
- Windows E2E: the original WinForward rule (`Process: ["pwsh.exe"]`, TCP, `RemoteCidr:
  ["192.168.100.4/32"]`, `RemotePort: ["40012"]`, proxy `main`) with the `pwsh` dual-mode client;
  the proxy reaches sing-box/target; pass/block remain.

## Risks

- The local address/redirect target is still on the V6 layer; do not normalize it — the listener
  binding and D6 stay as verified in Batch 5d/5e.
- The v4-mapped prefix detection must be byte-exact; do not use locale/string conversion in the
  driver.
- The redirect context ABI version/size must not change; only the family/address value.
- The WinForward userland target/relay must handle the IPv4 context; the 5d port-only run already
  proved the listener/relay, this batch changes the context to IPv4 and must re-run it.
- Diagnostic `probe_event` facts stay raw (WFP family/address); only the matcher view and the
  redirect context destination normalize. Do not silently change event counters or family fields.
- The spec (`wfp-architecture.md` §7.5 and `host-rule-blob.md` matcher facts) documents the old
  connected-mapped behavior; update it with the new fact conversion in the same batch.
- Build the driver with the VM WDK and reload the test-signed driver; record the new hash.

## Verification

```bash
nix develop ./kernel --command bash -lc 'kernel/tools/check.sh --full'
# Windows VM:
pwsh -File kernel/tools/windows/gate.ps1 -Root C:\workspace\WinForward.Kernel -Remote 192.168.100.4:40001
# Kernel host E2E with the original IPv4 CIDR rule, then the WinForward Windows E2E
```
