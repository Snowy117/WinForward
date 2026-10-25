# Implementation plan: Kernel backend integration, milestone 1

Date: 2026-10-25
Task: `.trellis/tasks/10-25-kernel-backend-integration`
Source: `WinForward.Kernel` `main` at `7ba4e6e` (same tree as `feat/block-sink`, clean).
Target: monorepo foundation + WinpkFilter default + Kernel-provider Host TCP.

This plan is ordered. Do not skip a phase's validation gate; later phases assume the earlier ones
are green. The WinpkFilter behaviour is the baseline; any regression stops the phase.

## Phase 0 — Import and build foundation

- [ ] Add the Kernel tree under `kernel/` with history (subtree or filtered merge). If history is
      impractical, import a snapshot and write `kernel/MIGRATION.md` with the source commit, branch
      tips, file manifest and the reason history was not preserved.
- [ ] Do not import kernel build outputs (`.git`, `build/`, `cmake-build-debug/`, `bin/`, `obj/`,
      `.direnv/`, `.idea/`, local configs, journals, archived task trees). Extend the root
      `.gitignore`; keep `kernel/MIGRATION.md`.
- [ ] Move/adapt the live Kernel specs into `.trellis/spec/backend/kernel/` and add them to the
      backend spec index; add the moved spec paths to `implement.jsonl`/`check.jsonl`. Keep the
      native `kernel/README.md` and `kernel/AGENTS.md` accurate.
- [ ] Copy every Kernel `.trellis/tasks/**/research/**` document plus every `verification.md` and
      `d7-decision.md` (live and archived) into `.trellis/spec/backend/kernel/research/`, preserving
      the source task names and adding an index. Do not copy journals/workspaces or the rest of the
      task bookkeeping. Add the index and key research paths to the context manifests.
- [ ] Adapt `kernel/tools/check.sh` and the Windows scripts to the new root (path discovery,
      `--build-dir`, `git` root, WDK project paths). Keep their exit-code contract.
- [ ] Keep the native build out of the .NET solution; add the new C# projects/tests to
      `WinForward.slnx` in Phase 1 as they are created.
- [ ] Add a Linux CI job (dotnet + CMake host tests) and a Windows CI job (WDK build + driver gate),
      or document the required manual Windows step if hosted WDK runners are unavailable.
- [ ] Merge the devShell requirements (dotnet 10, clang, cmake, ninja, cppcheck, clang-tidy) into
      the root `shell.nix`/flake; keep the `.NET` and native tools reproducible.
- [ ] Install one root pre-commit dispatcher: `dotnet format` + `kernel/tools/check.sh --fast
      --staged`; preserve the staged-blob C++ semantics.

Gate 0:

```bash
dotnet build WinForward.slnx -c Release
dotnet test WinForward.slnx -c Release
dotnet format WinForward.slnx --severity info --verify-no-changes --no-restore
kernel/tools/check.sh --fast
(cd kernel && cmake --build build/check --target wf_check)   # after configure
```

WinpkFilter behaviour and the existing test suite must be unchanged.

## Phase 1 — Shared contracts and configuration

- [ ] Create `src/WinForward.Kernel.Contracts` from the Kernel `Device/`, `Policy/` and
      `Forwarded/` code: ABI mirror, device client, host/forwarded blob compilers, process-path
      resolver, layout constants.
- [ ] Remove the Kernel `Configuration/`, `Control/` and `Relay/` from the product graph. Keep only
      the essential helpers (redirect-context query/parse, block sink, listener binding) in the
      contracts/backend project.
- [ ] Make `WinForward.Configuration.ValidatedConfiguration` the only configuration entry. Add a
      converter from WinForward's `PolicySnapshot` to the Kernel compiler input; preserve rule
      order and one-config-rule-to-one-blob-rule.
- [ ] Translate Kernel compiler exceptions into `ConfigDiagnostic` with
      `WinForward.Host.Rules[i]` paths. Do not leak the Kernel's separate error vocabulary.
- [ ] Add `BackendDriverDto`/`BackendDriver` and provider-aware validation:
      - `Provider` values exactly `WinpkFilter` / `WinForward.Kernel`.
      - Nested `WinpkFilter` and `WinForwardKernel` objects; unselected object present is an error.
      - Move `KernelPassCache` into `WinpkFilter`; keep the top-level key as a one-release warning
        alias only under `WinpkFilter`; both present is an error.
      - Capability gate: host adapter selectors, host UDP-capable proxy rules, forwarded rules,
        KernelPassCache under Kernel, path existence/511B, local-target proxy rules.
      - Non-default UDP settings under Kernel: warning, inert until host UDP.
      - `SetupWorkerCount` and `TcpFlowCapacity` remain meaningful in the Kernel backend.
- [ ] Port the ABI/layout/compiler tests into `tests/WinForward.Kernel.Tests` on the root xUnit
      stack; delete tests for retired Relay/Control/Configuration code. Keep the C++ host tests.
- [ ] Update `appsettings.example.json`, config docs and the config test matrix.

Gate 1:

```bash
dotnet test tests/WinForward.Configuration.Tests
dotnet test tests/WinForward.Kernel.Tests
dotnet test tests/WinForward.E2E.Tests
dotnet format ...; jb inspectcode ...
```

## Phase 2 — Backend seam

- [ ] Introduce `IInterceptionBackend` (run + snapshot + dispose) and move the existing capture
      loop into `WinpkFilterBackend` without behaviour change.
- [ ] Add provider selection in the composition root. Keep the report, exit codes, logging and
      console cancellation behaviour.
- [ ] Add `KernelBackend` with the device client, listener, block sink, rule/target publication and
      shutdown ordering from `design.md` §5.2; leave the accept/relay path for Phase 3.
- [ ] Add the raw-socket overload to `TcpProxyRelayFactory` and expose the relay end kind so the
      Kernel adapter can choose linger 0 on fault/stall. Keep the existing interface path and its
      tests unchanged.
- [ ] Ensure `WinpkFilterBackend` still owns `DurableCaptureBundle`, `LayeredCaptureRunner`,
      `TcpProxyCoordinator`, `UdpProxyCoordinator`, `SelfTrafficRegistry` and the NDISAPI driver.
- [ ] Add integration tests for provider selection, missing driver, ABI mismatch, and config
      diagnostics; no driver needed for the fake-backend tests.

Gate 2:

```bash
dotnet test WinForward.slnx -c Release
dotnet format ...; jb inspectcode ...
```

The WinpkFilter provider must still pass its full suite and E2E smoke.

## Phase 3 — Kernel-provider Host TCP

- [ ] Implement the startup order: open device, ABI check, disable host/forwarded config, empty
      forwarded blob, bind/listen, compile host blob, build the target table, publish/verify blob,
      bind the block sink if needed, publish/verify `ProbeConfig` (listener port/PID, self PID,
      block sink), start accepting, report ready.
- [ ] Implement the accept path: read redirect context, reject non-TCP/bad contexts, resolve
      `rule_index` → target, capacity gate, setup executor, relay establishment, completion
      observation, counter/log update.
- [ ] Enforce `TcpFlowCapacity`; a full backend closes with a reset and records the counter.
- [ ] Fail closed on missing/unknown target, out-of-range rule index, non-proxy rule, SOCKS5
      failure and relay fault; never fall back to a default target.
- [ ] Implement clean shutdown: disable config, stop accepting, drain relays, dispose block sink,
      clear blobs, dispose device. Verify no accepted socket is left unowned.
- [ ] Log provider/backend, listener, block sink, rule generation, target resolution failures and
      relay outcomes. Mirror the driver and relay counters.
- [ ] Windows VM validation with the test-signed driver:
      - driver missing / ABI mismatch / config rejection diagnostics;
      - host TCP pass, proxy to two named targets, block, fallback block, capacity rejection;
      - process filename/path selectors, path-existence rejection and 511-byte rejection;
      - host adapter selector rejection, UDP proxy rejection, forwarded rejection;
      - clean shutdown and restart with stale driver state.
- [ ] Add a Kernel-provider TCP-only E2E plan and a runner that deploys the driver + artifact.

Gate 3:

```bash
# Windows VM, elevated, test signing enabled
pwsh -File kernel/tools/windows/gate.ps1 -Remote <host:port>
# Kernel-provider E2E (TCP-only)
# WinpkFilter regression E2E
```

## Phase 4 — Observability and parity

- [ ] Add `kernel.*` counters/records to the heartbeat and log stream; document the key list.
- [ ] Teach `benchmarks/WinForward.E2E` the `provider` dimension and the Kernel deployment
      pre-step; the M1 analyzer gates cover scheduled attempts, rule/target parity, direct-lane
      no-leak, block/pass outcomes and capacity.
- [ ] Produce the M1 parity report: WinpkFilter vs Kernel for the shared TCP matrix, including the
      accepted differences (AccessDenied block, accept-then-reset capacity, unsupported UDP/
      forwarded).

Gate 4: the parity report is attached to the task; no unexplained decision mismatch.

## Phase 5 — Docs, dedupe and retirement

- [ ] Update `README.md`, `AGENTS.md`, `appsettings.example.json`, examples and the E2E/local-run
      docs for the two providers, build instructions and the test-signed driver prerequisite.
- [ ] Confirm no product path uses the retired Kernel `Relay/`, `Control/`, `Configuration/` or
      `WinForward.Controller`; remove dead files and tests.
- [ ] Write `kernel/MIGRATION.md` with the source commit/tag, branch tips, imported/retired paths,
      gate mapping and the retirement date. Archive the source repository after the import is
      verified; no further commits there.
- [ ] Run the final full-scope check: .NET build/test/format/inspectcode, native fast/full/staged,
      Windows driver gate, WinpkFilter E2E and Kernel TCP-only E2E.

## Later milestones (not in this plan)

1. Host UDP: listener/response path, local targets/DNS, UoT, session capacity/idle/receive buffer,
   reply ownership, connected-UDP refcount-leak mitigation.
2. Forwarded parity: local targets, adapter arrays, IPv6 L, multi-L/UDP hot-swap, adapter refresh,
   LWF on/off, config-disabled semantics.
3. Platform/productization: ARM64, production signing/attestation, installer/upgrade/rollback,
   crash watchdog, 24-hour stability and the D7 performance acceptance.
4. Only after the above: consider a target-id ABI extension, per-domain providers/mixed mode, or
   host adapter-selector approximation if the product still needs them.

## Risky files and rollback points

- `src/WinForward.Cli/Program.cs` and the new backend seam: the WinpkFilter extraction must remain
  behaviour-identical; keep the existing tests and the E2E smoke as the rollback signal.
- `src/WinForward.Runtime/TcpRedirect/TcpProxyRelayFactory.cs` / `TcpProxyRelay.cs`: the raw-socket
  overload and end-kind exposure must not change the packet path; revert the seam if those tests
  change.
- `src/WinForward.Configuration/ConfigurationModels.cs` / `ConfigurationRules.cs`: the legacy
  `KernelPassCache` alias and provider gate are the highest-risk config changes; keep the old
  WinpkFilter config test corpus green.
- `WinForward.slnx`, `Directory.Build.props`, CI and hooks: the import must not weaken the
  WinpkFilter gates; revert the CI/hook change if the baseline cannot pass.
- `kernel/tools/check.sh`, `gate.ps1`, `.clang-format`, `.clang-tidy`: path adaptation must keep
  the staged-blob and exit-code contracts.
- The driver config publish/disable order: a failed start must leave the driver disabled, and a
  clean stop must disable before closing the listener.

## Review gates before `task.py start`

- [ ] `prd.md`, `design.md` and `implement.md` reviewed by the user.
- [ ] `implement.jsonl` and `check.jsonl` contain real spec/research entries.
- [ ] The user explicitly approves this exact planning summary for implementation.
