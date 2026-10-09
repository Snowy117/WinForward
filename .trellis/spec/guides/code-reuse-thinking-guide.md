# Code Reuse Thinking Guide

> **Purpose**: stop and think before writing new code — does this already exist, and if it must exist
> twice, who owns it?

---

## The Problem

Duplication is the most reliable source of inconsistency bugs in this codebase: a fix lands in one
copy, the other keeps the old behaviour, and the two drift silently. A duplicated *contract* is worse
than duplicated lines — two call sites parsing the same payload with their own logic means two
private definitions of the same data.

---

## Before Writing New Code

**Search first.** Always, and before changing any existing value:

```bash
rg -n "KeywordOrTypeName" src/ tests/ benchmarks/
```

Then answer:

| Question | If yes |
|---|---|
| Does a similar function exist? | Use it, or extend it |
| Is this pattern used elsewhere? | Follow the existing pattern |
| Could this be a shared utility? | Create it where its owner belongs (see below) |
| Am I copying from another file? | **Stop** — extract |

---

## The Project's Reuse Rules

- **A shared parser or projection lives next to the type that owns the contract.** If the same field,
  byte range or payload fragment is parsed in two or more places, add the helper before a third reader
  appears. See mistake 4 in [cross-layer-thinking-guide.md](./cross-layer-thinking-guide.md).
- **Test helpers: two files, one home.** A fake or builder used by one test file stays in it; used by
  two or more, it moves to `tests/WinForward.TestSupport/`. When two fakes merge, take the
  **behavioural superset** (precedent: `FakeReinjector` records both the `DeviceFlags` and `Flags`
  planes; `TrackingSocket` takes an optional socket type/protocol). Full rules in
  [directory-structure.md](../backend/directory-structure.md).
- **Constants and configuration have one definition point.** This is the habit that prevents the
  "forgot to update X" class of bug across `Configuration`, `Runtime` and `Cli`.
- **Shared maths and frame construction have one home too.** Precedent: the checksum maths
  (`Sum`/`Finish`/`Set*Checksum`) and the frame builders were unified into
  `tests/WinForward.TestSupport/ChecksumMath.cs` and `FrameBuilders.cs`, leaving one-line wrappers at
  the call sites to pin defaults.

### When to abstract

**Do** abstract when the same code appears three or more times, when the logic is complex enough to
carry bugs, or when another component genuinely needs it. **Don't** when it is used once, is a trivial
one-liner, or when the abstraction would be harder to read than the duplication — `directory-structure.md`
records the same test for files: a split that damages readability or performance is not an improvement.

---

## After A Batch Modification

1. **Review** — did you catch every instance?
2. **Search** — `rg` for the old spelling; the ones you miss are the bug.
3. **Decide** — should this now be abstracted, or was it a one-off?

---

## Checklist Before Commit

- [ ] Searched for existing similar code before writing
- [ ] No copy-pasted logic that should be shared
- [ ] No field/payload parsing outside the owning type's helper
- [ ] No constant or config value defined in two places
- [ ] Similar patterns follow the same structure
