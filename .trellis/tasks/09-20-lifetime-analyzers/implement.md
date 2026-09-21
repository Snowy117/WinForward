# C2 — Lifetime enforcement analyzers: implementation plan

Ordered checklist. `src/**` must be byte-identical at the end (`git diff --stat -- src` empty).

1. **Scaffold the analyzer project.** `analyzers/WinForward.Analyzers/WinForward.Analyzers.csproj`
   per `design.md` §2 (`netstandard2.0`, `EnforceExtendedAnalyzerRules`, the two Roslyn
   `PackageReference`s with `PrivateAssets="all"`). Add the pins to `Directory.Packages.props`. Add
   the project to `WinForward.slnx` under a new `/analyzers/` folder.
   Gate: `dotnet build WinForward.slnx -c Release` green.
2. **Implement the four rules** — `DiagnosticDescriptors`, `WellKnownTypes` (resolved once per
   compilation), `AwaitableClassifier`, and the WF0001–WF0004 analyzers per `design.md` §3. Add
   `AnalyzerReleases.Shipped.md` (header only) and `AnalyzerReleases.Unshipped.md` (the four ids).
3. **Wire into `src/**`.** New `src/Directory.Build.props` with the explicit import of the repo-root
   props, plus the analyzer reference (`design.md` §5 — `.props` or `.targets`, whichever reaches
   every `src/` project and nothing outside). Run the Release build **without** an allowlist yet.
   Gate: the build **fails**, and its diagnostics are exactly the six temporary sites in
   `design.md` §5. Record the diagnostic list as the allowlist evidence. If the site list differs,
   stop and reconcile it against `design.md` §5 and the parent §3.2 before continuing.
4. **Scaffold the test project.** `tests/WinForward.Analyzers.Tests/` (xunit + the
   `Microsoft.CodeAnalysis.CSharp.Analyzer.Testing` / `.Xunit` harness), pins in
   `Directory.Packages.props`, entry in `WinForward.slnx` under `/tests/`.
   Gate: the analyzer tests run (green or empty) and the build stays green apart from step 3's
   expected failures.
5. **Write the analyzer tests** — one fires-test per rule plus every must-not-fire case from
   `design.md` §3. `WF0004`: a bare awaitable statement in **both** an `async` and a **non-async**
   method. `WF0001`: the embedded form `if (cond) _ = FooAsync();`, and `_ = await FooAsync(...)`
   must not fire.
6. **Land the allowlist and the severities.** New `[src/**.cs]` section at the **end** of
   `.editorconfig` (`design.md` §5): the four `dotnet_diagnostic.WF000n.severity = error` lines, the
   six temporary per-file entries, and the permanent `QuiescenceScope.cs` `WF0001` exemption. Each
   entry carries the evidence and the reason in the file's existing comment style, and each
   temporary entry names C3 or C4 as its remover.
   Gates: `dotnet build WinForward.slnx -c Release` green; `git diff --stat -- src` empty.
7. **Prove each entry is load-bearing.** For at least one entry per rule id: delete it, confirm the
   build goes red with that rule at that site, restore it. Record the output.
8. **Prove the scope.** Add a violating line to a `tests/` file, confirm the build stays green,
   revert. Record the output.
9. **Complete the spec table** in `.trellis/spec/backend/async-lifetime.md`: replace the three
   `stub — C2` statuses with the implemented rule + allowlist state and add the `WF0004` row
   (`design.md` §8).
10. **Full gates** (§ below) and the end-of-task check that `src/**` is untouched.

## Validation commands

```bash
dotnet format WinForward.slnx --severity info --verify-no-changes --no-restore   # exit 0, empty output
dotnet build WinForward.slnx -c Release                                          # 0 warnings, 0 errors
dotnet test WinForward.slnx -c Release                                           # all green
jb inspectcode -f=Xml -e=HINT -o=/tmp/jb-inspectcode.xml WinForward.slnx         # zero <Issue>
```

Filtered runs while iterating:

```bash
dotnet test WinForward.slnx -c Release --filter "FullyQualifiedName~WinForward.Analyzers.Tests"
```

Never mask an exit code (no `| tail`). `jb inspectcode` exits 0 even with findings — parse the XML.
Note that `grep -c '<Issue'` substring-matches `<Issues />`; count `<Issue ` elements instead.

## Risky files and rollback

- `src/Directory.Build.props` is new and affects **every** `src/` project. A missing root import
  silently drops `net10.0` / `TreatWarningsAsErrors` / `AnalysisLevel` from all of them. Rollback is
  deleting this file plus the new `.editorconfig` section plus the `WinForward.slnx` entries — the
  product code was never touched, so the rollback is complete by construction.
- `Directory.Packages.props` is shared by every project. Keep the change additive; a wrong Roslyn pin
  breaks the whole solution.
- `.editorconfig` globs: too narrow turns the build red (loud, fine), too broad exempts sites
  silently (closed by checklist step 7).
- `WinForward.slnx` is hand-formatted and not consistently indented; match the surrounding style.

## Pre-start follow-up checks

- `git status --short -- src` must be clean at HEAD, so step 6's `git diff --stat -- src` gate means
  something.
- The SDK's Roslyn version must be known before pinning `Microsoft.CodeAnalysis.CSharp`, so the
  analyzer is guaranteed to load. If the loaded compiler is older than the pin, the analyzer is
  silently inert and step 3's expected failure will not appear.
