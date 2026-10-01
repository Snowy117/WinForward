# Batch 2b — per-project convergence instructions

You are operating as the Trellis `trellis-implement` role. Working directory:
`/home/paff/Projects/WinForward`. Your assignment prompt names your project (`tests/<PROJ>/`) and its
file count.

Read these first, in order:

1. `.trellis/tasks/10-01-split-test-projects/prd.md`
2. `.trellis/tasks/10-01-split-test-projects/design.md` — especially §4 (the namespace move) and §5
   (friend access)
3. `.trellis/tasks/10-01-split-test-projects/implement.md` — the "Batch 2b" section

## Context

The monolithic `tests/WinForward.Core.Tests` is being split into twelve layered test projects. Your
project was just created, its test files were moved there, their `namespace` line already reads
`namespace <PROJ>;`, and every `using static WinForward.Core.Tests.<Helper>;` already reads
`using static WinForward.TestSupport.<Helper>;`.

What is **missing** is the rest of the `using` re-balancing. The old files relied on
`WinForward.Core.Tests` sitting *inside* `WinForward.Core`: C# walks outward through enclosing
namespaces, so `Endpoint`, `FlowKey`, `AddressFamilyKind` and friends resolved with no `using` at
all. Your new namespace provides a different ancestor, so those names went out of reach and the
compiler reports them as `CS0246` / `CS0103`.

A probe on the smallest project surfaced ~10 errors; expect a comparable or larger count.

## Task

Make `dotnet build tests/<PROJ>/<PROJ>.csproj -c Release` reach **zero warnings and zero errors**.

- Build, read the `CS0246` / `CS0103` diagnostics, and add the missing namespace to the affected
  file's `using` block.
- Keep each `using` block alphabetically sorted, with `using static` lines last.
- Repeat until the only errors left are `CS0122` / `CS0272` — those are friend-access gaps that you
  must **not** fix; report them (see below).
- A file that uses the helpers' top-level internal types (`FakeListener`, `FrameBuilders`,
  `ScriptedSocks5UdpServer`, `RecordingRuntimeLogger`, `CaptureRunnerHarness`, …) needs a plain
  `using WinForward.TestSupport;` in addition to any `using static`.
- Leave usings that the new ancestry makes redundant **in place**. `IDE0005` is not enforced by this
  repository's format gate (probe-verified on 2026-10-01), so removing them is churn beyond what the
  rename forces.

## Hard constraints

- Edit **only** files under `tests/<PROJ>/`. Never touch `src/`, `benchmarks/`, `WinForward.slnx`,
  `tests/Directory.Build.props`, `tests/WinForward.TestSupport/`, or another project's directory.
- Never change an assertion, a test method name, a `[Fact]`/`[Theory]` attribute, or any test logic.
  `using` directives are the only permitted edits — the `namespace` line is already correct.
- No git write commands (`git add`, `git commit`, `git checkout`, branch operations). Read-only git
  is fine.
- Do **not** run `dotnet format` or `jb inspectcode`; they are later-batch gates and take many
  minutes.
- Do **not** add `InternalsVisibleTo` anywhere — not to `src/`, not to `TestSupport`. Friend access
  from `TestSupport` to your project is already granted; gaps in **production-project** friend access
  are yours to report, not to fix.
- If a build fails with a file-lock or partially-written-output error, wait ~10 s and retry; other
  agents are building concurrently.

## Field notes from the wave (read before you start)

- **Friend-access gaps do not always look like `CS0122`.** Accessing an `internal` *member* (rather
  than a type) surfaces as `CS1061` ("does not contain a definition") or `CS0117`, and a missing
  internal constructor as `CS7036`. Treat those as friend-access gaps too: report them, do not try to
  fix them with `using` edits.
- **`InternalsVisibleTo` is already granted to your project** from `WinForward.Core` and
  `WinForward.TestSupport` (if you reference them). Report gaps in any *other* production project.
- **A single build's error list can be truncated by the compiler.** One sibling agent saw 38
  diagnostics on its first pass while many more were still hidden; the true set only appeared after a
  fix-and-rebuild. Never treat one build as proof of convergence — fix, rebuild, and confirm on a
  second deterministic build.

## Iterate past the friend-access wall

`CS0122` **masks** errors behind it: while a type is inaccessible the compiler stops analysing
expressions that use it, so `CS0103`/`CS0246` diagnostics for the *other* names in those expressions
stay hidden. Batch 2b's first project hit exactly this — it converged to 11 `CS0122`, and once the
grant landed a fresh set of `CS0103`/`CS0246` errors appeared.

So the loop is:

1. Iterate on `using` edits until only `CS0122`/`CS0272` remain.
2. Report the grants you need (see the report format below) and stop — you cannot apply them.
3. The parent session applies them and tells you.
4. **Rebuild and keep iterating.** More `using` errors are expected at this point; fix them the same
   way. Repeat until the build is genuinely zero-warning, zero-error.

Do not report "done" until a build of your project has completed with **0 Errors**.

## Report back (concise)

1. Final build status: warning and error counts, and the exact remaining diagnostics if any.
2. `dotnet test tests/<PROJ>/<PROJ>.csproj -c Release --no-build` — its summary line, or why it
   cannot run yet.
3. Files edited, and which namespaces you added (brief).
4. **Required `InternalsVisibleTo` grants**, one line each:
   `<owning src project> for <rejected member> (CS0122 at <file:line>)`.
   Write `none` explicitly if the build reached zero errors without any.
