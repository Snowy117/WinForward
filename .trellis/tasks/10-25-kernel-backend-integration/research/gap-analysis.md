# WinForward.Kernel as the second WinForward backend: capability and parity gap analysis

Date: 2026-10-25
Task: `.trellis/tasks/10-25-kernel-backend-integration`

## 1. Scope and clarified intent

WinForward.Kernel is not being adopted as a separate product. The repository is to be merged into
WinForward and retired there: driver, shared ABI, host tests, Windows/Linux gates, tooling, hooks,
Trellis specs and research move into this repository, and the substantial userland overlap is
consolidated. WinForward then ships two `BackendDriver.Provider` choices — `WinpkFilter` and
`WinForward.Kernel` — over one configuration contract, one policy model, one relay/SOCKS5/UDP
implementation and one benchmark harness.

The Kernel `AGENTS.md` already states the same end state: "WinForward.Kernel edition" is a backend
of WinForward, sharing `appsettings.json`, policy semantics, events and counters; the two editions
must not differ in user-visible decisions.

## 2. What the Kernel driver already provides

- Host: `FWPM_LAYER_ALE_CONNECT_REDIRECT_V4/V6` classifies once per TCP connection and once per UDP
  association. TCP/UDP, IPv4/IPv6, in-kernel first-match rule evaluation (host rule blob v2),
  process selectors from `ALE_APP_ID`, and a block sink enforced by four fixed
  `ALE_AUTH_CONNECT` filters. A `pass` decision never reaches userland.
- Forwarded: WFP L2 inbound/outbound native MAC-frame callouts, DNAT/SNAT, TCP and UDP, multiple
  ingress ifindexes, multiple listener addresses, hot rule replacement, native IPv6 at driver level,
  and control ABI v8.
- Quality: Linux gtest host tests for the portable blob/matcher core, Windows `gate.ps1`, `wfprobe`
  suites, differential fixtures, and a real Windows VM validation history.

This is enough to build a candidate Kernel backend; the gaps below are about parity, product
integration and operations, not about the driver being a prototype.

## 3. Functional and semantic gaps

| Gap | Kernel today | WinForward today | Impact and possible bridge |
|---|---|---|---|
| **Host rule target** | The host blob rule has no action payload — only `permit`/`block`/`redirect` (`.trellis/spec/backend/host-rule-blob.md`, rule table). The redirect context carries `rule_index`, `process_id`, original destination and protocol only (`src/common/redirect_context.h`). The Kernel controller's host relay uses one global `_defaultSocks5` (`src/userland/WinForward.Userland/Relay/RelayServer.cs`). | A proxy rule's `Target` selects a named SOCKS5 server or a `LocalTarget`; local targets carry DNS-hijack-style flows (`README.md`, rule keys). | Multi-target and local-target/DNS configurations cannot work as-is. `rule_index` maps 1:1 to the configured rule and is already in the context, so a userland mapping is possible without an ABI change; it must become a contract, and the fallback redirect sentinel (`0xFFFFFFFF`) needs a defined target. Local targets additionally need the host UDP relay. |
| **Host UDP userland** | The driver redirects host UDP and exposes the original destination through `IP_WFP_REDIRECT_CONTEXT` / `IPV6_WFP_REDIRECT_CONTEXT`; the userland `HostRedirectContextSource.TryReadUdp` returns "host UDP relay is not in this repository". Only `ForwardedUdpRelay` exists. | Full per-flow UDP: native `UDP ASSOCIATE`, UoT v2, local targets, setup queue, session capacity, idle sweep, receive-buffer sizing, response reinjection. | Host UDP (including DNS-shaped flows) cannot run on the Kernel provider. The response shape differs: WFP host UDP replies use the same listener socket's `sendto`, while WinForward's UdpResponseReinjector builds and injects frames; a WFP listener adapter is required rather than reusing the reinjector. Windows 11 26100 has a known connected-UDP WFP record refcount leak (36 B per datagram until reboot), which must be mitigated or explicitly accepted. |
| **Host adapter selector** | Host rules have no adapter selector; D3 says the ALE connect-redirect layer does not expose the interface index. | `Host.Rules` accepts `AdapterId`/`AdapterName` and uses them to narrow host traffic; capture scope also unions adapter-constrained rules. | Existing host configurations may not compile under the Kernel provider. Using `localAddressAndPort` is an approximation, not the same "observed on adapter" semantics. This is a product decision: reject in Kernel mode, remove the capability from WinForward, or accept an approximation. |
| **Forwarded target and adapter arrays** | The forwarded blob rule has one `upstream_id` and one `ingress_adapter_id`; the current userland maps `upstream_id` only to a SOCKS5 endpoint. The controller resolves one IPv4 listener address per ifindex; native IPv6 L is only exercised by `wfprobe`. | Forwarded rules can target a `LocalTarget`, can carry arrays of `AdapterId`/`AdapterName`, and use IPv4/IPv6. | Forwarded DNS hijacking, multiple adapters in one rule and IPv6 listener addresses need work. Expanding an adapter array into several blob rules is possible but breaks the "one configured rule → one blob rule" invariant and the rule-index trace identity. |
| **Block semantics** | Host TCP block is enforced by `ALE_AUTH_CONNECT` filters and surfaces as `AccessDenied(10013)`; a failed sink redirection is fail-open and counted (`host-rule-blob.md`, block section). Forwarded L2 block drops frames. | `Action: block` consumes the frame without reinjection; TCP clients normally observe a SYN timeout. Proxy-path failures fail closed. | The host TCP block is a user-visible difference (immediate refusal vs timeout). The parity gate must either accept the Kernel-edition behaviour explicitly or the product must change the semantics. |
| **Process selector compilation** | Path selectors must resolve to an existing file/directory at configuration compile time, are normalized to a `\device\...` path, and are capped at 511 UTF-8 bytes. Filename/path/directory-prefix matching itself mirrors WinForward. Process identity comes from `ALE_APP_ID` in the kernel. | Process selectors match the runtime process path from the owner table and do not require the path to exist when the configuration is validated. | A configuration that validates today can fail under the Kernel provider, for example an executable installed after the service starts or a path on a removable volume. Kernel attribution is more precise (especially UDP), but the configuration contract must be documented and diagnosed. |
| **UDP reply ownership** | `ForwardedUdpRelay` drops a response whose decoded source does not equal the flow's server (`ForwardedUdpRelay.cs`, receive loop). | WinForward delivers a reply from a foreign source and counts `udpResponseSourceMismatch`, because RFC 1928 does not pin the reply source and multi-homed/anycast servers may legitimately answer from another endpoint. | Reusing the Kernel relay as the product implementation loses legitimate multi-homed replies. Use WinForward's UDP transport; if the Kernel relay is kept, it must adopt the same rule. |
| **TCP relay lifecycle** | `RelayServer`/`PumpPair` are scaffolding: no 30-minute stall window, only a half-close drain timeout; no crafted client-visible close. | 30-minute stall reclamation, teardown grace, crafted RST|ACK on abnormal ends, capacity rejection resets. | The packet-level tombstone/RST machinery is mostly unnecessary under WFP because the real server never sees the connection, but the client-visible close/failure behaviour still needs parity. Reuse WinForward's acceptor/pump rather than the Kernel relay. |
| **Pre-existing UDP associations** | WFP classifies a UDP association when it is created; an association that already existed before the driver was enabled is not reclassified. | The packet path observes datagrams after start, so a pre-existing UDP association can begin being proxied on its next datagram. | A small behavioural difference for long-lived UDP sockets (for example a resolver socket opened before the service starts). It should be documented and tested rather than assumed away. |

## 4. Repository-merge implications

### 4.1 What to keep from the Kernel repository

- `src/driver/` — the WFP callout driver, probe/control IOCTL implementation, block filters,
  process-path conversion, rule table, forwarded paths, build project.
- `src/common/` — redirect context, host/forwarded blob and matcher headers, shared constants.
- `tests/host/` — CMake/gtest host suites and differential fixtures.
- `tools/linux/` — forwarded data-plane gates, raw fragment/IPv6 helpers, cost probes.
- `tools/windows/` — `gate.ps1`, `mutation.ps1`, `load-driver.ps1`, `WfBuild.psm1`, CodeQL helper,
  firewall and E2E setup scripts that remain useful after the merge.
- `tools/wfprobe/` — the driver-parity suite; keep it as a tool even after the product userland is
  deduped.
- `tools/check.sh`, `tools/lint/`, `.clang-format`, `.clang-tidy`, `flake.nix`/devShell, `.githooks`.
- `src/userland/WinForward.Userland/Device/` — the C# ABI mirror and device client.
- `src/userland/WinForward.Userland/Policy/` and `Forwarded/` — the blob compilers and their tests,
  after adapting their input to WinForward's `ValidatedConfiguration`.
- The Kernel `.trellis/spec/backend/` documents that describe the driver/ABI (WFP architecture, host
  rule blob, forwarded rule blob, connected-UDP leak, quality/logging conventions) and the task
  research that still constrains the design.

### 4.2 What to retire or dedupe

- `src/userland/WinForward.Userland/Relay/` — replaced by `WinForward.Runtime`'s relay, SOCKS5,
  UDP transports and session lifecycle.
- `src/userland/WinForward.Userland/Control/` and `src/userland/WinForward.Controller/` — replaced
  by the WinForward composition root/CLI backend implementation.
- `src/userland/WinForward.Userland/Configuration/` and its temporary JSON schema — replaced by
  `WinForward.Configuration`; do not maintain a second parser or a second `PolicyRule` model.
- The Kernel `src/userland/WinForward.Userland.slnx` and its standalone test runner are replaced by
  the root `WinForward.slnx` and the existing test projects, except where the Kernel ABI/compiler
  tests are moved into a dedicated test project.
- Kernel `.trellis` workspace/journals and archived tasks are not copied wholesale; only live specs,
  decisions and research that still apply are migrated, with a pointer to the archived Kernel
  history.

### 4.3 Proposed layout

Keep the native world under a dedicated root (for example `kernel/`) so the .NET solution, MSBuild
and the WDK/CMake build do not fight over the root:

```text
kernel/
  src/driver/            WFP driver (WDK)
  src/common/            shared C++ ABI and portable matcher
  tests/host/            CMake/gtest host tests
  tools/                 Linux/Windows gates and wfprobe
  Directory.Build.props? (native-only settings)
src/
  WinForward.Kernel.Contracts/   C# ABI mirror + device client + blob compilers
  WinForward.Runtime/            shared relay/SOCKS5/UDP, extended with the WFP listener seam
  WinForward.Cli/                composition root with BackendDriver selection
  ...
tools/
  check.sh, lint/        Kernel native gates, wireable into the root gate
.github/workflows/       .NET + WDK jobs
```

The exact names are a design decision, but the boundary is not: the driver build must remain
isolated, and the product must compile the Kernel ABI/compiler code from the same C# projects that
the WinpkFilter backend uses.

### 4.4 History

Preserve Kernel history where practical (`git subtree add --prefix kernel <kernel-repo> <commit>` or
an equivalent filtered merge), record the final Kernel commit/tag, and keep a migration note. If
history import is rejected, record the source commit and a file manifest so the imported state stays
traceable.

### 4.5 Build, gates and hooks

- Root .NET gates: `dotnet format`, `dotnet build -c Release`, `dotnet test -c Release`,
  `jb inspectcode`.
- Native gates: `tools/check.sh --fast/--full/--staged` (clang-format, effective-line limit,
  cppcheck, forbidden patterns), CMake/gtest `wf_check`, WDK `gate.ps1`, `mutation.ps1`, CodeQL.
- Windows: the WDK build and driver gate need a Windows runner with the WDK/SDK; the existing
  Linux host tests stay on the Linux runner.
- Pre-commit: one hook (or a dispatcher) must run both the .NET format gate and the native
  `check.sh --fast --staged`; keep the Kernel's "staged blob" semantics for C++ and the WinForward
  format gate for C#.
- Do not weaken either gate to make the merge green; if a gate is genuinely inapplicable to a
  subtree, scope it explicitly with a reason.

### 4.6 Trellis and documentation

- Merge the Kernel driver/ABI specs into `.trellis/spec/backend/` (or a `kernel/` layer) and update
  the WinForward backend index so the spec hub remains navigable.
- Record the decision that WinForward is the single maintained repository and that Kernel is a
  backend, not a second product.
- Update `README.md`, `AGENTS.md`, build instructions, and the E2E documentation for the provider
  switch and the driver installation prerequisites.

## 5. Integration recommendations

- **One policy path.** Compile from WinForward's `ValidatedConfiguration`, not from the Kernel's
  temporary JSON parser. Use the Kernel blob compilers as the ABI encoder, with WinForward's
  diagnostics and rule ordering. Do not keep two `PolicyRule`/`RuleMatcher` models.
- **Target mapping.** Make the matched host rule's target resolvable from the redirect path. The
  cheapest bridge is `rule_index` → configuration rule → `ProxyTarget`, because the host blob keeps
  one rule per configured rule and the context already carries `rule_index`; promote that field
  from "diagnostic only" to a contract, and define the fallback behavior. If the product prefers a
  clean contract, extend the host blob/context with a target id, but that raises the ABI version.
- **Host UDP.** Build a WFP listener adapter around the existing `IUdpProxyTransport` and the
  per-flow session model: the listener receives the original destination per datagram, the relay
  uses the existing SOCKS5/UoT/local-target transports, and replies go out through the same
  listener socket. This is not a reuse of `UdpResponseReinjector`; it is a new response leg.
- **Backend seam.** Keep it narrow: rule publication/compilation, listener/redirect setup,
  self-traffic, lifecycle, counters/events. Do not abstract the packet path itself; the WinpkFilter
  backend remains packet-driven and the Kernel backend connection-driven.
- **BackendDriver shape.** `Provider` plus provider-specific objects; move `KernelPassCache` under
  the WinpkFilter object; strict rejection of inapplicable keys; common limits stay top-level.
  The `Provider` scope (global vs per-domain) is a product decision because the Kernel host ALE and
  forwarded WFP L2 have very different maturity.
- **Rollout.** Keep WinpkFilter as the default provider. Enable the Kernel provider first for host
  TCP (then host UDP), with forwarded WFP L2 behind an explicit capability gate until its parity
  cases pass. A mixed "host Kernel + forwarded WinpkFilter" mode would require both drivers active;
  treat it as a separate product decision, not an implementation detail.
- **Parity gate.** The same config must produce the same rule hit, action, target, block/foreign/
  leak outcomes under both providers. The E2E harness is the place to enforce it, not a one-off
  manual comparison.

## 6. Evidence index

Kernel:
- `WinForward.Kernel/AGENTS.md` — integration seam, edition parity, current priorities.
- `.trellis/spec/backend/wfp-architecture.md` — D1–D9, host/forwarded facts, ABI v8, connected-UDP
  leak, D7 status, IPv6 E2E.
- `.trellis/spec/backend/host-rule-blob.md` — host blob v2 layout, process path constraints,
  block sink, known boundaries.
- `.trellis/spec/backend/forwarded-rule-blob.md` — forwarded blob v1, upstream_id, one ingress per
  rule, forwarded data-plane seam.
- `.trellis/tasks/archive/2026-10/10-23-fwd-b6-hardening/d7-decision.md` — adoption conditions.
- `src/common/redirect_context.h` — context fields.
- `src/driver/wfprobe.vcxproj` — x64-only WDK build.
- `src/userland/WinForward.Userland/Relay/RelayServer.cs` — host global socks5, scaffolding.
- `src/userland/WinForward.Userland/Relay/PumpPair.cs` — relay lifecycle scaffolding.
- `src/userland/WinForward.Userland/Relay/HostRedirectContextSource.cs` — host UDP not implemented.
- `src/userland/WinForward.Userland/Relay/ForwardedUdpRelay.cs` — forwarded-only UDP relay and
  foreign-source drop.
- `src/userland/WinForward.Userland/Control/WfpController.cs` — one IPv4 L per ifindex, self PID,
  clean-exit disable.
- `src/userland/WinForward.Userland/Forwarded/ForwardedPolicyRule.cs` — single ingress adapter.
- `src/driver/README.md` — test-signed `wfprobe.sys`; no production INF/CAT in the tree.

WinForward:
- `README.md` — x64/ARM64 support, config keys, host adapter selectors, Target/LocalTargets,
  UDP reply ownership, block/forwarding semantics, WinSW service.
- `src/WinForward.Configuration/ConfigurationModels.cs` — strict JSON context, top-level
  `KernelPassCache`.
- `src/WinForward.Configuration/ConfigurationRules.cs` — target/local-target validation.
- `src/WinForward.Cli/Program.cs` — direct NDISAPI composition; no backend abstraction today.
- `src/WinForward.Runtime/Capture/NdisPacketActionExecutor.cs` — packet block consumes the frame.
- `src/WinForward.Runtime/TcpRedirect/TcpProxyRelay.cs` — 30-minute stall window.
- `src/WinForward.Runtime/UdpProxy/UdpTransportContracts.cs` — transport seam.
- `src/WinForward.Runtime/UdpProxy/UdpResponseReinjector.cs` — packet-level UDP response leg.

## 7. Decisions still needed

1. **First merged milestone.** Foundation merge (monorepo, both builds/gates, dedupe, default
   WinpkFilter, Kernel host TCP only) versus full parity before any release. This is the current
   blocking question.
2. **Host adapter selector parity.** Reject in Kernel mode, remove from WinForward, or approximate
   via local address.
3. **Block semantics parity.** Accept `AccessDenied` on host TCP under the Kernel provider, or
   change/redefine block behaviour across editions.
4. **Provider granularity.** Global provider versus per-domain (host/forwarded) provider or a
   host-only mode for the Kernel driver.
5. **Process path contract.** Keep the Kernel's compile-time path-existence rule and 511-byte
   limit, or relax/adapt it for WinForward configuration compatibility.
6. **Driver distribution.** Production signing, installer/upgrade/rollback, and whether both drivers
   may be installed concurrently in a supported configuration.
