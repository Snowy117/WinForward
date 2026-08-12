# Configurable Runtime Log Verbosity

## Goal

Allow operators to select runtime log verbosity in `config.json` so normal runs stay concise while the most detailed mode exposes an end-to-end, correlatable account of how captured traffic is classified, evaluated, forwarded, proxied, reinjected, blocked, or dropped.

The diagnostic output should make it practical to answer why a particular flow received its observed result without changing forwarding behavior.

## Background

- Configuration is strict JSON: unknown properties are rejected, validation completes before interception starts, and the validated configuration remains immutable for the run (`ConfigurationModels.cs`, `README.md`).
- Runtime logging currently uses the project-owned `IRuntimeLogger` abstraction with `Info`, `Warn`, and `Error`; the CLI always constructs an unfiltered console logger (`FlowDispatcher.cs:33`, `Program.cs:117`, `Program.cs:359`).
- Existing logs primarily cover lifecycle events and exceptional/fail-closed paths. Normal packet classification, policy selection, pass/block/proxy execution, SOCKS5 relay progress, and reinjection do not form a complete diagnostic trace.
- SOCKS5 usernames and passwords are explicitly forbidden from logs (`README.md:78`).

## Requirements

- Add a top-level `config.json` setting that selects a validated log verbosity for `run`.
- Preserve the current concise operational output at the default verbosity when the setting is omitted.
- Support the ordered, case-insensitive values `error`, `warn`, `info`, `debug`, and `trace`; each level includes events from more severe levels, and omission defaults to `info`.
- `debug` records logical-flow lifecycle, policy-decision, proxy-session, and expiry/teardown events without emitting an event for every packet.
- `trace` includes `debug` and records every captured packet's processing stages and final disposition.
- At the most detailed level, emit enough correlated events to follow captured TCP, UDP, and non-flow traffic through classification, self-traffic handling, flow-table/policy resolution, selected action, proxy/coordinator handling, and final reinjection/drop outcome.
- Detailed traffic events contain processing metadata and outcomes only. They may include timestamp, adapter, direction, protocol, source/destination endpoints, process identity, matched rule index, action, configured proxy server name, processing stage, and failure reason.
- Human-facing runtime output remains on `stderr` as one line per event. Existing `[info]`, `[warn]`, and `[error]` messages remain compatible; new diagnostic events use a stable event name followed by `key=value` fields.
- Diagnostic records use a runtime packet sequence and the flow-table generation as correlation identifiers; field values must be rendered without credentials or payload bytes.
- Detailed logs always identify the executable by process name when attribution succeeds. They include the full process path only when the configured policy contains at least one path-based process selector; otherwise the path is omitted to reduce disclosure of local user/directory information.
- Detailed traffic logging must be observational only: it must not alter policy evaluation, packet disposition, relay behavior, shutdown, or fail-closed semantics.
- Never log configured SOCKS5 usernames or passwords, authentication frames, or raw configuration text.
- Never dump raw packet bytes or application-layer payloads, including SOCKS5 authentication traffic.
- Update configuration documentation and representative example configuration.
- Add focused tests for configuration parsing/defaulting/rejection and logger filtering; add runtime trace tests at the key decision boundaries selected by the final design.

## Out of Scope

- Configuration hot reload; the selected level remains fixed for a run.
- Windows Event Log, rotating files, remote log collection, or a Windows Service logging backend.
- Raw packet capture, hexadecimal dumps, and application-layer payload logging.
- Changing policy behavior or failure actions.
- Guaranteeing low overhead in the most detailed diagnostic mode; avoiding material overhead at the default level remains required.

## Acceptance Criteria

- [ ] A valid documented log-level value in `config.json` controls emitted runtime events, while omission uses the documented current-compatible default.
- [ ] `logLevel` accepts `error`, `warn`, `info`, `debug`, and `trace` case-insensitively, normalizes the result, and defaults to `info` when omitted.
- [ ] An unsupported or incorrectly typed value fails `validate` and `run` configuration loading with a path-specific diagnostic.
- [ ] Default-level output retains lifecycle, warning, and error reporting without per-packet diagnostic noise.
- [ ] The most detailed mode produces a correlatable trace for pass, block, TCP proxy, UDP proxy, self-owned traffic, reverse traffic, and unclassifiable/non-flow paths, including the final disposition or failure reason.
- [ ] Enabling detailed logging does not change packet decisions or execution outcomes in automated tests.
- [ ] Logs never expose SOCKS5 usernames/passwords, authentication payloads, or raw configuration content.
- [ ] No verbosity level emits raw packet bytes or application-layer payloads.
- [ ] Full process paths appear only when path-based process matching is configured; process names remain available when attribution succeeds.
- [ ] Existing tests pass, and new configuration/filtering/trace tests pass.
