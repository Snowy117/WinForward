# Batch 5a implementation plan

1. **Baseline.** Record the current root test total (1958), `check.sh --full` (8 PASS) and the
   native solution/wfprobe build results. Read the seven files to move and their consumers
   (`RelayServer`, `ForwardedUdpRelay`, `WfpController`, the Kernel tests) before moving.
2. **Project.** Add `src/WinForward.Kernel.Runtime/WinForward.Kernel.Runtime.csproj` with the root
   build props, `AssemblyName=WinForward.Kernel.Runtime`, transitional `RootNamespace=WinForward.Userland`,
   a contracts `ProjectReference`, and IVT for `WinForward.Userland.Tests` only if needed. Add it to
   `WinForward.slnx` (under `/src/`) and to `kernel/src/userland/WinForward.Userland.slnx` so the
   native Release build compiles it Release.
3. **Move.** `git mv` the seven files from
   `kernel/src/userland/WinForward.Userland/Relay/` to `src/WinForward.Kernel.Runtime/Relay/`:
   `SocketIoctl.cs`, `RedirectDestination.cs`, `IRedirectContextSource.cs`,
   `HostRedirectContextSource.cs`, `ForwardedRedirectContextSource.cs`, `BlockSink.cs`,
   `ListenerSet.cs`. Keep bodies identical; update only namespaces/usings and invalid comments.
4. **Shim.** Add a `ProjectReference` from the old `WinForward.Userland` to the new runtime project.
   Build the native solution, test runner and Controller; add direct references in consumers only if
   transitivity fails. Do not copy types or add a facade.
5. **Analyzers.** Resolve the root diagnostics for the new project: fix genuine issues; use narrow,
   reasoned suppressions for socket/PInvoke/interop false positives; no global NoWarn. Run
   `dotnet format` (apply then verify) if needed.
6. **Docs.** Update `kernel/MIGRATION.md` and `kernel/src/userland/README.md` for the moved helper
   paths, the new runtime project, the transitional namespace and the native/root solution entries.
7. **Do not touch** Runtime/NdisApi/driver, BackendDriver, KernelBackend/device config, host TCP
   accept/relay, raw-socket relay, host UDP/forwarded, or unrelated dirty files. No commit.

## Verification

```bash
dotnet build WinForward.slnx -c Release
dotnet test WinForward.slnx -c Release
dotnet format WinForward.slnx --severity info --verify-no-changes --no-restore
dotnet build kernel/src/userland/WinForward.Userland.slnx -c Release
dotnet build kernel/tools/wfprobe/wfprobe.csproj -c Release
nix develop ./kernel --command bash -lc 'kernel/tools/check.sh --full'
```

Confirm the helper DLL is under `src/WinForward.Kernel.Runtime/bin/Release/net10.0/` and not Debug.

## Risky files / rollback

- `ListenerSet`/`BlockSink`: socket ownership and bind/retry behavior must be unchanged; the Kernel
  tests are the oracle.
- The old project/solution references: verify the actual build across the kernel/root boundary.
- The native solution must include the new project; otherwise Release pulls a Debug helper.
- Rollback: revert the move/project/solution/reference changes; no runtime state.

## Report

Moved files, reference/transitivity result, analyzer suppressions, the exact gates and counts, and
any behavioural difference (should be none).
