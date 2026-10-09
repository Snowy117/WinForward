# Journal - qmazon (Part 2)

> Continuation from `journal-1.md` (archived at ~2000 lines)
> Started: 2026-10-07

---



## Session 59: TCP half-close fidelity: measure the mechanism, ship the close injection, verify on the VM
<!-- trellis-session: v=2 fp=1e17a3d5f084de37 -->

**Date**: 2026-10-07
**Task**: TCP half-close fidelity: measure the mechanism, ship the close injection, verify on the VM
**Branch**: `master`

### Summary

Opened task 10-06-tcp-half-close-fidelity and drove it to archive. Reproduced the symptom report's numbers exactly on the campaign AOT artifact (clean 55 / timeout 556 of 1201), then measured the mechanism instead of assuming it: the single-mode arms showed clean and halfClose failing identically (the trailers are innocent) with halfCloseViolation=0, and the relay does emit a FIN — its NetworkStream is disposed as RunPumpAsync returns — but that instant coincides with Completion completing, so the FIN races the retire that releases the reverse index and a half-closed client sees no end of stream at all. Fix: every relay end injects its client-visible close before the teardown (FIN|ACK sequenced from the server ISN plus the relay's delivered byte count, RST|ACK unchanged), plus the relay end kind in the tcp.relay.ended event. Verified on the VM through the unchanged harness: timeout 556 -> 23 of 1201 (clean 55 -> 592), halfClose 548 -> 50, clean 554 -> 30; gates green (build, tests, format, jb). Residual 2-8% is a different defect — the close is single-shot once the alias retires — captured with its Debug-level evidence and two candidate directions in the archived task and handed to child task 10-07-tcp-close-drain. Environment notes: the user's four-pass campaign was found hung (client stuck in MIX, sing-box gone, heartbeat stale) and was stopped by PID with its evidence preserved; C:\wfbench stayed read-only; the VM sandbox was deleted and the watchdog re-enabled, which restored the firewall.

### Git Commits

| Hash | Message |
|------|---------|
| `7cababb` | fix(tcp-redirect): inject the client-visible close before the session retires |
| `c828548` | docs(spec): record the client-visible close contract at relay end |
| `c58a64b` | chore(task): archive the TCP half-close fidelity task and open the close-drain child |

### Status

[OK] **Completed**

### Next Steps

- Child task 10-07-tcp-close-drain: design the bounded close drain (or the bounded repeat), then measure the residual against the port budget.


## Session 60: Spec revision: correct the drift, cut the padding, split the five oversized documents
<!-- trellis-session: v=2 fp=c2cb04b06a841207 -->

**Date**: 2026-10-09
**Task**: Spec revision: correct the drift, cut the padding, split the five oversized documents
**Branch**: `master`

### Summary

Session summary was not supplied.

### Main Changes

- 16 spec documents became 45, none over 400 lines; five hubs with topic maps and move tables; idle-expiry-sweep.md split out; directory-structure.md translated to English
- 61 stale references adjudicated against the code; 23 code comments in 19 files retargeted from numbers to surviving section titles
- verify-specs.py (size, index, family links, link resolution, identifiers, language) green; dotnet format exit 0; Release build 0 warnings

### Git Commits

| Hash | Message |
|------|---------|
| `0576ce0` | docs(spec): verify the library against the code, trim it, and split the oversized documents |
| `662c286` | chore(task): record the spec revision task |

### Status

[OK] **Completed**

### Next Steps

- Decide whether to fix the archived rename-table path in benchmarks/WinForward.E2E/scripts/check-readme-contract.py:47 (and check-fixture-drift.py:40) — the README contract gate currently exits 2
- Review the two families that grew (tcp-local-redirect +25.9 % words, measurement-harness +15.7 %) if the scaffolding cost is judged too high


## Session 61: E2E C#-native cleanup: four children, one parent, 1663 tests green
<!-- trellis-session: v=2 fp=8311db40a10857b9 -->

**Date**: 2026-10-09
**Task**: E2E C#-native cleanup: four children, one parent, 1663 tests green
**Branch**: `master`

### Summary

Finish what D21 deferred: remove the CPython emulation the analyzer was ported with, bring the four E2E trees to the repository's naming and structure, give every script a home that matches its scope, and make the documents true - including wiring the frozen-tree oracle into CI as its own ubuntu-latest gate.

### Main Changes

- C1: the CPython emulation is gone - System.Random for the bootstrap, the half-even BigInteger formatter deleted (VerbatimNumber 342 -> 105 lines), PythonExponential one format string, the three vector tables and their generator retired; the six resampled leaves carry a tolerance measured from the quantity's own seed-to-seed spread (5e-2), not from its print granularity
- C2: the acronym rule applied (SocketIO, IOError, OSDescription - member only, the value is frozen) and written into the spec, the (value, reason) tuples collapsed into one Measured<T>, LedgerViews/ArmContext/LaneTestDoubles split, five git mv, migration vocabulary cleared, the E2E test project's analyzer exemption documented with the 31 findings that justify it
- C3: two spent migration scripts deleted, effective-lines.py to a root tools/ (it is a solution-wide rule), the differ and the fairness guard into Analysis/verification/, seven broken paths repaired - the orchestrator's exit-code log, fixture-drift's exit code, publish-campaign's ledger names, deploy-campaign's staging directory
- C4: the sixteen stale claims disposed (none left false), the plan sets and configs documented by measurement instead of assertion, FROZEN.md's coverage overstatement corrected, and the oracle became a CI gate - a new ubuntu-latest job, because the differ's apphost has no extension and its frozen tree is pinned to /tmp
- 172 files changed, +9667/-8556. Eleven plan items were falsified by measurement along the way and each is recorded with its evidence, from the Truthy call sites and the JSON encoder direction to the 1e-2 tolerance that had to become 5e-2 and the four 'unused' usings that were load bearing

### Git Commits

| Hash | Message |
|------|---------|
| `a402289` | refactor(analysis): drop the free Python-emulation items (S0-S1) |
| `90bc922` | refactor(analysis): stop narrating CPython and drop the half-even formatter (S2-S3) |
| `9310eda` | refactor(analysis): draw the bootstrap from System.Random and register the statistical tolerance (S4-S6) |
| `8cfff82` | docs(analysis): correct three claims the check round falsified |
| `ed436af` | refactor(e2e): bring the four trees to the house naming and structure (C2 N0-N5) |
| `0d1edda` | chore(e2e): give every script a home, and make the broken ones work (C3 A-D) |
| `f1b556e` | docs(task): correct two false statements the C3 check round found |
| `b23eceb` | docs(e2e): make the documents true and wire the oracle into CI (C4 P0-P5) |
| `bf4bced` | docs(e2e): three corrections the C4 check round found |
| `836db2b` | chore(task): archive 10-09-e2e-csharp-native-cleanup |

### Testing

- [OK] dotnet test WinForward.slnx -c Release -m:1 -> 14 projects, 1663 passed, 0 failed, exit 0
- [OK] Release build 0 warnings; dotnet format empty; jb inspectcode 0 issues
- [OK] oracle 51 slices rc=0; check-fairness 14/14; boundary trees 5 trees + 12 controls; check-fixture-drift rc=0 (rc=2 when its input is missing); check-readme-contract 111 keys against 401 constants; effective-lines clean
- [OK] harness self-test end to end: 159.9 Mbps goodput, 0 protocol errors, 0 send failures

### Status

[OK] **Completed**

### Next Steps

- Add the PlanFile override/merge mechanism if the plan sets are to be deduplicated (needs loader behaviour, validation and PlanFileTests' 12-file count)
- Close the CI gap: no workflow runs dotnet build or dotnet test for the solution (D5)
- Local glue still needs attention: wf.sh's transfer race (T5), deploy-campaign.sh's missing C:\wfbench\stage, orchestrator's unused -Pass parameters, selftest.sh's heredoc
- Two wording items were left deliberately: DescriptiveStats.cs:119's CPython sum sentence (deleting the words deletes the reason) and oracle-diff.py's Report.show() 160-character truncation
- 10-06-e2e-competitor-benchmark's design.md:214 and implement.md:96 still list plots/ as an output though the C# analysis draws none


## Session 62: 代码树规范化：IPv4/IPv6 改名与全量注释精简
<!-- trellis-session: v=2 fp=42807aa1a20cc259 -->

**Date**: 2026-10-09
**Task**: 代码树规范化：IPv4/IPv6 改名与全量注释精简
**Branch**: `master`

### Summary

两阶段整理，63 个 commit。阶段一：58 个 .cs 文件里 850 次 Ipv→IPv 子串替换（842 处 token），逐文件以「HEAD 施加同一替换后字节恒等」证明只动了拼写，5 个 backend spec 的 9 处符号引用同步；编译期由 cref 校验兜底。阶段二：C 档全量重审 596 个文件的约 2 万行注释，按字节装箱切成 56 个主批次 + 2 个补漏批，每批一个子代理（禁用脚本编辑、禁用 dotnet），约 1001 处注释站点改写、13 处字符串字面量指针清除；每轮由父代理串行验证。全部门禁绿：Release 构建 0 警告、1663 测试通过（14 程序集）、dotnet format 空输出、jb inspectcode 清缓存后 0 Issue、E2E 分析 oracle-diff 51 slice 全 equal + check-fairness 14 guard 全 PASS。注释与字符串中的任务名/条目号/日期/design 章节引用全树归零。AGENTS.md 新增 Comment Conventions 一节固化本次判据与 S125/golden-pin 两个坑。

### Git Commits

| Hash | Message |
|------|---------|
| `488e06f` | refactor(naming): spell IPv4/IPv6 canonically across src |
| `fe97c11` | refactor(naming): spell IPv4/IPv6 canonically across tests and benchmarks |
| `85b199a` | docs(spec): follow the IPv4/IPv6 rename in the backend specs |
| `89d784f` | refactor(comments): trim archive metadata and narration in batch 01 |
| `c53a50a` | refactor(comments): trim archive metadata and narration in batch 55 |
| `17a439c` | refactor(benchmarks): drop archive pointers printed by the idle-pump notes |
| `60f4433` | refactor(benchmarks): drop archive pointers printed by the census and sweep notes |
| `c7db454` | docs(agents): record the comment conventions this campaign applied |

### Status

[OK] **Completed**
