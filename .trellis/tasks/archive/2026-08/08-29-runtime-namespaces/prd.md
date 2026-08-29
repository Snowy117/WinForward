# PRD: Reorganize WinForward.Runtime into sub-namespaces

## Problem

`src/WinForward.Runtime/` holds 31 `.cs` files flat in the project root (32 with csproj). All types (71 total) live in a single `namespace WinForward.Runtime`. The flat layout makes the package hard to navigate: capture drivers, TCP redirect machinery, UDP proxy, SOCKS5 transport, and dispatch core are visually indistinguishable.

## Goal

Introduce sub-namespaces with matching physical directories, keeping the split semantic and mechanical (pure move + rename, zero behavior change per `.trellis/spec/backend/directory-structure.md` 拆分纪律).

## Requirements

1. Files move into subdirectories; `namespace` declarations updated to match directory (`<Dir>` ↔ `WinForward.Runtime.<Dir>`).
2. All referencing projects (`WinForward.Cli`, `tests/WinForward.Core.Tests`, `benchmarks/WinForward.Benchmarks` — 30 files total) keep compiling with updated `using` directives.
3. No public API renaming, no type merging/splitting, no visibility changes, no logic edits.
4. Dispatch core (highest external usage: `SelfTrafficRegistry` ×13 files, `FlowDispatcher` suite) stays at root `WinForward.Runtime` to minimize blast radius.
5. Physical directories use PascalCase matching the namespace suffix.

## Acceptance Criteria

- [ ] `src/WinForward.Runtime/` root contains only dispatch core + logging files (5) + csproj.
- [ ] `dotnet build` succeeds solution-wide with zero new warnings.
- [ ] `dotnet test` passes (full suite, same count as baseline).
- [ ] `git diff` shows only file moves, namespace lines, and using-directive changes.
- [ ] No orphan `using WinForward.Runtime;` that became unnecessary is left in files that reference zero root types (verified via `dotnet format analyzers --verify-no-changes` or build warnings).

## Out of Scope

- Dependency-direction refactoring between groups (e.g. `NdisPacketActionExecutor` holding coordinators stays as-is).
- Any change to `WinForward.Protocols` (Socks5 codec already lives there).
- TestHelpers reorganization.
