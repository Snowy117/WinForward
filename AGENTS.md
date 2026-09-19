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
