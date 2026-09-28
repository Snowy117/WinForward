# C2 — Lifetime enforcement analyzers: technical design

Parent `design.md` §3 is normative for the rule set, the wiring mechanism, the `Run` door and the
rollout policy. This document adds only what C2 needs in order to be implementable and verifiable.

## 1. Deliverable and boundary

Ships:

1. `analyzers/WinForward.Analyzers/` — `netstandard2.0` analyzer project holding the four rules.
2. `src/Directory.Build.props` (plus, if needed, `src/Directory.Build.targets`) — imports the
   repo-root props and wires the analyzer into `src/**` and nowhere else.
3. `Directory.Packages.props` — central pins for `Microsoft.CodeAnalysis.CSharp`,
   `Microsoft.CodeAnalysis.Analyzers`, and the Roslyn test harness.
4. `tests/WinForward.Analyzers.Tests/` — analyzer tests.
5. `.editorconfig` — the four `error` severities, the six temporary allowlist entries, the one
   permanent exemption.
6. `.trellis/spec/backend/async-lifetime.md` — the `## WF rules` table completed (C1 left three
   rows marked `stub — C2`).
7. `WinForward.slnx` — both new projects, in `/analyzers/` and `/tests/` folders.

**Not shipped: any change to `src/**` product code.** The rules land together with the allowlist, not
with migration. Acceptance for this boundary is `git diff --stat -- src` being empty at the end of the
task.

## 2. Analyzer project shape

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>netstandard2.0</TargetFramework>
    <EnforceExtendedAnalyzerRules>true</EnforceExtendedAnalyzerRules>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.CodeAnalysis.CSharp" PrivateAssets="all" />
    <PackageReference Include="Microsoft.CodeAnalysis.Analyzers" PrivateAssets="all" />
  </ItemGroup>
</Project>
```

Constraints:

- `netstandard2.0` overrides the repo-root `net10.0`. The project inherits it because `analyzers/`
  has no `Directory.Build.props` of its own — that is intended; the remaining root properties
  (`Nullable`, `TreatWarningsAsErrors`, `AnalysisLevel=latest`) stay inherited and the project must
  satisfy them.
- `LangVersion` stays at the root's `14.0` while the target is `netstandard2.0`, so the analyzer code
  must avoid language features that need `netstandard2.1`+ runtime support: no `record`, `init`, or
  `required` unless an `IsExternalInit` polyfill is added. Plain `sealed class` / `readonly struct`,
  file-scoped namespaces and pattern matching are fine.
- Analyzer/dispatcher types must be `public` (the compiler instantiates them by reflection), carry
  `[DiagnosticAnalyzer(LanguageNames.CSharp)]`, and expose `SupportedDiagnostics`.
- Pin the newest stable `Microsoft.CodeAnalysis.CSharp` 4.x line that loads under this SDK's compiler.
  A wrong pin is caught by §6's scratch-violation gate: an analyzer that fails to load produces no
  diagnostics, so the gate silently loses its teeth if the build stays green.
- `EnforceExtendedAnalyzerRules=true` turns on the `RS1xxx` analyzer-authoring rules; RS2008 in
  particular requires `AnalyzerReleases.Shipped.md` and `AnalyzerReleases.Unshipped.md` in the
  project. `Shipped.md` stays header-only; `Unshipped.md` lists `WF0001`–`WF0004`.
- `dotnet format` and `jb inspectcode` both walk the solution, so the two new projects must be
  clean under the repo's own gates.

## 3. Rule detection

Shared infrastructure:

- `DiagnosticDescriptors` — the four `DiagnosticDescriptor`s, `DiagnosticSeverity.Error`, category
  `WinForward.Lifetime`, `isEnabledByDefault: true`.
- `WellKnownTypes` — resolved once per compilation in `RegisterCompilationStartAction` via
  `compilation.GetTypeByMetadataName(...)` for `System.Threading.Tasks.Task`, `Task<T>`, `ValueTask`,
  `ValueTask<T>`, `TaskFactory`, `TaskFactory<T>`. Resolving once per compilation, not per node, keeps
  the analyzer cheap.
- `AwaitableClassifier.IsAwaitable(ITypeSymbol?, WellKnownTypes)` — returns `true` when the type's
  `OriginalDefinition` is one of the well-known task types (`SymbolEqualityComparer.Default`), or,
  structurally, when it exposes a public parameterless `GetAwaiter()` whose return type has an
  `IsCompleted` property, an `OnCompleted(Action)` method and a `GetResult()` method. The structural
  branch is what makes custom awaitables covered the way the parent design §3.2 requires.

### WF0001 — unawaited awaitable discard

Register `OperationKind.SimpleAssignment`. Fire when the target is an `IDiscardOperation` and
`assignment.Value.Type` is awaitable. Report on `assignment.Syntax`.

- **Why operations and not `ExpressionStatementSyntax`:** `TcpRedirectSessionStore.cs:157` is
  `if (start is not null) _ = RunDisposeAsync(start);` — the discard is an embedded statement. A rule
  keyed on expression statements would miss it. The operation action sees the assignment wherever it
  appears.
- **Why `Value.Type` and not a syntax check for `await`:** `_ = await X` is a
  `SimpleAssignment(discard, IAwaitOperation)` whose value type is the *awaited result*, so the rule
  fires only if that result is itself awaitable, which is a genuine mistake. All three repo sites
  discard a non-awaitable result (`Socks5ControlConnection.cs:227`, `Socks5UdpTransport.cs:282,294`).

### WF0002 — `Task.ContinueWith`

Register `OperationKind.Invocation`. Fire when `method.Name == "ContinueWith"` and the target's
`ContainingType.OriginalDefinition` is the well-known `Task` **or** `Task<T>`. Keying on the resolved
symbol avoids flagging an unrelated user-defined `ContinueWith`.

**Correction (measured during implementation, 2026-09-21):** an earlier version of this design claimed
`Task<T>` inherits `ContinueWith` so its `ContainingType` would be `Task`. That is false —
`Task<TResult>` **declares its own** `ContinueWith` overloads and member lookup prefers them, so the
call at `TcpProxyRelay.cs:289` binds with `ContainingType` = `System.Threading.Tasks.Task<…PumpResult>`.
Matching only `Task` made the rule miss that site, which is precisely the site its allowlist entry
exists for. Both original definitions are now matched.

### WF0003 — `Task.Run` / `Task.Factory.StartNew`

Register `OperationKind.Invocation`. Fire when `method.Name is "Run" or "StartNew"` and the target's
`ContainingType.OriginalDefinition` is `Task`, `TaskFactory` or `TaskFactory<T>`.

### WF0004 — bare unawaited awaitable expression statement

Register `OperationKind.ExpressionStatement`. Fire when the statement operation's `Type` is awaitable
**and** the operation is neither an `IAssignmentOperation` nor an `IAwaitOperation`.

- The **assignment** exclusion covers every assignment kind, not only `ISimpleAssignmentOperation`.
  `_cleanupTask ??= CleanupCoreAsync();` is an `ICoalesceAssignmentOperation`; an earlier version of
  this design named only `ISimpleAssignmentOperation`, and implementing it literally produced three
  false positives (`UdpProxySession.cs:186`, `TcpProxyCoordinator.cs:659`,
  `CaptureLifecycle.cs:204`) — all legitimate "create the task once, await it later" sites. The
  exclusion also keeps `_ = FooAsync();` from being reported twice (WF0001 owns that shape) and keeps
  `field = FooAsync();` quiet, since storing an awaitable for a later consumer is legitimate.
- The **await** exclusion is the same argument: `await Task.WhenAny(runTask, demandTask);`
  (`LayeredCaptureRunner.cs:170`) is an `IAwaitOperation` whose type is the awaited *result*, which for
  `Task<Task>` is itself a `Task`. Nothing is left unobserved, so it must not fire.
- The rule reports the whole expression statement, semicolon included.
- The rule's whole reason for existing: measured 2026-09-21 on net10.0, `CS4014` fires for
  `async Task M() { WorkAsync(); }` but produces **no diagnostic at all** for
  `void M() { WorkAsync(); }`. The synchronous-method escape is exactly the remaining hole.

### Must-not-fire shapes (each becomes a test)

All from the parent design §3.2 benign list, which is the set that exists in `src/` today:

- `_ = await FooAsync(...)` — awaited; only a non-awaitable result is discarded
  (`Socks5ControlConnection.cs:227`, `Socks5UdpTransport.cs:282,294`);
- `_ = task.Exception` — `AggregateException` (`TcpProxyRelay.cs:290`);
- `_ = Interlocked.Add(...)` / `_ = Increment(...)` / `_ = GetOrAddCounter(...)` — numeric or
  non-awaitable (`RuntimeCounters.cs:58,95-96,104,112`);
- `_ = TryWrite(...)` / `_ = TryAdd(...)` / `_ = SendTo(...)` — `bool`/`int`
  (`IPAddressValue.cs:103`, `Socks5Udp.cs:35`, `RuntimeCounters.cs:94,103,111`,
  `Socks5UdpTransport.cs:255`);
- `_ = ShutdownSend(...)` — `bool` (`TcpProxyRelay.cs:284`);
- `_ = character switch { ... }` — non-awaitable (`RuntimeLogging.cs:120`).

## 4. Severity

The descriptors declare `DiagnosticSeverity.Error`, and `.editorconfig` records
`dotnet_diagnostic.WF000n.severity = error` for each id as the self-documenting anchor the per-file
exemptions hang off. `TreatWarningsAsErrors=true` is a second net, but it is not the primary
mechanism: the rules must be fatal even in a project that relaxes warning treatment.

## 5. Wiring and the allowlist

`src/Directory.Build.props` must **explicitly `<Import>` the repo-root `Directory.Build.props`** —
MSBuild stops at the nearest one, so without the import every `src/` project would silently lose
`net10.0`, `TreatWarningsAsErrors`, `AnalysisLevel` and the rest.

The analyzer is referenced as
`<ProjectReference ... OutputItemType="Analyzer" ReferenceOutputAssembly="false" />`. Whether that
item lives in `src/Directory.Build.props` or `src/Directory.Build.targets` is an implementation
choice — put it where it reliably applies to every project under `src/` and to nothing outside it,
and prefer `.targets` if props-ordering causes trouble. The requirement is the reach, not the split.

Allowlist sections go at the **end** of `.editorconfig` in a new `[src/**.cs]` section, because
`.editorconfig` resolution is last-match-wins by section order and the file already has a later
generic `[*.cs]` section. Per-file sections follow the file's existing path-relative glob style
(`tests/**.cs`), and comments match the file's existing habit of recording the evidence and the
reason.

| File | Rules | Why | Removed by |
|------|-------|-----|-----------|
| `src/WinForward.Runtime/TcpRedirect/TcpProxyRelay.cs` | `WF0001` `WF0002` | `:289` `_ = first.ContinueWith(...)` | C3 |
| `src/WinForward.Runtime/TcpRedirect/TcpRelayFaultObserver.cs` | `WF0001` `WF0002` | `:27` `_ = relay.Completion.ContinueWith(...)` | C3 (file deleted) |
| `src/WinForward.Runtime/TcpRedirect/TcpRedirectSessionStore.cs` | `WF0001` | `:157` embedded discard | C3 |
| `src/WinForward.Runtime/UdpProxy/UdpProxySession.cs` | `WF0001` | `:312` `_ = receiveFailureHandler(this);` | C3 |
| `src/WinForward.Runtime/Capture/MultiAdapterCaptureLoop.cs` | `WF0001` | `:110` `_ = ForwardDegradationAsync(...)` | C4 |
| `src/WinForward.Runtime/Capture/LayeredCaptureRunner.cs` | `WF0003` | `:144` `Task.Factory.StartNew`, `:149` `Task.Run` | C4 |

Permanent, not on the shrink list — `WF0001` for `src/WinForward.Runtime/QuiescenceScope.cs`: the
primitive's `_ = RunChildAsync(...)` (`:133`) and `_ = DrainCoreAsync(...)` (`:170`) are tracked
children whose leases and faults it owns.

A glob that is too broad would exempt sites silently. That risk is closed by §6's load-bearing gate
rather than by inspection.

## 6. Verification

- **Analyzer tests** (`tests/WinForward.Analyzers.Tests`): for each rule, one test proving it fires on
  a violating snippet, and the must-not-fire cases from §3. `WF0004` needs both an `async` and a
  **non-async** violating method. `WF0001` needs the embedded-statement form
  (`if (cond) _ = FooAsync();`) and the `_ = await FooAsync(...)` non-fire.
- **Build gate:** after wiring, `dotnet build WinForward.slnx -c Release` must fail with **11
  diagnostics**: nine across the six temporary files (two `WF0003` on
  `LayeredCaptureRunner.cs:144,149`; `WF0001` on `MultiAdapterCaptureLoop.cs:110`,
  `TcpRedirectSessionStore.cs:157`, `UdpProxySession.cs:312`; `WF0001`+`WF0002` on
  `TcpProxyRelay.cs:289` and `TcpRelayFaultObserver.cs:27`) plus the two permanent
  `WF0001`s on `QuiescenceScope.cs:133,170`. After the allowlist lands the build must be green. That
  first failure is the evidence the allowlist is complete — a missed site becomes a hard build error
  by construction. If the diagnostic set differs in any way, follow the evidence and record the
  difference rather than forcing it to match this document.
- **Scratch-violation gate (per rule):** add a temporary violating line to a `src/` file, confirm the
  build fails with that rule's id, revert. Do this for all four ids. This is also what proves the
  analyzer actually loaded.
- **Allowlist reaches-not-too-far gate:** temporarily delete one entry per rule id and confirm the
  build goes red at that site, then restore. Spot-check at least one entry per id.
- **Scope gate:** add a violating line to a `tests/` file, confirm the build stays green, revert.
- **Boundary gate:** `git diff --stat -- src` empty.
- **Full gates:** `dotnet format WinForward.slnx --severity info --verify-no-changes --no-restore`
  (exit 0, empty), `dotnet build WinForward.slnx -c Release` (0 warnings), `dotnet test
  WinForward.slnx -c Release` (green), `jb inspectcode -f=Xml -e=HINT -o=/tmp/jb-inspectcode.xml
  WinForward.slnx` (zero `<Issue>` — parse the XML, the tool exits 0 either way).

## 7. Risks

| Risk | Mitigation |
|------|-----------|
| `src/Directory.Build.props` import mistake silently drops root properties for all of `src/` | The explicit-import requirement in §5 plus the full build gate; a dropped `net10.0` fails loudly. |
| Roslyn pin does not load under the SDK compiler ⇒ rules silently inert | §6's per-rule scratch-violation gate fails if no diagnostic appears. |
| A rule's symbol matching misses a real site (as `WF0002` did before the `Task<T>` correction) ⇒ a legacy site is unenforced while its allowlist entry looks load-bearing | §6's build gate enumerates the true site list explicitly, and the load-bearing spot-check fails when an entry exempts nothing. |
| `.editorconfig` glob too broad ⇒ silent exemption | §6's load-bearing spot-check. |
| Allowlist forgotten for a benign shape ⇒ false positive breaks the build | §6's build gate is run before the allowlist is written precisely to enumerate the true site list. |
| Analyzer authoring rules (`RS1xxx`, RS2008) fail under `EnforceExtendedAnalyzerRules` | Release-tracking files from §2; the full gates catch the rest. |
| New projects trip `dotnet format` / `jb inspectcode` | Both are release gates; the projects must be clean, not exempted. |

## 8. Spec table

C1 created `.trellis/spec/backend/async-lifetime.md` and left the `## WF rules` table as stubs marked
`stub — C2`. C2 replaces those statuses with the implemented rule and the current allowlist state, and
keeps the table as the durable index of the four ids. Per parent design §3.5 the file is otherwise
C1's; C2 appends only.
