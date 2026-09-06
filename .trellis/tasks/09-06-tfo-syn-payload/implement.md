# Implementation Plan

## Steps

1. [x] `TcpProxyCoordinator.HandlePacketAsync`: remove the `WithPayload → Blocked` branch
       so data-bearing SYNs route into `HandleSynAsync`. Update the class-level doc comment
       if it references the block.
2. [x] `TcpFrameRewriter`: `TcpSynKind` deleted entirely (user review, 2026-09-06: a two-value
       enum with one `if` consumer collapsed into the boolean predicate `IsTcpSyn`);
       `ClassifyTcpSyn` removed with it. Benchmark call site renamed accordingly.
3. [x] Tests (`TcpProxyCoordinatorRewriteTests.cs`): replaced
       `SynWithPayloadIsBlockedWithoutClaim` with `SynWithPayloadIsRedirectedLikeBareSyn` and
       `RetransmittedSynWithPayloadReusesAssociation` per design §Test design.
4. [x] `NdisPacketActionExecutorLoggingTests.CoordinatorBlockedWarnCarriesRedirectReasonInsteadOfUninitializedText`
       reworked: the old trigger (payload-SYN fast-path block) no longer exists; the test now
       settles a redirect then sends a same-flow frame whose ethertype defeats the rewrite
       parse — the association-reuse fast path fails closed synchronously and the executor
       labels it `reason=redirect`.

## Validation

- [x] `dotnet build` — 0 warnings, 0 errors.
- [x] `dotnet test` — 501/501 green.
- [x] `rg -n "WithPayload" src` — zero hits.
- [x] `rg -rn "ClassifyTcpSyn|TcpSynKind" src tests benchmarks` — zero hits.

## Review gate

- [x] Confirm no doc/spec text still claims "SYN with data is blocked"
      (`rg -i "with.?payload|SYN with data" src .trellis/spec` — zero hits, verified by
      trellis-check 2026-09-06 alongside build -warnaserror 0/0 and 501/501 tests).

## Rollback

- `git revert` of the single commit; no state or config to unwind.
