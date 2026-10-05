# Suppression inventory — 10-05-local-dns-transport

Audit input, per `.trellis/spec/backend/quality-guidelines.md` §30. Scope: every suppression this
task added — code-level `// ReSharper disable once` pragmas plus any `.editorconfig` key. No
rule-level `.editorconfig` entry was added or changed by this task.

## Site pragmas (2)

### 1. `ConvertIfStatementToSwitchStatement` — `benchmarks/WinForward.Benchmarks/Stability/SoakOptions.cs:245`

Reason (verbatim): "Parse-time refusal: a switch over one pattern case adds ceremony without adding a
branch — the implicit false path (the argument combination is valid) is the normal flow, and the
sibling refusal below keeps the same if/throw shape."

Necessity: the finding survived the `MergeIntoPattern` form being adopted at the same site, so what
remains is R# asking for a single-pattern-case `switch`. The surrounding method is a chain of
independent parse-time refusals, each an `if … throw`; converting one of them changes the shape of
that chain without adding a branch. Same suppression shape as `ConfigurationLimits.cs:108/147/171`
and `NdisApiAbi.cs:170/179`.

### 2. `UseUtf8StringLiteral` — `tests/WinForward.Runtime.UdpProxy.Tests/LocalUdpTransportTests.cs:73`

Reason (verbatim): "The three bytes are an arbitrary non-text wire payload and the send helper takes
byte[]: the suggested u8 literal is a ReadOnlySpan<byte> and cannot bind that parameter."

Necessity: the literal is deliberately non-text (an intruder's datagram in the source-validation
test), and the helper's `byte[]` parameter cannot bind a UTF-8 literal — R#'s own quick-fix would not
compile. Written as a collection expression rather than `new byte[] { … }`, which the 09-20 audit
found also triggers the rule.

## Findings cleared by fixing rather than suppressing (32)

| Finding | Count | Disposition |
| --- | --- | --- |
| `RedundantUsingDirective` | 20 | Directive deleted. Most became redundant through this task's seam relocation (the transport contract left `WinForward.Runtime.Socks5`) and its type renames; the Release build is the check. |
| `UnusedVariable` | 2 | The never-read binding was dropped while keeping disposal (`await using (await composite.CreateAsync(…))`). |
| `DisposeOnUsingVariable` (warning) | 1 | Fixture restructured so the `using` scope *is* the disposal the ownership assertions observe — one dispose, still failure-safe. |
| `MemberCanBePrivate` (global ×2, local ×1) | 3 | Decided on evidence: `LocalUdpTransport.IsPossiblyTruncated` narrowed to `private static` (no reader outside the class), `RetentionHarness.Time` narrowed to `private`, and two test-helper `IpEndpoint` properties removed as dead API. None was the `InternalsVisibleTo` false-positive class: the SOCKS5 mirror stays `internal` because a test uses it as its oracle. |
| `UnusedAutoPropertyAccessor.Global` (warning) | 1 | Same dead property, removed with its sibling finding. |
| `InvalidXmlDocComment` (warning) | 1 | Ambiguous cref qualified to the overload the code calls (`IPAddress.TryParse(string, out IPAddress)`). |
| `RedundantNameQualifier` (warning) | 1 | Qualifier dropped. |
| `MergeIntoPattern` | 2 | Adopted as property patterns on the two parse-time refusals (zero allocation, identical predicates). |
| `ConvertToStaticClass` | 1 | Collection-definition class made `static`; xUnit never instantiates it (verified by the member facts passing). |
