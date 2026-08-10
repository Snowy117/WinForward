# Core and Configuration Audit Findings

Audit date: 2026-08-10

Scope: `src/WinForward.Core/{Domain,IpPrefix,PacketRuntime,Policy,ProcessSelectors}.cs` and
`src/WinForward.Configuration/ConfigurationModels.cs`, including their Runtime and CLI consumers.

## Data Flow and Consumer Trace

`Program.TryLoadConfig` parses and validates JSON before calling
`CaptureAdapterScopeResolver` or creating the capture runtime. `PacketFlowClassifier` constructs an
`Endpoint` and `FlowKey`; `FlowDispatcher` first calls `FlowTable.TryResolve`, attributes only a
new host flow, then uses `TryClaimResolved` to evaluate policy exactly once. `CapturePacketProcessor`
owns a `PacketLease` and blocks it on an exception. TCP and UDP coordinators keep their own
association tables; they do not consume `BoundedSetupQueue`.

## Confirmed Findings

### F1: Resolved flows did not refresh idle activity

- Severity/type: medium, flow-lifecycle ownership.
- Evidence: `src/WinForward.Core/Domain.cs:220-259` previously returned an existing state without
  touching it. `src/WinForward.Runtime/FlowDispatcher.cs:108-122` resolves every subsequent packet
  through this method, while `RemoveExpired` uses `LastActivityUtc` at `Domain.cs:206-218`.
- Reproduction: `FlowTableLookupRefreshesActivityBeforeIdleExpiry` set an existing flow's activity
  older than its idle timeout, resolved it, then observed that it could still be removed at the
  pre-lookup deadline.
- Fix: every exact, reverse, origin-flipped, and adapter-agnostic successful resolution calls
  `FlowState.Touch` before returning.
- Regression: `FlowAndConfigurationTests.FlowTableLookupRefreshesActivityBeforeIdleExpiry`.

### F2: Valid JSON null entries could crash configuration validation

- Severity/type: medium, fail-closed validation.
- Evidence: `src/WinForward.Configuration/ConfigurationModels.cs:136-169,222-285` previously parsed
  reference-type array elements but assumed non-null section entries in `ValidateServer`/`ParseRule`
  and typed match values in `ParseSet`, `ParseNetworks`, and `ParsePorts`. Those null values produced `NullReferenceException` rather
  than diagnostics; CLI validation cannot turn that exception into a field error.
- Reproduction: `ConfigurationRejectsNullSectionEntriesWithoutThrowing` and the protocol,
  address-family, CIDR, and port cases of `ConfigurationRejectsNullRuleMatchValuesWithoutThrowing`
  failed on the baseline for null server, rule, protocol, family, CIDR, and port entries.
- Fix: DTO array types model nullable JSON elements; validation reports indexed diagnostics; parsing
  skips entries already rejected by `ValidateNonEmpty`; `IpPrefix.TryParse` accepts null input as an
  invalid prefix rather than throwing.
- Regression: the two tests above and `IpPrefixRejectsNullInputsWithoutThrowing`.

### F3: Validated port ranges were not normalized

- Severity/type: low, configuration normalization/performance contract.
- Evidence: `src/WinForward.Configuration/ConfigurationModels.cs:266-312` previously returned
  input-order ranges, including duplicates and overlaps, despite the design's sorted/merged snapshot
  contract.
- Reproduction: `ConfigurationNormalizesAndMergesRemotePortRanges` expected `[80,250]` and `[443,443]`
  for unordered overlapping input but baseline returned four unmerged ranges.
- Fix: parse invariant decimal values, sort by start/end, and merge overlapping or adjacent inclusive
  intervals.
- Regression: `FlowAndConfigurationTests.ConfigurationNormalizesAndMergesRemotePortRanges`.

### F4: JSON parse errors lost field location and copied serializer text

- Severity/type: low, diagnostics/redaction hardening.
- Evidence: `src/WinForward.Configuration/ConfigurationModels.cs:80-86` previously emitted `"$"`
  and appended raw `JsonException.Message`. Unknown properties therefore lacked their JSON field path
  and diagnostics depended on serializer-controlled text.
- Reproduction: `ConfigurationParseDiagnosticsNameTheFailingFieldWithoutEchoingCredentials` failed
  on the baseline because an unknown field was reported only at `$`.
- Fix: preserve `JsonException.Path` when supplied and use a fixed error message that never echoes
  raw JSON values.
- Regression: `ConfigurationRejectsUnknownJsonFields` and
  `ConfigurationParseDiagnosticsNameTheFailingFieldWithoutEchoingCredentials`.

No test established that the prior serializer message emitted an actual configured credential. The
fixed template removes that dependency and the regression proves diagnostic output does not include
the supplied credential-shaped value.

### F5: Endpoint factory did not honor its null-input contract

- Severity/type: low, domain input validation.
- Evidence: `src/WinForward.Core/Domain.cs:46-47` previously dereferenced `address` before forwarding
  to the constructor, although the constructor explicitly validates the same argument at `Domain.cs:34`.
- Reproduction: `EndpointFactoryRejectsNullAddressWithArgumentException` observed a
  `NullReferenceException` from `Endpoint.From(null!, 53)` on the baseline.
- Fix: the factory now applies `ArgumentNullException.ThrowIfNull` before reading the address family.
- Regression: `FlowAndConfigurationTests.EndpointFactoryRejectsNullAddressWithArgumentException`.

## Owned File Conclusions

| File | Conclusion |
| --- | --- |
| `src/WinForward.Core/Domain.cs:30-85` | `Endpoint` enforces non-null, matching-family addresses and value equality; `FlowKey.Create` rejects mixed families and carries origin adapter identity/generation. Reverse and cross-adapter matching intentionally reuse one decision. F1 fixed at `Domain.cs:223-262`; F5 at `Domain.cs:46-51`. |
| `src/WinForward.Core/Domain.cs:100-272` | Claim uses one gate and capacity fails closed. Same tuple reuse, distinct remote isolation, reverse/origin/adapter reuse, and active expiry are covered. `TryGet` is currently unused outside this module and retains its exact/reverse-only semantics. |
| `src/WinForward.Core/IpPrefix.cs:5-52` | IPv4/IPv6 prefix lengths are bounded, networks are normalized, and family mismatch does not match. Null handling was made non-throwing as part of F2. |
| `src/WinForward.Core/PacketRuntime.cs:3-68` | `PacketLease` has one terminal disposition; `BoundedSetupQueue` copies input and rejects packet/byte overflow. No defect confirmed. |
| `src/WinForward.Core/Policy.cs:3-48` | Populated fields compose with AND, alternatives compose with OR, and ordered evaluation returns the first matching rule with its runtime index. Unknown process identity cannot match a process rule. No defect confirmed. |
| `src/WinForward.Core/ProcessSelectors.cs:6-50` | Filename selectors and separator-containing normalized full-path selectors are exact and case-insensitive. Slash/backslash normalization is portable; Windows additionally canonicalizes full paths. No defect confirmed. |
| `src/WinForward.Configuration/ConfigurationModels.cs:8-327` | Source-generated camel-case JSON rejects unknown members; omitted match arrays mean no constraint and empty arrays are errors. F2-F4 fixed null safety, port normalization, and safe field diagnostics. |

## Boundary Matrix

| Boundary | Status |
| --- | --- |
| Rule order, AND across fields, OR within a field, fallback | Covered by `PolicyUsesFirstMatchingRule`, `PolicyAndAcrossFieldsRequiresEveryPopulatedField`, and `PolicyAlternativesWithinFieldUseOrSemantics`. |
| IPv4/IPv6 endpoint and flow-key identity | Covered by endpoint equality, classifier IPv6, flow family validation, and IPv6 CIDR normalization/matching tests. |
| Origin and adapter generation | Covered by `FlowTableResolvesReversePacketAcrossOriginKindAndReusesDecision` and `FlowTableReusesDecisionAcrossAdapterBoundaries`; Runtime classifier preserves adapter generation. |
| Process filename/path/case semantics | Covered by `ProcessSelectorMatchesFilenameOrNormalizedFullPathExactly`; whitespace selector rejection is covered. |
| Omitted versus empty match arrays | Omitted returns null matcher condition; empty is rejected by `ConfigurationRejectsEmptyMatchAndProxyWithoutServer`. |
| Servers/actions/ports/CIDRs | Duplicate case-insensitive names, host and port bounds, proxy server requirements, fallback/failure actions, invalid CIDR/ports, and sorted/merged overlapping and adjacent port ranges are covered. |
| Unknown JSON and redaction | Unknown fields are rejected at their path; parse diagnostics use a fixed message. Pairing and UTF-8 credential limits are covered at both 255-byte acceptance and 256-byte non-ASCII rejection, with no diagnostic disclosure. |

## Coverage Gaps and Residual Risks

- `BoundedSetupQueue` currently has no Runtime consumer and is not thread-safe by design; its
  single-threaded bounds/copy behavior is covered, but future multi-producer use requires ownership
  or synchronization tests.
- Native capture, Windows path canonicalization, process attribution ambiguity, adapter recreation,
  and actual CLI output remain platform/integration coverage rather than pure host tests.
- `FlowTable.TryGet` is not used by Runtime. Its non-touching lookup behavior is intentionally left
  unchanged; any future use for packet observation should use `TryResolve` or explicitly refresh
  activity.
- `IpPrefix` scope-ID behavior for IPv6 link-local addresses is not policy-tested; the address-family
  and prefix-byte behavior is covered, while route/interface selection is Windows/socket dependent.

## Verification

Initial regression baseline:

- `dotnet test WinForward.slnx -c Release --filter 'FullyQualifiedName~FlowAndConfigurationTests' --no-restore`
  failed 9 tests: F1, F3, F4, and the F2 null-entry cases.

After fixes:

- Focused Release test: `dotnet test WinForward.slnx -c Release --filter 'FullyQualifiedName~FlowAndConfigurationTests' --no-restore` passed, 62/62.
- Full Release test: `dotnet test WinForward.slnx -c Release --no-restore` passed, 165/165.
- Release build: `dotnet build WinForward.slnx -c Release --no-restore` passed, 0 warnings, 0 errors.
- `git diff --check`: passed.
