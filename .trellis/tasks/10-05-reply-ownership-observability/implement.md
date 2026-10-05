# Implement — reply-ownership observability

## 1. Counter

`src/WinForward.Runtime/RuntimeCounters.cs`: add beside the other UDP members (`:20-23`, `:62-80`),
following the existing one-line `<summary>` shape that names the event:

```csharp
/// <summary>A UDP relay response declared a source other than the flow's own destination; see <c>udp.response.foreign_source</c>.</summary>
public const string UdpResponseSourceMismatch = "udpResponseSourceMismatch";
```

No registration step exists — `Snapshot()` picks up any key that has been incremented.

## 2. Check in the receive classification

`src/WinForward.Runtime/UdpProxy/UdpProxySession.cs`:

- add a `private long _lastForeignSourceLogTicks` field beside `_lastSkipSummaryTicks`
- in `TryGetReceiveSource`, after `source = Endpoint.From(address, response.DestinationPort)` (`:412`),
  compare `source` against `Flow.Remote`; on mismatch call a new `RecordForeignSource(source)` and then
  **return `true` exactly as today**, so the reply stays deliverable
- `RecordForeignSource` increments `RuntimeCounters.Shared` and emits the throttled warn; it touches
  nothing else — no session state, no counters that the skip path drains
- mirror the throttle shape of `MaybeLogSkipSummary` (`:472-479`): read the ticks, compare against the
  interval, CAS, and only then log

Do **not** add a member to `Socks5UdpReceiveSkipReason` and do **not** call `RecordSkippedDatagram`: that
path means "not delivered", and reusing it would turn this observation into a drop.

`Flow.Remote` is the flow key's own destination. Do not substitute the SOCKS5 request target — it is the
same value today, but not by contract.

## 3. Tests

The fake-transport harness lives in `tests/WinForward.Runtime.UdpProxy.Tests`
(`UdpRelayTests.cs`, `UdpReceiveResilienceTests.cs`, with `FakeResponseSink.Responses` for assertions).
Add a focused file:

- a reply whose declared source differs from the flow's destination → the counter advances and the reply
  is still injected (assert through `FakeResponseSink.Responses`)
- a matching reply → the counter does not move
- several mismatches inside one window → the counter advances once per reply and exactly one log line is
  emitted
- two flows to the same destination with a cross-delivered reply → the counter does not move; this pins
  the documented blind spot so a future reader cannot mistake the counter for a complete check
- read the counter through `RuntimeCounters.Get`/`Snapshot`, and restore any shared state in the test's
  teardown so parallel tests cannot see each other's increments

`tests/WinForward.Performance.Tests/HotPathAllocationGateTests.cs` must stay green: the comparison adds no
allocation, and a regression there is the signal that the check drifted onto the packet path's budget.

## 4. Documentation

- `README.md`: list the counter with the other diagnostics and add one sentence that a zero does not prove
  association sharing is safe for the reader's traffic.
- `.trellis/spec/backend/udp-relay.md`: the receive-classification section gains the check, the blind
  spot, and the fact that the observation is deliberately non-dispositional.

## Validation commands

| Gate | Command |
| --- | --- |
| build | `dotnet build WinForward.slnx -c Release` (zero warnings) |
| test | `dotnet test WinForward.slnx -c Release` (green) |
| format | `dotnet format WinForward.slnx --severity info --verify-no-changes --no-restore` (empty output) |
| inspector | `jb inspectcode -f=Xml -e=HINT -o=/tmp/jb-inspectcode.xml WinForward.slnx` (zero `<Issue>`; parse the XML, do not trust the exit code) |

Format and inspector are slow; run them once before reporting, not after every edit.

## Risky points

- **The check must not become a filter.** The only acceptable shape is "count, then deliver". A reviewer
  should be able to see from `TryGetReceiveSource` alone that the return value is unaffected.
- **Do not reuse the skip counters or their windowed semantics.** `MaybeLogSkipSummary` drains its
  counters into a summary line; this counter is a lifetime total.
- **Domain-typed responses** have no address and keep skipping as today
  (`RecordSkippedDomainDestination`, `:466`); the mismatch check must not change that path.
- **Test isolation**: `RuntimeCounters.Shared` is process-wide, so a test that increments without
  accounting for other tests will flake. Read the counter as a before/after delta.

## Rollback points

A single commit touching one counter constant, one session file, tests and docs. Reverting restores the
previous behaviour exactly.
