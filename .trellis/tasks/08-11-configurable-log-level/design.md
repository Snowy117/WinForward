# Configurable Runtime Logging Design

## 1. Scope and Boundaries

This feature spans configuration, runtime logging, capture/dispatch, TCP and UDP proxy coordination, and CLI output. It remains an in-process foreground-console facility. It does not add a third-party logging package, file sink, payload capture, hot reload, or policy changes.

Data flow:

```text
config.json logLevel
  -> WinForwardConfigDto
  -> ConfigurationLoader validation/defaulting
  -> ValidatedConfiguration.LogLevel
  -> ConsoleRuntimeLogger threshold
  -> runtime components emit operational or diagnostic events
  -> stderr single-line output
```

## 2. Configuration Contract

Add top-level optional `logLevel` to `WinForwardConfigDto` and a strongly typed `RuntimeLogLevel` to the validated configuration contract. Accepted strings are case-insensitive `error`, `warn`, `info`, `debug`, and `trace`; surrounding whitespace is normalized. Omission selects `info`. Null, blank, incorrectly typed, and unknown values fail through the existing parse/validation diagnostics at `logLevel`.

The enum belongs in `WinForward.Configuration` because configuration owns accepted values and Runtime already references Configuration. No reverse dependency is introduced.

## 3. Logger Contract

Extend the project-owned runtime logger rather than adopting `Microsoft.Extensions.Logging`: the current program is small, Native AOT enabled, and requires only one console sink and deterministic filtering.

`IRuntimeLogger` exposes:

- `bool IsEnabled(RuntimeLogLevel level)` for guarding high-frequency event construction.
- One severity-aware write operation, or equivalent `Trace`, `Debug`, `Info`, `Warn`, and `Error` methods. Existing call sites remain semantically unchanged.
- A structured diagnostic event operation whose formatter owns event names, key ordering, quoting, escaping, null omission, endpoint formatting, and invariant numeric/date rendering.

`NullRuntimeLogger` disables all levels. `ConsoleRuntimeLogger` compares event severity against the configured threshold and writes atomically as one line to `Console.Error`:

```text
[trace] packet.completed packet=42 flow=7 disposition=pass stage=reinject direction=adapter
```

Stable lowercase event names and keys are contracts covered by focused tests. Values containing whitespace, quotes, equals signs, or control characters are quoted and escaped centrally. IPv6 endpoints are bracketed. All formatting is culture-invariant.

## 4. Correlation Model

`CapturePacketProcessor` allocates a monotonically increasing runtime `packet` sequence and carries it in `CapturedFlowPacket`. Zero remains valid for directly constructed test packets and means unspecified.

`FlowTable` already assigns a monotonic `FlowState.Generation`. After a flow resolves or is created, `FlowDispatcher` carries that generation into the packet passed to executors/coordinators as `flow`. Reverse lookups preserve the same generation. Non-flow and self-owned traffic use `packet` without `flow`.

TCP redirect and UDP association tables have their own generations, but diagnostic records retain the dispatcher flow ID as the primary correlation ID. Component-local association IDs may be emitted as `tcpAssociation` or `udpAssociation` where useful; they never replace `flow`.

IDs are unique only within one process run. Startup emits the selected level, making run boundaries visible without inventing persisted IDs.

## 5. Event Coverage

### Operational levels

- `error`: startup/config-independent fatal failures and runtime termination failures.
- `warn`: recoverable faults, fail-closed drops, capacity/collision failures, degraded adapter/MAC conditions, and teardown failures.
- `info`: existing capture scope and lifecycle messages plus selected log level at startup.

### Debug level: logical-flow lifecycle

- `flow.created`: normalized flow tuple, origin, adapter, attributed process, rule index or fallback, action, and proxy server name.
- `flow.expired`: flow ID/count and reason where the owning table exposes it without expanding public policy behavior.
- `tcp.redirect.created`, `tcp.relay.started`, `tcp.relay.ended`, `tcp.redirect.closed`: flow and association correlation, translated endpoint, proxy server name, outcome/reason.
- `udp.session.created`, `udp.session.expired`, `udp.session.closed`: flow/association correlation, relay metadata, proxy server name, outcome/reason.
- SOCKS5 setup milestones may identify command, configured server name, selected server endpoint, authentication method (`none` or `usernamePassword`), and outcome. They never include username/password or encoded authentication bytes.

Debug does not emit one event for every packet. Existing-flow packet processing remains trace-only.

### Trace level: per-packet path

- `packet.captured`: packet ID, byte length, adapter ID/name, adapter handle, NDIS direction and flags.
- `packet.classified`: TCP/UDP tuple and origin, or `kind=nonFlow` with the available classification reason/category when parsers expose it.
- `packet.selfTraffic`: bypass outcome when loop prevention owns the flow.
- `packet.flowResolved`: flow ID and whether an existing state or new claim supplied the decision.
- `packet.reverseHandled`: TCP redirect or UDP response path and outcome.
- `packet.action`: pass/block/proxy and rule/proxy metadata.
- Proxy/coordinator stage events: parse, redirect/association reuse or setup, relay send/receive, rewrite and reinjection outcomes. Byte counts are allowed; bytes are not.
- `packet.completed`: terminal `PacketDisposition`, execution stage/target, and failure reason when known.

Every captured packet must have a terminal trace event unless process cancellation or a fatal exception interrupts handling. In those cases emit `packet.failed` before rethrow when logging is still possible.

## 6. Privacy and Safety

- Never pass `Socks5Server.Username`, `Socks5Server.Password`, raw config text, SOCKS5 authentication messages, packet spans, datagram payloads, or relay buffers to logging APIs.
- Endpoint addresses, process names, adapter names/IDs, rule indexes, proxy server names, packet sizes, and transport metadata are intentionally diagnostic and may be logged at debug/trace.
- Process full paths are logged only when any policy process selector is path-based. Compute this once from validated policy/configuration rather than scanning rules per packet.
- Exception type and sanitized exception message may be logged. Exceptions from authentication code already avoid credential values; tests protect this invariant.
- Logging failures must not affect forwarding. The console implementation should format before its single write and avoid throwing for ordinary values; no logging call participates in disposition decisions.

## 7. Performance and Concurrency

- Guard every debug/trace event with `IsEnabled` before allocating arrays, interpolated strings, or formatted field values.
- Default `info` adds only threshold checks and packet sequence increment at capture; no per-packet formatted logging.
- Console writes produce complete lines under a logger-owned lock to prevent interleaving from packet pumps and background relay loops.
- Trace intentionally permits high volume and synchronous console overhead; documentation warns it is a temporary diagnostic mode.
- Do not introduce unbounded event queues or background writer shutdown complexity in this scope.

## 8. Compatibility

- Missing `logLevel` preserves current `info` behavior and output destination.
- Existing `[info]`, `[warn]`, and `[error]` prefixes remain.
- Strict unknown-field rejection remains unchanged.
- Configuration is still immutable per run and `validate` performs no runtime logging.
- Record constructor changes should use optional/default trailing values where needed to avoid broad test fixture churn, without preserving obsolete external formats.

## 9. Rollback

The implementation is additive. Rollback removes the config field/validated enum, diagnostic logger methods, correlation metadata, and event call sites. Existing operational logging remains usable independently. No persisted data or migration is involved.
