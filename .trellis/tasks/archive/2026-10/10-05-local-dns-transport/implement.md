# Implementation plan — Local targets for UDP flows

Companion to `prd.md` and `design.md`. Ordered so that each phase is independently reviewable and
revertable; the whole change is opt-in, so no phase needs a migration.

## Phase A — rename `proxyServer` to `target` (mechanical, no behaviour change)

Land this alone: it touches ~98 sites, and keeping it separate keeps the behavioural diff readable.

- [x] A1. `RuleDto.ProxyServer` → `Target` with `[JsonPropertyName("target")]`
      (`src/WinForward.Configuration/ConfigurationModels.cs:64`); the loader recognises a
      `proxyServer` key only to emit a diagnostic naming `target` as its replacement, in the existing
      style (`ConfigurationRules.cs:68`).
- [x] A2. `FlowDecision.ProxyServerName` → `TargetName` (`src/WinForward.Core/Domain.cs:300`) and its
      readers: `FlowDispatcher.cs:203`, `:496`; `FlowAttributionPipeline.cs:339`, `:361`; the trace
      field currently logged as `proxy` (`FlowDispatcher.cs:487`) follows the rename — README's event
      table does not name that field, so the rename is a trace-only change.
- [x] A3. Examples (`examples/*.json`), `README.md`, tests, and benchmark config builders follow.
      Note the 63 `new Socks5Server(...)` sites are the *server* type and are unaffected; the rename
      only touches the rule field and the decision member.
- [x] A4. Gates: build (zero warnings) + full test run green + `dotnet format --verify-no-changes`
      empty + `jb inspectcode` zero `<Issue>`.

## Phase B — `localTargets` configuration surface

- [x] B1. `LocalTargetDto` (`name`, `host`, `port`) on `WinForwardConfigDto`; validated into a
      `LocalTarget(string Name, Endpoint Endpoint)` value (`Endpoint` is `WinForward.Core`'s
      address+port primitive).
- [x] B2. Validation, beside `ValidateServers` (`ConfigurationModels.cs:221`): `host` must parse as an
      IP literal (`IPAddress.TryParse`; not `IsValidHost`, which also admits hostnames), `port` in
      1..65535, and the name must be unique across both lists.
- [x] B3. Rule validation (`ConfigurationRules.ParseRule`): a rule whose target resolves to a local
      target is rejected unless its protocol selector is exactly UDP. A rule with **no** `protocol`
      selector matches every protocol and must be rejected too.
- [x] B4. Non-loopback target → a `warnings` entry (the channel at `ConfigurationModels.cs:106`);
      message names the address and both consequences.
- [x] B5. `ValidatedConfiguration.Servers` → `Targets`
      (`IReadOnlyDictionary<string, ProxyTarget>`) with the union type from `design.md` §3;
      `PrimeSocks5AddressCacheAsync` (`src/WinForward.Cli/UdpProxyComposer.cs:37`) iterates only the
      SOCKS5 entries, since a local target is already an IP literal.
- [x] B6. Tests in `tests/WinForward.Configuration.Tests`: cross-list name collision, non-literal
      host, TCP-matching rule rejection (with and without a protocol selector), the warning's trigger
      and text, and the legacy-key diagnostic.

## Phase C — transport seam moved and renamed (R3 owns it per the parent PRD)

- [x] C1. Move the contract out of `src/WinForward.Runtime/Socks5/Socks5UdpTransport.cs` into
      `src/WinForward.Runtime/UdpProxy/UdpTransportContracts.cs`: `IUdpProxyTransport`,
      `IUdpProxyTransportFactory`, `IUdpExchangeCounters`, `UdpAssociationLostException`, and the
      neutral receive vocabulary (`UdpTransportReceiveResult`, `UdpTransportSkipReason`,
      `UdpTransportDatagram`). `Socks5UdpTransport.cs` keeps only the SOCKS5 implementation.
- [x] C2. `Socks5UdpTransport.ReceiveAsync` maps the decoded `Socks5UdpDatagram` into
      `UdpTransportDatagram` (a `readonly record struct` copy; no allocation on the receive path).
- [x] C3. `CreateAsync(ProxyTarget, ct)`; add the composite `UdpTransportFactory` (`design.md` §4)
      and construct it in `UdpProxyComposer.Create` (`src/WinForward.Cli/UdpProxyComposer.cs:78`).
- [x] C4. Update every call site: `UdpProxyCoordinator.TrySendSpanAsync` /
      `SendAdmissionPathSpanAsync` (`UdpProxyCoordinator.Send.cs:21`, `:62`), `UdpSessionSetup`,
      `NdisPacketActionExecutor.ProxyAsync` (TCP branch asserts `target.Socks5`),
      `FlowDispatcher` warm and slow paths, `FlowAttributionPipeline`, `WinForward.TestSupport`
      fakes (`UdpTransportTestFactory.cs`), benchmark scenarios.
- [x] C5. `UdpProxyOptions.RelayReceiveBufferBytes` currently defaults to
      `Socks5UdpTransport.DefaultRelaySocketReceiveBufferSize`
      (`UdpProxyOptions.cs:29`); either keep it (it is the relay socket's own default, asserted equal
      to the configuration default at `ConfigurationLimitsTests.cs:185-186`) or point it at
      `ConfigurationLoader.DefaultUdpRelayReceiveBufferBytes`. Decide once and state it in the
      commit message.
- [x] C6. Update `.trellis/spec/backend/udp-relay.md`'s module-boundary paragraph in the same change:
      the seam now lives in UdpProxy and is implemented by more than SOCKS5.

## Phase D — the local transport

- [x] D1. `LocalUdpTransport` + `LocalUdpTransportFactory` in `src/WinForward.Runtime/UdpProxy/`,
      following `Socks5UdpTransport.Create`'s socket shape (`Socks5UdpTransport.cs:280-330`) with the
      association parts removed: per-flow socket, verbatim send from a frame-cap-sized reusable
      buffer, receive validation on exact address + port + family, oversized/malformed classified as
      skips, the flow's original destination (recorded on first send) synthesized as the declared
      reply source, self-traffic registration before the first datagram, the `SIO_UDP_CONNRESET`
      posture before bind (Windows-guarded, seam-asserted like the relay's), the receive buffer from
      `UdpProxyOptions`, and `IUdpExchangeCounters` so a completed DNS one-shot retires at 5 s.
- [x] D2. Counter and event (`RuntimeCounters`, `UdpProxyLogging`, README event table) distinguishing
      local-target flows and their failures.
- [x] D3. Tests in `tests/WinForward.Runtime.UdpProxy.Tests`: the synthesized source equals the flow's
      destination; a reply from any other source is counted by the R2 counter rather than injected
      (mirror `UdpResponseSourceMismatchTests`); two flows never share a socket (distinct local
      endpoints and registry tuples); no pool or capability interaction (a factory double asserts the
      pool is never touched); fail-closed on a send failure; one-shot retention; the `SIO` posture via
      the seam on any OS.
- [x] D4. A dispatch-level test (host or `WinForward.Integration.Tests`) that runs a UDP/53 flow
      through configuration → dispatcher → executor → coordinator with a local target, asserting the
      reply is reinjected with the original destination as source and that **no** SOCKS5 control
      connection or `UDP ASSOCIATE` happened.

## Phase E — measured column (R7)

Host scenarios: `udp.churn` and `udp.burstEstablishment` — both are association-agnostic and carry the
R1 accounting; `udp.sessionBudget` is excluded because its pooling verdict is meaningless without the
pool.

- [x] E1. `SoakOptions` (`benchmarks/WinForward.Benchmarks/Stability/SoakOptions.cs`): a new
      `--target <socks5|local>` knob (default `socks5`), parsed in the switch at `:238-288` with an
      explicit unknown-value refusal like `ParseReuseMode` (`:379-390`); being a public option
      property, it lands in `metadata.options` via `StabilityContext.cs:33`.
- [x] E2. Every row's `parameters` carries the column too (`UdpChurnScenario.cs:116`, `:185`;
      `UdpBurstScenario.cs:90-99`), per the self-description rule.
- [x] E3. A benchmark-side `LoopbackLocalUdpResponder`: a loopback UDP socket that answers each
      datagram verbatim (echo) and counts what it received — the analogue of
      `LoopbackSocks5UdpServer` with no TCP control channel, no ASSOCIATE, and no last-sender write
      path. Add a control-connection counter to `LoopbackSocks5UdpServer` so the "zero handshakes"
      claim is observed rather than asserted by construction.
- [x] E4. The local column uses the **product** `LocalUdpTransportFactory` (the column prices shipped
      code); the scenario passes a `ProxyTarget` whose `Local` is set to the responder's endpoint.
      Keep the SOCKS5 server running on the same run and report its control-connection count (must be
      0 for the local column).
- [x] E5. Run: `churn` in three columns (`--target socks5 --reuse off`, `--target socks5 --reuse
      auto`, `--target local`) plus the burst equivalent, each with `--output
      benchmarks/results/<date>-local-target/<file>.jsonl`; write that directory's `README.md`
      following `benchmarks/results/2026-10-05-udp-reuse-ownership/README.md` (dated title, task-id
      paragraph, host line, exact commands, tables, "Superseded"/"Still standing", "Series break").
      Mark superseded numbers in the older directories; do not silently replace them.
- [x] E6. `benchmarks/README.md`: usage block (`:89-103`), scenario list, and the local-target column
      described beside the reuse columns.

## Phase F — documentation and gates

- [x] F1. README: the `localTargets` list and `target` field (including the rename), the new event(s),
      the non-loopback warning, and an explicit statement that the flow's original destination is
      discarded as routing input — the local endpoint's own policy answers, and the destination is
      restored only as the reply's source.
- [x] F2. A new example, `examples/local-dns.json`, demonstrating UDP/53 pointed at a local target.
- [x] F3. Full gate run (below) on the final tree.

## Validation commands

```bash
dotnet build WinForward.slnx -c Release                     # zero warnings
dotnet test WinForward.slnx -c Release                      # green
dotnet format WinForward.slnx --severity info --verify-no-changes --no-restore   # empty output
jb inspectcode -f=Xml -e=HINT -o=/tmp/jb-inspectcode.xml WinForward.slnx         # zero <Issue>
```

Focused loops while iterating: `dotnet test tests/WinForward.Runtime.UdpProxy.Tests`,
`tests/WinForward.Configuration.Tests`, `tests/WinForward.Runtime.Socks5.Tests` (the seam move must
leave the SOCKS5 tests passing with renames only). The harness runs after the build:

```bash
dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- \
  --stability --scenario udpChurn --burst-flows 48 --churn-waves 3 --dial-delay-ms 50 \
  --target local --output benchmarks/results/<date>-local-target/churn-local.jsonl
```

## Risky files and rollback points

| Area | Why risky | Guard |
| --- | --- | --- |
| Phase A rename | ~98 sites; a missed one is a silent configuration regression | its own commit; the legacy-key diagnostic test; full build/test |
| Seam move (Phase C) | SOCKS5 is the shipped path; the receive loop and `UdpProxySession` classification are hot | SOCKS5 tests pass with renames only; no behavioural edit inside `UdpProxySession` |
| `UdpProxyOptions` buffer default | an equality test ties the two constants together | keep or move once; the test states the invariant |
| Harness results | numbers are evidence; silent replacement destroys the record | supersession blocks in the old directories, per R1's convention |
| Non-loopback targets | WinForward's own datagrams then traverse a captured adapter | self-traffic registration + per-flow alias, tested in D3 |

Rollback: revert the phase's commit; the feature is opt-in and no persisted state changes, so a
configuration that declares no local target is unaffected by any phase.

## Before `task.py start`

- [x] `prd.md`, `design.md`, and this file agree on the vocabulary (`localTargets`, `target`,
      `ProxyTarget`, `UdpTransportDatagram`) and on the acceptance criteria.
- [x] `implement.jsonl` and `check.jsonl` carry real spec entries (not the seeded placeholder).
- [x] The user has approved the final planning summary in a message after it was presented.
