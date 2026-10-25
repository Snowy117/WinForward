# Batch 2 design: BackendDriver configuration and capability gate

## Configuration types

```csharp
public enum BackendProvider { WinpkFilter, WinForwardKernel }

public sealed record BackendDriverConfiguration(
    BackendProvider Provider,
    bool KernelPassCache,
    string DriverServiceName)
{
    public static BackendDriverConfiguration Default { get; } =
        new(BackendProvider.WinpkFilter, KernelPassCache: false, DriverServiceName: "wfprobe");
}
```

`WinForwardConfigDto` gains `BackendDriverDto? BackendDriver` and keeps the existing top-level
`KernelPassCache` only as the legacy alias. Nested DTOs:

```csharp
public sealed class BackendDriverDto
{
    public string? Provider { get; init; }
    public WinpkFilterDriverDto? WinpkFilter { get; init; }
    public WinForwardKernelDriverDto? WinForwardKernel { get; init; }
}

public sealed class WinpkFilterDriverDto { public bool? KernelPassCache { get; init; } }

public sealed class WinForwardKernelDriverDto { public string? DriverServiceName { get; init; } }
```

All use explicit `[JsonPropertyName]`, and the source-generated context keeps
`UnmappedMemberHandling.Disallow`.

`ValidatedConfiguration` keeps its existing positional `KernelPassCache` parameter for the
WinpkFilter runtime and gains an init-only `BackendDriverConfiguration BackendDriver` property
defaulting to `BackendDriverConfiguration.Default`. The loader sets both from the same resolved
value; the old parameter is a compatibility projection until the runtime reads the new property.

## Resolution and validation order

1. Resolve the provider: absent `BackendDriver` or absent `Provider` means WinpkFilter. Accept the
   two canonical values case-insensitively and store the enum; an unknown value is an error at
   `WinForward.BackendDriver.Provider`.
2. Reject the unselected object: `WinForwardKernel` under WinpkFilter, or `WinpkFilter` under the
   Kernel provider, names the object path.
3. Resolve `KernelPassCache`:
   - Kernel provider: the top-level legacy key is an error; the nested `WinpkFilter` object is
     already rejected as unselected.
   - WinpkFilter: nested and legacy both set is an ambiguous error at `WinForward.KernelPassCache`;
     legacy only is used with a warning naming
     `WinForward.BackendDriver.WinpkFilter.KernelPassCache`; nested only is used; neither is false.
4. Resolve `DriverServiceName` for the Kernel provider: absent means `wfprobe`; present must be
   non-empty (trim; report the path otherwise). Under WinpkFilter the value is the default and the
   `WinForwardKernel` object must be absent.
5. Parse targets and both domains as today.
6. Run the Kernel capability gate over the parsed host/forwarded rules and the raw DTO:
   - host rule `Matcher.AdapterIds` / `AdapterNames` non-null → errors at
     `WinForward.Host.Rules[i].AdapterId` / `.AdapterName`;
   - host `Decision.Action == Proxy` with `Protocols` null or containing UDP → error at
     `WinForward.Host.Rules[i].Protocol`;
   - any forwarded rule or a non-default forwarded fallback → error at `WinForward.Forwarded`
     (the domain path; the message names the rule count);
   - any process selector whose UTF-8 byte count exceeds 511 → error at
     `WinForward.Host.Rules[i].Process` with the selector text in the message;
   - non-default `UdpSessionCapacity`, `UdpRelayReceiveBufferKb` or `UdpSessionIdleSeconds` →
     warning that the setting is inert for the Kernel provider until host UDP lands.
   The authoritative path-existence check is the Kernel compiler's; this batch records that
   boundary in the message/docs and does not guess at Windows path resolution.
7. If any error exists, return no validated configuration; warnings are surfaced as today.

## CLI behaviour

- `validate` prints the existing confirmation line unchanged and a new `Provider: <name>` line, so
  existing tests that assert the confirmation text keep working.
- `ReportResolvedRun` logs a new `[LoggerMessage]` `BackendSelected` event with the provider and
  driver service name.
- `run` checks the provider after loading/logging and before opening NDISAPI. A Kernel-provider run
  logs `KernelBackendUnavailable` (error) and returns exit 1 with a message that the Kernel backend
  is not available in this build; it must not open NDISAPI, start capture or silently fall back to
  WinpkFilter. The WinpkFilter path is untouched.
- `adapters` remains NDISAPI-only.

## Files

- `src/WinForward.Configuration/ConfigurationModels.cs` — DTOs, provider enum/record, validated
  property, loader resolution.
- `src/WinForward.Configuration/ConfigurationKernelCapabilities.cs` (new) — capability gate and
  path-qualified diagnostics; keep the rule in one place.
- `tests/WinForward.Configuration.Tests/BackendDriverConfigurationTests.cs` (new) — matrix.
- `src/WinForward.Cli/Program.cs`, `src/WinForward.Cli/Logging/StartupLog.cs` — provider output,
  backend-selected log, Kernel run refusal.
- `src/WinForward.Cli/appsettings.example.json`, `README.md` — canonical location and provider
  examples.
- Any existing `ValidatedConfiguration` construction in tests uses the new property default; no
  positional-parameter churn.

## Compatibility and rollback

- `BackendDriver` is additive. Its absence reproduces today's WinpkFilter configuration and
  `KernelPassCache` value.
- The legacy top-level `KernelPassCache` keeps working with a warning for one migration window.
- Reverting this batch restores the old config surface; the Kernel provider has no data path to
  roll back yet.

## Verification

```bash
dotnet test tests/WinForward.Configuration.Tests
dotnet test WinForward.slnx -c Release
dotnet build WinForward.slnx -c Release
dotnet format WinForward.slnx --severity info --verify-no-changes --no-restore
nix develop ./kernel --command bash -lc 'kernel/tools/check.sh --fast'
```

No Windows/Kernel driver test is required for this batch; the Kernel run refusal is a CLI path and
is reviewed in code. The Windows VM E2E for the Kernel provider belongs to the backend batch.
