# Quality Guidelines

> Code quality standards for backend development.

---

## Overview

<!--
Document your project's quality standards here.

Questions to answer:
- What patterns are forbidden?
- What linting rules do you enforce?
- What are your testing requirements?
- What code review standards apply?
-->

## Current Conventions

- Build with nullable analysis, `TreatWarningsAsErrors`, Native AOT/trim analyzers, and the centrally pinned analyzer packages.
- Keep packet/ABI hot paths allocation-conscious, but preserve explicit bounds checks and ownership guards.
- Use localized `#pragma warning` suppression only when the behavior is intentional and documented (for example, rollback cleanup that must continue after one restore failure).
- Process selectors are exact: a selector without a directory separator matches an executable filename, while a selector containing `/` or `\\` matches a normalized full path.
- UDP routing uses the original local/remote endpoint tuple plus protocol, address family, and origin context. PID and DNS transaction IDs are metadata/payload, never association keys.

## Testing Requirements

- Every flow or association ownership change needs a regression for same-key reuse, distinct-key isolation, and deterministic collision/failure behavior.
- Concurrency tests must use genuinely overlapping tasks, not only sequential repeated calls.
- Pure tests run on any host; NDISAPI and Windows attribution tests must remain behind ABI/platform seams.

---

## Forbidden Patterns

<!-- Patterns that should never be used and why -->

(To be filled by the team)

---

## Required Patterns

<!-- Patterns that must always be used -->

(To be filled by the team)

---

## Testing Requirements

<!-- What level of testing is expected -->

(To be filled by the team)

---

## Code Review Checklist

<!-- What reviewers should check -->

(To be filled by the team)
