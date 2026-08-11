# Windows Validation

Status: current-revision Windows managed build, tests, and Native AOT publish have passed. The
driver/DLL, packet-path, adapter-mode, proxy, and Hyper-V gates remain pending. Linux unit tests
validate managed seams only; they do not validate those live Windows integrations.

## Current-Revision Build Validation

Executed on 2026-08-11 on the supplied WinRM host against a verified archive of Git commit
`66cfc1d025f3b780044e5700a1c11abde0ac25ab`. The archive SHA-256 was
`1829d083277050bf58eb7882578ccb05e3dae5997abf10b90855e9ffe1f61f41` on both the local and
remote hosts before extraction.

- The repository pins SDK `10.0.109`; the host has `10.0.102` but not `10.0.109`. Only the remote
  temporary copy's `global.json` was changed to `10.0.102`; the repository was not changed.
- The host cannot reach `https://api.nuget.org/v3/index.json`, and warnings are errors, so normal
  restore fails with `NU1900`. Its global NuGet cache contained the locked dependencies. Validation
  restored the temporary copy with `dotnet restore WinForward.slnx --ignore-failed-sources
  -p:NuGetAudit=false`; this bypassed unavailable online vulnerability metadata only.
- `dotnet build WinForward.slnx --configuration Release --no-restore`: exit 0.
- `dotnet test WinForward.slnx --configuration Release --no-restore`: exit 0; 232 passed, 0 failed,
  0 skipped.
- `dotnet publish src\\WinForward.Cli\\WinForward.Cli.csproj --configuration Release --runtime
  win-x64 --no-restore`: exit 0.
- The resulting Native AOT `WinForward.exe` exists. `WinForward.exe adapters` exited 1 with the
  expected actionable error that `ndisapi.dll` must be beside the executable. No pinned app-local
  DLL was available in the repository or supplied to the temporary publish directory, so live
  adapter enumeration was intentionally not attempted.

The successful build, test, and publish gates are current-revision Windows evidence. The SDK,
NuGet connectivity/audit metadata, and pinned `ndisapi.dll` prerequisites must be satisfied for a
fully representative clean-host validation.

## Executed Environment Smoke

Executed on 2026-08-10 through the supplied WinRM host without starting interception, changing
adapter modes, or sending traffic through a proxy:

- Host: Windows build `10.0.22621.0`, PowerShell `5.1.22621.1778`; `NDISRD` was `Running` with
  `System` start type.
- `C:\Users\Neko\WinForward-0.1.0-win-x64\WinForward.exe adapters` exited `0` and reported two
  GUID-primary correlated adapters: `Ethernet` and `Ethernet 8`.
- The executable is an artifact timestamped 2026-08-07, outside a Git checkout, with
  `ndisapi.dll` version `3.6.1.1`. It is not the current audit revision and cannot validate any
  audit fix or the pinned v3.6.2 ABI claim.

This establishes that the remote host can enumerate adapters with its installed driver. All
current-revision binary, packet-path, adapter-mode, proxy, and Hyper-V gates below remain pending.

## Safety Preconditions

1. Use an elevated Windows 10 22H2+ or Windows 11 x64 host with the pinned WinpkFilter driver and
   matching `ndisapi.dll` placed beside the published executable.
2. Keep the management/WinRM subnet on an explicit `pass` rule. Do not use a catch-all proxy rule
   until the management path has been proved unaffected.
3. Record the selected adapters, original mode flags, DLL hash/version, driver version, host IPv4
   and IPv6 addresses, and whether a real Hyper-V guest/vSwitch is available.
4. Capture before/after adapter modes and restore evidence for every run. Stop the process with
   Ctrl+C before any destructive driver or adapter operation.

## Build and Inventory

```powershell
dotnet build WinForward.slnx -c Release
dotnet test WinForward.slnx -c Release
dotnet publish src/WinForward.Cli/WinForward.Cli.csproj -c Release -r win-x64
& .\src\WinForward.Cli\bin\Release\net10.0\win-x64\publish\WinForward.exe adapters
```

Run the published `WinForward.exe adapters` and compare stable ID, friendly name, and internal name
with:

```powershell
Get-NetAdapter -IncludeHidden | Select-Object Name, InterfaceGuid, MacAddress, ifIndex, Status
Get-NetIPAddress -AddressFamily IPv6 | Where-Object IPAddress -like 'fe80:*' | Select-Object IPAddress, InterfaceIndex
Get-NetTCPConnection | Select-Object LocalAddress, LocalPort, RemoteAddress, RemotePort, OwningProcess, State
Get-NetUDPEndpoint | Select-Object LocalAddress, LocalPort, OwningProcess
```

Expected: `adapters` exits 0, GUID-primary correlation succeeds despite duplicate MACs, hidden
adapters are represented, and nonzero IPv6 scope remains intact.

## Native Boundary Gates

1. Compile a native `sizeof`/`offsetof` probe from the pinned v3.6.2 header against the exact
   deployed DLL configuration. Compare all values with `NdisApiAbi.AssertManagedX64Layout()` and
   reject a 9014-byte jumbo binary unless the managed layout is changed deliberately.
2. Remove the app-local DLL while making another DLL reachable through a default loader path. Run
   `WinForward.exe adapters` under Process Monitor. Expected: actionable app-local-sidecar failure,
   no fallback image load.
3. Stop the NDISRD service/device and run `adapters`. Expected: `NdisApiDriver.Open()` fails at
   open/load validation, not later during enumeration.
4. Capture and reinject at least 100 packets in each direction for every selected adapter using the
   enumeration handle. Confirm captured `m_hAdapter` used as a request handle fails with error 87.
5. Use two active scope adapters with sustained traffic, reinjection, and a mode transition. Inspect
   ETW/Process Monitor for overlapping calls per native driver instance, loss, hangs, or errors.
6. Disable and re-enable an in-scope adapter while capture is running. Expected: queue/read failure
   faults the loop with the driver error rather than silently polling.
7. Inspect an outgoing pass reinjection originating from a packet with nonzero `m_Flags`; repeat for
   VLAN/802.1q and maximum non-jumbo frames. Expected: captured metadata flags are preserved.

## Traffic and Lifecycle Gates

1. Run `validate` with valid and invalid configurations. Verify documented exit codes and redacted
   field-path diagnostics.
2. Run `run` with pass and block policies. Verify exactly-once pass behavior, no block leakage, and
   exact mode restoration after Ctrl+C and handled capture failure.
3. With a real SOCKS5 server, validate host-originated IPv4 and IPv6 TCP redirect: SYN retransmit,
   TCP options, data, FIN/RST, half-close, reverse delivery, and unavailable-proxy fail-closed
   behavior.
4. Validate host-originated IPv4 and IPv6 UDP ASSOCIATE with a dynamic relay endpoint, payload
   preservation, expected relay-sender enforcement, reverse reinjection, expiry, and unavailable
   proxy fail-closed behavior.
5. With a real Hyper-V guest/vSwitch, repeat TCP and UDP for forwarded origin traffic. Verify
   reverse delivery uses the original adapter, `ON_SEND`, its enumeration handle, and its MAC.
6. Exercise an established but idle TCP relay and a one-sided stalled peer. Record timeout and
   teardown behavior.

For each run, record command/config hash, adapter IDs and original/restored flags, DLL/driver
versions, packet counts, exit code, failure diagnostics, and any rollback evidence. A failed gate is
release-blocking until classified and reproduced with a regression or a driver-specific constraint.
