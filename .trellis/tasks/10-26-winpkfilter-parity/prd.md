# Batch 5e: WinpkFilter edition parity (blocked on the VM environment)

Parent: `.trellis/tasks/10-25-kernel-backend-integration`

## Status

**Blocked (environment).** The WinLTSC VM has the `ndisrd` WinpkFilter driver running, but no
matching x64 `ndisapi.dll` sidecar and no WinForward WinpkFilter product directory. Searches of
`C:\Windows\System32`, Program Files, `C:\wfbench` (including zips), user dirs and `C:\workspace`
found neither. The same-config WinpkFilter run cannot start until the sidecar is deployed.

## Goal (when unblocked)

Run the exact host policy used by the Kernel E2E (same `Host.Rules`, `Socks5Servers`, target and
port-only/IPv4-CIDR variants) through `Provider=WinpkFilter` and compare proxy/pass/block/target
decisions. Expected accepted differences: host TCP block is WinpkFilter drop/timeout vs Kernel
`AccessDenied`; the block sink/RST details. The 5f dual-mode fix must land first so both editions
see the same dual-mode client decisions.

## Prerequisites

- A matching x64 `ndisapi.dll` from the WinpkFilter 3.6.2.1 package next to the CLI (the merged
  `WinForward.NdisApi` resolves it from the app directory only).
- A WinForward WinpkFilter-capable CLI (the merged publish) and the WinpkFilter driver service.
- A clean foreground run (second VM session) stopped with Ctrl+C so the NDISAPI adapter modes are
  restored; do not hard-kill it.

## Acceptance (when unblocked)

- [ ] 5e-AC1: the same policy through WinpkFilter yields the same proxy/pass/target decisions.
- [ ] 5e-AC2: the host TCP block form difference is recorded as accepted.
- [ ] 5e-AC3: evidence is stored under this task; the VM adapter modes/network are restored.

## Out of Scope

- Kernel-side behavior changes (5f owns the dual-mode fix); host UDP/forwarded/ARM64/productization.
