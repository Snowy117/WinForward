# 验证记录

> 本文件按 `prd.md` 的 Acceptance Criteria 逐项记录证据。未执行的项保持 `[ ]`。

## 0. 环境与基线

- [x] 工作分支：`feat/host-rule-arrays-process-match`（从 `main` 25daf21 切出；base branch = `main`）。
- [x] `main` 基线 `tools/check.sh --full`：**PASS**，8 PASS / 0 FAIL / 0 SKIP，106.782 s（独立 worktree）。
- [x] WinLTSC `192.168.100.2` 运行中；Linux 主机 `192.168.100.4:40001` TCP echo 已从 VM 实测 `connect=ok read=4 data=ping`。
- [x] 驱动门禁目标：`pwsh -File tools/windows/gate.ps1 -Root C:\workspace\WinForward.Kernel -Remote 192.168.100.4:40001`。

## 1. 功能验收（对照 prd.md）

| # | 验收项 | 方法 | 证据 | 状态 |
|---|---|---|---|---|
| A1 | 写满五个数组的配置规则编译成 1 条 blob 规则；驱动回读规则数相等 | C# `CompilerTests` + `rules` 套件 | `compiler/一条配置规则只产出一条 blob 规则` PASS；VM `rules` parity 用例 | [x] |
| A2 | 文件名选择器 `dns.exe` 命中 `\device\...\dns.exe`，`dns` 不命中 `dns.exe` | C++ matcher gtest + 差分 + wfprobe | gtest + `HostRuleParitySuite` 文件名用例（VM 实验 PASS） | [x] |
| A3 | 目录选择器命中直接子文件与深层后代，不命中 `MyApp2` | C++ matcher gtest + 差分 + wfprobe | gtest + parity 目录用例（补 SetRuleBlob 后 VM 实验 PASS） | [x] |
| A4 | 精确路径只命中该文件，同名文件在其他目录不命中 | C++ matcher gtest + 差分 + wfprobe | gtest + parity 精确路径用例 | [x] |
| A5 | 同字段 OR、字段间 AND、规则顺序与 `rule_index` 不变 | 差分用例 + `rules` 套件 | 42 组 × 15 连接差分通过；parity 数组用例 | [x] |
| A6 | block 精确路径数组生效；block 文件名 / 目录前缀编译期失败；驱动对同形 blob 整块拒绝 | C# 测试 + wfprobe 负向 + 驱动 | `compiler-selector/*` PASS；最终 VM gate 的 parity block 用例 + 驱动拒绝 PASS | [x] |
| A7 | 现有顺序歧义拒绝继续生效，消息带两条规则序号 | `RuleOrderTests` + 驱动 `rule_order_valid` | 288 个 C# 测试通过（`[tests] total=288 passed=288`）；VM rules conflict PASS | [x] |
| A8 | 现有编译期校验继续生效 | `CompilerTests` / `InvalidInputTests` / `ConfigurationTests` | 288 个 C# 测试通过 | [x] |
| A9 | C# blob 与 C++ `try_open_blob` + `match` 逐条一致 | `wf_check` 差分 | `check.sh --full` 8/8 PASS（ctest 84/84，含 `Differential.DecisionsMatchExpected`） | [x] |
| A10 | 驱动门禁 `appid` / `rules` / `udp-rules` / `forwarded-rules` / `selftest` / `gates` 全绿 | WinLTSC gate.ps1 | **GATE-RESULT=PASS**（2026-10-24 11:52）；vm-check 118/0；E2E 48 checks；驱动 SHA-256 `4027481512319429C772D940208D30A1CA20297EB98F7CBEA7AED8AD8E6D1C39` | [x] |
| A11 | 4096 条空 permit 表、forwarded UDP core 不回归 | gate.ps1 + Linux forwarded 门禁（如需要） | forwarded-rules / UDP / E2E 48 checks PASS（不回归部分已证）。**4096 子项未执行**：v2 的 64 KiB 上限只允许约 2046 条规则（56 + 32×N），仓库里也没有 4096 条空 permit 表的既有用例；PRD 的该数值按 `research/plan-review.md` P2 #13 记录为不可执行项，未计入已验证。 | [x]（仅不回归部分） |

## 2. 批次记录

### Batch 1：Linux 纵切（ABI v2 + matcher + C# 模型/编译/配置 + 差分 + wfprobe）

- 实现：共享 ABI/matcher、C# 产品层、测试/差分（多个子代理）。
- 检查：计划审查 + 结构审查（独立子代理），P0/P1 已修。
- 验证：`tools/check.sh --full` **8/8 PASS**（124.6 s）；`tools/userland/verify-gates.sh` **25 变异红 / 4 基线绿**；C# 288 测试、ABI 29、ctest 84。
- 提交：待提交（与 Batch 2 一并按逻辑提交）。

### Batch 2：驱动 + wfprobe 驱动用例 + WinLTSC 门禁

- 实现：`rule_table.cpp` 顺序/集合/block 边界/预算、`block_filters.cpp` 数组展开、`mutation.ps1`、wfprobe v2 夹具/parity 用例、VmChecks 数组/前缀/设备路径。
- 检查：结构审查（`research/structural-review.md`），关键项已修。
- 验证：**GATE-RESULT=PASS**；驱动 SHA-256 `4027481512319429C772D940208D30A1CA20297EB98F7CBEA7AED8AD8E6D1C39`；vm-check 118/0；E2E 48 checks。
- 提交：待提交。

### Batch 3：文档 / spec + 最终验收

- 实现：新增 `.trellis/spec/backend/host-rule-blob.md`；更新 `wfp-architecture.md`（§5 v2、O5 已决、§7.9/§7.10）、`index.md`、`forwarded-rule-blob.md`、`csharp-conventions.md`、`src/userland/README.md`、`src/driver/README.md`。
- 检查：最终干净上下文检查代理（独立复跑 Linux 门禁 + 变异门禁 + 验收证据核对）。
- 验证：文档批 `tools/check.sh --fast` 5/5 PASS；过时表述复查 0 处；最终检查结果见 `research/final-check.md`。
- 提交：待提交。

## 3. 已知边界（不是失败项）

- block 不支持文件名 / 目录前缀进程选择器；WFP auth filter 只能精确匹配 ALE_APP_ID。后续任务
  `10-24-block-classify-enforced-sink` 处理 classify-enforced block 与顺序语义。
- 路径型选择器要求编译期路径存在（文件或目录）。
- 网段池不做跨规则去重（blob 略大，规则区间连续）。
- blob v1 不再读取；控制器 / 驱动 / wfprobe 同批升级。

## 4. 最终验收命令清单

```bash
# Linux
tools/check.sh --full
tools/userland/verify-gates.sh

# Windows（WinLTSC，管理员）
pwsh -File tools/windows/gate.ps1 -Root C:\workspace\WinForward.Kernel -Remote 192.168.100.4:40001
```

## 5. 反向验证 / mutation

- [x] `tools/userland/verify-gates.sh` 覆盖新增编译期检查：**25 条变异全部变红，4 条基线为绿**
      （2026-10-24 复跑，见 `research/final-check.md`）。新增的 5 条（block 文件名/目录、预算、
      64 KiB、空数组、端口 0）已逐条手动确认是测试失败而不是构建失败。
- [x] `tools/windows/mutation.ps1` 的 `selector-utf8-guard` 已随校验移入 `src/common/rule_blob.h`；
      block 表达边界由 `rules` 的负向用例覆盖，顺序集合检查复用既有 `rule-order-conflict` /
      `fallback-block-conflict`（未新增独立变异项）。
- [x] 「删掉检查后测试变红」的证据：5 条新增 C# 变异各自命中对应测试（`compiler-selector/*`、
      `compiler-limit/*`、`config-array/*`），记录见 `research/final-check.md`。
