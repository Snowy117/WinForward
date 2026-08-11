# 分离网卡转发与普通流量路由

## Goal

Allow the user to proxy traffic from virtual adapter A without implicitly
proxying traffic from virtual adapter B merely because an ordinary host-traffic
catch-all proxy rule exists. Intentional proxying through explicitly selected
adapters must remain supported.

## Background

- The reported configuration selects virtual adapter A for proxy forwarding and
  also contains an adapter-unqualified catch-all proxy rule. Traffic from a VM
  attached through virtual adapter B is not intended to use the proxy.
- `CaptureAdapterScopeResolver` widens capture to every MSTCP-bound adapter when
  any rule has no adapter constraint, including an explicit catch-all proxy
  rule (`src/WinForward.Runtime/CaptureAdapterScopeResolver.cs:25-49`). A
  configuration containing only adapter-A-constrained rules does not directly
  capture B.
- NDIS direction classifies `ON_SEND` as `Host` and `ON_RECEIVE` as
  `Forwarded`, while retaining the observed adapter identity
  (`src/WinForward.Runtime/PacketFlowClassifier.cs:15-40`).
- `RuleMatcher.IsMatch` matches process, adapter, protocol, family, CIDR, and
  port, but not `FlowOriginKind` (`src/WinForward.Core/Policy.cs:12-23`). An
  unconstrained proxy rule therefore matches both host and forwarded flows.
- `fallbackAction` is also evaluated without origin distinction
  (`src/WinForward.Core/Policy.cs:39-47`), although the configuration format
  intentionally does not allow `fallbackAction: "proxy"`; proxy fallback is an
  explicit empty-match proxy rule (`src/WinForward.Configuration/ConfigurationModels.cs:120`).
- Existing flow resolution deliberately reuses a decision across direction,
  origin, and adapter changes (`src/WinForward.Core/Domain.cs:139-159`), so any
  forwarded-adapter eligibility gate must occur after existing-flow resolution
  and before new-flow policy evaluation.
- The README describes host traffic as process-selected and forwarded traffic
  as adapter-selected (`README.md:141-143`), but the current matcher does not
  enforce that separation.

## Requirements

- Preserve existing host-originated rule ordering, matching, process
  attribution, and configured `fallbackAction` behavior.
- For a new flow classified as `Forwarded`, evaluate only rules containing an
  `adapterId` and/or `adapterName` constraint. Preserve their original order and
  all additional matcher conditions.
- If no adapter-qualified rule matches new forwarded traffic, pass it unchanged
  into the normal Windows network path, independently of unqualified rules and
  the host `fallbackAction`.
- Apply equivalent adapter-qualified-only/default-pass behavior to forwarded
  packets that cannot be classified as TCP/UDP flows.
- Use the existing direction-derived `FlowOriginKind.Forwarded` classification
  as the MVP separation boundary. This intentionally includes new inbound
  traffic addressed to local host services as well as traffic Windows later
  routes across adapters.
- Keep the JSON configuration schema and capture adapter scope behavior
  unchanged.

## Acceptance Criteria

- [x] A catch-all proxy rule for ordinary traffic does not cause traffic from an
      unselected forwarded adapter to be proxied.
- [x] Traffic from an explicitly selected forwarded adapter can still be
      proxied according to policy.
- [x] Existing ordinary host-traffic routing behavior remains unchanged.
- [x] The selected behavior for unselected forwarded-adapter traffic is
      observable and covered for both flow and non-flow packets.
- [x] An adapter-B forwarded flow is passed when only adapter A has matching
      adapter-qualified rules, even if an unqualified catch-all proxy rule or a
      host `fallbackAction: "block"` follows it.
- [x] Adapter-qualified rules retain first-match ordering and can still apply
      protocol, family, CIDR, and port conditions to forwarded traffic.
- [x] New inbound traffic addressed to a local host service follows the same
      adapter-qualified-only semantics because it is classified as
      `Forwarded` by the current NDIS direction model.
- [x] Existing decisions are reused for reverse and cross-adapter observations
      before origin-specific policy is considered again.
- [x] The current defect and corrected behavior are documented with code and
      regression-test evidence.

## Out of Scope

- Replacing the existing ordered-rule model or adding route/NAT management to
  WinForward is out of scope.
- Distinguishing traffic actually routed by Windows from inbound traffic
  addressed to the host is deferred.
- Redesigning the pre-existing flow table to coordinate concurrent first
  observations of the same routed tuple on different adapters is out of scope.

## Technical Notes

- The current implementation cannot authoritatively distinguish a packet that
  Windows will forward from an unsolicited packet addressed to a local service;
  both are initially observed as `ON_RECEIVE` and classified as `Forwarded`.
- Capture exclusion alone is not sufficient: ordinary host policy may require
  observing all adapters, and a routed tuple may later be observed on another
  captured adapter.
