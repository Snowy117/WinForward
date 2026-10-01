# Implementation Plan — Split monolithic test project into layered test projects

Active task: `.trellis/tasks/10-01-split-test-projects`

Every sub-agent dispatch prompt starts with that line, then names the role
(`trellis-implement` or `trellis-check`), then the batch-scoped instruction. Dispatched agents read
`prd.md` → `design.md` → this file, in that order.

## Pre-start checks

- [ ] Working tree clean; `git status --short` shows only the task directory.
- [ ] Baseline recorded: `dotnet test WinForward.slnx -c Release` → **1159 passed** (1141 + 18), 0 failed.
- [ ] Baseline wall clock recorded (design measured 42.7 s including build).
- [ ] `research/file-map.json` re-verified against the live tree (126 test files + 25 support files,
      no duplicates, no omissions).
- [ ] Confirm every occurrence of `WinForward.Core.Tests` in a `.cs` file is either a `namespace`
      declaration or a `using static` line — the rewrite depends on it:
      `rg -n 'WinForward\.Core\.Tests' -g '*.cs' tests/ | rg -v '^\S+:\d+:(namespace|using static) '`
      must be empty.

## Commit strategy

One commit per batch, on `master`, gated by the pre-commit quality gate in `AGENTS.md`. Batches are
ordered so the tree is green after each one; a batch that cannot reach green is reverted before the
next is attempted.

---

## Batch 1 — Extract `WinForward.TestSupport`

Owner: **main session**. Touches shared files (`src/` friend lists, the existing project, `.slnx`),
so it stays serial.

1. `git mv tests/WinForward.Core.Tests/TestHelpers/*.cs tests/WinForward.TestSupport/`
2. Create `tests/WinForward.TestSupport/WinForward.TestSupport.csproj`: `IsPackable=false`,
   `<PackageReference Include="xunit" />`, and `ProjectReference`s to Configuration, Core, NdisApi,
   Protocols, Runtime, Windows. **No** `Microsoft.NET.Test.Sdk`.
3. Rewrite namespaces: `namespace WinForward.Core.Tests;` → `namespace WinForward.TestSupport;`, and
   the two in-project references at `TcpCoordinatorFakes.cs:11-12` from `WinForward.Core.Tests.` to
   `WinForward.TestSupport.`.
4. Re-balance usings under compiler guidance (design §4): build, read the `CS0246`/`CS0103` list, add
   the missing namespaces, rebuild until clean.
5. Grant friend access from evidence (design §5): start with `Runtime` and `Windows`; add more only if
   the compiler rejects a member.
6. Add `ProjectReference` to `WinForward.TestSupport` in `WinForward.Core.Tests.csproj`; add the new
   project to `WinForward.slnx`.
7. **Gate:** `dotnet build WinForward.slnx -c Release` zero warnings, and
   `dotnet test WinForward.slnx -c Release` → 1159 passed. All 126 test files still declare
   `WinForward.Core.Tests` and still pass.
8. Commit.

Rollback point: `git revert` of this commit; nothing else has moved yet.

---

## Batch 2 — Move eleven projects out

Owner: **main session for the mechanical move, sub-agents for per-project convergence.**

### 2a. Main session — mechanical move (no sub-agents)

1. Move the 120 non-Core files per `research/file-map.json` with `git mv` into the eleven new
   project directories.
2. Generate each `.csproj` from one template plus the reference table (design §2).
3. Rewrite each moved file's `namespace` line to its new project name, and any remaining
   `using static WinForward.Core.Tests.<Helper>;` to `WinForward.TestSupport.<Helper>`.
4. Add all eleven projects to `WinForward.slnx`.
5. Create `tests/Directory.Build.props` here rather than in batch 3 (design §6): the new projects
   contain allocation-gate tests and need `TieredCompilation=false` from their first run.
   `WinForward.Core.Tests` keeps its own copy of the setting until batch 3 reconciles it.
6. Run one full-solution build and capture the diagnostic set. This is the shared input for 2b.

### 2b. Sub-agents — per-project convergence (parallel)

Dispatch one `trellis-implement` sub-agent per test project (eleven in total; run in waves of at most
four to keep MSBuild file-lock contention low).

Each agent's contract:

- **Scope:** only files under `tests/<its project>/`. It may edit `.cs` files (using blocks) and its
  own `.csproj`. It must **not** touch `src/`, `benchmarks/`, `WinForward.slnx`, or another project's
  directory.
- **Goal:** `dotnet build tests/<its project>/<its project>.csproj -c Release` completes with zero
  warnings and zero errors.
- **Method:** add the missing `using` directives the compiler reports. Leave usings that the new
  ancestry makes redundant in place — `IDE0005` is **not** part of the repository's format gate
  (probe-verified 2026-10-01), and deleting them is churn beyond what the rename forces.
- **Never** change an assertion, a `[Fact]`/`[Theory]`, a test name, or a helper's behavior. Usings
  and the `namespace` line are the only permitted edits.
- **Report back:** build status, files edited, and — separately — the exact list of
  `InternalsVisibleTo` grants it needs, with the rejected member and `CS0122`/`CS0272` evidence. It
  reports these; it does not apply them.

### 2c. Main session — friend access and gate

1. Collect every agent's `InternalsVisibleTo` request, apply the grants to the owning production
   projects, deduplicate against existing entries, and drop any `WinForward.Core.Tests` entry whose
   last consumer has moved away.
2. Rebuild the full solution after each grant wave to confirm the set converges.
3. **Gate:** `dotnet test WinForward.slnx -c Release` → 1159 passed across 12 test assemblies, plus
   per-assembly counts (`dotnet test tests/<project> -c Release --no-build`).
4. Commit.

Rollback point: `git revert` of this commit restores all 120 files, their namespaces, and the
`.slnx`; batch 1's `TestSupport` project survives it.

---

## Batch 3 — Trim the leftover project and centralize test settings

Owner: **main session**.

1. Reduce `WinForward.Core.Tests.csproj` to Core + Protocols + Runtime + TestSupport (design §2) and
   drop the now-unused references.
2. Verify `tests/Directory.Build.props` created in batch 2a still carries `TieredCompilation=false`
   and the explicit `<Import Project="$(MSBuildThisFileDirectory)../Directory.Build.props" />`
   (design §6).
3. Remove the superseded `TieredCompilation` block from `WinForward.Core.Tests.csproj`.
4. Delete the two dead `.editorconfig` globs at `:278` and `:421` (design §7).
5. Confirm `WinForward.Analyzers.Tests` duration is still ~4 s; if it regressed, add an explicit
   `<TieredCompilation>true</TieredCompilation>` to that project.
6. **Gate:** `dotnet test WinForward.slnx -c Release` → 1159 passed; wall clock recorded for AC8.
7. Commit.

---

## Batch 4 — Gates, verification, and spec update

Owner: **sub-agents for independent verification, main session for gates and docs.**

### 4a. Sub-agent — `trellis-check` (content and reference audit)

Dispatch one `trellis-check` sub-agent with read/execute scope but no edit authority, to run and
report:

- **AC4 content purity:** for all 151 moved files, strip `namespace` and `using` lines from both the
  working copy and its `HEAD` revision and diff the remainder; every file must be byte-identical.
- **AC6 reference necessity:** per test project, compare the set of `WinForward.*` namespaces used in
  its `.cs` files against its `ProjectReference` list; report any reference without a corresponding
  use, and any use without a reference.
- **AC5 production purity:** `git diff --stat <baseline-sha>..HEAD -- src/ benchmarks/` must show
  additions only, and only `InternalsVisibleTo` lines.

The agent reports findings; the main session fixes anything it finds.

### 4b. Main session — gates

1. `dotnet build WinForward.slnx -c Release` → zero warnings.
2. `dotnet test WinForward.slnx -c Release` → 1159 passed; per-assembly counts verified (AC1, AC2);
   wall clock under 2 minutes (AC8).
3. `dotnet format WinForward.slnx --severity info --verify-no-changes --no-restore` → exit 0, empty
   output (AC7). Budget several minutes; never pipe it in a way that hides the exit code.
4. `jb inspectcode -f=Xml -e=HINT -o=/tmp/jb-inspectcode.xml WinForward.slnx` → zero `<Issue>`
   (AC7). Parse the XML; the command exits 0 even when it finds issues. Budget 10–20 minutes.
5. For each `InternalsVisibleTo` entry added by this task, remove it and rebuild; restore it only if
   the build breaks. Any entry that survives removal was never evidence-backed (design §5).

### 4c. Main session — spec update (R8)

1. `.trellis/spec/backend/directory-structure.md`: replace the single-project `tests/` layout with the
   new tree, and rewrite the "测试 fake/helper 组织" rules — the ≥2-file extraction target becomes
   `tests/WinForward.TestSupport/`, the namespace rule becomes "the support project's own namespace",
   and the new-project naming convention is recorded.
2. `.trellis/spec/backend/quality-guidelines.md:26`: update the recorded test-count baseline from the
   2026-09-19 figure to the post-split total, and note that the suite now spans twelve assemblies.

## Validation reference

| Purpose | Command | Expected |
|---|---|---|
| Full test count | `dotnet test WinForward.slnx -c Release` | 1159 passed, 0 failed |
| One assembly | `dotnet test tests/<p>/<p>.csproj -c Release --no-build` | its expected count |
| Build | `dotnet build WinForward.slnx -c Release` | 0 warnings, 0 errors |
| Format gate | `dotnet format WinForward.slnx --severity info --verify-no-changes --no-restore` | exit 0, empty output |
| Inspector gate | `jb inspectcode -f=Xml -e=HINT -o=/tmp/jb.xml WinForward.slnx` | 0 `<Issue>` |
| Namespace leftovers | `rg -n 'WinForward\.Core\.Tests' -g '*.cs' tests/` | only the six Core files |

## Risks and rollback points

| Risk | Where it bites | Mitigation |
|---|---|---|
| Missing `using` after the namespace move | Batch 2 — mass `CS0246` | Compiler-driven re-balancing (design §4); the spike measured 116 errors on helpers alone, so expect a large but fully mechanical first-pass error list |
| Redundant `using` after the move | Harmless — `IDE0005` is not enforced by the format gate | Probe-verified 2026-10-01; leftovers stay within the R5 diff allowance and still name a genuinely referenced project |
| `InternalsVisibleTo` over-granted | Batch 2c | Every grant must survive a removal-and-rebuild attempt (batch 4b step 5) |
| Parallel sub-agents contend on shared MSBuild outputs | Batch 2b | Waves of ≤4 agents; each edits only its own directory; retry on file-lock errors |
| Test wall clock exceeds the bound | Batch 3 gate | Recorded at batch 3 and batch 4; AC8 allows up to 2 minutes against a 42.7 s baseline |
| `.editorconfig` suppression silently stops matching | Batch 3 | Two dead globs identified and deleted (design §7); the format gate would otherwise fail on `IDE0130` |
| `WinForward.Analyzers.Tests` slows under `TieredCompilation=false` | Batch 3 | Duration compared against its 4 s baseline; explicit override if it regresses |
