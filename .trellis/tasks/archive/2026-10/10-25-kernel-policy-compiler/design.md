# Batch 5b design: Kernel policy compilation

## Surfaces

- `WinForward.Core.PolicySnapshot`: make `HostFallbackAction` public (it is already set by the
  constructor and used by `EvaluateHost`); no other behaviour change.
- CLI: add a `WinForward.Kernel.Contracts` project reference. `WinForward.Integration.Tests` gets a
  direct Contracts reference for the test fixtures.
- New internal `src/WinForward.Cli/Backends/Kernel/KernelPolicyCompiler.cs` plus a small
  `UnsupportedProcessPathNormalizer` for non-Windows validation.

```csharp
internal sealed record KernelPolicyCompilation(
    byte[] HostBlob,
    ProxyTarget?[] TargetsByRuleIndex,
    int RuleCount);

internal static class KernelPolicyCompiler
{
    internal static bool TryCompile(
        ValidatedConfiguration configuration,
        IProcessPathNormalizer normalizer,
        out KernelPolicyCompilation? compilation,
        out IReadOnlyList<ConfigDiagnostic> errors);
}
```

## Rule mapping

For each `WinForward.Core.PolicyRule` at index `i` in `PolicySnapshot.HostRules`, in order:

| WinForward | Kernel `PolicyRule` |
|---|---|
| `Matcher.Processes` (`IReadOnlySet<string>?`) | `IReadOnlyList<ProcessSelector>?` via `ProcessSelector.Parse` (syntax; paths resolved by the normalizer during `RuleBlobCompiler.Compile`) |
| `Matcher.RemoteNetworks` (`IPPrefix[]?`) | `RemoteNetwork.Parse(prefix.ToString())` list |
| `Matcher.RemotePorts` (tuple list) | `new PortRange(start, end)` list |
| `Matcher.Protocols` (`Tcp`/`Udp` set) | `RuleProtocol.Any` for null/both, else `Tcp`/`Udp` |
| `Matcher.AddressFamilies` | `RuleAddressFamily.Any` for null/both, else `Ipv4`/`Ipv6` |
| `Decision.Action` | `Pass→Permit`, `Proxy→Redirect`, `Block→Block` |
| `Decision.Target` (proxy) | not in the blob; resolved into `TargetsByRuleIndex[i]` |

`PolicySnapshot.HostFallbackAction` maps pass→`Permit`, block→`Block`; any other value is an error
(the configuration gate cannot produce it). The `PolicyTable` is compiled with
`RuleBlobCompiler.Compile(table, normalizer)`.

`TargetsByRuleIndex[i]` is the resolved `ProxyTarget` for a proxy rule and null otherwise. A local
target is rejected with a diagnostic at `WinForward.Host.Rules[i].Target` (M1 has no host UDP
relay; the capability gate already rejects the rule shape, this is defence in depth). A missing
target name is the same diagnostic.

## Diagnostics

- Process selector syntax/control-character failures: `WinForward.Host.Rules[i].Process`.
- Normalizer/path existence failures: the same path; the message names the selector. On non-Windows
  the unsupported normalizer throws a clear "requires Windows" exception and the converter reports
  it.
- CIDR/port mapping failures: the corresponding rule path (`RemoteCidr`/`RemotePort`).
- `RuleBlobCompiler`/`PolicyLimits` failures: `WinForward.Host` with the compiler message; if the
  compiler's message names `规则 #N`, keep it in the text. The converter does not invent a second
  rule-numbering scheme.
- Always assert `compiled.Rules.Count == HostRules.Count`; a mismatch is a compiler/contract error
  and fails the whole compilation.
- No silently dropped rules: every configured host rule becomes one Kernel rule.

## Validate integration

`Program.Validate` keeps the existing load/source/confirmation output. For
`Provider = WinForward.Kernel`:

1. Choose the normalizer: `WindowsProcessPathNormalizer.ForWindows()` on Windows; otherwise the
   unsupported normalizer (filename-only policies still compile).
2. Call `KernelPolicyCompiler.TryCompile`; on failure print each `ConfigDiagnostic` to stderr and
   return 1 *before* the "Configuration is valid" line (or keep the line and return 1? Use the
   existing invalid-configuration shape: diagnostics + exit 1, no valid confirmation).
3. On success print a separate line naming the compiled rule count and blob bytes. Do not change
   the existing confirmation line or the WinpkFilter path.
4. `run` still returns the Batch 2 Kernel refusal before NDISAPI; no backend/device is constructed.

## Tests

Add `tests/WinForward.Integration.Tests/KernelPolicyCompilerTests.cs`:

- Build `ValidatedConfiguration` from JSON (Kernel provider, TCP-only proxy rules) plus a fake
  `IProcessPathNormalizer`; compile.
- Assert header magic/version/rule count/fallback, one blob rule per configured rule, action byte,
  protocol/family/port/network/process counts for representative rules, and the target table.
- Cover pass/block/proxy, multiple targets, filename and path selectors, CIDR/port mapping, a
  missing target, a local target (defensive), and a path selector under the unsupported normalizer.
- The test uses the C# blob layout types from Contracts; read the header/rule structs with
  `MemoryMarshal` and the documented 32-byte rule stride.

## Risks

- Name collisions (`PolicyRule`/`PolicyTable` exist in both Core/CLI and Contracts); use aliases in
  the converter/tests.
- The Kernel compiler normalizes paths itself; do not pre-normalize or duplicate its control-char/
  length rules.
- The target table must use the configured rule index, not the compiler's internal pool order.
- The fallback property is a Core API addition; keep it get-only.
- `RuleBlobCompiler` may allocate (cold path); do not put the converter on a packet path.
- The full-solution jb remains limited by the global tool; use project-level jb for the CLI/Contracts
  if needed and record the gap.
