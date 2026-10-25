# Batch 2: BackendDriver configuration and the Kernel capability gate

Parent: `.trellis/tasks/10-25-kernel-backend-integration`

## Goal

Add the `WinForward.BackendDriver` configuration section and provider-aware validation. The default
stays WinpkFilter and the WinpkFilter data path is unchanged. The Kernel provider is recognized and
capability-gated, but `run` still refuses it with an actionable diagnostic until the Kernel backend
lands. No Kernel contracts, blob compiler or interception data path is added in this batch.

## Requirements

- **B2-R1 Configuration shape.** Add optional `WinForward.BackendDriver` with `Provider` and nested
  `WinpkFilter` / `WinForwardKernel` objects. Absent section means `Provider = WinpkFilter` and the
  current defaults. Provider values are `WinpkFilter` and `WinForward.Kernel`; an unknown value is
  a path-qualified error. The nested objects are strictly typed; unknown keys are errors.
- **B2-R2 Provider-specific objects.** `WinpkFilter.KernelPassCache` is the canonical location.
  `WinForwardKernel.DriverServiceName` defaults to `wfprobe` and is validated non-empty when the
  Kernel provider is selected. The unselected provider object must be absent; its presence is an
  error. `KernelPassCache` under the Kernel provider is an error.
- **B2-R3 Legacy alias.** The top-level `WinForward.KernelPassCache` remains accepted only under the
  WinpkFilter provider. It maps to `BackendDriver.WinpkFilter.KernelPassCache`, emits a warning
  naming the new location, and fails as ambiguous when both are present. Under the Kernel provider
  it is an error. This is a one-release migration aid.
- **B2-R4 Kernel capability gate (milestone 1).** Under `Provider = WinForward.Kernel`, reject with
  `WinForward.Host.Rules[i]...` paths:
  - `AdapterId` / `AdapterName` selectors;
  - any `proxy` rule whose `Protocol` selector is absent, is `udp`, or contains `udp`;
  - any `Forwarded` rules or a non-default forwarded policy;
  - a process selector whose UTF-8 form exceeds 511 bytes.
  The path-existence check belongs to the Kernel process-path resolver and is deferred to the
  contracts/compiler batch; this batch must record that boundary in the diagnostic/docs. Pass and
  block rules remain valid for any protocol.
- **B2-R5 Validated result and CLI.** `ValidatedConfiguration` exposes a
  `BackendDriverConfiguration` (provider, KernelPassCache, DriverServiceName). `validate` prints the
  selected provider. `run` with the Kernel provider fails with a clear "Kernel backend not
  available in this build" diagnostic (exit 1) before opening NDISAPI; the WinpkFilter path keeps
  its current behaviour and exit codes.
- **B2-R6 Tests and docs.** Add configuration unit tests for the matrix above and the legacy alias;
  update `appsettings.example.json` and the README configuration section. Existing WinpkFilter
  configuration tests must stay green.

## Acceptance Criteria

- [x] B2-AC1: an omitted `BackendDriver` behaves exactly as `Provider = WinpkFilter`; the existing
      configuration test corpus is green.
- [x] B2-AC2: provider/nested-object/unknown-key/unselected-object/legacy-alias cases produce the
      specified diagnostics and warnings, and `KernelPassCache` resolves to the same value the
      WinpkFilter runtime consumes.
- [x] B2-AC3: every Kernel capability refusal names the exact configuration path; a
      Kernel-capable host-TCP configuration validates.
- [x] B2-AC4: `validate` reports the selected provider; `run` with the Kernel provider exits 1 with
      an actionable message and never opens NDISAPI; the WinpkFilter run is unchanged.
- [x] B2-AC5: `dotnet build`, `dotnet test`, `dotnet format` and the Kernel native fast gate pass.

## Out of Scope

- The `WinForward.Kernel.Contracts` project, ABI mirror, blob compilers, device client.
- The backend seam, Kernel host TCP, host UDP, forwarded, ARM64, driver packaging.
- The process-path existence check (Kernel resolver) and any rule-index target mapping.

## Known environment gap

The repository-level `jb inspectcode` gate could not produce a report: the global
`JetBrains.ReSharper.GlobalTools` 2026.2.3.1 Roslyn worker fails to load
`System.Composition.AttributedModel, Version=10.0.0.12` while instantiating the
`Microsoft.CodeAnalysis.NetAnalyzers` fixers. The tool was stopped after repeating this analyzer-load
exception, not after finding a code issue. The primary gates (`dotnet build` with
`TreatWarningsAsErrors`, `dotnet test`, `dotnet format`, the Kernel fast gate) are green. This is an
environment/tooling gap to resolve before the next C# batch, not a batch acceptance failure.

