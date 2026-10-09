# Thinking Guides

> **Purpose**: widen your thinking before you code, so the bug you would have shipped is one you
> already asked about. These are checklists that point at the specs — the rules themselves live in
> [`../backend/`](../backend/index.md).

---

## Available Guides

| Guide | Purpose | When to use |
|-------|---------|-------------|
| [Code Reuse Thinking Guide](./code-reuse-thinking-guide.md) | Find the existing thing before writing a new one; know who owns a shared contract | When you notice a repeated pattern, or are about to copy code |
| [Cross-Layer Thinking Guide](./cross-layer-thinking-guide.md) | Map the data flow and the boundary you are crossing | Any feature spanning 3+ layers, or a config/wire/ABI field |

---

## Thinking Triggers

### When to think about cross-layer issues

- [ ] Feature touches 3+ layers (Configuration, Runtime, NdisApi/Protocols, Cli)
- [ ] Data format changes between layers
- [ ] Multiple consumers need the same data
- [ ] You are adding a config field, wire-format field, ABI member, or trace event
- [ ] Consumer code starts parsing raw payload/frame fields inline
- [ ] You are not sure where a piece of logic belongs

→ [Cross-Layer Thinking Guide](./cross-layer-thinking-guide.md)

### When to think about code reuse

- [ ] You are writing code similar to something that exists
- [ ] The same pattern appears 3+ times
- [ ] You are adding a field to multiple places
- [ ] **You are modifying any constant or configuration value**
- [ ] **You are creating a new utility or helper** ← search first
- [ ] Two files read the same untyped payload field with local casts
- [ ] Multiple branches update the same derived state from `kind` / `action`

→ [Code Reuse Thinking Guide](./code-reuse-thinking-guide.md)

### When auditing analyzer suppressions

- [ ] A suppressed rule may still be live → neutralize the directive in an isolated worktree and rerun
      the gate; for the JetBrains gate, delete the whole directive line (blanking or commenting it out
      can hide the finding)
- [ ] A rule-level `severity = none` landed → the matching site pragmas are now redundant; remove them
- [ ] You are about to suppress instead of fix → state a repo-verifiable reason and scope the
      `.editorconfig` glob to exactly the paths the evidence covers

→ [Quality Guidelines](../backend/quality-guidelines.md)

### When verifying AI cross-review results

- [ ] Reviewer claims "user input can be malicious" → check the actual data source (internal manifest?
      user config? external API?)
- [ ] Reviewer flags "missing validation" → is the data from a trusted internal source?
- [ ] Reviewer says "behaviour change" → read the code comments; is it an intentional design?
- [ ] Reviewer identifies a "bug" in a test → mentally delete the feature being tested; does the test
      still pass? If yes, the test is tautological

**Common AI-reviewer false positives**: treating internal data (bundled JSON manifests) as untrusted
input; flagging behaviour that a comment documents as intentional; misreading a variable without
tracing it to its definition (a map keyed by path, not by name).

**Verification rule**: every CRITICAL/WARNING finding is verified against the code before it is
prioritized. Budget roughly a 35 % false-positive rate for AI reviews.

### When implementing from an audit or a defect list

- [ ] The list may describe an **older tree** → before implementing an item, re-verify it against
      current code (a grep or a failing test) and record `implemented | fixed | deferred` with a
      re-runnable command
- [ ] The audit's "do not touch" section → check it before rewriting a subsystem the list also
      complains about
- [ ] A "fixed by deletion" item → deleting the dispatcher or type can close a defect without a new
      rule; say so instead of adding a guard
- [ ] Evidence belongs in the task's `research/`, not in the chat: commands, exit codes, before/after
      numbers

→ [Measurement Harness](../backend/measurement-harness.md) (what a comparison may claim) and
[Quality Guidelines](../backend/quality-guidelines.md)

### When claiming a refactor is behaviour-neutral

- [ ] A token or multiset comparison alone is not proof → it is blind to reordering and to which type
      owns a member; add an ordered check and a per-method body comparison
- [ ] Something will be left over (a constant changing owner, a nested type promoted, two statements
      swapped) → write it down as a registered difference with its behavioural argument
- [ ] The baseline pair is not the only comparison → the same binary twice gives the noise floor; a
      batch difference inside that floor is not a regression
- [ ] A published key sitting on a zero-width band → the band was never measured, so "it did not move"
      may be vacuous; check per key and say which ones
- [ ] A test that stays green when the line is deleted → the assertion is not wired to the production
      path; drive the real collaborator or add a counter-proof

→ [Measurement Harness — proving a refactor is behaviour-neutral](../backend/measurement-judgement.md)
and [Test Stability](../backend/test-stability.md)

---

## Pre-Modification Rule (CRITICAL)

> **Before changing ANY value, search for it first.**

```bash
rg -n "value_to_change" .
```

This one habit prevents most "forgot to update X" bugs.

---

## How to Use These Guides

1. **Before coding**: skim the guide for the kind of change you are making.
2. **During coding**: when something feels repetitive or complex, check the trigger lists above.
3. **After a bug**: if it was a "didn't think of that" moment, add the trigger to this index.
