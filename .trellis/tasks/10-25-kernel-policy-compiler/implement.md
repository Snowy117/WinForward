# Batch 5b implementation plan

1. **Baseline.** Read `PolicySnapshot`, `ValidatedConfiguration`, `WinForward.Kernel.Contracts`'s
   `PolicyRule`/`PolicyTable`/`ProcessSelector`/`RemoteNetwork`/`PortRange`/`RuleBlobCompiler`, the
   CLI `Program.Validate`/Kernel refusal, and the Integration test style. Record the 1958 test
   baseline.
2. **References.** Add `WinForward.Kernel.Contracts` to `WinForward.Cli.csproj` and a direct
   reference in `WinForward.Integration.Tests.csproj` for the blob fixtures.
3. **Fallback.** Make `PolicySnapshot.HostFallbackAction` public (get-only); no behaviour change.
4. **Converter.** Add `src/WinForward.Cli/Backends/Kernel/KernelPolicyCompiler.cs` with the mapping
   and target table from `design.md`. Use aliases for the two `PolicyRule`/`PolicyTable` types.
   Handle:
   - null selectors = any; empty arrays cannot reach here (the configuration validator rejects
     them); lists preserve the configured order.
   - protocol/family: null or both → `Any`; single → that value.
   - ports: one `PortRange` per configured tuple (already merged/sorted by the config parser).
   - networks: `RemoteNetwork.Parse(prefix.ToString())`; catch and report the rule path.
   - processes: `ProcessSelector.Parse`; let the compiler resolve paths with the supplied
     `IProcessPathNormalizer`.
   - targets: proxy → resolve `configuration.Targets[name]`; local is rejected defensively; pass/
     block → null. The target table length equals the host-rule count.
   - fallback: `HostFallbackAction` pass→permit/block→block; anything else is an error.
   - compile with `RuleBlobCompiler.Compile(table, normalizer)`; assert rule count equality; map
     `PolicyCompileException`/`ArgumentException`/normalizer failures to `ConfigDiagnostic` at the
     closest rule path (domain path for blob-limit failures).
5. **Non-Windows normalizer.** Add a small CLI normalizer that throws a clear "Kernel process path
   resolution requires Windows" for path selectors; filename-only selectors do not call it. The
   `Program.Validate` chooses the Windows normalizer only on Windows.
6. **Validate.** For the Kernel provider, run the compiler after the existing configuration gate;
   print path-qualified diagnostics and return 1 on failure; on success print the compiled rule
   count and blob bytes in a separate line. Keep the WinpkFilter path and the Kernel `run` refusal
   unchanged.
7. **Tests.** Add `KernelPolicyCompilerTests` as in the design; use a fake normalizer and a JSON
   Kernel config. Assert header magic/version/rule count/fallback, one rule per configured rule,
   action/protocol/family/port/network/process shape, and the target table. Cover missing/local
   target and unsupported-normalizer path selectors.
8. **Gates.** Root build/test/format; Kernel fast gate; project-level jb for the CLI/Contracts if
   feasible (full-solution jb remains limited by the global tool). Do not weaken the gate; record
   any environment gap.
9. **Do not touch** Runtime/NdisApi/driver/Contracts ABI/layout, BackendDriver, KernelBackend,
   device config, accept/relay, raw-socket relay, host UDP/forwarded, or unrelated dirty files. No
   commit.

## Verification

```bash
dotnet build WinForward.slnx -c Release
dotnet test WinForward.slnx -c Release
dotnet format WinForward.slnx --severity info --verify-no-changes --no-restore
nix develop ./kernel --command bash -lc 'kernel/tools/check.sh --fast'
jb inspectcode -f=Xml -e=HINT -o=/tmp/jb-cli.xml src/WinForward.Cli/WinForward.Cli.csproj
```

## Risks / rollback

- The mapping is the rule-index contract: assert one-to-one and test the target table.
- Do not pre-resolve paths or duplicate the Kernel control-char/511-byte rules; the compiler owns
  them.
- Name collisions: use aliases, not a copy of the models.
- The CLI's new Contracts reference must not create a cycle; Contracts has no Configuration
  dependency.
- Rollback: remove the converter/tests and the CLI reference; the Core fallback getter is harmless.
