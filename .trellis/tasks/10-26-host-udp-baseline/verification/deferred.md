# Host UDP baseline — deferred completion

Date: 2026-10-26
State: **deferred** — the listener/coordinator scaffolding stays, the host UDP proxy is disabled end
to end, and this scaffolding is committed with the capability gate closed. This note claims no host
UDP proxy support; the reply-source injection remains future work.

## Final scope

- **Capability gate.** `ConfigurationKernelCapabilities.ValidateHostRules` refuses every host `proxy`
  rule whose `Protocol` selector can match UDP (absent or containing `udp`) at
  `WinForward.Host.Rules[i].Protocol`. Pass/block rules may still match UDP. Non-default
  `UdpSessionCapacity` / `UdpRelayReceiveBufferKb` / `UdpSessionIdleSeconds` warn as inert under the
  Kernel provider again (the host UDP coordinator is not started); an explicitly-default
  `UdpSessionCapacity` keeps only the pre-existing headroom warning.
- **Data path off.** `KernelHostService.HostUdpProxyEnabled` is false and the TODO below records the
  constraint. The UDP service and its coordinator are constructed (composition/dispose stay wired)
  but `KernelHostUdpService.Start()` is not called, so no receive loop runs. The
  `KernelBackend` UDP pools/executor/clock stay as scaffolding with a TODO.
- **Scaffolding documented.** `HostUdpResponseSink` and `KernelHostUdpService` state that the
  listener `sendto` reply exposes the listener endpoint, that they are not on the product path, and
  that the later driver reply-source injection replaces the sink. `KernelHostUdpDatagram` is
  unchanged (the entry path is reusable).
- **Docs.** README (Kernel provider section) and the host UDP section (§7.11) of
  `.trellis/spec/backend/kernel/wfp-architecture.md` state the disabled proxy, the reason and the
  WinpkFilter pointer. WinpkFilter UDP documentation is unchanged.

## Evidence pointers

- `verification/a4-failure.md` — A4 failed: the proxied reply arrives with the listener source
  (`192.168.100.2:<listener-port>`), not the original destination (`192.168.100.4:40010`); the pass
  arm is correct; userland raw-socket source spoofing is not viable (firewall on or off).
- `verification/a8-connected.txt` — the connected A8 sample: the connected IPv4 client received a
  listener-sourced reply (`FROM=127.0.0.1:59089`) while its printed peer stayed the original
  destination, and the v4-mapped dual-mode form timed out. The reply shape a connected client accepts
  is the listener's, so one mechanism cannot satisfy both the original-source contract and connected
  clients.
- `.trellis/tasks/10-26-kernel-host-udp/research/host-udp-driver.md` §3.3 and
  `.trellis/tasks/10-26-host-udp-reply-source/research/{wfp-reply-source,injection-design}.md` — the
  reply source is not preserved by any documented userland mechanism; the accepted leg is
  driver-side transport-receive injection.

## Exact gate message

Path: `WinForward.Host.Rules[{index}].Protocol`

> The WinForward.Kernel host UDP proxy is not available yet: the listener reply cannot preserve the
> original destination as its source, so an unconnected client would see the proxy listener, and the
> driver-side reply injection is pending. Add "Protocol": ["tcp"] or use Provider = WinpkFilter for
> UDP. Pass and block rules may match any protocol.

Inert warnings (paths `WinForward.UdpSessionCapacity`, `WinForward.UdpRelayReceiveBufferKb`,
`WinForward.UdpSessionIdleSeconds`):

> {Setting} is inert under Provider = WinForward.Kernel because the host UDP coordinator is not
> started; it takes effect under Provider = WinpkFilter.

## Exact TODO text

`src/WinForward.Cli/Backends/Kernel/KernelHostService.cs`:

```
// TODO: make this return true once the driver can inject the relay reply with the original
// destination as its source; until then the host UDP proxy stays fail-closed. It is a property
// rather than a const because a constant false would turn the guarded Start call into unreachable
// code the compiler rejects. The constraint and the scaffolding are described in the Host UDP
// section of .trellis/spec/backend/kernel/wfp-architecture.md
private static bool HostUdpProxyEnabled => false;
```

`src/WinForward.Cli/Backends/Kernel/KernelBackend.cs`:

```
// TODO: these UDP pools, the activity clock and the host composition feed a coordinator the
// orchestrator constructs but does not start until the driver can inject the relay reply with
// the original destination as its source. See the Host UDP section of
// .trellis/spec/backend/kernel/wfp-architecture.md
```

## Log-line changes

- `kernel.listener.bound`: adds "The host UDP proxy is disabled, so proxying UDP needs
  Provider = WinpkFilter."; the `{UdpEnabled}` flag is gone.
- `kernel.ready`: "Kernel host TCP interception is ready: … TCP flow capacity {FlowCapacity}. The
  host UDP proxy is disabled: the listener reply cannot preserve the original destination as its
  source and the driver-side reply injection is pending, so the host UDP session capacity
  {UdpSessionCapacity} and the {UdpDatagramBytes}-byte datagram cap are inert."
- `kernel.started`: "Kernel host TCP interception started with the host UDP proxy disabled.
  Press Ctrl+C to stop."
- `kernel.stop` reports the zero UDP counters and the block-sink receive count from the constructed
  (not started) service, so no null path is touched; the message gained those fields and the TCP
  wording is now explicit.

## Gates run (this closure)

| Gate | Result |
|---|---|
| `dotnet format WinForward.slnx --severity info --verify-no-changes --no-restore` | exit 0, empty output |
| `dotnet build WinForward.slnx -c Release` | 0 warnings, 0 errors |
| `dotnet test WinForward.slnx -c Release` | 2,017 passed, 0 failed, 0 skipped (14 projects); no load-sensitive rerun needed |
| `nix develop ./kernel --command bash -lc 'kernel/tools/check.sh --full'` | 8 PASS, 0 FAIL, 0 SKIP |
| `jb inspectcode` per changed project | `WinForward.Cli` 0 and `WinForward.Kernel.Runtime` 0 after narrow file-scoped suppressions for the known IVT/cross-project false positives (`MemberCanBePrivate` on `ToListenerEndpoint`/`Normalize`; `ClassNeverInstantiated` on `WfpUdpReceiver`). `WinForward.Configuration` 11 findings, all in files this tree does not touch (`ConfigurationBackendDriver`, `ConfigurationLayering`, `ConfigurationModels*`) and pre-existing at HEAD. Fixed in this closure: a redundant `using`, two `MergeIntoLogicalPattern`, an if→switch conversion, an ambiguous xmldoc cref, and four members narrowed to `private`; two unused usings (`KernelHostService.cs`, `KernelHostTcpService.cs`) were removed earlier. |

## Tests

`BackendDriverConfigurationTests`: UDP-capable proxy rules (explicit `udp`, `tcp`+`udp`, and a
catch-all) are rejected at `WinForward.Host.Rules[i].Protocol` with the new message; pass/block UDP
rules are accepted; non-default UDP budgets warn as inert; explicitly-default capacity keeps only the
headroom warning; TCP-only configs are unchanged. The host UDP parser/flow/sink/composition tests stay
green as scaffolding.

## Remaining work — `10-26-host-udp-reply-source`

1. Driver-side transport-receive injection of the relay reply with the original destination as the
   source (new control IOCTL/ABI in lockstep, statistics, lifecycle drain), per its research design.
2. Replace `HostUdpResponseSink` with the injector-backed sink; keep the coordinator/session/transport
   engine unchanged.
3. Rerun A1–A5, A8 (unconnected/connected, mapped and native IPv6) and A10; record the evidence.
4. Then lift the capability gate, flip `HostUdpProxyEnabled`, and commit this batch's
   listener/coordinator code together with the response leg. Local target/DNS and UoT remain the
   sibling batch.
