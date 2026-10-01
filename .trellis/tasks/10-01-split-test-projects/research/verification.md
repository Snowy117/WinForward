# Batch 4 verification record — 10-01-split-test-projects

Baseline for every diff in this file is `32b67f7` (the commit before batch 1).

## Gates

| Gate | Command | Result |
|---|---|---|
| Build | `dotnet build WinForward.slnx -c Release` | 0 warnings, 0 errors |
| Tests | `dotnet test WinForward.slnx -c Release` | 1159 passed, 0 failed, 13 assemblies, 19 s wall (AC8 bound: 2 min) |
| Format (AC7) | `dotnet format WinForward.slnx --severity info --verify-no-changes --no-restore` | exit 0, empty output, 60 s |
| Inspector (AC7) | `jb inspectcode -f=Xml -e=HINT -o=/tmp/split-jb2.xml WinForward.slnx` | exit 0, **0 `<Issue>`** (1706 s; first run found the 136 redundant usings below) |

## Per-assembly counts (AC1, AC2)

| Assembly | Tests |
|---|---|
| WinForward.Core.Tests | 60 |
| WinForward.Configuration.Tests | 101 |
| WinForward.Protocols.Tests | 71 |
| WinForward.NdisApi.Tests | 74 |
| WinForward.Windows.Tests | 58 |
| WinForward.Runtime.Capture.Tests | 115 |
| WinForward.Runtime.Flow.Tests | 151 |
| WinForward.Runtime.TcpRedirect.Tests | 154 |
| WinForward.Runtime.UdpProxy.Tests | 154 |
| WinForward.Runtime.Socks5.Tests | 52 |
| WinForward.Integration.Tests | 22 |
| WinForward.Performance.Tests | 129 |
| **twelve split projects** | **1141** |
| WinForward.Analyzers.Tests (untouched, pre-existing) | 18 |
| **solution total** | **1159** |

## AC4 — content purity (`research/verify-content-purity.py`)

All 151 moved files were compared against their `32b67f7` revision with `namespace` and `using` lines
stripped. 148 are byte-identical; 3 differ, and all three are the one documented cross-project
relocation recorded in `ivt-log.md`:

| File | Difference |
|---|---|
| `tests/WinForward.Protocols.Tests/IPFragmentTests.cs` | 3 call sites requalified `TcpFragmentHandlingTests.Build*Fragment` → `FrameBuilders.Build*Fragment` |
| `tests/WinForward.Runtime.TcpRedirect.Tests/TcpFragmentHandlingTests.cs` | the two fragment builders moved out verbatim |
| `tests/WinForward.TestSupport/FrameBuilders.cs` | the same two builders moved in |

No assertion, test name, or test body changed anywhere else. The script has no allowlist, so it reports
`AC4: 3 file(s) differ`; the deviation is accepted and explained above.

## AC5 — production purity

`git diff 32b67f7 -- src/ benchmarks/` is 28 insertions and 6 deletions across 7 files, and **every one
of those lines is an `InternalsVisibleTo` declaration** (0 non-IVT changed lines).

- 28 additions = the 32 grants batch 2c derived minus the 4 the batch 4b sweep proved unproven.
- 6 deletions = the pre-existing `WinForward.Core.Tests` grants whose consumers moved to their own
  projects (batch 2c step 1 requires dropping them). `WinForward.Core`'s grant to `Core.Tests` survives.

## AC6 — reference necessity (`research/verify-references.py`)

All 13 test projects (including `WinForward.TestSupport`) report `OK`: every `WinForward.*` namespace used
in a project's `.cs` files has a matching `ProjectReference`, and every reference has a use.

## Batch 4b step 5 — IVT necessity sweep (`research/verify-ivt-necessity.py`)

39 grants checked: 29 kept on `CS0122`/`CS1061`/`CS0117` evidence after remove-and-rebuild, 8 removed as
unproven, 2 removed as dead (the friend does not reference the owning project). Per-grant verdicts are in
`research/ivt-necessity.md`; the removal list is in `ivt-log.md`.

The sweep also validates its own method: an incremental build of a friend that is not in the owner's
reference closure compiles nothing and "passes" in ~2 s, so the script computes the reference closure
first and forces `--no-incremental` on every incremental pass.

## Redundant `using` directives the inspector gate forced out

Design §4 and batch 2b deliberately left in place the usings that the namespace move made redundant:
`dotnet format` does not enforce `IDE0005` (probe-verified 2026-10-01), so the rename batches did not pay
for them. The inspector gate does — `CheckRedundantUsingDirective` reported **136**
`RedundantUsingDirective` issues across 102 test files on the first batch-4 run, and AC7 requires zero
`<Issue>`. The pre-split report (`/tmp/f5-jb2.xml`, 03:45 the same day) is empty, so every one of them is
a product of the move.

They were removed rather than suppressed: the repository's suppression policy only admits glob-scoped
entries whose evidence names the exact paths, and a tests-wide `redundant_using_directive` entry would
stop the rule from ever firing again in the tree. Each of the 136 sites lost exactly its one flagged
directive, nothing else. Rebuild: 0 warnings; full suite: 1159 passed; inspector rerun: 0 issues.

## Documented gate filter
The `hot-path.md` per-gate proof procedure used `FullyQualifiedName~WinForward.Core.Tests.$gate`, which
the split made vacuous (the gates moved to four projects). Its `totals` map now carries fully-qualified
class names and the loop filters on `FullyQualifiedName~$gate`; the updated form was verified against
`WinForward.Performance.Tests.HotPathAllocationGateTests` (11/11).
