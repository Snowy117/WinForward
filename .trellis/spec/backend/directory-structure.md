# Directory Structure

> Where backend code lives: the project graph, the layout, the file-length ceiling, and where a type
> belongs when it has to move.

## Scope / Trigger

Read before adding a project, directory, namespace or file; before splitting a file; before
extracting a shared test fake; before moving a type across layers.

---

## Project Graph

7 production projects and 14 test projects in one .NET 10 solution (`WinForward.slnx`). Dependency
direction is fixed:

```
Cli     → { Core, Configuration, Protocols, NdisApi, Runtime, Windows }
Runtime → { Core, Configuration, Protocols, NdisApi, Windows }
Windows, Protocols, NdisApi, Configuration → Core
```

Never reverse an edge. When a type appears to need one, confirm the direction before touching
anything and move the type instead.

## Layout

```
src/
├── WinForward.Cli/            # entry point + composition root (TcpRedirectComposer/UdpProxyComposer)
├── WinForward.Configuration/  # JSON DTOs + ConfigurationLoader + ValidatedConfiguration
├── WinForward.Core/           # dependency-free primitives (Endpoint, IPAddressValue, FlowKey, IPPrefix…)
├── WinForward.NdisApi/        # NDISAPI interop (Abi declarations / Driver / Gate / Buffer)
├── WinForward.Protocols/      # pure protocol codecs (Socks5Messages, Socks5UdpCodec)
├── WinForward.Runtime/        # capture/dispatch runtime; five sub-namespaces (below)
└── WinForward.Windows/        # Windows-only helpers (AdapterIdentity, owner tables)
tests/
├── Directory.Build.props      # shared test config (TieredCompilation=false; imports the root props)
├── WinForward.TestSupport/    # shared fakes/builders — NOT a test project (IsTestProject=false)
├── WinForward.<Layer>.Tests/  # Core, Configuration, Protocols, NdisApi, Windows
├── WinForward.Runtime.<Sub>.Tests/  # Capture, Flow, Socks5, TcpRedirect, UdpProxy
├── WinForward.Integration.Tests/    # cross-layer end-to-end + Cli composition
├── WinForward.E2E.Tests/            # the E2E harness, over benchmarks/WinForward.E2E*
├── WinForward.Performance.Tests/    # allocation gates + GC soak + benchmark-harness checks
└── WinForward.Analyzers.Tests/      # analyzer rules (standalone; no TestSupport)
benchmarks/
├── WinForward.Benchmarks/     # BenchmarkDotNet hosts (Perf/ one class per file, Stability/ one scenario per file)
├── WinForward.E2E/            # end-to-end harness (contract in measurement-harness.md)
└── WinForward.E2E.Analysis/   # report generator for harness runs
```

Cli has no test project of its own: it is covered through `WinForward.Integration.Tests`.
`WinForward.Runtime.Flow.Tests` carries the Runtime **root** namespace surface (dispatcher,
attribution, sweeper, diagnostics); the other four mirror the sub-namespaces.

### Test project split (2026-10-01, task 10-01-split-test-projects)

All 126 test classes used to live in a single `WinForward.Core.Tests`, whose csproj referenced every
production project plus `benchmarks/` — a dependency graph that said nothing. The split established
the mapping above.

- A test project references only the production projects its own sources actually use:
  `ProjectReference` = the set of projects named by its `using` directives ∪ `TestSupport`.
- Test namespace = project name, so `--filter` and stack traces point straight at the project.
- Changing namespaces changes C#'s **implicit parent-namespace visibility**. The old tree sat in
  `WinForward.Core.Tests` (parent `WinForward.Core`), so `Endpoint`, `FlowKey` and friends resolved
  with no `using` at all; after the split that ancestor is unreachable and every such file needs an
  explicit `using`. This was the largest class of edit in the split.
- `InternalsVisibleTo` from production to test is decided by compiler evidence. A missing grant
  surfaces as `CS0122` (and member-level `CS1061`/`CS0117`/`CS7036`), and `CS0122` **masks** the
  errors behind it — the compiler stops analysing expressions that use an unreachable type. Grant,
  rebuild, and iterate once more before the tree converges.

### Runtime Sub-namespaces (2026-08-29)

`WinForward.Runtime` is one project split into five namespaces by domain; the directory name is the
namespace suffix.

| Namespace | Contents |
|---|---|
| `WinForward.Runtime` (root) | the vocabulary every group shares: `FlowDispatcher` and its packet types (`CapturedFlowPacket`, `PacketCaptureMetadata`, `NativeFrameHandle`, `IPacketActionExecutor`, `ISelfTrafficGuard`), `PacketFlowClassifier`, `SelfTrafficRegistry`, `IdleExpirySweeper`, `QuiescenceScope`, `SetupExecutor`, the flow-attribution pipeline, `InterceptionHealthMonitor`, `RuntimeCounters`/`RuntimeHeartbeat`/`RuntimeLogThrottle` |
| `WinForward.Runtime.Logging` | the `[LoggerMessage]` definitions, one file per group (`TcpRedirectLog`, `UdpProxyLog`, `CaptureLog`, `FlowLog`) over `RuntimeLogging` |
| `WinForward.Runtime.Capture` | NDIS capture: generation lifetime, adapter mode control, packet processing, reinjection (`IPacketReinjector` lives here) |
| `WinForward.Runtime.TcpRedirect` | the TCP path: redirect table/session/listener/injection, relay, frame rewrite, `ClientResetInjector` |
| `WinForward.Runtime.UdpProxy` | UDP sessions, response reinjection, and the transport seam (`UdpTransportContracts.cs`: `IUdpProxyTransport`, its factory and the receive-result vocabulary) |
| `WinForward.Runtime.Socks5` | SOCKS5 dialing and UDP transports shared by the TCP and UDP paths (codecs stay in `WinForward.Protocols`) |

- New files go to their domain; the root takes only vocabulary that every group references.
- Cross-group references are direct `using`s. The edges in use today: the root → `TcpRedirect`,
  `UdpProxy`, `Logging`; `Capture` → `TcpRedirect`/`UdpProxy` (via `NdisPacketActionExecutor`) and
  `Logging`; `TcpRedirect`/`UdpProxy` → `Capture`'s `IPacketReinjector`, `Socks5`, `Logging`;
  `Socks5` → `UdpProxy`; `Logging` → `TcpRedirect` (log fields typed by its enums). A new cycle is
  first a question about where the type belongs, and only then a question about adding a `using`.

---

## File Length Ceiling (2026-08-28; extended to benchmarks/ 2026-08-29)

- **Every `.cs` file stays at or under 400 effective lines**, where an effective line is one that
  carries code: blank lines and lines that are only a comment do not count. `wc -l` is a reference
  number, never the criterion. The gate is
  `python3 tools/effective-lines.py <paths>` (exit 1 when a file is over); it is run by hand, because
  no CI workflow runs it.
- **`benchmarks/` obeys the same ceiling.** The host splits by scenario family: `Perf/` holds one
  benchmark class per file; `BenchmarkShared.cs` sits at the project root in the root namespace and
  is shared by `Perf/` and `Stability/`; `Stability/` holds one scenario per file plus
  `StabilityShared.cs` (latency statistics, the product-event census) and `UdpBurstInstrumentation.cs`.
  Two host-specific rules: BDN benchmark classes are never `sealed` (BDN generates a derived proxy),
  and async benchmark methods carry the `Async` suffix (VSTHRD200 is fatal under `benchmarks/`).
- Over the limit, split along a natural seam first — a cluster of static pure functions, a nested
  type ready to be promoted, a `// ----` partition, a second top-level type — and only then invent a
  module.
- **Do not split for splitting's sake.** A split must not damage readability or performance.
  `Cli/Program.cs` stays whole at 377 effective lines because it is one cohesive entry point; a tiny
  type keeps its own file when it is widely shared — `WindowsAdapter.cs` is 7 effective lines and is
  referenced from ~34 files.
- **Meeting the ceiling is not a licence to stop.** A file near 400 lines splits along its existing
  seams before it goes over, not after. Precedent (2026-10-04): `ConfigurationModels.cs` was kept
  whole at 367 effective lines on the strength of the bullet above, then reached 375 while still
  growing, and split on the loader/validator seam already drawn by `ConfigurationLimits` into
  `ConfigurationModels.cs` + `ConfigurationRules.cs`.

### Files and Types

- **Filename = primary type name**, one primary type per file.
- Co-location is allowed only for tight clusters: an interface beside its single implementation
  (`IUdpResponseSink` inside `UdpResponseReinjector.cs`), interop declarations for one ABI directory,
  or a record plus the static class that operates on it.
- A second, unrelated top-level type is the split signal. Precedent: `Socks5Client.cs` held five
  types and became `Socks5ControlConnection.cs` + `Socks5UdpTransport.cs`.
- A seam type belongs next to its **implementation**, not next to its callers.

### Split Discipline (behaviour-neutral refactors)

- A split is a physical move plus visibility changes; logic does not move with it. Lock bodies move
  verbatim so single-lock semantics survive — precedent: `TcpRedirectSessionStore` absorbed the
  coordinator's entire `_gate`-guarded state.
- Deleting dead public surface requires a repo-wide `rg` (including `tests/` and `benchmarks/`) to
  re-confirm zero call sites. P/Invoke declarations are the exception: a complete ABI directory stays
  complete even where no managed caller exists (`SendPacketsTo*` was deleted; `NdisApiNative`'s
  declarations stayed).
- A pass-through alias — a one-line forwarder — gets no method of its own; inline it at the call
  site (precedent: `ReinjectExistingSynAsync`, since inlined).

---

## Naming Conventions

- **Acronyms in identifiers.** A two-letter acronym is all caps (`IO`, `IP`, `OS`); `Id` is the
  exception and stays `Id`. Three or more letters read as a word (`Html`, `Json`, `Tcp`, `Udp`, `Dns`,
  `Cpu`). `IPv4`/`IPv6` keep `IP` caps and a lower-case `v`. Measured examples in the tree:
  `src/WinForward.Core/IPPrefix.cs`, `IPAddressValue`, `SupportedOSPlatform`, `StableId` (40 uses).
  The rule applies to new and touched code; the 108 existing `Ipv[46]` spellings across 12 `src/`
  files are a separate follow-up, not a reason to keep writing them.
- Production files are the primary type in PascalCase. xunit files are the test-class name plus a
  `Tests` suffix, named by subject (`TcpProxyCoordinatorLifecycleTests`,
  `ConfigurationValidationTests`).
- Test fakes and helpers:
  - Used by one file → keep it there (private nested or file-private both fine).
  - **Used by two or more test files → move it to `tests/WinForward.TestSupport/`**, grouped by
    category (`ChecksumMath`, `FrameBuilders`, `FlowBuilders`, the fake families). TestSupport is a
    **non-test project** with an explicit `<IsTestProject>false</IsTestProject>`: `xunit.core.props`
    sets it to true unconditionally, which makes `dotnet test` start a test host for the library and
    abort the whole run with exit 1.
  - TestSupport files use their own namespace (`WinForward.TestSupport`, matching the project name)
    and promote the fakes from private nested to `internal`.
  - **`IsTestProject` also gates the analyzer packages**, and at restore time it is the *project's own*
    value that decides: NuGet restores with `ExcludeRestorePackageImports=true`, so `xunit.core.props`
    never runs and the root `Directory.Build.props` ItemGroup (`'$(IsTestProject)' != 'true'`) hands
    the four analyzers to every test project that does not declare the property itself. Measured
    2026-10-09: 14 of 15 test projects resolve Meziantou + VSTHRD + Roslynator + Sonar;
    `tests/WinForward.E2E.Tests` resolves none, because it declares `<IsTestProject>true</IsTestProject>`
    explicitly. Writing that line is therefore a real decision, not boilerplate: deleting it turns the
    analyzers on, and the test sources then report 31 findings under `TreatWarningsAsErrors`
    (MA0006 ×14, S3358 ×5, MA0002 ×5, S127 ×2, MA0009 ×2, S2344, S1118, MA0008). The E2E test project
    keeps the line with a comment stating that measurement and the revisit trigger: clear the list,
    then drop the line so it is analysed like its siblings.
  - Because the fakes are `internal`, every consuming test project needs an `InternalsVisibleTo` in
    `WinForward.TestSupport.csproj`. The library declares its own `xunit` reference for the helpers
    that assert.
  - When TestSupport itself needs a production internal seam, that production project grants
    `InternalsVisibleTo("WinForward.TestSupport")`. As of 2026-10-01 only `WinForward.Runtime` does;
    `WinForward.Windows` grants its owner-table seam to `WinForward.Benchmarks` instead.
  - Merging duplicate fakes takes the behavioural superset of the two (precedent: `FakeReinjector`
    records both the `DeviceFlags` and `Flags` planes; `TrackingSocket` takes an optional socket
    type/protocol).
