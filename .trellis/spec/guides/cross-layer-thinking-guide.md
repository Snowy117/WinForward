# Cross-Layer Thinking Guide

> **Purpose**: think through data flow across layers *before* implementing. Most bugs in this
> codebase live at layer boundaries, not inside a layer.

---

## The Problem

The failures this project actually sees at boundaries:

- Configuration defines a shape the runtime parses differently (a wrongly-cased key silently never
  loads — see [traffic-policy-lifecycle.md](../backend/traffic-policy-lifecycle.md)).
- An NDISAPI ABI struct drifts from the managed declaration (layout/offset assumptions).
- Two layers implement the same logic differently (checksum, endpoint rewrite, field extraction).

---

## Before Implementing A Cross-Layer Feature

**1. Map the data flow.** Write the arrows down and check each one:

```
Config JSON → ConfigurationLoader → ValidatedConfiguration → Runtime wiring
Captured frame → parse → classify → dispatch → rewrite → reinject
```

For every arrow: what format is the data in, what can go wrong, and who validates it?

**2. Identify the boundary you are actually crossing:**

| Boundary | What goes wrong there |
|---|---|
| Cli ↔ Runtime composition | wiring arity drift — the same value passed twice, or not at all |
| Runtime ↔ NdisApi interop | struct layout/offset mismatch, handle vs. pointer confusion |
| Runtime ↔ Protocols codecs | parse strictness differs between sibling parsers |
| Core primitives ↔ all consumers | equality/hash semantics assumed differently per caller |

**3. State the contract at that boundary:** exact input format, exact output format, and the errors
that can occur.

---

## Common Cross-Layer Mistakes

### 1. Implicit format assumptions

Assuming byte order, struct layout or string format without checking. **Instead:** convert
explicitly and assert the layout at the boundary (`NdisApiAbi.AssertManagedLayout`).

### 2. Scattered validation

Validating the same thing in several layers. **Instead:** validate once, at the entry point.

### 3. Leaky abstractions

A coordinator that knows ABI-level struct details. **Instead:** each layer knows only its neighbours;
interop details stay in the interop project.

### 4. Every consumer parses the same payload

Each call site parses the same fields with its own inline logic. It looks local, but every consumer
then owns a private copy of the contract — the next field change updates one call site and misses
another. **Instead:** decode once at the boundary and export a typed projection that consumers use.
For config files, captured frames and relay payloads, exactly one owner defines the types and the
parse/normalize helpers; callers may format values but must not redefine the contract. See the
"search before you write" rule in [code-reuse-thinking-guide.md](./code-reuse-thinking-guide.md).

---

## Checklist

Before implementation:

- [ ] The complete data flow is mapped, arrow by arrow
- [ ] Every boundary it crosses is identified
- [ ] The format at each boundary is written down
- [ ] It is decided where validation happens, and only there

After implementation:

- [ ] Edge cases exercised: null, empty, invalid, truncated
- [ ] Error handling verified at each boundary
- [ ] Data survives a round trip (a rewrite-back is byte-identical)
- [ ] Consumers use the shared parser/projection instead of re-parsing locally

**Write a flow document when** the feature spans three or more layers, has caused bugs before, or
carries a complex format (ABI structs, wire protocols) — and put it in the task's `research/`, not in
the chat.
