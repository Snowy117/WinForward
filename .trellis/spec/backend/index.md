# Backend Development Guidelines

> Coding guidelines for WinForward — a single-repo .NET solution with no frontend or database layer, so this is the only package/layer spec directory besides `guides/`.

---

## Guidelines Index

| Guide | Description | Status |
|-------|-------------|--------|
| [Directory Structure](./directory-structure.md) | File layout, size limits, split discipline, TestHelpers organization, Runtime sub-namespaces | Active |
| [Error Handling](./error-handling.md) | Fail-closed semantics, bounded exemptions, validation diagnostics | Active |
| [Quality Guidelines](./quality-guidelines.md) | Code standards, forbidden patterns, testing requirements | Active |
| [Logging Guidelines](./logging-guidelines.md) | `IRuntimeLogger` structured logging, log levels, privacy rules | Active |
| [Windows NDISAPI Interop](./windows-ndisapi.md) | Adapter identity (GUID-primary), enumeration vs captured handles, native-call gates, batched capture ABI & pooling | Active |
| [TCP Local Redirect](./tcp-local-redirect.md) | WinpkFilter local_redirect transform, forwarded DNAT shape, client-reset lifecycle, teardown grace | Active |
| [UDP Relay](./udp-relay.md) | SOCKS5 UDP relay wiring, response reinjection, cross-family setup | Active |
| [Traffic Policy & Lifecycle](./traffic-policy-lifecycle.md) | Host vs forwarded policy domains, non-flow pass, loop prevention, idle expiry sweep | Active |
| [Hot-Path Conventions](./hot-path.md) | Zero-allocation packet pipeline: raw addresses, sync fast path, native lease lifetime, in-place reinjection | Active |

> History: on 2026-08-29 the former 470-line `windows-ndisapi.md` monolith was split (paths verified against the post-08-29-refactor source tree) into `windows-ndisapi.md` + `tcp-local-redirect.md` + `udp-relay.md` + `traffic-policy-lifecycle.md`, and the never-filled `frontend/` layer and `database-guidelines.md` template were removed.

---

## Conventions

1. Guidelines document the project's **actual conventions** (not ideals), with code examples from this codebase.
2. Every guideline entry states its scope/trigger so an implementer knows when it applies.
3. New lessons from debugging land in the relevant file via the `trellis-update-spec` flow (Phase 3.3).

---

**Language**: All documentation should be written in **English**.
