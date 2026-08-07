# Error Handling

> How errors are handled in this project.

---

## Overview

<!--
Document your project's error handling conventions here.

Questions to answer:
- What error types do you define?
- How are errors propagated?
- How are errors logged?
- How are errors returned to clients?
-->

## Current Conventions

- Unknown or ambiguous process attribution does not guess an owner; process rules do not match and evaluation continues to the next rule/fallback.
- A proxy setup failure is fail-closed in release 1. Proxy-selected packets are never silently downgraded to pass.
- UDP relay alias collisions are rejected because sharing an alias would make reverse routing nondeterministic.
- Per-caller cancellation of a shared UDP send does not tear down a session unless the shared setup/send operation itself failed.

## Common Mistakes

- Keying UDP state by PID, DNS transaction ID, or only the local port mixes independent datagrams.
- Returning an existing association solely because its relay alias matches can cross-wire two original flows.

---

## Error Types

<!-- Custom error classes/types -->

(To be filled by the team)

---

## Error Handling Patterns

<!-- Try-catch patterns, error propagation -->

(To be filled by the team)

---

## API Error Responses

<!-- Standard error response format -->

(To be filled by the team)

---

## Common Mistakes

<!-- Error handling mistakes your team has made -->

(To be filled by the team)
