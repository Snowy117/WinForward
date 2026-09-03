# PRD: Process selector directory matching

## Background

WinForward policy rules select host flows by owning process via the `process` field.
Current semantics (see `src/WinForward.Core/ProcessSelectors.cs` and README "Process
selection" section):

- Value without `/` or `\`: exact executable filename match (case-insensitive).
- Value containing `/` or `\`: normalized full path **exact** match only.

Users managing software suites installed under one folder (e.g. games, Electron apps,
portable app bundles) must enumerate every executable path individually.

## Requirement

When a `process` selector containing a directory separator refers to a directory, it
must match every program located in that directory **and any subdirectory at any
depth** below it.

Example: selector `C:\Program Files\MyApp` matches:

- `C:\Program Files\MyApp\app.exe`
- `C:\Program Files\MyApp\bin\sub\tool.exe`

It must NOT match sibling directories sharing a string prefix, e.g. selector
`C:\Tools` must not match `C:\ToolsFoo\app.exe`.

## Acceptance criteria

1. Path-based selectors keep the existing exact normalized full-path match
   (backward compatible; existing configs behave identically).
2. Path-based selectors additionally match when the normalized process path starts
   with `<normalized selector>\` (case-insensitive), covering the directory itself
   and all descendant subdirectories.
3. Selectors ending with a trailing separator (`C:\Tools\`, `C:/Tools/`) normalize
   to the same directory semantics.
4. Filename-only selectors (no separator) are unaffected.
5. Matching stays pure in-memory string logic: no filesystem I/O (works on any OS,
   deterministic in tests, safe on the hot flow-matching path).
6. README process-selection semantics updated to document directory matching.
7. Unit tests cover: direct child match, deep subdirectory match, sibling-prefix
   non-match, case-insensitivity, trailing separator form, and preserved exact-path
   matching.
