Active task: .trellis/tasks/10-09-trim-comment-metadata

You are the executor for comment-trim batch 43 in /home/paff/Projects/WinForward. Do not delegate. Do not commit.

## Mandatory prohibitions (violating these fails the task)

1. NEVER edit files with a script. `sed -i`, `awk`, `perl -i`, a python script that writes files, or any bulk regex replacement are all forbidden. Every comment rewrite must go through reading and understanding, edited one site at a time with the edit tool. Read-only commands (`rg`, `wc`, `git diff`, `git status`) are fine.
2. Change comments, and archive pointers inside string literals, ONLY. Never touch identifiers, values, interpolation expressions or format specifiers, statement structure, blank lines or line endings.
3. Never delete a comment that carries a real constraint.
4. Never end a rewritten `//` line with `;` (or any other token that makes Sonar read the line as commented-out code - S125 fails the build). End the sentence with `.`, or restructure it. The same applies to a bare trailing `)`, `{`, `}` or `=>`.

## Your files (batch 43)

```
benchmarks/WinForward.Benchmarks/Perf/FrameworkSetupBenchmarks.cs
benchmarks/WinForward.Benchmarks/Stability/ChurnCountingSink.cs
benchmarks/WinForward.E2E.Analysis/Checks/WindowOverflow.cs
benchmarks/WinForward.E2E.Analysis/Cli/AnalysisOptions.cs
benchmarks/WinForward.E2E.Analysis/Cli/AnalysisRunner.cs
benchmarks/WinForward.E2E.Analysis/Findings/LedgerFindings.DnsTotals.cs
benchmarks/WinForward.E2E.Analysis/Verdict/VerdictWriter.cs
benchmarks/WinForward.E2E/Client/Arms/ControlArm.cs
benchmarks/WinForward.E2E/Client/Arms/LatencyMetricsWriter.cs
benchmarks/WinForward.E2E/Client/Arms/LossWindow.cs
benchmarks/WinForward.E2E/Wire/TcpCommand.cs
src/WinForward.Cli/UdpProxyComposer.cs
src/WinForward.Protocols/PacketPathProbe.cs
tests/WinForward.E2E.Tests/JsonPaths.cs
tests/WinForward.E2E.Tests/SocketsTests.cs
tests/WinForward.Performance.Tests/BenchmarkFrameBuilderTests.cs
tests/WinForward.Runtime.TcpRedirect.Tests/SequenceTrackerTests.cs
tests/WinForward.Runtime.UdpProxy.Tests/UdpResponseSourceMismatchTests.cs
tests/WinForward.TestSupport/AdapterFakes.cs
```

## Delete these

- Archive pointers: task names and in-task item numbers (`task 09-17`, `R1-A`, `R2.3`, `B11`, `P3`, `M0`, `C4`, `S3/D3`, `(B1/B2)`), dates (`2026-09-30`, `measured 2026-08-27`), and cross-references to planning docs (`design §3.5`, `PRD`, `AC2`).
- Evidence footnotes: paths into `benchmarks/results/...` or task research dirs. If the same sentence also states a constraint on the current code (capacity floor, allocation budget, timeout, upper bound), KEEP the constraint and drop the path.
- Redundant narration: comments that restate the adjacent code (`i++;` described as "increment i"), or explain an obvious assignment/call/return.
- Stale content: comments describing code that no longer exists or a design that no longer holds.

## Keep, and rewrite shorter

- Invariants and preconditions: why a lock is required, why a bound must hold, why this order cannot change.
- Motivation: why this approach rather than the obvious one.
- Boundaries and reject paths: what input is refused, what state results, why it must fail closed.
- Performance and allocation constraints: zero-alloc hot path, parse-once, pool rent/return balance, buffer caps.
- External-spec basis: RFC clauses, Windows/NDISAPI behaviour, protocol field layout.
- Ownership and lifetime: who creates, who releases, who awaits whom.

Rewrites must be SHORTER than the original, not the metadata swapped for a same-length new sentence. One comment, one idea. If a long comment mixes metadata with a real constraint, drop the metadata, keep the constraint, split into two lines if that reads better.

## Archive pointers inside string literals

A printed note, assertion message or exception text can carry the same archive pointers. You MAY delete
those pointers from string literals in your files. Judge each candidate in context - these are NOT
pointers and must stay:

- interpolation format specifiers: `{x:F1}`, `{n:X8}`, `{rate:P0}`;
- the file's own stage or component labels that match its method names (the `S1`/`A4`/`C2` prefixes of
  `StageS1_...` / `ComponentA4b_...` in `SessionSetupDecompositionBenchmarks`);
- text that describes code behaviour and merely mentions a directory, e.g. "neither the live nor the
  archive under .trellis/tasks/archive".

A literal edit changes printed output, so it is only safe when nothing pins that text. After your literal
edits run `dotnet build WinForward.slnx -c Release` and `dotnet test WinForward.slnx -c Release --nologo`;
if anything fails, revert that literal and report it. Golden and contract tests may pin exact strings -
when in doubt, leave the literal alone and list it under UNSURE.

## Structure preservation

- XML doc comments must keep every tag, paired and complete: `<summary>`, `<param>`, `<returns>`, `<remarks>`, `<see cref="…">`, `<paramref name="…">`, `<c>`, `<para>`. Never delete a tag and keep only the prose.
- `cref` / `paramref` targets must still resolve; a broken one fails the solution build (`TreatWarningsAsErrors`).
- If you cannot tell whether a comment carries a real constraint or is mere narration, KEEP it and list it under UNSURE.

## Steps

1. Read each file.
2. Rewrite comments one site at a time with the edit tool.
3. `git diff --stat -- <your files>`
4. Prove no executable code moved. This MUST print nothing:
   `git diff -U0 -- <your files> | rg '^[+-]' | rg -v '^(\+\+\+|---)' | rg -v '^[+-]\s*(///?|//|\*|/\*)'`
   The one permitted exception: a line that differs ONLY inside a string literal's text (an archive
   pointer you deleted, or surrounding prose you shortened to make it read well). Such a line will print -
   list every one of them explicitly in your report under LITERAL_EDITS so the difference can be checked.
   Anything else that prints means you changed executable code: revert that hunk and say so.
5. Comment-line counts before/after: `rg -c '^\s*//' <your files>` and `git show HEAD:<file> | rg -c '^\s*//'`.

## Do not run dotnet

Never invoke `dotnet build`, `dotnet test` or `dotnet format`: several agents edit this workspace at the
same time, and parallel dotnet runs exhaust the machine's memory. Your verification is `rg`, `wc` and
`git diff` only. The parent agent runs the build and the test suite serially after your batch lands.

## Report format (exactly this, plain text)

```
BATCH: 43
FILES: <n> processed
EDITS: <n> comment sites rewritten
DELETED: <n> comments removed outright
XML_STRUCTURE: <ok | problems>
CODE_TOKENS: <clean | offending lines>
UNSURE: <none | file:line — why>
LITERAL_EDITS: <none | file:line - the literal text you changed>
RESIDUAL_METADATA: <none | file:line — which pattern>
NOTES: <one or two lines, only if genuinely surprising>
```