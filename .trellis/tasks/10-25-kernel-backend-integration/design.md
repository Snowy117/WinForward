# Design: merge WinForward.Kernel into WinForward as the second backend

Date: 2026-10-25
Task: `.trellis/tasks/10-25-kernel-backend-integration`
Scope: milestone 1 (foundation merge + Kernel-provider Host TCP); later milestones are named but
not designed here.

## 1. Boundaries

Milestone 1 merges the repositories and proves one data path: WinForward's host TCP policy served
by the Kernel WFP driver, selected through `WinForward.BackendDriver.Provider`. It does not add
host UDP, forwarded parity, ARM64, production signing or a mixed-driver mode. The WinpkFilter
backend remains the default and must keep its current behaviour.

The design has two hard boundaries:

- **One product userland.** `WinForward.Configuration`, `WinForward.Core`, `WinForward.Runtime` and
  the `WinForward.Cli` composition root remain the only configuration, policy and relay path. The
  Kernel userland contributes an ABI/codec layer, not a second product.
- **One active driver.** The global provider selects exactly one interception mechanism. The
  Kernel driver may be installed but disabled while WinpkFilter runs; it is enabled only for a
  Kernel-provider run. No per-domain provider and no supported mixed mode in milestone 1.

## 2. Repository layout and import

The Kernel source is `main` at `7ba4e6e` (the same tree as `feat/block-sink`; the working tree is
clean). `experiment/connected-udp-ale-nat` and other experimental branches are not imported; the
migration note records their tips.

Proposed layout:

```text
kernel/
  src/driver/              WFP driver (WDK; unchanged build inputs where possible)
  src/common/              shared C++ ABI, blob and matcher headers
  tests/host/              CMake/gtest host tests
  tools/                   check.sh, lint/, linux/, windows/, wfprobe/, socks5/
  README.md                native build, gates and VM workflow (adapted from the Kernel repo)
  AGENTS.md                native-specific assistant rules; the root AGENTS.md stays authoritative
  .clang-format
  .clang-tidy
  .editorconfig            C++ scope only
  MIGRATION.md             source commit/tag, imported paths, retired paths, branch tips

src/WinForward.Kernel.Contracts/     C# ABI mirror, device client, host/forwarded blob compilers
tests/WinForward.Kernel.Tests/       ABI/layout/compiler/config tests on the root xUnit stack
tools/                               root .NET/native gates; kernel tools remain under kernel/tools/
```

Import with a history-preserving subtree/filtered merge. If that is impractical, import a snapshot
and record the source commit plus a file manifest; the migration note must make the imported state
traceable either way.

### 2.1 What moves, what retires

Move and adapt:

- `kernel/src/driver`, `kernel/src/common`, `kernel/tests/host`, `kernel/tools`.
- The Kernel `.clang-format`, `.clang-tidy`, `check.sh`, `lint/`, `flake.nix`/devShell content,
  `.githooks`, and the live Kernel specs under `.trellis/spec/backend/kernel/` (WFP architecture,
  host blob, forwarded blob, the connected-UDP leak note, C++/quality conventions).
- Every `research/**` document plus every `verification.md` and `d7-decision.md` from the Kernel
  `.trellis/tasks` (both the live task set and `archive/`), copied to
  `.trellis/spec/backend/kernel/research/` with the source task names preserved and an index that
  maps each document to its topic. The Kernel workspaces/journals, `task.json` bookkeeping and the
  rest of the task tree are not copied; the live decisions and evidence are what matter.
- The C# `Device/` ABI mirror, the host/forwarded blob compilers, their process-path resolver and
  the tests that pin the ABI/layout/compiler. Their namespace may change; the byte layout may not.
- The essential helpers from the retired userland: redirect-context query/parse (`SocketIoctl`,
  `RedirectDestination`), the block sink, and the listener/port binding code. They move into the
  Kernel backend or the contracts project, not into a second relay.

Retire:

- Kernel `Relay/` (`RelayServer`, `PumpPair`, `OutboundConnector`, `Socks5Client`,
  `ForwardedUdpRelay`, `ListenerSet` as a product relay) — replace with `WinForward.Runtime` plus a
  small raw-socket relay adapter.
- Kernel `Control/` and the `WinForward.Controller` project.
- Kernel `Configuration/` and its temporary JSON schema — replace with `WinForward.Configuration`.
- The Kernel `PolicyTable`/`PolicyRule` product model and its config parser; keep only the
  compiler's internal value types as the ABI encoding layer.
- Kernel workspace/journals and archived task trees; migrate live decisions and research pointers,
  not the whole history.

The Kernel driver's `wfprobe` device/probe naming may stay in milestone 1 (test delivery); the
production service/device naming is a later productization milestone.

### 2.2 Build and quality gates

The root must build both worlds. Keep the native tree under `kernel/` so WDK/MSBuild/CMake and the
.NET solution do not share a root build graph.

- .NET: add the new C# projects/tests to `WinForward.slnx`; keep `dotnet build -c Release`,
  `dotnet test -c Release`, `dotnet format WinForward.slnx --severity info --verify-no-changes
  --no-restore`, and `jb inspectcode` as the root gates. The Kernel C# code must land analyzer-clean
  or with narrow, documented suppressions; its previous conventions do not override the root
  rules.
- Native: run `kernel/tools/check.sh --fast/--full/--staged` from the root gate; keep the
  clang-format/cppcheck/forbidden-pattern and effective-line checks. The CMake host test target
  `wf_check` remains the Linux native gate.
- Windows: the WDK build plus `kernel/tools/windows/gate.ps1` (host/forwarded-rules/pool checks)
  runs on a Windows runner; `mutation.ps1` and CodeQL remain optional deep gates.
- Pre-commit: one root hook runs the .NET format gate and `kernel/tools/check.sh --fast --staged`;
  keep the Kernel's staged-blob semantics for C++.
- CI: a Linux job for .NET + CMake host tests, a Windows job for the WDK build + driver gate. The
  devShell must provide both toolchains or document the split.

Do not weaken a gate to make the import green. If a Kernel check is genuinely inapplicable to a
path, scope it with a reason; do not delete it.

### 2.3 Trellis and docs

Move the live Kernel specs under `.trellis/spec/backend/kernel/` and add them to the backend spec
index. Update the root `README.md` (two providers, build, driver setup), `AGENTS.md` (native
quality gates and the single-repository decision), and the E2E/local-run documentation. Record the
repository-retirement decision and the migration note in `kernel/MIGRATION.md`.

## 3. Configuration contract

### 3.1 Shape

```json
{
  "WinForward": {
    "BackendDriver": {
      "Provider": "WinpkFilter",
      "WinpkFilter": { "KernelPassCache": false },
      "WinForwardKernel": { "DriverServiceName": "wfprobe" }
    }
  }
}
```

- `BackendDriver` is optional; absent means `Provider = WinpkFilter` and the current defaults.
- `Provider` is case-sensitive PascalCase like every other key; accepted values are exactly
  `WinpkFilter` and `WinForward.Kernel`.
- `WinpkFilter` holds WinpkFilter-only settings; `KernelPassCache` moves here from the top level.
- `WinForwardKernel` holds Kernel-only settings. Milestone 1 exposes only `DriverServiceName`
  (default `wfprobe`) for diagnostics and driver-operation messages; other keys are added later
  under a reviewed schema change.
- The unselected provider object must be absent; its presence is an error. Unknown keys are errors.
- **Legacy alias:** the top-level `KernelPassCache` is accepted only under `Provider = WinpkFilter`,
  maps to `BackendDriver.WinpkFilter.KernelPassCache`, and emits a validation warning naming the
  new location. If both are present, validation fails as ambiguous. Under the Kernel provider the
  top-level key is an error. This is a deliberate one-release migration aid; remove it after the
  documented deprecation window.

### 3.2 Capability gate (milestone 1)

The Kernel provider validates the *whole* configuration before opening the driver. The WinpkFilter
provider keeps its current validation, with the legacy alias above.

| Configuration shape | WinpkFilter | WinForward.Kernel (M1) |
|---|---|---|
| Host `AdapterId` / `AdapterName` | allowed | error: the ALE connect-redirect layer has no interface index |
| Host rule `Action = proxy`, `Protocol` absent or containing `udp` | allowed | error: no host UDP relay yet |
| Host rule `Action = proxy`, `Protocol = ["tcp"]` | allowed | allowed |
| Host `Action = pass` / `block` with any protocol | allowed | allowed (pass/block are kernel-executable) |
| Host fallback `pass` / `block` | allowed | allowed |
| Forwarded rules / non-default forwarded policy | allowed | error: forwarded is not part of M1 |
| `KernelPassCache` (nested or legacy) | allowed | error |
| Path process selector that does not resolve | allowed | error (Kernel contract) |
| Path process selector over 511 UTF-8 bytes | allowed | error (Kernel contract) |
| `LocalTargets` configured but unused | allowed | allowed |
| A proxy rule targeting a `LocalTarget` | allowed (UDP only) | error via the UDP-proxy gate |
| Non-default UDP capacities/buffers/idle settings | allowed | warning: inert until host UDP lands |
| `SetupWorkerCount` | allowed | allowed; bounds relay setup concurrency |
| `TcpFlowCapacity` | allowed | allowed; bounds active relay sessions |
| `Socks5Servers` with auth / UoT | allowed | allowed; UoT is inert for TCP |
| Multiple named SOCKS5 targets | allowed | allowed; resolved by `rule_index` |
| Unselected provider object present | error | error |

The gate is implemented in `WinForward.Configuration` (provider-aware validation) so both
`validate` and `run` use the same decision and the same `WinForward.Host.Rules[i]` diagnostic
paths. The Kernel compiler's own exceptions are translated into those diagnostics; they never
escape as a second, differently-shaped error vocabulary.

### 3.3 Shared settings

- `TcpFlowCapacity` bounds the Kernel backend's active relay sessions. The listener is shared, so
  the old "two ephemeral ports per flow" rationale becomes "the relay's own local sockets"; the
  limit still protects the ephemeral-port pool.
- `SetupWorkerCount` bounds concurrent relay establishment; the Kernel backend uses the existing
  `SetupExecutor` (auto = 2×logical, floor 16) or an equivalent bounded executor.
- `ProxyUnavailableAction` / `ProcessingFailureAction` remain block-only. The Kernel relay fails
  closed by refusing the accepted connection, with a client-visible reset.
- UDP capacities/buffers/idle: accepted with a warning in M1; they become live with host UDP.

## 4. Backend seam

Keep the seam narrow and at the composition boundary, not inside the packet path:

```csharp
internal interface IInterceptionBackend : IAsyncDisposable
{
    Task RunAsync(CancellationToken cancellationToken);
    BackendStatus Snapshot(); // provider, active sessions, capacity, counters/health
}
```

- `WinForward.Cli` loads `ValidatedConfiguration`, resolves `BackendDriver.Provider`, constructs
  the backend, and owns the console lifetime/exit codes. `Program.RunCaptureLoopAsync` moves behind
  `WinpkFilterBackend` without behaviour change; the existing `DurableCaptureBundle`,
  `LayeredCaptureRunner`, `TcpProxyCoordinator` and `UdpProxyCoordinator` stay inside it.
- `KernelBackend` owns the WFP device client, host blob publication, listener, block sink, relay
  sessions and shutdown ordering. It does not use `FlowDispatcher`, `NdisCapture`, `FlowTable`,
  `TcpRedirectTable` or `UdpProxyCoordinator`; those are the packet backend's implementation.
- Shared services: `ValidatedConfiguration`, `Socks5AddressCache`, `TcpProxyRelayFactory` (with a
  raw-socket overload), `SetupExecutor`, logging, `RuntimeCounters`, `QuiescenceScope` where
  appropriate. The WinpkFilter-only `SelfTrafficRegistry` stays in that backend; the Kernel
  backend uses the driver's self-PID gate.
- The `Program` startup report gains the provider and backend name. Exit codes keep their current
  meanings: 1 for a driver/config/ABI problem, 2 for usage/platform, 3 for a runtime failure.

## 5. Kernel-provider Host TCP path

### 5.1 Rule and target compilation

1. Convert the WinForward host policy into the Kernel compiler's input, preserving order: one
   configured rule → one blob rule. Reject (do not drop) any rule the capability gate refuses.
2. Compile the host blob, and build `ProxyTarget?[] TargetsByRuleIndex` from the same
   `PolicySnapshot`: only proxy rules have a target; pass/block rules have null. For M1 every
   resolved target must be a SOCKS5 server (a local target is UDP-only and is already refused).
3. Assert the compiler produced exactly `HostRules.Count` blob rules; otherwise fail startup. The
   `rule_index` contract is load-bearing: if a future compiler splits or merges rules, it must
   return an explicit index map and the backend must use it.
4. Resolve process paths with the Kernel production resolver at validation time; path existence,
   `\device\...` normalization and the 511-byte limit are Kernel contracts. Diagnostics keep the
   WinForward configuration path.

### 5.2 Startup and shutdown order

Startup:

1. Open `WfpDeviceClient`; verify the control ABI (v8) and read stats.
2. Disable host config; disable forwarded config and install an empty forwarded blob so a previous
   run's state cannot leak into this one.
3. Bind the TCP listener on an ephemeral port (IPv4 + IPv6 wildcard; the driver targets loopback
   when the local address is ANY and the local address otherwise). Bind the block sink only when
   the compiled policy contains block or fallback block.
4. Publish the host blob and verify the readback (installed, rule count, fallback, size).
5. Publish `ProbeConfig` with the listener port/PID, the process's own PID as the self-PID, and the
   block sink port; verify the readback.
6. Start the accept loop and report ready.

Shutdown reverses the visibility first: disable host config (no new redirects), stop accepting,
drain/dispose relay sessions, dispose the block sink after the driver is disabled, clear the blobs,
then dispose the device. The driver's unload is outside the process; milestone 1 does not own
production driver lifecycle.

### 5.3 Per-connection flow

1. Accept a client socket. Read `SIO_QUERY_WFP_CONNECTION_REDIRECT_CONTEXT`; parse the original
   destination. Reject non-TCP contexts (there should be none under the M1 capability gate), bad
   versions, or a missing context by closing with `LingerOption(true, 0)` and counting the refusal.
2. Resolve the target: `rule_index` must be in range and the rule must be a proxy rule with a
   SOCKS5 target. An out-of-range index means the blob/target table disagreed — fail closed and log
   a contract violation; never fall back to a default target.
3. Apply the capacity gate (`TcpFlowCapacity`). A full backend closes the accepted connection with
   a reset and records `kernel.relay.capacityBlocked`; it does not queue indefinitely.
4. Establish the relay through the existing WinForward SOCKS5 path (auth supported; UoT is irrelevant
   for TCP), with the setup executor bounding concurrent establishment. The relay factory's
   self-traffic registration is harmless; the driver's self-PID gate is the actual WFP loop guard.
5. Pump with the existing `TcpProxyRelay` (30-minute stall window, half-close drain, fault
   classification). Expose the relay end kind so the Kernel adapter can set linger 0 for a
   faulted/stalled end before disposing the client socket; a clean end keeps the normal FIN path.
6. Observe completion exactly once; decrement the active count, remove the session, log the
   outcome and mirror counters. Never leave an accepted socket without an owner.

### 5.4 Relay reuse seam

`TcpProxyRelayFactory.EstablishAsync` currently requires its private `TcpAcceptedConnection`
concrete type (`TcpRedirectListener.cs`), which the Kernel backend cannot construct. Add a
public overload that takes the accepted `Socket` plus the original destination and server, and
delegate the existing interface method to it. Keep the existing packet-path behaviour and tests.
The Kernel adapter owns the client socket until the relay takes it; a failed establishment closes
it with linger 0.

## 6. Observability and E2E

- Add Kernel-provider counters in the `RuntimeCounters` naming style: kernel host classify/
  redirect/block/pass (mirrored from `probe_stats` deltas), relay accepted/refused/capacity-blocked/
  faulted, bytes in/out, and config generation/ABI. The exact key list is part of the implementation
  checklist, not a new public metric taxonomy.
- Add a `kernel.stats` heartbeat/log record carrying the driver's `probe_stats` snapshot and the
  relay's active/capacity counts. The existing WinpkFilter heartbeat remains unchanged.
- The E2E harness gains a `provider` dimension and a Kernel deployment pre-step (driver load, test
  signing, service name). The M1 plan is TCP-only and uses the existing proxy chain/target lanes;
  the analyzer gains a Kernel counter adapter and a gate for the M1 invariants (scheduled attempts,
  target/rule parity, direct-lane no-leak, block/pass outcomes, no capacity surprise).
- The parity gate compares the WinpkFilter and Kernel providers for the M1 matrix: rule hit, action,
  target, and fail-closed outcome. It explicitly records the accepted differences (host TCP block
  returns AccessDenied; capacity rejection is accept-then-reset; UDP/forwarded are unsupported).

## 7. Testing

- Unit: provider-aware configuration validation and diagnostics; legacy alias; capability matrix;
  process-path diagnostics; blob/target-table order; rule-index target resolution including
  out-of-range and non-proxy failures; capacity gate; relay end-kind → close behaviour.
- Contract: existing Kernel ABI/layout tests, adapted to the new project paths, plus the C++ host
  tests. The C# ABI mirror stays byte-identical to the headers.
- Integration (Windows VM): test-signed driver load, ABI mismatch/missing-driver diagnostics,
  host TCP pass/proxy/block, multiple targets, capacity, proxy failure, clean shutdown, and a
  stale-state start (previous run left config enabled).
- Regression: the full WinpkFilter suite, format/inspectcode, and the existing E2E baseline.
- The M1 E2E must not require forwarded or UDP lanes; use a dedicated TCP-only plan rather than
  weakening the existing release plans.

## 8. Compatibility, rollout and rollback

- BackendDriver is additive; an absent section leaves WinpkFilter exactly as today. The only
  intentional configuration change is the nested `KernelPassCache`; the legacy top-level key is
  accepted with a warning for one migration window.
- Default provider remains WinpkFilter. Kernel is opt-in.
- Rollback is config-only: set `Provider = WinpkFilter` and restart. The Kernel backend disables
  the driver config on clean shutdown; a crashed run is covered by the test/VM watchdog in M1 and
  by the later product watchdog.
- The repository import is additive at first; the WinpkFilter build can be restored by reverting the
  merge commit before the retirement commit. Record the Kernel tag/commit before archiving.

## 9. Risks and trade-offs

| Risk | Mitigation |
|---|---|
| `rule_index` silently drifts from configuration order | Compile through one adapter; assert count/order at startup; fail closed on an out-of-range or non-proxy index; pin with a test. |
| The UDP capability gate rejects common catch-all proxy configs | Document that M1 Kernel configs must put `Protocol: ["tcp"]` on proxy rules; offer a path-qualified diagnostic; keep WinpkFilter for UDP. Host UDP is the next milestone. |
| Kernel driver load fails because forwarded L2 registration fails | Known Kernel risk; M1 has no host-only driver mode. The provider fails closed with a diagnostic and the user can fall back to WinpkFilter. |
| Kernel C# code fails the root analyzers/format gate | Budget analyzer cleanup/suppression in the import; keep the root gate, do not lower it. |
| The raw-socket relay overload changes packet-path relay behaviour | Delegate the existing interface method to the new overload; keep the packet path's concrete-type check and all existing tests. |
| The legacy `KernelPassCache` alias lingers | One documented migration window; remove it in the next major config revision. |
| Test-signed driver cannot be used outside the VM | M1 is development-only; production signing/installer is a later milestone. |
| Two build systems and duplicated specs | `kernel/` boundary, one root gate dispatcher, and a moved Kernel spec family; no second product userland. |
| The Kernel's experimental branches disappear from view | Record their tips and status in `kernel/MIGRATION.md`; do not import them. |

## 10. Deferred

Host UDP (listener/response, local targets/DNS, UoT, capacity/idle/receive buffer, connected-UDP
leak mitigation), forwarded parity (local targets, adapter arrays, IPv6 L, multi-L/UDP hot-swap,
adapter refresh, LWF, config-disabled semantics), ARM64, production signing/installer/upgrade/
rollback, crash watchdog, 24-hour stability/performance, and any rule_index→target-id ABI
extension are later milestones. The PRD's "Deferred Items" section is authoritative.
