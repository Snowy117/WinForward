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
