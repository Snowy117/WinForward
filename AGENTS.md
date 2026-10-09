<!-- TRELLIS:START -->
# Trellis Instructions

These instructions are for AI assistants working in this project.

This project is managed by Trellis. The working knowledge you need lives under `.trellis/`:

- `.trellis/workflow.md` — development phases, when to create tasks, skill routing
- `.trellis/spec/` — package- and layer-scoped coding guidelines (read before writing code in a given layer)
- `.trellis/workspace/` — per-developer journals and session traces
- `.trellis/tasks/` — active and archived tasks (PRDs, research, jsonl context)

If a Trellis command is available on your platform (e.g. `/trellis:finish-work`, `/trellis:continue`), prefer it over manual steps. Not every platform exposes every command.

If you're using Codex or another agent-capable tool, additional project-scoped helpers may live in:
- `.agents/skills/` — reusable Trellis skills
- `.codex/agents/` — optional custom subagents

Managed by Trellis. Edits outside this block are preserved; edits inside may be overwritten by a future `trellis update`.

<!-- TRELLIS:END -->

# Comment Conventions

A comment describes the code as it stands. Write one only where the code cannot speak for itself:

- **Explain the non-obvious** — invariants and preconditions, why a lock or an ordering is required, boundary and reject paths, performance and allocation constraints, ownership and lifetime, and the external spec a behaviour follows (RFC clause, Windows or NDISAPI semantics).
- **State the motivation** — why this approach rather than the one a reader would reach for first.

Do not write:

- **Archive pointers** — task names and in-task item numbers (`task 09-17`, `R1-A`, `B11`, `P3`, `design §3.5`, `PRD`, `AC2`), dates, and paths into `benchmarks/results/` or task research directories. The task gets archived, and the pointer decays into a reference no maintainer can resolve. Keep the constraint, drop the pointer.
- **Narration** — comments that restate the adjacent statement, and comments describing code that no longer exists.
- **History** — what the code used to do, what it replaced, or how it changed. To justify a line, justify the line.

Rules that outlive a single edit:

- XML doc comments keep every tag paired and complete (`<summary>`, `<param>`, `<returns>`, `<remarks>`, `<see cref>`, `<paramref>`, `<c>`, `<para>`); a `cref` that no longer resolves fails the build under `TreatWarningsAsErrors`.
- Every suppression carries a reason verifiable against this repository — the quality gate below requires it, so the reason is not optional prose.
- References that stay: live specs under `.trellis/spec/`, sibling types and tests inside this tree, protocol constants, interpolation format specifiers, and any label a test or golden file pins.
- A printed string is user-visible output: rewrite one only when nothing pins it, and re-run the gates afterwards.
- A rewritten `//` line never ends in `;`, `)`, `{`, `}` or `=>` — Sonar `S125` reads such a line as commented-out code and fails the build.

# Pre-Commit Quality Gate

Before creating **any** git commit in this repository, run:

```bash
dotnet format WinForward.slnx --severity info --verify-no-changes --no-restore
```

Commit only when this command exits 0 with **empty output**. If it reports diagnostics, resolve every one of them first:

- fix the code when following the diagnostic genuinely improves quality or maintainability, otherwise
- suppress narrowly with a documented reason — a localized `#pragma warning disable <RULE> // <reason>`, or a glob-scoped `.editorconfig` `severity = none` entry (scope it to the exact paths the evidence covers; never suppress globally with a "the rest of the tree currently has none" rationale).

Do not commit while diagnostics remain. See `.trellis/spec/backend/quality-guidelines.md` for the full suppression policy and audit method.

Notes:
- The command analyzes the entire solution and takes several minutes — budget for it, and never pipe it in a way that hides the exit code (e.g. `dotnet format ... | tail` masks the real status).
- Build and test gates also use Release: `dotnet build WinForward.slnx -c Release` must be zero-warning and `dotnet test WinForward.slnx -c Release` must stay green.

Second gate — the JetBrains inspector (reports HINT severity and above):

```bash
jb inspectcode -f=Xml -e=HINT -o=/tmp/jb-inspectcode.xml WinForward.slnx
```

The report must contain **zero** `<Issue>` entries before committing. Note `jb inspectcode` exits 0 even when it finds issues — parse the XML, never trust the exit code. Resolve findings like the format gate: fix the code when that genuinely improves quality, otherwise suppress narrowly with a concrete reason (`// ReSharper disable once <InspectionId> // <reason>`, or a glob-scoped `.editorconfig` `resharper_<inspection_id>_highlighting = none` entry). Hard constraints:

- Performance-first: never adopt suggestions that add allocations/delegates to packet paths (loop→LINQ conversions are suppressed repo-wide for this reason).
- Readability: inverted `if`s are adopted only when they read better as early-exit paths; keep explicit nesting where inverting hurts readability.
- Known false-positive classes that must not be "fixed": `MemberCanBePrivate` on members used by friend assemblies via `InternalsVisibleTo`; `UnusedAutoPropertyAccessor` on BenchmarkDotNet `[Params]` setters (reflection-injected).

Notes:
- The full run takes ~10–20 minutes — budget for it.
- If a genuinely green tree yields `CSharpErrors` (or cascading unused-member findings), the incremental cache is corrupted: clear `~/.local/share/JetBrains/` (and `/tmp/JB`), then rerun before acting on the report.
