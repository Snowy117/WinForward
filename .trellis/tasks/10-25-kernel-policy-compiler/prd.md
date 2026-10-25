# Batch 5b: Kernel policy compilation and the rule-index target table

Parent: `.trellis/tasks/10-25-kernel-backend-integration`

## Goal

Compile WinForward's host policy into the Kernel host blob and build the `rule_index` →
`ProxyTarget` table in the same order. Wire the Kernel provider's `validate` to this real compiler
so path existence, blob limits and rule mapping are checked before a run. The Kernel `run` still
refuses (the backend lands next); no device/accept/relay is added.

## Requirements

- **R5b-1 Fallback visibility.** Expose the host fallback action on `PolicySnapshot` (currently
  private) so the compiler can map it to the Kernel `RuleAction`; no behaviour change.
- **R5b-2 Compiler.** Add an internal `KernelPolicyCompiler` in the CLI composition layer that
  accepts `ValidatedConfiguration` and an `IProcessPathNormalizer`, maps each WinForward host rule
  to exactly one Kernel `PolicyRule` (process/path, CIDR, port ranges, protocol, family, action),
  compiles the `PolicyTable` through `RuleBlobCompiler.Compile`, and builds
  `ProxyTarget?[] TargetsByRuleIndex` from the same ordered rules. Proxy rules resolve their
  configured target; pass/block rules store null. Local targets are rejected defensively (M1 has no
  host UDP relay). Fallback maps pass→permit, block→block.
- **R5b-3 Diagnostics.** Map `ProcessSelector.Parse`/normalizer/`RuleBlobCompiler` failures to
  `ConfigDiagnostic` at `WinForward.Host.Rules[i]...` where the converter has the index; domain-level
  blob-limit failures use `WinForward.Host`. Never drop a rule silently. Assert the blob rule count
  equals the configured host-rule count before returning success.
- **R5b-4 Validate.** For `Provider = WinForward.Kernel`, `validate` runs the compiler after the
  existing configuration gate and prints path-qualified errors, returning 1 on failure. On success
  it reports the compiled rule count and blob size. On non-Windows, path selectors produce a clear
  "requires Windows" diagnostic; filename-only policies compile on any host. The WinpkFilter
  `validate`/`run` is unchanged; Kernel `run` still returns the Batch 2 refusal.
- **R5b-5 Tests and boundaries.** Add Integration tests with a fake `IProcessPathNormalizer` covering
  rule count/order, action/fallback mapping, target table, process file-name/path, CIDR/ports/
  protocol/family masks, and the local-target/rule-count failure paths. No device/accept/relay/
  raw-socket/host UDP/forwarded/Runtime/NdisApi changes, no unrelated dirty files.

## Acceptance Criteria

- [x] R5b-AC1: the compiler returns a valid host blob with one rule per configured host rule and a
      target table of the same length; proxy/pass/block and fallback map correctly.
- [x] R5b-AC2: `validate` accepts a Kernel host-TCP config and rejects path-existence/local-target/
      blob-limit failures with path-qualified diagnostics; WinpkFilter validate/run unchanged.
- [x] R5b-AC3: root build/test/format and the Kernel fast gate pass; target size/layout tests still
      pass; no data-path changes.
- [x] R5b-AC4: no unrelated changes; the Kernel `run` refusal is still before NDISAPI.

## Out of Scope

- `KernelBackend` device configuration, listener/accept/redirect-context relay, raw-socket relay
  overload, host UDP, forwarded, ARM64, packaging, Windows E2E.
