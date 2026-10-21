# 执行计划：Host UDP B3

## 0. 准备

- [ ] `windows-vm status`；建 checkpoint/快照（避免反复重启取池基线）。
- [ ] 工作区 tar 同步到 VM；`load-driver.ps1` 构建/安装；确认 ABI v3。
- [ ] 先用临时/手工方式复测 connected UDP context 释放窗口（1/10/60/180/300s），记录到
      `research/connected-context.md`；决定 U12 断言口径。

## 1. UdpSuite（工具子代理 A）

- [ ] `tools/wfprobe/UdpSockets.cs`：IPv6/dual-mode listener、cmsg level 0/41、
      `IPV6_WFP_REDIRECT_CONTEXT`、`sockaddr_in6`、`[addr]:port`。
- [ ] `tools/wfprobe/UdpSuite.cs`：U1–U13；`UdpCommands.Client` 支持 IPv6/`pid=`。
- [ ] `Program.cs`：`udp-rules` 命令与 usage；`PrintEvent` 输出 protocol；
      `PrintStats` 输出 4 个 UDP 计数器。
- [ ] `WfpDevice` / `Protocol`：按需加方法/常量，不改 ABI。
- [ ] `control --clear-self`：count=0 时清零 `self_process_ids`。
- [ ] `WinForward.Controller/StateCommand`：counters 行输出 UDP 计数。
- [ ] 每个用例独立 reset；U3 控制测量窗口；U7 ICMP 用 `ping -n 2`。
- [ ] 宿主 `tools/check.sh --full`；VM 手工/套件跑 U1–U13。

## 2. gate / mutation（工具子代理 A）

- [ ] `tools/windows/gate.ps1`：在 `rules` 后加 `udp-rules`；失败即 gate 失败。
- [ ] 池断言按 connected 实测结论写；卸载后 WfPr 断言按结论处理。
- [ ] `tools/windows/mutation.ps1`：加入 6 个 UDP id，跑 `udp-rules` 期望非 0；
      每条命中数 1，运行前后字节一致。
- [ ] VM 完整 `gate.ps1` PASS；mutation 全红。

## 3. 文档（文档子代理 B，文件集不相交）

- [ ] `src/driver/README.md`：ABI v3、UDP 规则/context/事件/计数、block 静默丢包、
      `udp_blocked` 定义、any/fallback 6/17、ICMP 不被拦、listener/self-PID、测试命令。
- [ ] `.trellis/spec/backend/wfp-architecture.md`：D7 Host TCP+UDP；UDP 事实；
      未决清单。
- [ ] `AGENTS.md`：结构性缺口更新；不要改 TRELLIS 标记块以外的受管区。
- [ ] `src/userland/README.md`、`Policy/RuleOrderValidator.cs` 注释：删除“无条件 filter”。
- [ ] 只改文档/注释；不碰 `tools/`、`src/driver/*.cpp`、`src/userland/*.cs` 逻辑。

## 4. 合并与结构审查

- [ ] 并发合并后读一遍整体：UdpSuite 与 gate/observability 是否重复实现；
      文档是否与代码一致；文件集是否越界。
- [ ] 独立检查子代理：计划 vs diff、U1–U13 断言强度、gate/mutation、文档一致性。

## 5. 最终验收与归档

- [x] 宿主 `tools/check.sh --full` 全绿；C# 测试；ctest。
- [x] VM 完整 `gate.ps1` PASS（505 断言）；mutation PASS。
- [x] 验收 A1–A12 逐条勾选；未通过项写明原因。
- [ ] 归档 B0–B3 子任务与父任务；写 workspace journal；`update_goal complete`。

## 回滚点

- 工具/文档两批独立提交；最终 gate 失败按最近一批回退。不要伪绿。

## 交付记录（2026-10-22）

- **A（工具/验证）**：`wfprobe udp-rules` U1–U13；IPv6/dual-mode；connected 多 datagram；
  U12 干净基线严格断言、connected 只记 LIMIT；28 处 UDP 计数不变式；`--only` 0 条 exit 9；
  `--clear-self`；PrintEvent protocol / PrintStats / StateCommand 4 计数；
  `gate.ps1` 加 `udp-rules` + 卸载 WfPr before/after；`mutation.ps1` 6 条 UDP 变异。
- **B（文档）**：驱动 README ABI v3/UDP/block/生命周期；wfp-architecture D7/§7.11/O7；
  userland README 与 RuleOrderValidator 文案。AGENTS.md 被 `.gitignore` 忽略，只本机生效，
  未 force-add。
- 干净启动 gate #1：PASS，507 断言；同 boot gate #2：PASS，505 断言；最终修复后 gate：
  PASS，505 断言。udp-rules 255–257 断言。
- mutation 16–21：PASS，6 条全红，baseline 完整 U1–U13 PASS，源码逐字节一致。
- connected context：1/10/60/180/300s 均不释放，`sc stop` 也不回收；记录为 LIMIT，不伪绿。
- 同 socket 回包不重新 classify；self-PID 由 U1 独立 socket 场景验证。
- 检查者两轮：首轮有条件通过（U12 伪绿等），修复后最终通过。
- 待办：归档 B0–B3 与父任务、写 journal、`update_goal complete`。
