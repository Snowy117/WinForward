Active task: .trellis/tasks/10-09-trim-comment-metadata

You are the executor for catch-up batch 58 in /home/paff/Projects/WinForward. The comments in these files
were already trimmed in an earlier pass; the archive pointers that remain live inside STRING LITERALS -
printed notes, assertion messages, exception text. Do not delegate. Do not commit.

## Mandatory prohibitions (violating these fails the task)

1. NEVER edit files with a script. `sed -i`, `awk`, `perl -i`, a python script that writes files, or any bulk regex replacement are all forbidden. Every edit goes through reading and understanding, one site at a time with the edit tool. Read-only commands (`rg`, `wc`, `git diff`, `git status`) are fine.
2. Change archive pointers inside string literals ONLY. Never touch identifiers, values, interpolation expressions, format specifiers, statement structure, blank lines or line endings. Leave comments alone - they are already clean.
3. Never delete text that carries a real constraint; delete only the archive pointer and any words that exist solely to carry it.

## Your files (batch 58)

```
benchmarks/WinForward.Benchmarks/Stability/ResidencyCensusScenario.cs
benchmarks/WinForward.Benchmarks/Stability/SweepPauseScenario.cs
benchmarks/WinForward.Benchmarks/Stability/ScalingContentionScenario.cs
benchmarks/WinForward.Benchmarks/Stability/UdpSessionBudgetAcceptance.cs
tests/WinForward.E2E.Tests/Lanes/LatencyPolicyTests.cs
```

## What counts as an archive pointer

Task names and in-task item numbers (`task 09-17`, `F5.1`, `F5.2`, `D18.6 #5`, `D19.3 C`, `A1 item 1a`,
`(A4 item 9)`, `R1`, `B11`, `M2`, `S4`, `design §3`, `design §7`), dates (`2026-09-19`, `measured 2026-08-27`),
references to a "pre-change" or "the 09-28 series" baseline, and measurement-artifact paths.

## What must stay (judge each candidate in context)

- interpolation format specifiers: `{x:F1}`, `{n:X8}`, `{rate:P0}`;
- the file's own stage or component labels that match its method names (`S1`, `A4`, `C2` in
  `SessionSetupDecompositionBenchmarks`);
- prose that describes code behaviour and merely mentions a directory, e.g. "neither the live nor the
  archive under .trellis/tasks/archive";
- every invariant, bound, threshold, RFC reference and fail-closed statement.

Rewrite the surviving sentence so it reads naturally without the pointer - shorter, not padded.

## Structure preservation

- Interpolated strings must keep every `{...}` hole and every format specifier exactly as it is.
- Do not change a literal that a golden or contract test pins. If a test fails after your edit, revert that
  literal and report it under UNSURE.

## Steps

1. Read each file and list every string literal that carries a pointer.
2. Edit one site at a time with the edit tool.
3. Verify nothing but literal text moved:
   `git diff -U0 -- <your files> | rg '^[+-]' | rg -v '^(\+\+\+|---)'`
   Every printed line must differ only inside a string literal. List each one under LITERAL_EDITS.
4. Do NOT run dotnet: parallel runs exhaust this machine's memory. The parent agent builds and tests
   serially after your batch lands. Your evidence is the diff itself - list every literal you changed.

## Do not run dotnet

Never invoke `dotnet build`, `dotnet test` or `dotnet format`: several agents edit this workspace at the
same time, and parallel dotnet runs exhaust the machine's memory. Your verification is `rg`, `wc` and
`git diff` only. The parent agent runs the build and the test suite serially after your batch lands.

## Report format (exactly this, plain text)

```
BATCH: 58
FILES: <n> processed
LITERAL_EDITS: <file:line - old text -> new text, one line per site>
UNSURE: <none | file:line - why>
NOTES: <one or two lines, only if genuinely surprising>
```