# Logging Guidelines

## Overview

Runtime logging uses the project-owned `IRuntimeLogger` abstraction and the foreground
`ConsoleRuntimeLogger`; no third-party logging package or file sink is used. Output is one
line per event on stderr. Logging is observational and must never participate in packet
disposition, fail-closed, relay, or shutdown decisions.

## Log Levels

Levels are ordered from most severe to most verbose: `error`, `warn`, `info`, `debug`, `trace`.
The configured threshold includes all more-severe levels and defaults to `info`. `info` is
concise operational lifecycle output; `debug` records logical flow and proxy lifecycle; `trace`
records per-packet processing stages and terminal outcomes. High-frequency event construction
must be guarded with `IsEnabled`.

## Structured Logging

Existing operational lines retain `[info]`, `[warn]`, and `[error]` markers after a local
wall-clock timestamp prefix. Diagnostic lines use a stable lowercase event name followed by
invariant `key=value` fields. Formatting owns timestamps, escaping, quoting, IPv6 endpoint
brackets, null omission, and atomic writes under concurrency. Packet diagnostics use the runtime
packet sequence and flow-table generation when available.

## What to Log

Log lifecycle events, recoverable and fail-closed faults, policy/action decisions, classification,
proxy setup/relay/teardown, reinjection, drops, and terminal packet outcomes. Metadata may include
transport and endpoints, adapter/process identity, rule index, proxy server name, stage, reason,
and byte counts.

## What NOT to Log

Never log SOCKS5 usernames or passwords, authentication frames, raw packet bytes, payloads,
relay buffers, or raw configuration text. Full process paths are emitted only when validated
policy configuration contains a path-based process selector; process names remain permitted.

## Executable Contract

### Scope / Signatures

This contract applies to `config.json` logging, `IRuntimeLogger`, packet dispatch, and proxy
coordinators. `ConfigurationLoader.TryParse` rejects a non-string `logLevel` at path `logLevel`;
`ConfigurationLoader.TryValidate` normalizes it into `ValidatedConfiguration.LogLevel` and derives
the process-path privacy flag. High-frequency callers must check
`IRuntimeLogger.IsEnabled(RuntimeLogLevel)` before creating fields. Structured events use
`IRuntimeLogger.Event(RuntimeLogLevel, string, params RuntimeLogField[])`.

### Boundary Contracts

- Accepted values are `error`, `warn`, `info`, `debug`, and `trace`, case-insensitive with
  surrounding whitespace ignored. Omission means `info`.
- Threshold ordering is `error < warn < info < debug < trace`; a threshold includes itself and all
  more-severe levels.
- Lines use `yyyy-MM-dd HH:mm:ss.fff [level] event.name key=value` on `stderr`. The timestamp is
  local wall-clock time formatted with the invariant culture; values remain invariant,
  escaped/quoted when unsafe, bracketed for IPv6 endpoints, and omitted when null.
- Packet diagnostics use the runtime `packet` sequence; flow diagnostics use the `FlowTable`
  generation. TCP/UDP association generations may be additional fields.
- Allowed metadata includes endpoints, adapters, process identity, rule/action, stage, reason, and
  byte counts. Full process paths require a path-based process selector.

### Validation and Error Matrix

| Condition | Required result |
| --- | --- |
| `logLevel` omitted | Validate successfully with `info` |
| Valid level string | Normalize and store the matching enum |
| Null, blank, unknown, or wrong type | Fail with a `logLevel` diagnostic without echoing raw input |
| Event below the threshold | Skip timestamp/field formatting and output |
| Logger writer/formatter failure | Do not affect packet behavior |
| Classification/dispatch failure | Emit trace `packet.failed` when possible, dispose the lease, and rethrow |
| Proxy or reinjection failure | Preserve existing fail-closed behavior and log metadata/reason only |

### Good / Base / Bad Cases

- Good: `"logLevel": " Trace "` produces trace output such as
  `2026-08-27 14:03:21.517 [trace] packet.completed packet=42 flow=7 disposition=pass`.
- Base: omitted `logLevel` preserves concise lifecycle, warning, and error output without
  per-packet formatting.
- Bad: passing a SOCKS5 password, packet span, UDP payload, authentication frame, relay buffer,
  or raw configuration JSON to `Event`.

### Required Tests

- Configuration tests cover defaulting, all five values, case/whitespace normalization, wrong
  types, blank/unknown values, and the `logLevel` diagnostic path.
- Logger tests cover threshold filtering, a parseable local `yyyy-MM-dd HH:mm:ss.fff` prefix on
  every emitted line, stable field order, escaping, null omission, invariant formatting, IPv6
  rendering, and one-line output.
- Runtime tests cover packet/flow correlation, terminal completion/failure, process-path privacy,
  proxy lifecycle metadata, and unchanged packet dispositions.
- Static review/search confirms credentials, payloads, raw frames, and relay buffers never reach
  logging calls.

### Wrong vs Correct

Wrong:

```csharp
logger.Event(RuntimeLogLevel.Trace, "packet", new("payload", packet.Lease.Frame.Span));
```

Correct:

```csharp
if (logger.IsEnabled(RuntimeLogLevel.Trace))
{
    logger.Event(RuntimeLogLevel.Trace, "packet.completed",
        new("packet", packet.PacketSequence), new("bytes", packet.Lease.Frame.Length));
}
```
