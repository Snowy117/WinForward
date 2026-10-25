# Batch 3 implementation plan

## Steps

1. **Baseline.** Record the current Kernel test counts and build commands:
   `nix develop ./kernel --command bash -lc 'kernel/tools/check.sh --full'` and
   `dotnet build kernel/src/userland/WinForward.Userland.slnx -c Release`. Note the current
   `wf_check` test count (the Batch 1 run saw 254 C# cases and 84 ctest). Do not start from an
   unverified baseline.
2. **Create the project.** Add `src/WinForward.Kernel.Contracts/WinForward.Kernel.Contracts.csproj`
   with `AssemblyName=WinForward.Kernel.Contracts`, transitional
   `RootNamespace=WinForward.Userland` (with a comment naming the deferred rename), the root build
   props, and IVT for `WinForward.Userland.Tests`. Add it to `WinForward.slnx` under `/src/`.
3. **Move the contract sources.** `git mv` `Device/`, `Policy/` and `Forwarded/` from
   `kernel/src/userland/WinForward.Userland/` to the new project. Do not edit layout constants,
   golden bytes, compiler logic or IOCTL structs. Only update path/namespace comments that would
   otherwise point at a retired Kernel `.trellis` path.
4. **Make the old project a shim.** Replace the moved compile content with a project reference to
   the new project. Keep `Configuration/`, `Control/` and `Relay/` in place so the Controller/test
   runner/`wfprobe` keep working. Add direct references in the test/controller/wfprobe projects only
   if project-reference transitivity does not expose the new assembly; do not duplicate types.
5. **Native gates.** Update `kernel/tools/userland/verify-gates.sh` paths for `ProbeIoctl.cs` and
   `RuleBlobCompiler.cs` to the new root project; keep the Configuration/Relay paths. Verify the
   script fails loudly if a mutation target is missing, rather than scanning zero files. Do not
   change the Windows gate's build surface or the Controller output path.
6. **Root analyzers.** Build the new project under the root solution and resolve every diagnostic:
   fix genuine issues; narrowly suppress ABI/layout-only false positives with a concrete reason
   (`#pragma` or project-scoped `.editorconfig`); never change the byte layout or compiler output to
   satisfy an analyzer. Re-run `dotnet build` until zero warnings.
7. **Native regression.** Build the Kernel userland solution and run `check.sh --full`; confirm the
   test count is unchanged and no suite was skipped. Build `WinForward.Controller` and `wfprobe`
   through the solution; if Windows-only assets are unavailable on Linux, report the exact build
   boundary rather than claiming the Windows gate ran.
8. **Docs.** Update `kernel/MIGRATION.md` (moved paths, shim, deferred scaffolding/namespace rename)
   and the parent `research/contracts-project.md` if the actual reference behavior differs. Record
   the `jb inspectcode` environment gap in the batch report; do not treat it as a code finding.
9. **Do not touch** `WinForward.Runtime`, `WinForward.NdisApi`, `BackendDriver`, the Kernel driver
   or unrelated dirty files. Do not commit.

## Verification

```bash
dotnet build WinForward.slnx -c Release
dotnet test WinForward.slnx -c Release
dotnet format WinForward.slnx --severity info --verify-no-changes --no-restore
dotnet build kernel/src/userland/WinForward.Userland.slnx -c Release
nix develop ./kernel --command bash -lc 'kernel/tools/check.sh --full'
```

Optionally run the Windows `gate.ps1` on the VM if the cross-tree build is stable; otherwise record
it as not run in this Linux batch.

## Risky files / rollback

- `kernel/src/userland/WinForward.Userland/WinForward.Userland.csproj` and the test/controller/
  wfprobe project files: the shim must not hide a compile break. Build the native solution, not just
  the root solution.
- `kernel/tools/userland/verify-gates.sh`: wrong relative paths can make the mutation gate a no-op;
  assert the target file exists and is mutated.
- The moved ABI/layout code: any change beyond plumbing invalidates the batch. Compare the moved
  files with `git diff --stat` and check the layout golden tests.
- The root analyzer suppressions: each one needs a reason that survives review; no blanket NoWarn.
- Rollback: revert the move/project/solution/tool-path commits; the Kernel history still holds the
  original paths.

## Report

Changed/moved files, the shim/reference behavior actually observed, the exact native and root
verification counts, analyzer suppressions with reasons, the deferred scaffolding/namespace rename,
and the still-broken `jb` environment gap.
