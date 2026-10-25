# Merge WinForward.Kernel into WinForward as the second backend

## Goal

Make WinForward the single maintained repository for both interception mechanisms: the existing
WinpkFilter/NDISAPI backend and the WinForward.Kernel WFP backend. The Kernel repository's driver,
shared ABI, tests, tools, gates, specs and development workflow move into this repository;
duplicated userland logic is consolidated; `WinForward.BackendDriver.Provider` selects the backend;
the Kernel repository is retired after the import is verified.

## User value

- One product, one configuration contract, one policy model, one relay/proxy implementation, one
  benchmark harness.
- A path to the connection-level WFP backend and its CPU benefit without maintaining a second
  product.
- WinpkFilter remains the default until the Kernel provider passes the parity, performance and
  stability gates, so existing deployments keep their behaviour.

## Background

The Kernel driver already provides host ALE redirect (TCP/UDP, IPv4/IPv6, rule blob v2, process
selectors, block sink) and a WFP L2 forwarded data plane (TCP/UDP, multi-ingress/multi-L/hot-swap,
native IPv6 at driver level, control ABI v8). Its
`research/gap-analysis.md` records what still blocks full replacement: host target mapping, host
UDP userland, host adapter selectors, forwarded local targets/adapter arrays/IPv6 L, block
semantics, process-selector compile-time constraints, ARM64, driver packaging/signing,
observability, and the D7 adoption conditions.

The Kernel repository's `Relay/`, `Control/` and `Configuration/` are explicitly scaffolding. Its
`Policy/`, `Forwarded/`, `Device/` and tests are the parts worth keeping. The Kernel `AGENTS.md`
already states the final form: a WinForward backend sharing the same `appsettings.json`, policy
semantics, events and counters.

## Milestone 1 (implementation target)

The first merged milestone is the foundation merge plus a Kernel-provider Host TCP path:

- Import the Kernel repository at commit `7ba4e6e` (`main`, the same tree as `feat/block-sink`,
  clean working tree) with history preserved where practical, and retire the source repository
  after the import is verified.
- Make the root repository build and gate both worlds: the .NET solution (C#, analyzers,
  `dotnet format`, `jb inspectcode`, tests) and the native world (WDK driver, CMake host tests,
  clang-format, cppcheck, forbidden-pattern checks, Windows `gate.ps1`).
- Deduplicate the Kernel userland: keep the driver, `common/`, the `Device/` ABI mirror, the host
  and forwarded blob compilers, and their tests; retire the Kernel `Relay/`, `Control/`,
  `Configuration/` and `WinForward.Controller` in favour of `WinForward.Runtime`,
  `WinForward.Configuration`, and the WinForward composition root.
- Add `WinForward.BackendDriver` with a global `Provider` and strict provider capability gating.
  The default remains `WinpkFilter`.
- Ship a Kernel-provider Host TCP path: compile the WinForward host policy into the host blob,
  resolve the configured target through the `rule_index` bridge, reuse the WinForward TCP relay,
  enforce `TcpFlowCapacity`, keep the existing fail-closed posture, and expose the counters and
  events the E2E harness needs.
- Use a test-signed Kernel driver for development and VM validation. Production signing,
  installer, upgrade/rollback, host UDP, forwarded parity and ARM64 belong to later milestones.

The Kernel provider in milestone 1 accepts only what it can actually serve. `validate` must reject,
with an actionable path-qualified diagnostic:

- Host `AdapterId`/`AdapterName` selectors (the ALE layer has no interface index).
- Any host rule whose action is `proxy` and whose protocol selector can match UDP (including an
  absent `Protocol` selector); the Kernel provider has no host UDP relay yet.
- Any `Forwarded` rules or a non-default forwarded policy.
- `KernelPassCache` and other WinpkFilter-only settings under the Kernel provider.
- Path-type process selectors that do not resolve to an existing file or directory, or that exceed
  the 511-byte process path limit.

## Decisions

| # | Decision |
|---|---|
| D1 | WinForward becomes the only maintained repository; the Kernel repository is imported and retired, not kept as a second release line. |
| D2 | The merge source is Kernel `main` at `7ba4e6e`; `feat/block-sink` points at the same commit and the working tree is clean. Experimental branches are not imported; the migration note records them. |
| D3 | Milestone 1 is the foundation merge plus Kernel-provider Host TCP. Host UDP, forwarded parity, ARM64 and production driver distribution are later milestones. |
| D4 | `BackendDriver.Provider` is global. There is no per-domain provider and no supported mixed-driver mode in milestone 1; capability gating rejects unsupported domains. |
| D5 | Host adapter selectors are rejected under the Kernel provider with a configuration diagnostic. The WinpkFilter provider keeps them. |
| D6 | Kernel host TCP `block` keeps the WFP `AccessDenied(10013)` behaviour. This is an accepted, documented edition difference; the parity gate allows it while still requiring both editions to fail closed. |
| D7 | Path-type process selectors keep the Kernel contract: the path must resolve to an existing file or directory at validation time, is normalized to the `\device\...` form, and is capped at 511 UTF-8 bytes. Diagnostics use the WinForward configuration path. |
| D8 | Host-rule target resolution uses the existing `rule_index` context field as a contract, with one configured rule mapping to one blob rule. No ABI bump in milestone 1. The WinForward host fallback is pass/block only, so no fallback target is needed; any future fallback redirect must be defined before use. |
| D9 | Milestone 1 uses a test-signed Kernel driver. Production signing, installer, upgrade/rollback and crash-recovery operations are later milestones. |
| D10 | The Kernel `Relay/`, `Control/`, `Configuration/` and `WinForward.Controller` are retired. The `Device/` ABI mirror, `Policy/`/`Forwarded/` compilers, driver, `common/`, tests and tools are kept and adapted. |

## Requirements

### R1 — Repository consolidation

Import the Kernel repository into this repository in a layout that keeps the WDK/MSVC/CMake build
separate from the .NET solution, preserving history where practical. Migrate its host tests, Linux
gates, Windows gates, formatting/lint rules, pre-commit hooks, devShell pieces, and the Trellis
specs/tasks/research that are still relevant. Define what is retired (Kernel `Relay/`, `Control/`,
`Configuration/`, `WinForward.Controller`) and what is kept (driver, `common/`, `Policy/`,
`Forwarded/` compilers, `Device/` ABI mirror, tests, tools). After a verified import the Kernel
repository is no longer a maintenance target.

### R2 — One build and quality-gate surface

The root repository must build and test both worlds: the .NET solution with its C# tests,
analyzers, `dotnet format` and `jb inspectcode` gates, and the WDK driver with CMake host tests,
clang-format, cppcheck and forbidden-pattern gates. The existing WinForward gates must not be
weakened; the Kernel gates must not be dropped. CI and pre-commit hooks cover both.

### R3 — BackendDriver configuration

Add `WinForward.BackendDriver` with `Provider` (`WinpkFilter` | `WinForward.Kernel`) and
provider-specific settings. Move `KernelPassCache` under the WinpkFilter provider. Kernel-specific
settings (driver service/device, expected ABI, listener address overrides) live under the Kernel
provider. Strict validation: unknown or inapplicable provider keys are errors; common settings stay
top-level. The default provider remains WinpkFilter until acceptance.

### R4 — One userland

One configuration parser/validator, one policy model, one rule-compiler path, one relay/SOCKS5/UDP
implementation. The Kernel ABI mirror and the host/forwarded blob compilers are integrated into the
shared userland; its duplicate relay/control/config code is removed or reduced to test fixtures.
No second proxy implementation.

### R5 — Kernel host TCP

A Kernel-provider host TCP path with policy compilation to the host blob, target resolution from
the matched rule (a `rule_index` contract or an explicit ABI extension), listener/acceptor/relay
reuse, capacity and failure semantics, self-traffic handling, counters/events, configuration
diagnostics, and E2E coverage.

### R6 — Kernel host UDP

A host UDP listener and response path (same-socket `sendto`, original destination via cmsg), target
and local-target selection, UoT, session capacity/idle/receive-buffer settings, reply-ownership
semantics, and a mitigation or explicit acceptance for the Windows 11 26100 connected-UDP refcount
leak.

### R7 — Kernel forwarded

Forwarded parity for local targets, adapter selector arrays, IPv6 listener addresses, multi-L and
UDP hot-swap, adapter-recreation refresh, LWF visibility, and the config-disabled fail semantics.

### R8 — Observability and parity

A shared event/counter contract with backend-specific keys, an E2E harness that runs both providers
with the same plans, and a differential gate comparing decisions (rule hit, action, target,
foreign/leak/block outcomes) for a common configuration.

### R9 — Platform and productization

ARM64 driver support, production signing/attestation, installation/upgrade/rollback of the Kernel
driver, crash-safe disable/watchdog behaviour, ABI v8 lockstep, and coexistence rules for the two
drivers.

### R10 — Non-regression

The WinpkFilter provider keeps its current behaviour, defaults and performance. The Kernel provider
is opt-in until the parity, performance and stability gates pass.

## Acceptance Criteria

- [ ] AC1: A fresh clone builds the .NET solution and the Kernel driver/host tests from the root
      instructions; CI runs both.
- [ ] AC2: `WinForward validate` accepts `BackendDriver.Provider=WinpkFilter` and
      `WinForward.Kernel`, rejects unknown or inapplicable provider keys, and reports capability
      gaps (for example an unsupported forwarded feature) with actionable diagnostics.
- [ ] AC3: A host TCP E2E through the Kernel provider completes with the same policy decision and
      target as WinpkFilter for a shared configuration; capacity and failure paths are covered.
- [ ] AC4: A host UDP E2E including a local-target/DNS-shaped flow and UoT completes; reply source
      and session lifecycle are verified.
- [ ] AC5: Forwarded TCP/UDP parity cases pass, including local targets, multiple adapters, IPv6,
      multi-L and adapter recreation; LWF on/off is recorded.
- [ ] AC6: The counters/events consumed by the E2E analyzer exist for both providers, and the
      parity gate reports zero decision mismatches for the agreed matrix.
- [ ] AC7: The Kernel driver builds for ARM64 and x64; the production install/upgrade/rollback and
      crash-recovery paths are exercised on the VM.
- [ ] AC8: The Kernel repository has no remaining maintenance role; its final commit/tag and
      migration notes are recorded in this repository.
- [ ] AC9: The WinpkFilter provider regression suite and benchmarks remain green.

Milestone 1 is complete when M1-AC1–M1-AC5 pass. AC1–AC3 and AC9 apply to this milestone; AC4–AC7
belong to later milestones.

## Milestone 1 Acceptance

- [ ] M1-AC1: A fresh clone builds the .NET solution and the Kernel driver/CMake host tests from
      the root instructions; CI runs both; the native `check.sh`/`gate.ps1` and the .NET
      format/inspectcode gates are wired in.
- [ ] M1-AC2: `validate` accepts both providers for a supported configuration and rejects every
      unsupported Kernel-provider capability listed in "Milestone 1" with a path-qualified
      diagnostic.
- [ ] M1-AC3: A Kernel-provider host TCP E2E (TCP-only proxy rules, pass, block, multiple targets,
      capacity) completes with the same rule/target decision as WinpkFilter for the shared matrix;
      `rule_index` target selection is covered.
- [ ] M1-AC4: The Kernel `Relay/`, `Control/`, `Configuration/` and `WinForward.Controller` are no
      longer on the product path; the shared userland compiles the Kernel blobs and uses
      `WinForward.Runtime`.
- [ ] M1-AC5: The WinpkFilter provider regression suite is green, and the Kernel source commit/tag
      is recorded; the source repository is archived after the import is verified.

## Deferred Items

These are explicitly out of milestone 1 and are tracked here so they are not lost:

- Host UDP: listener/response path, local targets/DNS, UoT, session capacity/idle/receive buffer,
  reply-ownership semantics, and the Windows 11 26100 connected-UDP refcount-leak mitigation.
- Forwarded parity: local targets, adapter selector arrays, IPv6 listener addresses, multi-L and
  UDP hot-swap, adapter-recreation refresh, LWF on/off, and config-disabled semantics.
- Platform/productization: ARM64 driver build, production signing/attestation, installer/
  upgrade/rollback, crash-safe watchdog, and 24-hour stability/performance acceptance.
- ABI target-id extension (if the `rule_index` contract ever proves insufficient), per-domain
  providers or mixed-driver mode (rejected for milestone 1), and host-adapter-local-address
  approximation (rejected; Kernel mode rejects the selector).
- The Kernel `experiment/connected-udp-ale-nat` branch and other experimental branches are not
  imported; the migration note records them.


## Out of Scope

- New proxy protocols or features unrelated to the merge.
- Changing the default provider to Kernel before AC3–AC7 pass.
- Keeping the Kernel repository as a separately maintained product or a second release line.
- Rewriting the WFP driver beyond what parity, ARM64, packaging and stability require.

## Constraints

- One `appsettings.json` contract for both providers; no silently ignored settings.
- Edition parity is the acceptance criterion, not "the Kernel driver works in isolation".
- The Kernel's duplicate userland must not become a second implementation.
- The WinpkFilter path is the baseline and must not regress.
- Windows x64 and ARM64 are both target platforms; milestone 1 validates x64 only.
