# Batch 5c-2 implementation plan

1. **Baseline + API reading.** Record 1974 tests and the Kernel fast gate. Read:
   - `src/WinForward.Kernel.Contracts/Device/{WfpDeviceClient,ProbeConfig,ProbeStats,ProbeRuleInfo,ProbeForwardedConfig,ProbeIoctl}.cs`
   - `src/WinForward.Kernel.Contracts/Forwarded/{ForwardedBlobCompiler,ForwardedPolicyTable,ForwardedPolicyRule}.cs`
   - `src/WinForward.Kernel.Runtime/Relay/{ListenerSet,BlockSink,HostRedirectContextSource,RedirectDestination}.cs`
   - `src/WinForward.Config/...` (ValidatedConfiguration/ProxyTarget/Socks5Server)
   - `src/WinForward.Runtime/TcpRedirect/TcpProxyRelayFactory.cs`, `Socks5/Socks5AddressCache.cs`,
     `SelfTrafficRegistry`, `RuntimeCounters`
   - `src/WinForward.Cli/Backends/Kernel/KernelPolicyCompiler.cs`, `Program.cs`,
     `WinpkFilterBackend.cs`, `IInterceptionBackend.cs`
   - the Kernel controller (`kernel/src/userland/.../Control/WfpController.cs`) as the call-order
     reference for the device APIs.
2. **Compiler `ContainsBlock`.** Add `bool ContainsBlock` to `KernelPolicyCompilation` (fallback
   block or any mapped rule action block) and a test.
3. **CLI reference.** Add `WinForward.Kernel.Runtime` to `WinForward.Cli.csproj`.
4. **Backend files.** Implement `KernelBackend` (startup/run/shutdown/exit codes),
   `KernelHostTcpService` (device publication/listener/block sink/accept loops),
   `KernelRelaySession` (context -> target -> relay -> close), `KernelBackendLog` (LoggerMessage).
   Follow `design.md` for the order, capacity/setup, linger 0, and no-default-target fail-closed.
5. **Program wiring.** Replace the Kernel refusal with the backend switch; keep the provider guard
   and the `[SupportedOSPlatform("windows")]` boundary; keep the WinpkFilter path and exit codes.
6. **Tests (Linux).** The device path is Windows-only; test the compiler `ContainsBlock` and keep
   the policy/target/relay tests green. The Kernel backend's startup/accept path is code-reviewed
   (Batch 5d runs it on the VM); do not claim a Windows E2E in this batch.
7. **Quality.** Root build/test/format, Kernel fast, CLI project-level jb 0. Follow the root
   analyzers/format; narrow, reasoned suppressions only. No global opt-out.
8. **Do not touch** WinpkFilter behavior, host UDP/forwarded, driver/ABI, Contracts layout, or
   unrelated dirty files. No commit.

## Verification

```bash
dotnet build WinForward.slnx -c Release
dotnet test WinForward.slnx -c Release
dotnet format WinForward.slnx --severity info --verify-no-changes --no-restore
nix develop ./kernel --command bash -lc 'kernel/tools/check.sh --fast'
jb inspectcode -f=Xml -e=HINT -o=/tmp/jb-cli-5c2.xml src/WinForward.Cli/WinForward.Cli.csproj
```

## Risky files / rollback

- `Program.cs`: the backend switch must not change the WinpkFilter path or the provider guard.
- `KernelBackend`: device call order/readback and the disable-before-release shutdown are blocking.
- `KernelRelaySession`: socket ownership and the linger-0 decision; no double dispose.
- The CLI's AOT build (`PublishAot=true`) analyzes the new references; the build must stay
  warning-free (the real publish runs on the VM/CI in 5d).
- Rollback: remove the backend files/switch/reference; the compiler `ContainsBlock` is additive.

## Report

Files/APIs used, the startup/shutdown/relay order, the device-call evidence, tests/gates, the
deferred Windows E2E, and any uncovered/uncertain point.