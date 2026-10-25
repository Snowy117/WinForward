# Batch 2 implementation plan

## Steps

1. **Read the current surface.** `ConfigurationModels.cs`, `ConfigurationRules.cs`,
   `ConfigurationLimits.cs`, `ConfigurationLayering.cs`, the existing `Configuration.Tests`,
   `Program.cs`, `StartupLog.cs`, `appsettings.example.json`, and the README configuration section.
   Keep the current strict reader/source-generation style.
2. **DTOs and public types.** Add `BackendDriverDto`, `WinpkFilterDriverDto`,
   `WinForwardKernelDriverDto`, `BackendProvider`, `BackendDriverConfiguration` and the
   `WinForwardConfigDto.BackendDriver` property. Keep the top-level `KernelPassCache` property as the
   legacy alias with an XML comment saying so. Add the nested DTOs to the source-generated context
   automatically (they are reachable from the root DTO).
3. **Resolve the provider and nested objects** in a dedicated helper:
   - absent section/provider → WinpkFilter;
   - case-insensitive canonical values; unknown → error at
     `WinForward.BackendDriver.Provider`;
   - unselected nested object → error at its path;
   - `KernelPassCache` legacy/nested resolution with the ambiguity/warning rules from the design;
   - `DriverServiceName` default `wfprobe`, trimmed, non-empty under the Kernel provider.
4. **Capability gate.** Add `ConfigurationKernelCapabilities.cs` with one entry point that takes the
   raw DTO, the parsed host rules, the parsed forwarded rules and the forwarded fallback. Emit
   `WinForward.Host.Rules[i]...` / `WinForward.Forwarded` diagnostics for:
   adapter selectors, UDP-capable proxy rules, any forwarded rules/non-default fallback, and
   process selectors over 511 UTF-8 bytes. Add the inert-UDP-settings warnings. Do not resolve
   process paths or check existence here.
5. **Wire into the loader.** Resolve `BackendDriver` before/alongside the existing validation;
   call the capability gate after domains parse; fail with the collected errors. Set both
   `ValidatedConfiguration.KernelPassCache` (existing positional parameter) and the new
   `BackendDriver` property from the same resolved values. Ensure the default property reproduces
   WinpkFilter.
6. **CLI and logging.** Keep the existing `validate` confirmation line and add a separate
   `Provider: <name>` line. Add `StartupLog.BackendSelected` and `StartupLog.KernelBackendUnavailable`
   `[LoggerMessage]` events with fresh EventIds. In `run`, after the resolved-run report and before
   `NdisApiDriver.Open()`, refuse a Kernel-provider run with exit 1 and the actionable message; do
   not open NDISAPI or fall back silently. Preserve the WinpkFilter path and exit codes.
7. **Docs/examples.** Move `KernelPassCache` in `appsettings.example.json` to
   `BackendDriver.WinpkFilter.KernelPassCache`; add a short `BackendDriver` section to the README
   key table and a Kernel-provider paragraph naming the M1 capabilities and the unavailable-run
   state. Do not rewrite unrelated docs.
8. **Tests.** Add `BackendDriverConfigurationTests` covering: absent section default; absent
   provider; both canonical values; case-insensitive values; unknown provider; unknown nested key;
   unselected object; nested vs legacy `KernelPassCache` (nested, legacy warning, both error, Kernel
   error); empty `DriverServiceName`; Kernel capability matrix (adapter, catch-all proxy, UDP-only
   proxy, TCP-only proxy, pass/block UDP, forwarded rules, process selector >511 bytes); inert UDP
   warnings. Use the existing test helpers and JSON paths. Keep all existing configuration tests
   green.
9. **Do not touch** the backend/data path, `WinForward.Runtime`, `WinForward.NdisApi`, the Kernel
   contracts, `kernel/src/**`, or the unrelated dirty files in the worktree. Do not commit.

## Risky files / rollback

- `ConfigurationModels.cs` is the strict-schema source; a source-generation or unmapped-member
  mistake breaks every config test. Keep the existing exact-key behavior and run the full
  configuration suite.
- `Program.cs` provider refusal must happen before `NdisApiDriver.Open()`; a misplaced check could
  silently run WinpkFilter with a Kernel config. Verify the order by inspection and, where possible,
  an integration test with an injected provider.
- The legacy `KernelPassCache` alias must not change the WinpkFilter runtime value. Assert the
  nested, legacy and default cases all produce the same `ValidatedConfiguration.KernelPassCache`.
- Revert this batch by reverting the config/CLI/docs/test files; no runtime state is involved.

## Report

Changed files, the provider/capability matrix, the test commands and results, any diagnostic wording
or path that had to deviate from the plan, and the deferred path-existence boundary.
