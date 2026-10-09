# Quality Guidelines

> Code standards, analyzer-suppression policy, and testing requirements for backend development.
> The two gates below are what `AGENTS.md` invokes for every commit.

---

## Current Conventions

### Build and analyzers

- Build with nullable analysis, `TreatWarningsAsErrors`, and the Native AOT/trim analyzers (`Directory.Build.props`).
- Analyzer packages are pinned centrally in `Directory.Packages.props`; the WF0001–WF0004 lifetime rules apply to `src/**` only (`src/Directory.Build.props`, `analyzers/WinForward.Analyzers/`).
- **One project opts out of the four analyzer packages on purpose.** `tests/WinForward.E2E.Tests` declares `<IsTestProject>true</IsTestProject>`, which at restore time is the only thing that makes the root `Directory.Build.props` analyzer `ItemGroup` false; deleting it turns on Meziantou/Roslynator/Sonar/VSTHRD and the test sources then report 31 findings that `TreatWarningsAsErrors` turns into build errors. The measurement, the reason (NuGet restores with `ExcludeRestorePackageImports=true`, so `xunit.core.props` never runs) and the revisit trigger (clear the 31, then drop the line) are in [directory-structure.md](./directory-structure.md) and beside the property itself.
- Keep packet/ABI hot paths allocation-conscious, but preserve their explicit bounds checks and ownership guards.
- Use a localized `#pragma warning disable <RULE> // <reason>` only when the behavior is intentional; e.g. rollback cleanup must continue after one restore failure.

### Packet rewrite primitive

- `PacketChecksums.TryRewriteUdpEndpoints` / `TryRewriteTcpEndpoints` rewrite **only** IP src/dst addresses, transport src/dst ports, and the IPv4 header and transport checksums.
- They must **never** touch TCP seq/ack/flags/window/options/payload or UDP length/payload — this invariant is the bedrock of transparent local redirect (design §8 step 3), and the mutable-offset assertion in `TcpEndpointRewriteTests` enforces it byte-for-byte.
- Take `Span<byte>` in and out, bounds-check fully before any write, and reject-without-mutating on every malformed input.
- The layout-driven overload `TryRewriteTcpEndpoints(span, in PacketLayout, …)` consumes the parse's proofs: it keeps only `layout.IsTcp`, `frame.Length >= layout.TransportEnd`, and the argument-family equality. Version, IHL, protocol byte, fragment bits, total length, the IPv6 extension chain and the `dataOffset` bound are already the view's proofs.
- `IsTcp` is a validity gate, not a raw transport compare: `PacketTransport.Tcp` is `0`, so a defaulted layout would otherwise read as TCP at the IP header's offset. The span entry point stays the independent oracle.

### Checksums and fragments

- TCP has **no** optional-zero-checksum provision (RFC 9293): `WriteTcpChecksum` stores the folded result verbatim and never inverts a computed `0x0000` to `0xFFFF`.
- This deliberately diverges from `WriteUdpChecksum`, which **must** invert `0`→`0xFFFF` per RFC 768. Do not unify the two helpers behind a flag — the divergence is load-bearing.
- IPv4 fragment rejection uses mask `0xbfff` (reserved bit + MF + fragment offset; DF allowed) in both the canonical `IPTcpUdpPacket` parser and the UDP-only `IPUdpPacket` parser. A new transport rewrite mirrors the canonical parser for that transport.

### UDP and TCP proxy coordinators

- `UdpProxyCoordinator` and `TcpProxyCoordinator` mirror one structural shape: a flow-keyed session dict + lock + shutdown CTS + capacity + `DisposeAsync` teardown.
- The UDP side decomposes that shape into `UdpSetupCooldownTable` (setup-failure cooldowns, leaf lock), `UdpSetupQueueBudget` (global setup byte budget, `Interlocked`-only), `UdpSessionSetup` (dial/claim/construct/flush pipeline behind the `IUdpSessionSlotHost` seam), and `UdpProxyLog` (static event formatting) — mirroring the TCP store/setup/acceptor split.
- Coordinator slot state is touched only through the slot host's gate-taking operations; `UdpSessionSlot` is an opaque handle to `UdpSessionSetup`.
- The coordinator gate stays the single gate for slot state, with one exception: the *ready* send path reads a pre-allocated direct-mapped session cache validated by the slot's own `Session.Flow` and by the volatile `Ready`/`Session` pair published under the gate, so it takes no gate entry at all.
- `_sessions`, every mutation (admission, removal, dispose), and every slot-dictionary read (`SessionCount`, `SessionState`, `SessionForDiagnostics`) still run under `_gate`.
- Terminology: a TCP "tombstone" is TIME_WAIT grace; the UDP 1 s setup-failure window is a "setup cooldown" (tombstone is retired from UDP).
- A flow is claimed exactly once in its association table; a retransmitted or duplicate initial packet reuses the existing association (touch + re-inject) and never allocates a second listener or transport.
- Every setup, rewrite, injection, or relay failure fails closed and releases exactly the resources acquired so far, in acquisition order — a proxy-selected flow is never silently passed (design §8, R8).

### Teardown

- Coordinator teardown is single-flight: the first `DisposeAsync` marks the coordinator closed, cancels shared setup, snapshots and releases owned sessions, and all concurrent disposal callers await that same cleanup task.
- UDP expresses that as an explicit one-shot claim (`Interlocked.Exchange`) plus a join on `QuiescenceScope.DrainAsync()`; TCP memoizes one `_disposeTask` under `_disposeGate` instead. Either way the claim guarantees the owner teardown body runs **once**, but a later caller joins only the drain: it does not observe a fault the claimant's own teardown throws and may return before the claimant's own awaits finish (`async-lifetime.md` D11).
- New sends after teardown begins fail with `ObjectDisposedException`, and a canceled waiter never cancels shared setup owned by other callers.
- Capture composition owns the proxy coordinators inside the capture-loop disposal boundary: stop the sweeper and capture pumps, then dispose the UDP and TCP sessions, and only afterward restore adapter modes. An active `StopAsync` must cancel and await the capture run before releasing those resources.

### Flow claim races

- Concurrent initial packets for the same flow race through the coordinator's pre-claim fast path (resolve-by-original returns empty for all racers before any `TryClaim` runs). The association table's `TryClaim` is the single exactly-once arbiter: later racers receive the existing association, not a new one.
- The coordinator **must** detect that (compare the returned association's translated tuple to the just-allocated listener's) and release the redundant listener, falling back to the re-inject path; failing to do so causes a duplicate-key crash on the session dict.
- Expose the concurrent-loser counter (`TcpRedirectDiagnostics.ConcurrentLoserCount`, asserted in `Diagnostics`) so a test can deterministically assert the defense branch fired.
- When testing proxy-coordinator concurrency, gate the allocation seam on a barrier or `TaskCompletionSource` so all racers provably pass the fast path before any claim lands, and assert through an instrumented counter rather than relying on scheduler interleaving.
- A `Task.Yield()`-only fake is scheduler-dependent and **non-load-bearing** — it does not reliably reproduce the race.
- R8 moved new-flow setup off the pump path, so the redirect-table exactly-once race is unreachable through the coordinator: every burst caller returns `SetupPending` immediately, and the pending index absorbs retransmissions (overwrite, never a second setup task) while the one background setup is parked in the factory.
- `ConcurrentSynBurstWhileListenerSetupIsParkedIsAbsorbedIntoOneSetup` pins exactly-once by construction and asserts `Diagnostics.ConcurrentLoserCount == 0`; the old barrier test that forced the race is gone, and `PreClaimedAssociationReinjectsAgainstExistingClaim` covers the loser branch instead.

### Association tables and the warm path

- `UdpAssociationTable` and `TcpRedirectTable` use a single instance `_gate` lock for **all** mutating methods and for every read that consults the `Dictionary` authorities, including internal lookup helpers.
- Do not lock the `Dictionary` object itself in a helper while other methods lock `_gate` — that diverges from the reference, breaks the documented single-gate contract, and risks a lock-ordering deadlock if a method holding one lock calls another that acquires the other.
- Warm-probe exception: `TcpRedirectTable`'s per-packet probes (`TryResolveByReverse`, `TryResolveByOriginal`) may serve a **validated** entry from a pre-allocated direct-mapped cache before taking `_gate`; an unpopulated slot or a collision still takes `_gate` and consults the authority, so those are false misses only.
- The validated field pairs are get-only and set in the constructor, which is what makes a served entry exact — it is the association the slot named, never a different one.
- A **stale** entry (one a removal has not cleared yet) is served, not missed: a probe that loaded the slot before the removal's `ReferenceEquals`-guarded clear can still return that association — the same one-in-flight-probe window the flow table's warm cache documents in [warm-path-dispatch.md](./warm-path-dispatch.md). The guarantee is therefore "exact, or one in-flight probe old", not miss-only.
- Every mutation — including the factored `RemoveUnderGate` used by `TryRemove` and both `RemoveExpired` bodies — still runs under `_gate` and clears its cache entries with a `ReferenceEquals` guard. `TryResolveByAddressPair` (the fragment path) stays gated.

### Flow lookup activity

- `FlowTable.TryResolve` is the packet-observation lookup: every successful exact, reverse, origin-flipped, or adapter-agnostic resolution **must** refresh activity before returning, so active flows cannot expire at their pre-lookup deadline.
- The refresh is one volatile bucket store (`TouchBucket(clock.Current)`) that `TryResolveWarm` performs only **after** its snapshot and tuple corroboration succeed — a rejected snapshot (torn read, recycled state, collision) defers the refresh to the slow path, which re-resolves under the gate and touches there.
- The invariant is unchanged: a resolution that is actually served refreshes activity.

### Boundary constructors and parsers

- Be intentional about null inputs: `Endpoint.From(IPAddress, ushort)` rejects a null address with `ArgumentNullException`, while `IPPrefix.TryParse(string?, out IPPrefix)` returns `false` without throwing.
- Configuration validation turns null JSON array elements into indexed diagnostics rather than allowing a `NullReferenceException`.
- A null-valued member of a non-null struct parameter is reported at the member path, not the struct: every capture-boundary lease guard routes through `CapturedFlowPacketGuards.ThrowLeaseRequired()`, which throws `ArgumentNullException` with `ParamName = "packet.Lease"`.
- Analyzers reject member-path `paramName`s (MA0015/S3928), so that throw is centralized under scoped, documented suppressions instead of per-site pragmas.

### Policy matching

- Normalized remote-port intervals are sorted by start/end and merged when overlapping or adjacent; downstream rule matching receives the canonical disjoint interval list, never user ordering or duplicate ranges.
- Process selectors are exact-or-directory: a selector without a directory separator matches an executable filename, while one containing `/` or `\` matches a normalized full path exactly and also matches every program in that directory or any subdirectory below it (directory prefix + `\` boundary, case-insensitive; see `ProcessSelectorMatcher`).
- UDP routing is keyed by the original local/remote endpoint tuple plus protocol, address family, and origin context. PID and DNS transaction IDs are metadata/payload, never association keys.
- TCP reverse routing is keyed by its full pre-rewrite wire tuple (`client-address:proxy-port` to `server-address:original-client-port`), never a listener port alone.
- A reverse probe that is not an exact association must not touch activity or rewrite the frame, and a caller cancellation on an existing TCP retransmit/rewrite must not tear down the shared redirect association.

### Structural refactors

- Structural refactors are behavior-zero and gate on the test baseline: record the totals with `dotnet test WinForward.slnx -c Release` at task start, then hold the solution total **and** each project's own count through the move (1,660 across thirteen test assemblies on 2026-10-09).
- Prefer mechanical line-range moves (script-assisted) over retyping, and never modify assertion semantics while moving tests.
- Additive regression tests raise the baseline by exactly their count, and every batch commits on a zero-warning build.
- Run the gates in **Release** (`dotnet build -c Release` / `dotnet test -c Release`): the `HotPathAllocationGateTests` zero-allocation gates are exact only in Release and fail deterministically in Debug. See [directory-structure.md](./directory-structure.md) for file-size and split conventions.

### Deep modules

- Optional dependencies arrive as one public options record (`TcpRedirectOptions`, `UdpProxyOptions`, `NdisCapturePumpOptions`) — production knobs public, test seams `internal init` reached through `InternalsVisibleTo` (NdisApi friend-lists the capture and benchmark assemblies in `NdisApiAbi.cs`) — with create-if-absent ownership expressed in the options semantics.
- Read-only metric clusters consolidate into one `internal` diagnostics snapshot per module (`TcpRedirectDiagnostics`, `UdpProxyDiagnostics`, `NdisPumpDiagnostics`); surfaces with production consumers (`HoldsFlow` as the sweeper's hold predicate) or mutation duties (`Tombstones`, `CapacityResetCooldowns`) stay direct on the type.
- `SetupExecutor.RentItem(handler)` requires the pipeline at rent time, so a handler-less item is unrepresentable.
- Cli coordinator wiring lives in `TcpRedirectComposer` / `UdpProxyComposer`, and the composition records carry the bundle-created pools; `DurableCaptureBundle` keeps creation, registration, rollback, and ordered teardown.
- The config's `0 = auto` worker sentinel is translated to `null` only at the single bundle call site.
- Ownership is expressed in the constructor signature, not in boolean flags or create-if-null branches — a coordinator that cannot run without a pool must fail construction, not fabricate one.
- Durable capture ownership: `DurableCaptureBundle` (Cli) creates and disposes the native buffer pools and the **single shared** `SetupExecutor`; both coordinators take them as required non-null constructor dependencies and never dispose injected collaborators.
- `DisposeAsync` on a coordinator or pump quiesces only its own background work (awaits the tasks it started) and disposes only internally constructed state; there is no global task registry — the quiescence boundary is an ownership-await tree.

### Hard invariant linkages

- `FlowKey` and the flow table's `TransportTuple` hash through the shared `FlowHash.CombinePacked`, which `Combine` (materialized), `CombineCanonical`, and `CombineCanonicalPacked` (order-independent) all delegate to: one transport-only field set backs origin-aware equality and the orientation-agnostic index — collisions are fine, divergence is not.
- The key is packed to 64 B and the tuple to 48 B with the same field set, and the slot function reads the packed form.
- `SetupExecutor.DefaultRingCapacity >= TcpPendingSynSetupIndex.DefaultCapacity + FlowAttributionPendingIndex.DefaultCapacity` is an asserted inequality, not a "matches" comment: a larger ring is always safe, a smaller one can reject setup under load.
- Clocks for expiry-class reads flow only through the injected `TimeProvider` (default `TimeProvider.System`): `FlowTable`, `UdpSetupQueueBudget`, the whole TCP family (`TcpRedirectOptions.TimeProvider` threaded into the coordinator, the session store's tombstone write, the setup claim, and the capacity-reset cooldown), and `IdleExpirySweeper`'s sweep tick and log throttle.
- A `MutableTimeProvider` regression pins the `HoldsFlow` grace boundary deterministically.

### Field naming

- The [.editorconfig](../../../.editorconfig) naming rules match on **declared** accessibility: `public`/`protected` (including `protected internal`) fields are PascalCase — public fields are allowed where performance demands (interop layout structs, hot-path state containers) and stay PascalCase even on non-public types.
- `internal`/`private`/`private protected` static fields use the `s_` + camelCase pattern; their instance fields use the `_` + camelCase pattern.
- `private`/`internal const` stays PascalCase while local consts are camelCase.
- A `[ThreadStatic]` field uses the `t_` marker with a localized `#pragma warning disable IDE1006` (naming rules cannot match attributes); the pragma and its reason sit at the declaration (`PacketRuntime.cs`, `PacketPathProbe.cs` — the only two such fields in the tree).
- `dotnet format` cannot apply IDE1006 renames (`NamingStyleCodeFixProvider` has no Fix All in Solution) — rename via a reviewed script or the IDE and verify with `dotnet format style --diagnostics IDE1006`.

### Analyzer suppression policy — the `dotnet format` gate

- The format gate is the repo's zero-diagnostic bar: `dotnet format WinForward.slnx --severity info --verify-no-changes --no-restore` must exit 0 with empty output before commit, alongside the `-c Release` zero-warning build and a green `-c Release` test run (`AGENTS.md`). Those two are **local-only**: `.github/workflows/analyzer-gate.yml` runs `dotnet format` and `jb inspectcode`, and `.github/workflows/release-build.yml` publishes `src/WinForward.Cli`; neither runs `dotnet build` or `dotnet test`, so the 1,660-test suite has no CI enforcement point yet (registered as its own task, not a silent gap).
- Every suppression — an inline `#pragma warning disable <RULE>` with a reason, or an `.editorconfig` `severity = none` with a comment — must state a reason verifiable against **this** repository. Never inherit text from another codebase: no foreign type names, hosts, or workflows.
- Scope `.editorconfig` entries with globs to exactly the paths the evidence covers (`[tests/**.cs]`, `[tests/<project>/TestHelpers/**.cs]`, …). Never suppress globally on a "the rest of the tree currently has none" rationale — future code in those paths must still hit the rule.
- Fixers run batched and may need a second invocation to converge; always build the Release gates after a fixer pass.
- Note analyzer default severities before assuming a rule is build-breaking: many Meziantou/Roslynator/CA rules default to Info even when the family looks like warnings, so a re-enable experiment can trip `TreatWarningsAsErrors` only for genuinely warning-level rules.

#### Re-verifying that a suppression is still necessary

- Flip all `severity = none` entries on (or turn the pragmas into `restore`), run `dotnet format --severity info --verify-no-changes --report` once, and judge each entry against its measured hit count and samples.
- Delete entries with zero hits and no plausible trigger pattern; rewrite reasons that do not match this repo; keep warning-default guards whose documented trigger pattern is merely unexercised.
- The 2026-09-19 audit behind these rules is `.trellis/tasks/archive/2026-09/09-19-src-analyzer-cleanup/research/suppression-audit.md`.

### Analyzer suppression policy — the JetBrains gate

- `jb inspectcode -f=Xml -e=HINT -o=<path> WinForward.slnx` must report **zero** `<Issue>` entries before commit. It exits 0 even with findings, so parse the XML and never trust the exit code (`AGENTS.md`; `.github/workflows/analyzer-gate.yml`).
- The tool is pinned at 2026.1.3 by the CI job's `JetBrains.ReSharper.GlobalTools` install; a local full run takes ~10–20 minutes.
- Rule-level suppression uses glob-scoped `.editorconfig` `resharper_<inspection_id>_highlighting = none` entries; site-level suppression uses `// ReSharper disable once <InspectionId> // <reason>`. Every suppression carries a repo-verifiable reason and lands in the task's suppression inventory for the audit.

#### jb false positives that must not be "fixed"

- `MemberCanBePrivate` on members consumed by friend assemblies via `InternalsVisibleTo` — private-izing breaks the friend consumers (the build gate catches it); e.g. `PacketChecksums.TryRewriteUdpEndpoints(IPAddress…)` is the tests' protocol oracle.
- `UnusedAutoPropertyAccessor` on BenchmarkDotNet `[Params]` properties — BDN injects the setters by reflection.
- `NotAccessedPositionalProperty` on record positional properties — they carry synthesized equality/hash and serialization semantics even when never read directly.
- Raw `[LibraryImport]` ABI wrappers that merely look unused.

#### Cross-tool conflicts

- Resolve conflicts in favor of the enforced gate with a measured-overlap note: jb `ArrangeRedundantParentheses` and the enforced RCS1123 disagree on the same tokens (`4 + (2 * 8)`), so RCS1123 wins and jb's rule is suppressed at rule level with the 32/37 evidence recorded in `.editorconfig`.
- Suppress style-only inspections whose direction contradicts de facto style the same way: `ArrangeObjectCreationWhenTypeNotEvident` (the repo uses target-typed `new`), the LINQ-conversion family (performance posture — loops stay explicit), and `InvertIf` (adopted at only 4 of 50 sites; explicit nesting is preferred where inversion hurts the guard-clause read order — the rule-level rationale in `.editorconfig` records the adoption/rejection statistics).

#### jb cache hygiene

- The analyzer cache (`~/.local/share/JetBrains/`, `/tmp/JB`) can be poisoned by runs interleaved with rapid edit cycles, after which it reports `CSharpErrors` and cascading unused-member findings against a tree that builds cleanly in both configurations.
- Treat "green build + jb `CSharpErrors`" as cache corruption, not a code defect: clear those directories and rerun before acting on the report.

#### Re-verifying that a jb suppression is still necessary

- Prove necessity in an isolated worktree by neutralizing each directive and rerunning the full analysis — but for jb **delete the directive line entirely**.
- Never blank it to a same-length placeholder or merely comment it out: some inspections (`DuplicatedSequentialIfBodies` was observed) flip their verdict based on adjacent comment lines, so a text-prefix neutralization keeps the rule quiet and yields a false STALE verdict, while a rule that reappears only once the surrounding comment is removed is genuinely NECESSARY.
- The audit's completeness proof is a conservation identity: total neutralized hits = rule-level-key hits + pragma-site hits, with zero unexplained findings.
- A rule-level `severity = none` makes every site pragma for that rule redundant — remove them (Roslyn-domain pragmas are invisible to jb anyway), leaving the rule-level entry as the single suppression boundary (MA0038 precedent). The 2026-09-20 audit behind these rules is `.trellis/tasks/archive/2026-09/09-20-jb-inspectcode-cleanup/research/suppression-audit.md`.

---

## Testing Requirements

### Ownership and concurrency

- Every flow or association ownership change needs a regression for same-key reuse, distinct-key isolation, and deterministic collision/failure behavior.
- Concurrency tests must use genuinely overlapping tasks, not only sequential repeated calls.
- Lifecycle tests must assert coordinator disposal precedes mode restoration on normal completion, capture failure, and concurrent stop, using an ordered event seam rather than scheduler timing.
- Pure tests run on any host; NDISAPI and Windows attribution tests must remain behind ABI/platform seams.

### Packet rewrite

- Validate checksums with INDEPENDENTLY reimplemented `Sum`/`Finish` helpers (`ChecksumMath` in `tests/WinForward.TestSupport/`), never the production routines under test.
- Assert only the expected mutable bytes changed, via an explicit offset set, and prove a round-trip rewrite-back-to-original is byte-identical.
- Assert every reject path leaves the input span unchanged.
- The layout-driven overload gets the same discipline: `ViewDrivenRewriterRejectsWhatTheSpanRewriterRejects` drives every truncation of an IPv4/IPv6 TCP frame plus a cross-family call through both entry points and asserts identical decisions, byte-identical output for the accepted ones and an unmutated span for the rejected ones.
- `DefaultedLayoutIsRefusedByteIdenticallyRatherThanRewritten` requires the defaulted layout (`PacketTransport.Tcp == 0`) to be refused without touching the frame.

### Flow lookup and configuration

- Verify that an observation refreshes activity before an idle-expiry boundary (the bucket form: a stamp in the current bucket is retained until the cutoff bucket passes it).
- Preserve a non-observing lookup test where applicable: `TcpProxyCoordinatorRewriteTests.NonExactReverseProbeDoesNotRefreshActivity` is the reverse-probe example, with its timestamp bucket-aligned.
- Configuration tests must cover null DTO array entries, merged adjacent/overlapping port ranges, unknown JSON field paths, and paired credential limits at both the 255-byte accepted and 256-byte rejected UTF-8 boundaries.

### Native buffer balance

- Every native buffer rent site ships a balance regression: rent N → return N → dispose → assert the pool reports `InPool == N`, `Rented == Returned`, `Outstanding == 0`.
- An overflow or exception path that rents must additionally prove the buffer is still returned (the L1 pattern).
- Add a dispose-race test where returns overlap `Dispose`, and prove no buffer is stranded or double-freed. See [native-lease-and-pool-lifetime.md](./native-lease-and-pool-lifetime.md).

### Allocations

- Steady-state paths must not materialize managed copies: no `ToArray()`, no `new byte[]`, and no fresh `EndPoint` handed to a socket send overload (serialize the destination once and cache the `SocketAddress`).
- An allocation gate that uses a fake collaborator cannot see a trap inside the real one — for any path whose real collaborator can allocate, include at least one gate against the real collaborator (loopback) or a dedicated regression test. See [allocation-gates.md](./allocation-gates.md) → "Allocation-gate stability".

---

## Scenario: Bounded Pooled SOCKS5 UDP Receive Storage

### Contracts

- Scope: the UDP relay's bounded pooled receive storage. Change this contract when you touch that storage, SOCKS5 UDP decoding, response reinjection, or the pinned NDIS maximum frame size.
- `UdpProxyCoordinator.ReceiveWindowSize(maximumFrameSize)` sizes one window (`cap + 22 + 1`) and `UdpProxyCoordinator.ReceiveWindowPoolCapacity(sessionCapacity)` sizes the shared pool as `sessionCapacity + ReceiveWindowRetireHeadroom(sessionCapacity)`, with the allowance `max(ReceiveWindowRetireFloor = 64, sessionCapacity / 16)`. `DurableCaptureBundle` passes both, so the pool and the session window can never disagree.
- Composition passes the same pinned `maximumFrameSize` (`UdpProxyOptions.MaximumFrameSize`, defaulting to `UdpFrameBuilder.DefaultMaximumEthernetFrame`) to the coordinator and to `UdpResponseReinjector`, which owns the rebuilt-frame bound. A relay response that cannot fit the reinjection cap must never be accepted into a larger independent receive contract.
- Per active UDP session, rent one buffer sized `maximumFrameSize + 22 + 1`: maximum Ethernet frame, maximum SOCKS5 UDP header (`MaximumSocks5UdpHeaderSize`), and one oversize sentinel byte.
- The receive loop owns the rented array for its full lifetime and returns it exactly once in `finally` — including cancellation, socket disposal, malformed input, immediate receive failure, and normal session teardown.
- Decoded payloads are owner-bound `ReadOnlyMemory<byte>` slices that must be consumed before the next receive or buffer return, and must not escape the awaited response-sink call.
- `Socks5UdpTransport.ReceiveAsync(Memory<byte>, CancellationToken)` returns a discriminated `UdpTransportReceiveResult` (the transport-neutral seam vocabulary owned by `WinForward.Runtime.UdpProxy`): a datagram, or a skip with reason `UnexpectedSource` / `Oversized` / `Malformed` / `ConnectionReset`. A receive whose byte count fills the supplied buffer reports the `Oversized` skip instead of a datagram.
- Per-datagram anomalies skip, never tear down: the session receive loop counts skips, emits a per-session rate-limited (>= 5 s) summary log, and continues; only socket-level exceptions (`SocketException`, `ObjectDisposedException`, shutdown cancellation) are fatal through `_receiveFailure`, and per-response sink (`InjectAsync`) failures are isolated per response.
- A single >= 1537 B or malformed relay datagram must NOT stop response delivery for the flow.

#### The shared receive-window pool is a bound, not a preallocation

- Size the pool from the configured session capacity, and never make it *exactly* that capacity.
- One lease per live session is the population, because the receive loop rents one window for its whole life and returns it in `finally`; the retire allowance covers the admit-while-retiring overlap, which has no in-code bound — slot removal happens under the gate and only then awaits session disposal outside it, while admission is gated on the slot count, and receive-failure teardowns are concurrent scope children that never take the sweep gate.
- A capacity of exactly `udpSessionCapacity` would therefore re-enter the tracked-overflow path under the very event the pool exists to smooth.
- A first fill of N sessions overflows N times at *any* capacity, so the sizing proof is **zero growth across a second population cycle** (`UdpReceiveWindowPoolTests`), never an absolute `OverflowAllocations == 0`. The recorded residual: a fault storm larger than the allowance still allocates fresh leases transiently and self-correctingly.

### Validation & Error Matrix

| Condition | Required result |
| --- | --- |
| `maximumFrameSize <= 0` | Constructor throws `ArgumentOutOfRangeException` |
| Receive result is smaller than the sentinel window and decodes successfully | Await the response sink before reusing the buffer |
| Receive result fills the sentinel window | Skip (`Oversized`); do not decode or reinject; do NOT tear down the session |
| Malformed SOCKS5 UDP header/payload | Skip (`Malformed`); rate-limited summary log; session survives |
| Datagram source port/family does not match the relay | Skip (`UnexpectedSource`); session survives |
| Socket-level receive failure / disposal / shutdown cancellation | Fatal: `_receiveFailure` -> session teardown (existing path) |
| Response sink (`InjectAsync`) throws non-cancellation | Rate-limited warn, skip that one response, loop continues |
| Receive, decode, sink, cancellation, or disposal exits the loop | Return the rented array exactly once |
| Rebuilt Ethernet frame exceeds the same pinned cap | Drop fail-closed (rate-limited log) without native injection |

### Tests Required

- Assert coordinator and reinjector receive the same non-default frame cap from composition.
- Assert the pool's sizing rule (`ReceiveWindowPoolCapacity(cap) == cap + ReceiveWindowRetireHeadroom(cap)`, the allowance bands, and that it is never exactly `cap`) and its behavioural proof: a pool built from the rule shows zero overflow growth across a second population cycle, with the balance identity (`Rented == Returned`, `Outstanding == 0`, `InPool == population`) asserted after every lease is released (`UdpReceiveWindowPoolTests`).
- Inject a tracking `ArrayPool<byte>` and assert one rent/one return after normal shutdown and immediate receive failure.
- Assert a receive that fills the sentinel window is rejected before decode/sink invocation.
- Preserve the malformed-datagram, oversized rebuilt frame, host/forwarded reinjection direction, client-MAC, cancellation, expiry, and coordinator single-flight disposal tests.

### Good / Base / Bad Cases

- Good: a 1514-byte frame cap rents a 1537-byte receive window; a valid smaller relay datagram is decoded as a view, awaited through the sink, then the buffer is reused.
- Base: cancellation or socket disposal ends the receive loop and returns the pool rental.
- Bad: rent 65,535 bytes per session, or accept `receivedBytes == buffer.Length` as complete; both defeat the memory bound and can silently process a truncated datagram.
- `receivedBytes >= buffer.Length` means the datagram may be truncated — it is a **skip**, not a session failure (superseded 2026-08-28, task 08-28-udp-loss-design-flaws D2).

### Wrong vs Correct

```csharp
// Wrong: independent unbounded receive storage can retain ~64 KiB per session
// and gives no reliable truncation signal.
var buffer = new byte[65_535];
var received = await transport.ReceiveAsync(buffer, cancellationToken);

// Correct: share the pinned frame cap, reserve one sentinel byte, and always return the rental.
var buffer = pool.Rent(maximumFrameSize + MaximumSocks5UdpHeaderSize + 1);
try
{
    var received = await transport.ReceiveAsync(buffer.AsMemory(0, receiveBufferSize), cancellationToken);
    await sink.InjectAsync(flow, source, received.Payload, clientMac, cancellationToken);
}
finally
{
    pool.Return(buffer);
}
```
