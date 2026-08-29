# Cross-Layer Thinking Guide

> **Purpose**: Think through data flow across layers before implementing.

---

## The Problem

**Most bugs happen at layer boundaries**, not within layers.

Common cross-layer bugs:

- Configuration defines a shape the runtime parses differently
- NDISAPI ABI structs drift from the managed declaration (layout/offset assumptions)
- Multiple layers implement the same logic differently (e.g. checksum, endpoint rewrite)

---

## Before Implementing Cross-Layer Features

### Step 1: Map the Data Flow

Draw out how data moves:

```
Config JSON → ConfigurationLoader → ValidatedConfiguration → Runtime wiring
Captured frame → parse → classify → dispatch → rewrite → reinject
```

For each arrow, ask:

- What format is the data in?
- What could go wrong?
- Who is responsible for validation?

### Step 2: Identify Boundaries

| Boundary | Common Issues |
|----------|---------------|
| Cli ↔ Runtime composition | wiring arity drift (same value passed twice, or not at all) |
| Runtime ↔ NdisApi interop | struct layout/offset mismatch, handle vs pointer confusion |
| Runtime ↔ Protocols codecs | parse strictness differs between sibling parsers |
| Core primitives ↔ all consumers | equality/hash semantics assumed differently per caller |

### Step 3: Define Contracts

For each boundary:

- What is the exact input format?
- What is the exact output format?
- What errors can occur?

---

## Common Cross-Layer Mistakes

### Mistake 1: Implicit Format Assumptions

**Bad**: Assuming byte order, struct layout, or string format without checking

**Good**: Explicit conversion and layout assertions at boundaries (e.g. `AssertManagedX64Layout`)

### Mistake 2: Scattered Validation

**Bad**: Validating the same thing in multiple layers

**Good**: Validate once at the entry point

### Mistake 3: Leaky Abstractions

**Bad**: A coordinator knowing ABI-level struct details

**Good**: Each layer only knows its neighbors; interop details stay in the interop project

### Mistake 4: Every Consumer Parses The Same Payload

**Bad**: Each call site parses the same fields/bytes with its own inline logic. This looks local, but it means every consumer owns a private version of the contract. The next field change will update one call site and miss another.

**Good**: Decode once at the boundary, export the typed projection, make consumers use it.

**Rule**: For config files, captured frames, or relay payloads, create one owner for the type definitions and parse/normalize helpers. Callers may format values, but must not redefine the contract.

---

## Checklist for Cross-Layer Features

Before implementation:

- [ ] Mapped the complete data flow
- [ ] Identified all layer boundaries
- [ ] Defined format at each boundary
- [ ] Decided where validation happens

After implementation:

- [ ] Tested with edge cases (null, empty, invalid, truncated)
- [ ] Verified error handling at each boundary
- [ ] Checked data survives round-trip (e.g. rewrite-back is byte-identical)
- [ ] Checked that consumers use the shared parser/projection instead of re-parsing locally

---

## When to Create Flow Documentation

Create detailed flow docs when:

- Feature spans 3+ layers
- Feature has caused bugs before
- Data format is complex (ABI structs, wire protocols)
