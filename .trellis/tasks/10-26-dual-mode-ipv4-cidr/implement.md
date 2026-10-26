# Batch 5f implementation plan

1. **Read the driver path.** `src/common/rule_blob.h`/`matcher.h`/`redirect_context.h`,
   `src/driver/probe_classify.cpp` (host facts + context), `probe_forwarded*` (do not change),
   `redirect_target.cpp` (D6), `tests/host/{matcher,redirect_context}_test.cpp`, and the Windows
   host rule suite in `tools/wfprobe` (RuleSuite/HostRuleParitySuite/WfpSockets).
2. **Helper.** Add a portable `try_normalize_v4_mapped` in `src/common/` (kernel dialect: no STL,
   exceptions, RTTI, allocation). Detect `::ffff:0:0/96` and return family 4 + the four bytes.
3. **Classify.** In the host classifier, normalize the family/address used for the matcher
   `connection_view` and for the redirect context's `original_destination`. Leave the local
   address/redirect target, listener, WFP layer, rule table and redirect table unchanged. Apply the
   same fact conversion in the host UDP classify path for the future backend.
4. **ABI.** Keep `redirect_context_version`/size unchanged; only the family/address values change
   for the mapped case. Update the spec text for the new fact conversion
   (`.trellis/spec/backend/kernel/wfp-architecture.md` §7.5 and `host-rule-blob.md` matcher facts);
   keep `probe_event` diagnostic facts raw.
5. **Tests.** Add the portable helper tests, matcher/redirect-context tests, and a `wfprobe` host
   rule case with a dual-mode client + IPv4 CIDR; update `SelfTest.TryCheckContext` and U10's
   v4-mapped connected expectation; keep the native IPv4/IPv6/U10 family cases.
6. **Gates.** `kernel/tools/check.sh --full`; the Windows WDK build/gate; `wfprobe host`; record the
   driver hash. Do not weaken the mutations/forbidden checks.
7. **Windows E2E.** Reload the test-signed driver; run the Kernel provider with the original
   proxy rule (`Process: ["pwsh.exe"]`, TCP, IPv4 CIDR, port 40012, proxy `main`); the `pwsh`
   dual-mode client must proxy via sing-box/target; recheck pass 40014/block 40013; record the
   evidence; clean the VM.
8. **Boundaries.** No WinFore/userland behavior change unless the IPv4 context requires a fix; no
   ABI size/version; no host UDP/forwarded/ARM64; no unrelated dirty files. No commit until the
   E2E evidence is complete.

## Verification

```bash
nix develop ./kernel --command bash -lc 'kernel/tools/check.sh --full'
# VM: build/load the driver, gate.ps1, wfprobe host; then the WinForward Kernel E2E with the
# original IPv4 CIDR rule; keep the client/sing-box/stats/cleanup evidence.
```

## Risks

- The local address/D6 stay V6-layer; do not normalize them.
- Byte-exact v4-mapped detection; no string/locale operations in the driver.
- The context ABI stays 36 bytes/version 1; the userland must handle the IPv4 destination (the
  Batch 5d listener/relay already did; this changes the context family and needs the E2E).
- Driver reload and adapter/WFP state; use the test-signed load/rollback scripts and verify the
  VM network after.
