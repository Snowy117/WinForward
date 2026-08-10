# Windows Attribution and Adapter Boundary Audit

Date: 2026-08-10

## Scope and Data Flow

| Owner | Native/input boundary | Projection/output | Consumer contract |
|---|---|---|---|
| `AdapterIdentity.cs` | NDIS internal name, runtime handle, MAC | `WindowsAdapter` stable ID and friendly name | CLI inventory, selector resolution, capture adapter scope |
| `IpHelperAbi.cs` | IP Helper owner-PID row ABI | layout assertions and byte-order decoding | `IpHelperTables` only |
| `ProcessAttribution.cs` | `GetExtendedTcpTable` / `GetExtendedUdpTable`, process handle | `ProcessIdentity?` | `FlowDispatcher` host-flow policy context |
| `Platform.cs` | OS/elevation status and interfaces | diagnostics, adapters, attribution API | CLI startup and Runtime dependency boundary |

Runtime behavior is intentionally conservative: `FlowDispatcher` only calls the attributor for host-originated flows (`src/WinForward.Runtime/FlowDispatcher.cs:126`), and an unknown identity leaves process data null, so process rules do not match and normal policy fallback applies (`src/WinForward.Core/Policy.cs:14`). Forwarded traffic has no claimed host PID.

## Fixed Findings

| ID | Finding | Fix and regression |
|---|---|---|
| W1 | IPv6 UDP used table class `3`, which is not a valid `UDP_TABLE_CLASS` owner-PID value. | Both IPv4 and IPv6 now use `UDP_TABLE_OWNER_PID = 1` in [IpHelperAbi.cs](/home/neko/Projects/WinForward/src/WinForward.Windows/IpHelperAbi.cs:8) and [ProcessAttribution.cs](/home/neko/Projects/WinForward/src/WinForward.Windows/ProcessAttribution.cs:212). [WindowsBoundaryAuditTests.cs](/home/neko/Projects/WinForward/tests/WinForward.Core.Tests/WindowsBoundaryAuditTests.cs:10) locks the shared value. |
| W2 | IPv6 IP Helper scope IDs must be passed through as host-order DWORDs; only IP Helper port fields are network byte order. | [DecodeIpv6Address](/home/neko/Projects/WinForward/src/WinForward.Windows/IpHelperAbi.cs:23) preserves `scopeId`; TCP and UDP projections use it at [ProcessAttribution.cs](/home/neko/Projects/WinForward/src/WinForward.Windows/ProcessAttribution.cs:219) and [ProcessAttribution.cs](/home/neko/Projects/WinForward/src/WinForward.Windows/ProcessAttribution.cs:251). The nonzero-scope regression is at [WindowsBoundaryAuditTests.cs](/home/neko/Projects/WinForward/tests/WinForward.Core.Tests/WindowsBoundaryAuditTests.cs:16). |
| W3 | A non-GUID NDIS adapter with an all-zero MAC could uniquely correlate by MAC. | [AdapterIdentity.cs](/home/neko/Projects/WinForward/src/WinForward.Windows/AdapterIdentity.cs:81) rejects unusable all-zero MACs before fallback. Regressions cover zero MAC, duplicate MAC ambiguity, and GUID-primary behavior despite duplicate MACs at [AdapterSelectorTests.cs](/home/neko/Projects/WinForward/tests/WinForward.Core.Tests/AdapterSelectorTests.cs:90). |
| W4 | Image-path lookup used `Process.MainModule`, which can require module-read rights despite the recorded limited-query path contract. | [ProcessAttribution.cs](/home/neko/Projects/WinForward/src/WinForward.Windows/ProcessAttribution.cs:110) opens with `PROCESS_QUERY_LIMITED_INFORMATION` and invokes `QueryFullProcessImageNameW`. Access failure remains an absent path/unknown identity, never a guess. This native access-right path is Windows-gated. |

The production ABI rows now have a single owner in [IpHelperAbi.cs](/home/neko/Projects/WinForward/src/WinForward.Windows/IpHelperAbi.cs:38), which both layout assertions and `IpHelperTables` use. The port conversion assertion is at [WindowsBoundaryAuditTests.cs](/home/neko/Projects/WinForward/tests/WinForward.Core.Tests/WindowsBoundaryAuditTests.cs:26).

## Confirmed Contracts

- GUID is primary and accepts `\\DEVICE\\{GUID}` or bare GUID, normalizing case/braces at [AdapterIdentity.cs](/home/neko/Projects/WinForward/src/WinForward.Windows/AdapterIdentity.cs:75). MAC is only an exact-one, nonzero fallback. An uncorrelated adapter retains its internal name at [AdapterIdentity.cs](/home/neko/Projects/WinForward/src/WinForward.Windows/AdapterIdentity.cs:54).
- `AdapterSelector` requires exact case-insensitive stable ID/name matches and returns an ambiguity error at [Platform.cs](/home/neko/Projects/WinForward/src/WinForward.Windows/Platform.cs:43). `CaptureAdapterScopeResolver` applies the same no-guess behavior to config selector sets.
- TCP matches the full local/remote tuple; UDP matches local address/port plus the relevant wildcard address and requires exactly one distinct PID at [ProcessAttribution.cs](/home/neko/Projects/WinForward/src/WinForward.Windows/ProcessAttribution.cs:179). This intentionally does not manufacture a per-socket UDP owner.
- Owner-table and process-access failures are mapped to unknown at [ProcessAttribution.cs](/home/neko/Projects/WinForward/src/WinForward.Windows/ProcessAttribution.cs:51) and [ProcessAttribution.cs](/home/neko/Projects/WinForward/src/WinForward.Windows/ProcessAttribution.cs:87). [CapturePipelineTests.cs](/home/neko/Projects/WinForward/tests/WinForward.Core.Tests/CapturePipelineTests.cs:280) proves host unknown attribution reaches the configured fallback.
- PID plus process creation time is the cache key at [ProcessAttribution.cs](/home/neko/Projects/WinForward/src/WinForward.Windows/ProcessAttribution.cs:67), preventing a later cache hit from reusing an earlier process identity.
- Platform checks only use Windows principal APIs after the OS guard at [Platform.cs](/home/neko/Projects/WinForward/src/WinForward.Windows/Platform.cs:10). Off Windows, the attributor returns unknown at [ProcessAttribution.cs](/home/neko/Projects/WinForward/src/WinForward.Windows/ProcessAttribution.cs:30).

## Residual Coverage Gaps

- IP Helper reports an owner PID, not the owning process creation time. A process can exit and have its PID reused between the table snapshot and `GetProcessById`; the creation-time cache prevents stale cache reuse but cannot prove the PID read later is the process in the earlier table row. The safe long-term enhancement is an owner-module table or an explicit native projection seam that supplies row creation data. No deterministic Linux test can prove this Windows race.
- Captured IPv6 packets contain an address but no interface scope. Decoded link-local owner rows have a nonzero scope, so they safely do not match a scope-zero packet endpoint. An adapter stable-ID to IPv6 interface-index/scope projection is required before link-local process attribution can be supported without cross-interface guessing.
- There is no injected native-table seam for arbitrary IPv4/IPv6 rows, table growth, owner-table errors, or protected-process access. Managed layout/decoder tests validate the projection helpers only.
- Adapter inventory is a startup snapshot. Adapter-list change re-resolution remains explicitly deferred in `src/WinForward.Cli/Program.cs:160`.
- `PlatformRequirements.TryCheck` elevation, live `NetworkInterface` correlation, NDISAPI enumeration, and limited-query path success/failure need a Windows host.

## Linux Verification

Executed on this Linux host. These are managed/injected tests, not Windows hardware evidence.

```bash
dotnet build -c Release --no-restore
dotnet test tests/WinForward.Core.Tests/WinForward.Core.Tests.csproj -c Release --no-restore --filter 'FullyQualifiedName~AdapterSelectorTests|FullyQualifiedName~NdisApiAbiTests|FullyQualifiedName~WindowsBoundaryAuditTests|FullyQualifiedName~CapturePipelineTests'
dotnet test tests/WinForward.Core.Tests/WinForward.Core.Tests.csproj -c Release --no-restore
git diff --check
```

Results: Release build passed with 0 warnings/errors; focused tests passed 48/48; full tests passed 212/212; `git diff --check` passed.

## Windows Validation Gate

Run in elevated PowerShell on Windows 10 22H2+/Windows 11 x64 with the matching WinpkFilter driver and `ndisapi.dll` sidecar:

```powershell
dotnet build -c Release
dotnet test -c Release
dotnet publish src/WinForward.Cli/WinForward.Cli.csproj -c Release -r win-x64
.\src\WinForward.Cli\bin\Release\net10.0\win-x64\publish\WinForward.exe adapters
Get-NetAdapter -IncludeHidden | Select-Object Name, InterfaceGuid, MacAddress, ifIndex, Status
Get-NetTCPConnection | Select-Object LocalAddress, LocalPort, RemoteAddress, RemotePort, OwningProcess, State
Get-NetUDPEndpoint | Select-Object LocalAddress, LocalPort, OwningProcess
Get-NetIPAddress -AddressFamily IPv6 | Where-Object IPAddress -like 'fe80:*' | Select-Object IPAddress, InterfaceIndex
```

Expected: `adapters` exits 0 and its stable IDs/friendly names agree with `Get-NetAdapter`, including hidden adapters and duplicate MACs. IPv6 UDP owner lookup must use `UDP_TABLE_OWNER_PID` for AF_INET6. A normal process can supply a full image path when limited-query access permits; protected, exited, ambiguous, or inaccessible cases must remain unknown and follow policy fallback. Link-local process attribution remains unsupported until capture adapter scope is projected into the attribution endpoint.
